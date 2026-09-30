using System.Text.Json;
using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Analysis;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using Xunit;

namespace AquaHub.Tests;

/// <summary>Attached files: a question about them is answered from them, not by searching the PC.</summary>
public class AttachmentPlanningTests
{
    private static readonly AskOptions Pc = new() { Computer = true, Web = true };

    [Theory]
    [InlineData("Summarise the attached notes in two bullets")]
    [InlineData("What's wrong in this screenshot?")]
    [InlineData("Translate this document into French")]
    [InlineData("Any mistakes?")]
    public void AMessageAboutTheAttachmentsSearchesNothing(string question)
    {
        var plan = AskPlanner.Heuristic(question, Array.Empty<LlmMessage>(), Pc, attachments: 1);
        Assert.True(plan.AboutAttachments);
        Assert.DoesNotContain(plan.Intent, new[] { "files", "screen" });
        Assert.Empty(plan.FileTerms);
    }

    [Fact]
    public void AskingForOtherFilesStillSearches()
    {
        var plan = AskPlanner.Heuristic("Find my other invoices from this company", Array.Empty<LlmMessage>(), new AskOptions { Computer = true }, attachments: 1);
        Assert.False(plan.AboutAttachments);
        Assert.Equal("files", plan.Intent);
        Assert.Contains("invoices", plan.FileTerms);
    }

    [Fact]
    public void TheModelCantTurnItIntoAFileSearch()
    {
        const string question = "Summarise the attached notes in two bullets";
        var rules = AskPlanner.Heuristic(question, Array.Empty<LlmMessage>(), Pc, attachments: 1);
        using var doc = JsonDocument.Parse("""{"intent":"files","web_queries":[],"file_terms":["notes","summarise","attached","bullets"],"format":"list"}""");
        var plan = AskPlanner.Merge(doc.RootElement, rules, question, Pc);
        Assert.Equal("chat", plan.Intent);
        Assert.Empty(plan.FileTerms);
        // And without attachments, those words never count as a file's name anyway.
        Assert.Equal(new[] { "notes" }, AskPlanner.NameTerms(question));
    }

    [Theory]
    [InlineData("the newest articles about SpaceX", "SpaceX")]
    [InlineData("latest news on the Dublin Bus strike", "the Dublin Bus strike")]
    [InlineData("Look through https://arstechnica.com/space/ and tell me the newest articles about SpaceX, with links", "SpaceX")]
    public void QueriesDropWhatAPageLists(string text, string query) => Assert.Equal(query, AskPlanner.WebQuery(text));

    [Fact]
    public void QuestionsAboutYourOwnRecordsSearchYourFiles()
    {
        const string question = "When is my boiler service due, and who did the last one?";
        var plan = AskPlanner.Heuristic(question, Array.Empty<LlmMessage>(), new AskOptions { Computer = true });
        Assert.Equal("files", plan.Intent);
        Assert.Contains("boiler", plan.FileTerms);
        Assert.Contains("service", plan.FileTerms);
        Assert.NotEqual("files", AskPlanner.Heuristic(question, Array.Empty<LlmMessage>(), new AskOptions { Web = true }).Intent);
        Assert.NotEqual("files", AskPlanner.Heuristic("What's the best boiler service in Dublin?", Array.Empty<LlmMessage>(), new AskOptions { Computer = true, Web = true }).Intent);
    }

    [Fact]
    public void ThePlannerIsToldWhatIsAttached()
    {
        var request = AskPlanner.Request("Summarise the attached notes", Array.Empty<LlmMessage>(), Pc,
            new PlanContext { Attachments = new[] { "notes.txt" } }, DateTimeOffset.Now);
        Assert.Contains("Attached to this message: notes.txt", request.Messages[0].Content);
    }

    [Fact]
    public void ArticlesWithLinksAreAList()
    {
        const string question = "Look through https://arstechnica.com/space/ and tell me the newest articles about SpaceX, with links";
        var rules = AskPlanner.Heuristic(question, Array.Empty<LlmMessage>(), new AskOptions { Web = true });
        Assert.Equal("list", rules.Format);
        using var doc = JsonDocument.Parse("""{"intent":"site","web_queries":["SpaceX"],"urls":[],"file_terms":[],"format":"direct"}""");
        Assert.Equal("list", AskPlanner.Merge(doc.RootElement, rules, question, new AskOptions { Web = true }).Format);
        Assert.Equal("direct", AskPlanner.Heuristic("Who founded SpaceX?", Array.Empty<LlmMessage>(), new AskOptions { Web = true }).Format);
    }

    [Fact]
    public void LinksFromAquasOwnAnswersAreNotOpenedOnTheirOwn()
    {
        var rules = AskPlanner.Heuristic("open that one", Array.Empty<LlmMessage>(), new AskOptions { Web = true });
        using var doc = JsonDocument.Parse("""{"intent":"page","web_queries":[],"urls":["https://evil.example/collect"],"file_terms":[],"format":"direct"}""");
        var history = new[] { new LlmMessage("user", "find me a page about this"), new LlmMessage("assistant", "Try https://evil.example/collect [1].") };
        Assert.Empty(AskPlanner.Merge(doc.RootElement, rules, "open that one", new AskOptions { Web = true }, null, history).Urls);
    }

    [Fact]
    public void ResearchSearchesFromAtLeastThreeAngles()
    {
        Assert.Equal(new[] { "Starship flight 14 latest", "Starship flight 14 analysis" }, AskAgent.ResearchAngles("Starship flight 14", recent: true));
        Assert.Equal(new[] { "heat pumps explained", "heat pumps review" }, AskAgent.ResearchAngles("heat pumps site:example.com", recent: false));
    }

    [Fact]
    public void OnlyLinksFromTheChatAreOpened()
    {
        const string question = "and anything about NIRSpec?";
        var rules = AskPlanner.Heuristic(question, Array.Empty<LlmMessage>(), new AskOptions { Web = true });
        using var doc = JsonDocument.Parse("""
            {"intent":"site","web_queries":["NIRSpec"],"urls":["https://en.wikipedia.org/wiki/NIRSpec","https://arstechnica.com/space/"],"file_terms":[],"format":"direct"}
            """);
        var history = new[] { new LlmMessage("user", "look through arstechnica.com/space for SpaceX news"), new LlmMessage("assistant", "Here's what I found.") };
        var plan = AskPlanner.Merge(doc.RootElement, rules, question, new AskOptions { Web = true }, null, history);
        Assert.Equal(new[] { "https://arstechnica.com/space/" }, plan.Urls); // the invented Wikipedia address is dropped
    }
}

/// <summary>Typing, keys and clicks after untrusted text; links in the browser; what the approval card offers.</summary>
public class ApprovalRuleTests
{
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;
    private static JsonElement Args(string query) => JsonDocument.Parse(JsonSerializer.Serialize(new { query })).RootElement;

    private static AskRun Run(HubSettings? s = null, bool untrusted = false, bool sawPrivate = false, HashSet<string>? allowed = null,
        HashSet<string>? privateTerms = null, string question = "") => new()
    {
        State = new HubState(null), Settings = s ?? new HubSettings(), Book = new SourceBook(), Options = new AskOptions(), Host = new NullHost(),
        AllowedForChat = allowed ?? new HashSet<string>(), SawUntrusted = untrusted, SawPrivate = sawPrivate,
        PrivateTerms = privateTerms ?? new HashSet<string>(StringComparer.Ordinal), Question = question,
    };

    private sealed class ActTool(string name, bool afterUntrusted = false, bool oneAtATime = false, bool link = false) : AskTool
    {
        public override string Name => name;
        public override string Description => name;
        public override JsonObject Parameters => new();
        public override ToolAccess Access => ToolAccess.Act;
        public override bool ConfirmAfterUntrusted => afterUntrusted;
        public override bool OneAtATime(JsonElement args) => oneAtATime;
        public override bool OpensLink(JsonElement args) => link;
        public override string Describe(JsonElement args) => name;
        public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct) => Task.FromResult(new ToolResult("ok", "ok"));
    }

    [Fact]
    public void AfterUntrustedTextTypingAsksEveryTimeWhateverWasAllowed()
    {
        var type = new ActTool("type_text", afterUntrusted: true);
        var quiet = new HubSettings();
        quiet.Ask.ConfirmActions = false;
        Assert.Null(AskAgent.ApprovalFor(type, NoArgs, "Type", Run(quiet)));
        var approval = AskAgent.ApprovalFor(type, NoArgs, "Type", Run(quiet, untrusted: true, allowed: new() { "type_text", "type_text:untrusted" }));
        Assert.NotNull(approval);
        Assert.False(approval!.AllowForChat);
    }

    [Fact]
    public void KeyPressesAndEnterAreApprovedOneAtATime()
    {
        Assert.False(AskAgent.ApprovalFor(new ActTool("press_keys", oneAtATime: true), NoArgs, "Press", Run())!.AllowForChat);
        Assert.True(AskAgent.ApprovalFor(new ActTool("launch_app"), NoArgs, "Open", Run())!.AllowForChat);
    }

    [Fact]
    public void ALinkOpenedInTheBrowserFollowsTheWebRules()
    {
        var open = new ActTool("open_item", link: true);
        var approval = AskAgent.ApprovalFor(open, NoArgs, "Open https://example.com/?q=x in your browser", Run(sawPrivate: true, allowed: new() { "open_item" }));
        Assert.Equal("web-after-private", approval!.Tool);
        Assert.Null(AskAgent.ApprovalFor(new ActTool("open_item"), NoArgs, "Open notes.txt", Run(sawPrivate: true, allowed: new() { "open_item" })));
    }

    [Fact]
    public void NamesFromWhatAquaRemembersNeedAnOkToGoOnline()
    {
        var terms = AskAgent.PrivateTermsFor(new HubState(null), new[] { "My sister Aoife lives in Galway", "I prefer answers in bullet points" }, DateTimeOffset.Now);
        var run = Run(privateTerms: terms, question: "What should I get my sister for her birthday?");
        Assert.Equal("web-with-calendar", AskAgent.ApprovalFor(new WebSearchTool(), Args("birthday gift Aoife Galway"), "Search", run)!.Tool);
        Assert.Null(AskAgent.ApprovalFor(new WebSearchTool(), Args("birthday gift ideas for a sister"), "Search", run));
        Assert.Null(AskAgent.ApprovalFor(new WebSearchTool(), Args("bullet points answers"), "Search", run));
    }

    [Fact]
    public void TheCardShowsTheWholeQueryAndAddress()
    {
        var query = "gift ideas " + string.Join(' ', Enumerable.Repeat("word", 30));
        Assert.Contains(query, new WebSearchTool().Describe(Args(query)));
        const string url = "https://example.com/search?q=something-private&page=2";
        Assert.Equal("Read " + url, new ReadWebpageTool().Describe(JsonDocument.Parse(JsonSerializer.Serialize(new { url })).RootElement));
    }
}

/// <summary>Which apps Ask may start and operate.</summary>
public class DesktopRuleTests
{
    [Theory]
    [InlineData("explorer")]
    [InlineData("cmd")]
    [InlineData("WindowsTerminal")]
    [InlineData("Code")]
    [InlineData("KeePassXC")]
    [InlineData("AquaHub")]
    [InlineData("regedit.exe")]
    [InlineData("pythonw")]
    public void AppsThatRunCommandsOrHoldSecretsAreOffLimits(string process) => Assert.NotNull(DesktopRules.WhyBlocked(process));

    [Theory]
    [InlineData("notepad")]
    [InlineData("Spotify")]
    [InlineData("WINWORD")]
    [InlineData("msedge")]
    [InlineData("ApplicationFrameHost")]
    public void OrdinaryAppsCanBeOperated(string process) => Assert.False(DesktopRules.IsBlockedProcess(process));

    [Theory]
    [InlineData("Command Prompt", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\cmd.exe")]
    [InlineData("Run", "Microsoft.Windows.Shell.RunDialog")]
    [InlineData("Windows PowerShell", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("Terminal", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")]
    [InlineData("Registry Editor", @"{F38BF404-1D43-42F2-9305-67DE0B28FC23}\regedit.exe")]
    [InlineData("Python 3.12 (64-bit)", @"C:\Python312\python.exe")]
    [InlineData("Visual Studio Installer", @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe")]
    [InlineData("My tool", @"C:\tools\run.bat")]
    [InlineData("Ubuntu", "CanonicalGroupLimited.Ubuntu_79rhkp1fndgsc!ubuntu")]
    [InlineData("Shortcut", "C:\\Windows\\System32\\cmd.exe /c del x")]
    public void ShellsInstallersAndSystemToolsAreNeverStarted(string name, string target) => Assert.NotNull(DesktopRules.LaunchRefusal(name, target));

    [Theory]
    [InlineData("Notepad", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe")]
    [InlineData("Spotify", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")]
    [InlineData("Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData("Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE")]
    [InlineData("Steam", @"C:\Program Files (x86)\Steam\steam.exe")]
    public void OrdinaryAppsCanBeStarted(string name, string target) => Assert.Null(DesktopRules.LaunchRefusal(name, target));

    [Fact]
    public void YouCanLimitAskToTheAppsYouList()
    {
        Assert.Null(DesktopRules.NotAllowed("notepad", "any", Array.Empty<string>()));
        Assert.NotNull(DesktopRules.NotAllowed("cmd", "any", Array.Empty<string>()));
        Assert.Null(DesktopRules.NotAllowed("notepad.exe", "listed", new[] { "Notepad", "spotify" }));
        Assert.NotNull(DesktopRules.NotAllowed("WINWORD", "listed", new[] { "notepad" }));
        Assert.NotNull(DesktopRules.NotAllowed("cmd", "listed", new[] { "cmd" })); // off-limits apps stay off limits
    }

    [Theory]
    [InlineData("alt+f11")]
    [InlineData("alt+tab")]
    [InlineData("ctrl+shift+esc")]
    [InlineData("ctrl+esc")]
    public void ShortcutsThatLeaveTheAppAreRefused(string keys) => Assert.Null(KeyChords.Parse(keys));
}

/// <summary>Citations that match their sources, files that can be opened, dated sources.</summary>
public class CitationCheckTests
{
    [Fact]
    public void ACitationOnTheWrongSourceIsDropped()
    {
        var book = new SourceBook();
        var prima = book.Add("NASA picks PRIMA far-infrared probe", "Ars Technica", "https://arstechnica.com/prima", "news",
            "The far-infrared telescope would launch in 2032 and cover a wide wavelength range.");
        var jwst = book.Add("NIRSpec", "NASA", "https://jwst.nasa.gov/nirspec", "web", "NIRSpec, the near-infrared spectrograph on JWST, observes 0.6 to 5.3 microns.");
        var text = $"NIRSpec on JWST covers 0.6 to 5.3 microns [{prima}][{jwst}]. PRIMA would launch in 2032 [{prima}].";

        var verified = book.Verify(text, out var removed);

        Assert.Equal(1, removed);
        Assert.Equal($"NIRSpec on JWST covers 0.6 to 5.3 microns [{jwst}]. PRIMA would launch in 2032 [{prima}].", verified);
    }

    [Fact]
    public void ALongClaimSharingOnlyGenericWordsIsNotSupported()
    {
        var book = new SourceBook();
        var prima = book.Add("With PRIMA, NASA will try to build a billion-dollar space telescope in record time", "Ars Technica", "https://arstechnica.com/prima", "news",
            "The far-infrared space telescope would study how galaxies and planets form.");
        var text = $"The James Webb Space Telescope's NIRSpec splits light from distant objects into spectra to reveal their chemistry, temperature and motion [{prima}].";
        Assert.DoesNotContain($"[{prima}]", book.Verify(text, out var removed));
        Assert.Equal(1, removed);
    }

    [Fact]
    public void MadeUpLinksOnASiteAquaReadAreMendedOrDropped()
    {
        var seen = new Dictionary<string, string>
        {
            ["https://arstechnica.com/space/2026/09/starships-first-orbital-launch-gives-lift-to-spacexs-next-gen-starlinks/"] = "SpaceX's Starship goes orbital, deploying first next-gen Starlinks",
            ["https://arstechnica.com/space/2026/09/after-seven-years-a-spacecraft-company-is-releasing-its-otters-into-the-wild/"] = "After seven years, a spacecraft company is releasing its Otters into the wild",
        };
        const string answer = "*   https://arstechnica.com/space/2026/09/starships-first-orbital-launch-gives-lift-to-spacexs-next-gen-starlinks/ [4]\n" +
                              "*   https://arstechnica.com/space/2026/09/spacex-releases-otters-after-seven-years-in-space-program/ (inferred from context)\n" +
                              "*   [Rocket Report](https://arstechnica.com/space/2026/09/rocket-report-made-up/)\n" +
                              "*   NASA's own page: https://www.nasa.gov/humans-in-space/";

        var mended = AnswerText.RepairLinks(answer, seen, out var changed);

        Assert.Equal(2, changed);
        Assert.Contains("https://arstechnica.com/space/2026/09/after-seven-years-a-spacecraft-company-is-releasing-its-otters-into-the-wild/", mended);
        Assert.DoesNotContain("spacex-releases-otters", mended);
        Assert.DoesNotContain("inferred", mended);
        Assert.Contains("*   Rocket Report", mended);          // no match: the link goes, the title stays
        Assert.DoesNotContain("rocket-report-made-up", mended);
        Assert.Contains("https://www.nasa.gov/humans-in-space/", mended); // a site Aqua didn't read is left alone
    }

    [Fact]
    public void AListingPagesHeadlineLinksAreKeptWhenThePictureLinksFirst()
    {
        // Ars Technica's section pages: each article is linked from its picture (no text), then from its headline.
        const string html = """
            <a href="https://arstechnica.com/space/2026/09/nasa-has-a-dragon-dilemma/"><img src="x.jpg" alt=""/></a>
            <h2><a class="text-gray-700" href="https://arstechnica.com/space/2026/09/nasa-has-a-dragon-dilemma/">NASA has a Dragon dilemma</a></h2>
            <a href="https://arstechnica.com/space/2026/09/starship-goes-orbital/"><img src="y.jpg"/></a>
            <h2><a href="https://arstechnica.com/space/2026/09/starship-goes-orbital/">SpaceX's Starship goes orbital</a></h2>
            """;
        var links = ArticleExtractor.ExtractLinks(html, "https://arstechnica.com/space/");
        Assert.Equal(new[] { "NASA has a Dragon dilemma", "SpaceX's Starship goes orbital" }, links.Select(l => l.Text));
    }

    [Fact]
    public void NonBreakingSpacesAndPlaceholderCitationsAreTidied()
    {
        Assert.Equal("Due by 14 October 2026.", AnswerText.Tidy("Due by 14 October 2026 [n]."));
    }

    [Fact]
    public void HtmlCodeTagsBecomeInlineCode() =>
        Assert.Equal(@"It's at `C:\x\notes.txt`.", AnswerText.Tidy(@"It's at <code>C:\x\notes.txt</code>."));

    [Fact]
    public void AFencedPathBecomesAnInlinePath()
    {
        Assert.Equal("Here: `C:\\Users\\me\\Pictures\\my_logo5.png`.", AnswerText.Tidy("Here: ``C:\\Users\\me\\Pictures\\my_logo5.png``."));
        Assert.Equal("It's here:\n`C:\\Users\\me\\final_v3.png`\nOpen it.", AnswerText.Tidy("It's here:\n```\nC:\\Users\\me\\final_v3.png\n```\nOpen it."));
        const string code = "```\nvar x = 1;\n```";
        Assert.Equal(code, AnswerText.Tidy(code));
    }

    [Fact]
    public void CopiedContextLabelsBecomeCitations()
    {
        Assert.Equal("It was cancelled [6][5].", CitationText.Normalize("It was cancelled [6][PAGE 5]."));
        Assert.Equal("See [3] and [4].", CitationText.Normalize("See [Source 3] and [source #4]."));
        Assert.Equal("A [page] of notes.", CitationText.Normalize("A [page] of notes."));
    }

    [Fact]
    public void TheUsersOwnFilesAndUncitedTextAreLeftAlone()
    {
        var book = new SourceBook();
        var file = book.Add("notes.txt", "Your files", @"C:\x\notes.txt", "file", "shopping list");
        const string plain = "Nothing cited here.";
        Assert.Equal(plain, book.Verify(plain, out _));
        Assert.Equal($"Your budget looks fine [{file}].", book.Verify($"Your budget looks fine [{file}].", out var removed));
        Assert.Equal(0, removed);
    }

    [Fact]
    public void AFileNamedInTheAnswerBecomesAChip()
    {
        var book = new SourceBook();
        var n = book.Add("my_logo5.png", "Your files", @"C:\Users\me\Pictures\my_logo5.png", "file");
        Assert.Contains(book.CitedIn("It's `my_logo5.png` in your Pictures folder."), c => c.Number == n);
        Assert.Empty(book.CitedIn("I couldn't find it."));
    }

    [Fact]
    public void ASearchResultBecomesAFullSourceOnceItsPageIsRead()
    {
        var book = new SourceBook();
        var n = book.Add("Starship flight 11", "space.com", "https://www.space.com/starship-11", "result", "snippet");
        Assert.Equal("result", book.All.Single().Kind);
        Assert.Equal(n, book.Add("Starship flight 11", "space.com", "https://www.space.com/starship-11", "web", "the article"));
        Assert.Equal("web", book.All.Single().Kind);
    }

    [Fact]
    public void FileLinksInAnswersAreLocalPathsOnly()
    {
        Assert.Equal(@"C:\Users\me\Pictures\final_v3.png", AnswerText.LocalPath(@"file://C:\Users\me\Pictures\final_v3.png"));
        Assert.Equal(@"C:\Users\me\My Pictures\a b.png", AnswerText.LocalPath("file:///C:/Users/me/My%20Pictures/a%20b.png"));
        Assert.Equal(@"C:\x\y.pdf", AnswerText.LocalPath(@"C:\x\y.pdf"));
        Assert.Null(AnswerText.LocalPath(@"\\server\share\x.png"));
        Assert.Null(AnswerText.LocalPath("file://server/share/x.png"));
        Assert.Null(AnswerText.LocalPath(@"C:\x\..\Windows\notepad.exe"));
        Assert.Null(AnswerText.LocalPath("https://example.com/x.png"));
    }

    [Fact]
    public void SourcesCarryTheirDate()
    {
        var now = new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);
        var yesterday = TimeText.Dated(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), now);
        Assert.Contains("28 Sep 2026", yesterday);
        Assert.Contains("ago", yesterday);
        Assert.DoesNotContain("ago", TimeText.Dated(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), now));
    }

    [Fact]
    public void OnlyFeedItemsNamingWhatWasAskedAreRelated()
    {
        var prima = new HubHit("article", "a1", "NASA picks PRIMA far-infrared probe", "A wide wavelength range telescope", "Ars Technica", null, null, 1.5);
        const string question = "What wavelength range does JWST's NIRSpec cover?";
        Assert.False(AskAgent.Related(prima, question));
        Assert.True(AskAgent.Related(prima with { Title = "JWST's NIRSpec finds water on a distant world" }, question));
        Assert.True(AskAgent.Related(prima, "what wavelength range counts as far infrared")); // no names: the search's own threshold decides
        var budget = new HubHit("story", "s1", "Budget 2027: what it means for renters", "Tax credits and energy supports", "RTÉ", null, null, 1.5);
        Assert.True(AskAgent.Related(budget, "What's in the Irish Budget for renters?"));
    }

    [Fact]
    public void DatesComeFromLinksForTheNewestFirst()
    {
        Assert.Equal(new DateTime(2026, 9, 28), AskAgent.LinkDate("https://arstechnica.com/space/2026/09/28/starship/"));
        Assert.Equal(new DateTime(2025, 9, 1), AskAgent.LinkDate("https://arstechnica.com/space/2025/09/spacex-news/"));
        Assert.Null(AskAgent.LinkDate("https://arstechnica.com/space/"));
    }

    [Fact]
    public void ShopsAreReadOnlyWhenNothingBetterTurnsUp()
    {
        var results = new List<WebResult>
        {
            new("Starship flight 11 launch: everything we know", "https://www.basenor.com/blogs/news/starship-flight-11", "Starship flight 11 launch orbit booster", "basenor.com"),
            new("Starship flight 11 launch recap", "https://www.somesite.net/starship-flight-11", "Starship flight 11 launch orbit booster", "somesite.net"),
        };
        Assert.Equal("somesite.net", AskAgent.PickPages(results, "Starship flight 11 launch", 1).Single().Site);
    }

    [Fact]
    public void FeedFurnitureIsNotASummary()
    {
        var points = TextTools.Extract(new[]
        {
            "Read more: Starship's next flight could come within weeks, SpaceX says. Starship flew its eleventh test flight on Monday from Starbase in Texas.",
        }, new[] { "Starship flight" });
        Assert.NotEmpty(points);
        Assert.DoesNotContain(points, p => p.StartsWith("Read more", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>"Remember …" and "Forget …" only when that's all the message is.</summary>
public class MemoryCommandTests
{
    [Theory]
    [InlineData("Note the differences between Python and Go")]
    [InlineData("Forget that, search for Z instead")]
    [InlineData("Remember that my flight is at 9 — what's the weather then")]
    [InlineData("Remember that the meeting moved to Friday, can you draft an email about it")]
    [InlineData("Forget it")]
    [InlineData("Remember that I'm vegetarian. Suggest a dinner")]
    public void OrdinaryMessagesAreNotMemoryCommands(string text) => Assert.Null(Workbench.MemoryCommand(text));

    [Theory]
    [InlineData("Remember that I like tea, not coffee", "remember", "I like tea, not coffee")]
    [InlineData("remember my dentist is Dr. Kavanagh", "remember", "my dentist is Dr. Kavanagh")]
    [InlineData("Forget what I said about the logos", "forget", "the logos")]
    public void StatementsAreStillRemembered(string text, string kind, string fact)
    {
        var command = Workbench.MemoryCommand(text);
        Assert.Equal((kind, fact), command);
    }

    [Fact]
    public void ForgettingCanBeUndone()
    {
        var wb = new Workbench(null);
        wb.Remember("My logos are in Pictures\\Brand");
        Assert.Empty(wb.Matching("the weather tomorrow"));
        var gone = wb.Forget("logos pictures");
        Assert.Single(gone);
        Assert.Empty(wb.Memories());
        wb.Restore(gone);
        Assert.Equal("My logos are in Pictures\\Brand", Assert.Single(wb.Memories()).Text);
    }
}
