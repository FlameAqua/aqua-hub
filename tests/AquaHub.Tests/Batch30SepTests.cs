using System.Text.Json;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Settings;
using Xunit;

namespace AquaHub.Tests;

/// <summary>Workbench: a skill the model returns with other key spellings is still read.</summary>
public class SkillDraftKeyTests
{
    // The owner's failing "Improve" reply (29 Sep): the model copied the capitalised keys it had been shown.
    private const string PascalCase = """
        {"Name":"Analyze Screen","Description":"Captures and analyzes the user's current screen to find relevant information based on their request.",
         "Triggers":["what's on my screen","look at my screen","analyze my screen"],
         "Instructions":"Take a screenshot, then answer the user's question about it.",
         "Steps":[{"Tool":"take_screenshot","Args":[{"Name":"screen","Value":"2"}],"Note":"the second monitor"}],
         "Parameters":[]}
        """;

    [Fact]
    public void CapitalisedKeysAreMatchedToTheSchema()
    {
        Assert.True(LlmClient.TryParseJson(PascalCase, SkillDrafter.Schema(), out var doc));
        using (doc)
        {
            var skill = SkillDrafter.Parse(doc!.RootElement);
            Assert.Equal("Analyze Screen", skill.Name);
            Assert.Equal(3, skill.Triggers.Count);
            var step = Assert.Single(skill.Steps);
            Assert.Equal("take_screenshot", step.Tool);
            Assert.Equal("2", step.Args["screen"]);
            Assert.Equal("the second monitor", step.Note);
        }
    }

    [Fact]
    public void OtherStylesOfKeyAreMatchedToo()
    {
        var schema = AskPlanner.Schema();
        Assert.True(LlmClient.TryParseJson("""{"Intent":"files","WebQueries":[],"file-terms":["logo"],"FORMAT":"direct"}""", schema, out var doc));
        using (doc)
        {
            Assert.Equal("files", doc!.RootElement.GetProperty("intent").GetString());
            Assert.Equal("logo", doc.RootElement.GetProperty("file_terms")[0].GetString());
            Assert.Equal("direct", doc.RootElement.GetProperty("format").GetString());
        }
    }

    [Fact]
    public void TheCurrentSkillIsShownInTheRepliesOwnShape()
    {
        var current = new AskSkill
        {
            Name = "Focus time", Description = "When I want to concentrate", Triggers = { "focus time" }, Instructions = "Pause the music.",
            Steps = { new SkillStep { Tool = "media_control", Args = { ["action"] = "pause" } } },
        };
        var prompt = SkillDrafter.Prompt("focus time", Array.Empty<ToolInfo>(), current, "also turn on do not disturb");
        Assert.Contains("\"name\":\"Focus time\"", prompt);
        Assert.Contains("\"steps\":[{\"tool\":\"media_control\"", prompt);
        Assert.DoesNotContain("\"Name\"", prompt);
    }
}

/// <summary>YouTube channels: the name and logo are found wherever the page puts them.</summary>
public class YouTubeChannelPageTests
{
    private static string Page(int headScript) =>
        "<html><head><script>" + new string('x', headScript) + "</script>" +
        "<meta property=\"og:title\" content=\"TechLinked\"><meta property=\"og:image\" content=\"https://yt3.googleusercontent.com/abc=s900-c-k-no\">" +
        "<meta itemprop=\"identifier\" content=\"UCeeFfhMcJa1kjtfZAGskOCA\"></head><body>" +
        "<script>var d={\"vanityChannelUrl\":\"http://www.youtube.com/@techlinked\"};</script></body></html>";

    [Fact]
    public void MetaTagsDeepInALongHeadAreRead()
    {
        // YouTube's channel pages carry ~760 KB of inline script before og:title.
        var info = AquaHub.Core.Sources.YouTubeChannels.ParsePage(Page(760_000));
        Assert.NotNull(info);
        Assert.Equal("UCeeFfhMcJa1kjtfZAGskOCA", info!.ChannelId);
        Assert.Equal("TechLinked", info.Name);
        Assert.Equal("@techlinked", info.Handle);
        Assert.Equal("https://yt3.googleusercontent.com/abc=s176-c-k-no", info.AvatarUrl);
    }

    [Fact]
    public void ThePagesOwnDataStandsInForMissingMetaTags()
    {
        const string html = "<html><head><title>Exurb1a - YouTube</title><link rel=\"canonical\" href=\"https://www.youtube.com/channel/UCimiUgDLbi6P17BdaCZpVbg\"></head>" +
                            "<body><script>{\"channelMetadataRenderer\":{\"title\":\"exurb1a\",\"description\":\"\",\"avatar\":{\"thumbnails\":[{\"url\":\"https://yt3.ggpht.com/xyz=s900-c-k\"}]}}}</script></body></html>";
        var info = AquaHub.Core.Sources.YouTubeChannels.ParsePage(html);
        Assert.Equal("exurb1a", info!.Name);
        Assert.Equal("https://yt3.ggpht.com/xyz=s176-c-k", info.AvatarUrl);
    }

    [LiveFact]
    public async Task RealChannelsResolveWithNameAndLogo()
    {
        var http = new AquaHub.Core.Net.HttpFetcher(null);
        foreach (var input in new[] { "@techlinked", "https://www.youtube.com/@Exurb1a", "UCHnyfMqiRRG1u-2MsSQLbXA" })
        {
            var info = await AquaHub.Core.Sources.YouTubeChannels.ResolveAsync(http, input);
            Assert.NotNull(info);
            Assert.False(string.IsNullOrWhiteSpace(info!.Name), input + ": no name");
            Assert.NotEqual("YouTube channel", info.Name);
            Assert.False(string.IsNullOrWhiteSpace(info.AvatarUrl), input + ": no logo");
        }
    }
}

/// <summary>Logs: capped while the app runs, and a separate log of problems with their full details.</summary>
public class LogFileTests
{
    [Fact]
    public async Task LogsRollWhileRunningAndProblemsKeepTheirDetails()
    {
        using var dir = new TempDir();
        AquaHub.Core.Util.Log.Init(dir.Path);
        try { throw new InvalidOperationException("the model server went away"); }
        catch (InvalidOperationException ex) { AquaHub.Core.Util.Log.Warn("test", "Couldn't reach the model", ex); }
        var line = new string('x', 1000);
        for (var i = 0; i < 2300; i++) AquaHub.Core.Util.Log.Info("test", line); // > 2 MB

        var old = Path.Combine(dir.Path, "aquahub.old.log");
        var problems = Path.Combine(dir.Path, "aquahub-problems.log");
        for (var i = 0; i < 100 && !(File.Exists(old) && File.Exists(problems)); i++) await Task.Delay(100);

        Assert.True(File.Exists(old), "the log didn't roll over while running");
        Assert.True(new FileInfo(Path.Combine(dir.Path, "aquahub.log")).Length < 2 * 1024 * 1024);
        var text = File.ReadAllText(problems);
        Assert.Contains("WARN  [test] Couldn't reach the model :: InvalidOperationException: the model server went away", text);
        Assert.Contains("    thread ", text);
        Assert.Contains("at AquaHub.Tests.LogFileTests", text); // the stack trace
        Assert.DoesNotContain(line, text);                     // routine lines stay out of the problems log
        var recent = AquaHub.Core.Util.Log.RecentProblems.Last(p => p.Area == "test");
        Assert.Contains("InvalidOperationException", recent.Detail);
    }

    // Review 30 Sep (L1): a crash that ends the app went to the background writer, which might never get another turn.
    [Fact]
    public void AFatalCrashIsOnDiskBeforeTheAppEnds()
    {
        using var dir = new TempDir();
        AquaHub.Core.Util.Log.Init(dir.Path);
        AquaHub.Core.Util.Log.Fatal("app", "Unhandled exception", new InvalidOperationException("the last thing that happened"));
        var problems = File.ReadAllText(Path.Combine(dir.Path, "aquahub-problems.log"));
        Assert.Contains("ERROR [app] Unhandled exception :: InvalidOperationException: the last thing that happened", problems);
        Assert.Contains("the last thing that happened", File.ReadAllText(Path.Combine(dir.Path, "aquahub.log")));
    }
}

/// <summary>Each chat keeps what its answers read, so going back to it (and asking a follow-up) has that material again.</summary>
public class ChatMemoryTests
{
    private static readonly string Chapter = string.Join(" ", Enumerable.Range(1, 60).Select(i =>
        $"In part {i} of the chapter, Subaru returns to the mansion and talks with Emilia about the loop and the witch's scent."));

    [Fact]
    public void AnAnswerKeepsWhatItCitedAndWhatItRead()
    {
        var book = new SourceBook();
        var page = book.Add("Chapter 12", "novel.example", "https://novel.example/ch12", "web", Chapter);
        book.Add("Search result", "other.example", "https://other.example/x", "result", "a short snippet");
        var file = book.Add("notes.txt", "Your files", @"C:\notes.txt", "file", "short");
        var kept = book.Keep($"Subaru goes back to the mansion [{page}] and to his notes [{file}].");
        Assert.Equal(new[] { "Chapter 12", "notes.txt" }, kept.Select(k => k.Title));
        Assert.True(kept[0].Text.Length <= 6000);
        Assert.DoesNotContain(kept, k => k.Kind == "result");
    }

    [Fact]
    public void AFollowUpGetsBackTheSourcesItIsAbout()
    {
        var earlier = new List<ChatSource>
        {
            new("Chapter 12", "novel.example", "https://novel.example/ch12", "web", Chapter),
            new("Heat pump grants", "seai.ie", "https://seai.ie/grants", "web", "Grants of up to 6,500 euro for heat pumps in homes built before 2021."),
        };
        Assert.Equal("Heat pump grants", Assert.Single(AskAgent.PickEarlier(earlier, "How much is the heat pump grant?")).Title);
        Assert.Equal(2, AskAgent.PickEarlier(earlier, "What else did it say?").Count);          // a follow-up: the latest ones
        Assert.Empty(AskAgent.PickEarlier(earlier, "What's the weather in Galway tomorrow afternoon?"));
    }

    // Review 30 Sep (M3): "Will it rain tomorrow?" got the last chapter and an old screenshot back (two words or fewer,
    // or any "it", brought back the latest sources), which also made a plain web search ask for approval.
    [Fact]
    public void AnUnrelatedQuestionGetsNothingBack()
    {
        var chapter = string.Join(" ", Enumerable.Range(1, 40).Select(i =>
            i == 5 ? "Rain began to fall on the academy as the students arrived." :
            i == 30 ? "He would unpack tomorrow, after the first lesson." :
            $"Zorian walked on through part {i} of the long, quiet street."));
        var earlier = new List<ChatSource>
        {
            new("1. Good Morning Brother", "royalroad.com", "https://www.royalroad.com/fiction/x", "web", chapter),
            new("Notes.txt", "Your files", @"C:\scratch\Notes.txt", "file", "Boiler service is due in March; call the plumber first."),
        };
        Assert.Empty(AskAgent.PickEarlier(earlier, "Will it rain tomorrow?"));        // both words are in the chapter, pages apart
        Assert.Empty(AskAgent.PickEarlier(earlier, "and now?"));
        Assert.Empty(AskAgent.PickEarlier(earlier, "Is it open on Sundays?"));
        Assert.Equal("1. Good Morning Brother", Assert.Single(AskAgent.PickEarlier(earlier, "Does Zorian meet anyone at the academy?")).Title);
        Assert.Equal("Notes.txt", Assert.Single(AskAgent.PickEarlier(earlier, "When is the boiler service due?")).Title);
        Assert.Equal(2, AskAgent.PickEarlier(earlier, "What else did the chapter say?").Count);
    }

    [Fact]
    public void TheScreenAndTheClipboardAreNeverKept()
    {
        var book = new SourceBook();
        var screen = book.Add("Screenshot", "Your screen", null, "screen", new string('x', 900) + " what was on screen");
        var clip = book.Add("Clipboard", "Your clipboard", null, "clipboard", "whatever was copied");
        var page = book.Add("A page", "example.org", "https://example.org/a", "web", new string('y', 700));
        var kept = book.Keep($"From your screen [{screen}], the clipboard [{clip}] and a page [{page}].");
        Assert.Equal("A page", Assert.Single(kept).Title);
    }

    [Fact]
    public void SourcesAreSavedWithTheChat()
    {
        using var dir = new TempDir();
        var db = new AquaHub.Core.Data.HubDatabase(Path.Combine(dir.Path, "hub.db"));
        var store = new ChatStore(db);
        var now = DateTimeOffset.Now;
        var answer = new SavedChatMessage(false, "It's about the loop [1].", null, "on-device", true)
        {
            At = now, Sources = new() { new ChatSource("Chapter 12", "novel.example", "https://novel.example/ch12", "web", "Subaru returns to the mansion.") },
        };
        store.Save(new ChatSummary { Id = "c", Title = "Chapter 12", Created = now, Updated = now }, new[] { new SavedChatMessage(true, "Summarise it", null, null, true), answer });
        var loaded = new ChatStore(db).Load("c");
        Assert.Equal("Subaru returns to the mansion.", Assert.Single(loaded[1].Sources!).Text);
        Assert.Equal(now.ToUnixTimeSeconds(), loaded[1].At!.Value.ToUnixTimeSeconds());
    }
}

/// <summary>Copy chat: the whole conversation as Markdown, with what Aqua did.</summary>
public class ChatExportTests
{
    [Fact]
    public void QuestionsAnswersStepsReasoningAndSourcesAreAllThere()
    {
        var at = new DateTimeOffset(2026, 9, 30, 9, 12, 0, TimeSpan.Zero);
        var messages = new[]
        {
            new SavedChatMessage(true, "Find the Re:Zero leaker Ice on twitter", null, null, true, Attachments: new() { "shot.png" }, Mode: "Web · Think") { At = at },
            new SavedChatMessage(false, "Ice is @rezero_ice on X [1].", new() { new Citation(1, "Re: Zero News by Ice (@rezero_ice) / X", "x.com", "https://x.com/rezero_ice", "result") },
                "qwen3.5:9b · 9.0s · 1 source cited", true,
                Steps: new() { "Plan: search “Re:Zero leaker Ice twitter”; look on x.com", "✗ Couldn't read reddit.com" }, Reasoning: "They want the X account.") { At = at.AddSeconds(9) },
        };
        var md = ChatExport.ToMarkdown("Re:Zero leaker Ice on X", messages, at.AddMinutes(1));
        Assert.StartsWith("# Re:Zero leaker Ice on X\n_Aqua Hub · exported 30 Sep 2026", md);
        Assert.Contains("### You · 30 Sep 09:12 · Web · Think\n\nFind the Re:Zero leaker Ice on twitter\n\n_Attached: shot.png_", md);
        Assert.Contains("**What Aqua did**\n- Plan: search “Re:Zero leaker Ice twitter”; look on x.com\n- ✗ Couldn't read reddit.com", md);
        Assert.Contains("<details><summary>Reasoning</summary>\n\nThey want the X account.\n\n</details>", md);
        Assert.Contains("Ice is @rezero_ice on X [1].", md);
        Assert.Contains("1. [Re: Zero News by Ice (@rezero_ice) / X](https://x.com/rezero_ice) — x.com (search snippet only)", md);
        Assert.Contains("_qwen3.5:9b · 9.0s · 1 source cited_", md);
    }
}

/// <summary>Screens: which one to capture, and never the private ones.</summary>
public class ScreenChoiceTests
{
    [Theory]
    [InlineData("What's on my second screen?", "{\"screen\":\"2\"}")]
    [InlineData("look at both monitors", "{\"screen\":\"all\"}")]
    [InlineData("what is on my left monitor", "{\"screen\":\"1\"}")]
    [InlineData("check display 3 for me", "{\"screen\":\"3\"}")]
    [InlineData("what's on the main screen", "{\"screen\":\"main\"}")]
    [InlineData("what's on my screen?", "{}")]
    public void TheScreenComesFromTheQuestion(string question, string args) => Assert.Equal(args, AskAgent.ScreenArgs(question));

    [Theory]
    [InlineData("What's on my second screen?")]
    [InlineData("look at both monitors and tell me what's open")]
    [InlineData("summarise this page")]
    public void ScreenQuestionsArePlannedAsSuch(string question) =>
        Assert.Equal("screen", AskPlanner.Heuristic(question, Array.Empty<LlmMessage>(), new AskOptions { Computer = true }).Intent);

    [Theory]
    [InlineData("KeePassXC", true)]
    [InlineData("CredentialUIBroker", true)]
    [InlineData("chrome", false)]
    [InlineData("WindowsTerminal", false)] // can't be operated, but it can be looked at
    public void PasswordManagersAndSignInPromptsAreNeverCaptured(string process, bool isPrivate) => Assert.Equal(isPrivate, DesktopRules.IsPrivateWindow(process));
}

/// <summary>Long pages are read whole (a web novel chapter used to be cut to an excerpt).</summary>
public class LongPageTests
{
    private const string Chapter = "https://www.royalroad.com/fiction/21220/mother-of-learning/chapter/301778/1-good-morning-brother";

    [LiveFact]
    public async Task AWholeChapterIsExtracted()
    {
        using var reader = new AquaHub.Core.Net.WebReader();
        var page = await reader.ReadAsync(Chapter, CancellationToken.None);
        Assert.True(page.Text.Length > 35_000, $"only {page.Text.Length} characters extracted");
        Assert.Contains("wake up until tomorrow morning", page.Text.Replace('’', '\''));
    }
}

/// <summary>Review 30 Sep (L5): the diagnostics summary said "nothing personal" but copied raw problem messages.</summary>
public class RedactTests
{
    private static string Mask(string text) => AquaHub.Core.Util.Redact.ForSharing(text, "Sam", "SAMS-PC-2", @"C:\Users\Sam");

    [Fact]
    public void PathsKeepTheirKnownFolderAndExtensionOnly()
    {
        Assert.Equal(@"Couldn't read %USERPROFILE%\Documents\…\<file>.pdf: access denied",
            Mask(@"Couldn't read C:\Users\Sam\Documents\Clients\Acme Ltd\Tax return 2025.pdf: access denied"));
        Assert.Equal(@"Opened %USERPROFILE%\Pictures\<file>.png", Mask(@"Opened C:\Users\Sam\Pictures\my logo.png"));
        Assert.Equal(@"D:\Games\…\<file>.log locked", Mask(@"D:\Games\Steam\logs\content_log.log locked"));
    }

    [Fact]
    public void AddressesKeepTheirHostAndNamesAreMasked()
    {
        Assert.Equal("GET https://www.example.org/… failed (503)", Mask("GET https://www.example.org/fiction/21220/x?page=2 failed (503)"));
        Assert.Equal("https://example.org is up", Mask("https://example.org is up"));
        Assert.Equal("<user>'s session on <pc> ended", Mask("Sam's session on SAMS-PC-2 ended"));
        Assert.Equal("Samsung SSD 990 PRO", Mask("Samsung SSD 990 PRO"));   // a word that merely starts with the name stays
    }
}

/// <summary>Review 30 Sep (L4): with a fixed 8K window, a "whole" page was cut off its end again to fit.</summary>
public class WholePageBudgetTests
{
    [Fact]
    public void AFixedWindowGetsWhatItHasRoomFor()
    {
        static int Budget(int window) => AskAgent.WholeBudget(new AquaHub.Core.Settings.HubSettings { Ask = { ContextWindow = window } });
        Assert.Equal(26_000, Budget(0));                  // automatic: the window grows to fit
        Assert.True(Budget(8192) <= 3000, Budget(8192).ToString());
        Assert.InRange(Budget(16384), 20_000, 26_000);
        Assert.Equal(26_000, Budget(65536));
    }
}

public class TextChunkTests
{
    [Fact]
    public void LongTextIsSplitAtParagraphsWithoutLosingAnything()
    {
        var paragraphs = Enumerable.Range(1, 40).Select(i => $"Paragraph {i}: " + string.Join(' ', Enumerable.Repeat("word", 60))).ToList();
        var text = string.Join("\n\n", paragraphs);
        var parts = TextChunks.Split(text, 3000);
        Assert.All(parts, p => Assert.True(p.Length <= 3000));
        Assert.True(parts.Count >= 5);
        Assert.Equal(paragraphs.Count, parts.Sum(p => p.Split("Paragraph ").Length - 1)); // every paragraph, once
        Assert.StartsWith("Paragraph 1:", parts[0]);
        Assert.StartsWith("Paragraph 40:", parts[^1].Split("\n\n").Last());
    }

    [Fact]
    public void AnEndlessParagraphIsSplitBySentences()
    {
        var text = string.Join(" ", Enumerable.Range(1, 200).Select(i => $"Sentence number {i} says something."));
        var parts = TextChunks.Split(text, 1000);
        Assert.All(parts, p => Assert.True(p.Length <= 1000));
        Assert.All(parts, p => Assert.EndsWith(".", p));
        Assert.Equal(200, parts.Sum(p => p.Split("Sentence number").Length - 1));
    }
}

public class MissingSourceCitationTests
{
    [Fact]
    public void ANumberThatWasNeverASourceIsDropped()
    {
        var book = new SourceBook();
        var page = book.Add("Chapter 1", "Royal Road", "https://www.royalroad.com/ch1", "web", "Zorian wakes up and packs for the academy.");
        var text = $"Zorian wakes up [{page}]. Later he reaches the academy [3]. He sleeps [4].";
        var verified = book.Verify(text, out var removed);
        Assert.Equal(2, removed);
        Assert.Equal($"Zorian wakes up [{page}]. Later he reaches the academy. He sleeps.", verified);
    }
}

/// <summary>Sums, dates and units, done exactly (small models guess these).</summary>
public class UtilityToolTests
{
    [Theory]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(2 + 3) * 4", "20")]
    [InlineData("2^3^2", "512")]
    [InlineData("15% of 80", "12")]
    [InlineData("1,250 × 1.23", "1537.5")]
    [InlineData("sqrt(2) * 10", "14.1421356237")]
    [InlineData("round(2.675, 2)", "2.68")]
    [InlineData("-3 + 5", "2")]
    [InlineData("5!", "120")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("max(3, 9, 4) / 2", "4.5")]
    [InlineData("2(3+4)", "14")]
    [InlineData("3 x 4", "12")]
    public void Arithmetic(string expression, string result) => Assert.Equal(result, MathEval.Format(MathEval.Evaluate(expression)));

    [Theory]
    [InlineData("1 / 0")]
    [InlineData("2 +")]
    [InlineData("(1 + 2")]
    [InlineData("foo(3)")]
    public void NonsenseIsExplainedNotGuessed(string expression) => Assert.Throws<FormatException>(() => MathEval.Evaluate(expression));

    // Review 30 Sep (M2): 3,000 nested brackets overflowed the stack and took the whole app down.
    [Fact]
    public void RunawayInputIsAnErrorNotACrash()
    {
        Assert.Throws<FormatException>(() => MathEval.Evaluate(new string('(', 3000) + "1" + new string(')', 3000)));
        Assert.Throws<FormatException>(() => MathEval.Evaluate(new string('(', 200) + "1" + new string(')', 200)));   // under the length cap, over the depth cap
        Assert.Throws<FormatException>(() => MathEval.Evaluate(string.Join("^", Enumerable.Repeat("1", 100))));          // a power tower
        Assert.Equal(3, MathEval.Evaluate(new string('(', 40) + "1+2" + new string(')', 40)));
        Assert.Equal(-5, MathEval.Evaluate(string.Concat(Enumerable.Repeat("- ", 201)) + "5"));                          // signs are a loop
        Assert.Equal(-4, MathEval.Evaluate("-2^2"));                                                                   // the power first, as in maths
        Assert.Equal(4, MathEval.Evaluate("(-2)^2"));
        Assert.Equal(0.5, MathEval.Evaluate("2^-1"));
    }

    private static readonly DateTime Today = new(2026, 9, 30); // a Wednesday

    [Theory]
    [InlineData("today", "2026-09-30")]
    [InlineData("tomorrow", "2026-10-01")]
    [InlineData("next friday", "2026-10-02")]
    [InlineData("friday", "2026-10-02")]
    [InlineData("next wednesday", "2026-10-07")]
    [InlineData("last monday", "2026-09-28")]
    [InlineData("25 December 2026", "2026-12-25")]
    [InlineData("25th Dec", "2026-12-25")]
    [InlineData("2027-01-15", "2027-01-15")]
    [InlineData("03/10/2026", "2026-10-03")] // day first
    public void DatesPeopleWrite(string text, string iso) => Assert.Equal(DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture), DateMath.Parse(text, Today));

    [Fact]
    public void AddingPeriodsAndCountingDays()
    {
        Assert.Equal(new DateTime(2026, 10, 21), DateMath.Add(Today, "3 weeks"));
        Assert.Equal(new DateTime(2026, 9, 20), DateMath.Add(Today, "-10 days"));
        Assert.Equal(new DateTime(2026, 12, 5), DateMath.Add(Today, "2 months 5 days"));
        Assert.Null(DateMath.Add(Today, "soon"));
        Assert.Equal(5, DateMath.WorkingDays(new DateTime(2026, 9, 28), new DateTime(2026, 10, 5)));
    }

    // The live Think test (30 Sep): the model asked date_math for "today 14:10" and "2026-09-30T14:10", both were
    // refused, and it then got the bus's arrival wrong in its head. Times are part of dates people (and models) write.
    [Theory]
    [InlineData("today 14:10", "2026-09-30T14:10")]
    [InlineData("2026-09-30T14:10", "2026-09-30T14:10")]
    [InlineData("2026-09-30T14:10:00Z", "2026-09-30T14:10")]
    [InlineData("2026-09-30 14:10", "2026-09-30T14:10")]
    [InlineData("14:10", "2026-09-30T14:10")]
    [InlineData("tomorrow at 9pm", "2026-10-01T21:00")]
    [InlineData("friday 9:30 am", "2026-10-02T09:30")]
    [InlineData("25 Dec 2026, 12:00 am", "2026-12-25T00:00")]
    public void DatesWithTimes(string text, string iso) => Assert.Equal(DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture), DateMath.Parse(text, Today));

    [Fact]
    public void TimesAndGaps()
    {
        var now = new DateTime(2026, 9, 30, 11, 20, 0);
        Assert.Equal(now, DateMath.Parse("now", now));
        Assert.Null(DateMath.Parse("25:10", Today));   // not a time, and not a date
        Assert.Equal(new DateTime(2026, 9, 30, 16, 45, 0), DateMath.Add(new DateTime(2026, 9, 30, 14, 10, 0), "2h35m"));
        Assert.Equal(new DateTime(2026, 9, 30, 16, 55, 0), DateMath.Add(new DateTime(2026, 9, 30, 13, 50, 0), "3 h 5 min"));
        Assert.Equal(new DateTime(2026, 9, 30, 0, 45, 0), DateMath.Add(Today, "45m"));   // a lone m is minutes
        Assert.Equal(new DateTime(2026, 11, 30), DateMath.Add(Today, "2 mo"));
        Assert.Equal("10 min", DateMath.Span(TimeSpan.FromMinutes(10)));
        Assert.Equal("3 h 5 min", DateMath.Span(new TimeSpan(3, 5, 0)));
        Assert.Equal("1 day 2 h", DateMath.Span(new TimeSpan(1, 2, 0, 0)));
        Assert.Equal("minus 10 min", DateMath.Span(TimeSpan.FromMinutes(-10)));
    }

    [Fact]
    public async Task TheDateToolGivesTheGapBetweenTwoTimes()
    {
        using var args = System.Text.Json.JsonDocument.Parse("""{"from":"today 13:50","to":"today 16:55"}""");
        var run = new AskRun
        {
            State = new HubState(null), Settings = new HubSettings(), Book = new SourceBook(), Options = new AskOptions(),
            Host = new QuietHost(), AllowedForChat = new HashSet<string>(),
        };
        var result = await new DateMathTool().RunAsync(args.RootElement, run, CancellationToken.None);
        Assert.True(result.Ok, result.Text);
        Assert.Contains("3 h 5 min", result.Text);
    }

    private sealed class QuietHost : IAskHost
    {
        public void Status(string text) { }
        public int StepStarted(string icon, string text) => 0;
        public void StepFinished(int id, string text, bool ok = true, string? url = null) { }
        public void Thinking(string delta) { }
        public void Text(string delta) { }
        public void ResetText() { }
        public Task<bool> ApproveAsync(ToolApproval request, CancellationToken ct) => Task.FromResult(false);
    }

    [Theory]
    [InlineData(5, "miles", "km", 8.04672)]
    [InlineData(100, "°F", "°C", 37.777778)]
    [InlineData(0, "C", "K", 273.15)]
    [InlineData(1, "stone", "kg", 6.350293)]
    [InlineData(2, "pints", "l", 1.136523)]
    [InlineData(1, "GiB", "MB", 1073.741824)]
    [InlineData(100, "km/h", "mph", 62.137119)]
    [InlineData(1, "kWh", "kcal", 860.420650)]
    public void UnitConversions(double value, string from, string to, double expected) => Assert.Equal(expected, Units.Convert(value, from, to), 5);

    [Fact]
    public void UnitsThatDontFitAreRefused()
    {
        Assert.Throws<FormatException>(() => Units.Convert(1, "kg", "km"));
        Assert.Throws<FormatException>(() => Units.Convert(1, "cubits", "m"));
    }

    [Fact]
    public void TheToolsAreAlwaysThereAndNeedNoOk()
    {
        var names = AskAgent.HubTools().Select(t => t.Name).ToList();
        Assert.Contains("calculate", names);
        Assert.Contains("date_math", names);
        Assert.Contains("convert_units", names);
        Assert.All(AskAgent.HubTools(), t => Assert.False(t.NeedsApproval(new AquaHub.Core.Settings.AskSettings())));
    }
}

/// <summary>Review 30 Sep (M4): the page on screen is the one in the address bar, not a link further down.</summary>
public class ScreenLinkTests
{
    // Review 30 Sep (L6): the tool refused "left" and "second", which the model uses.
    [Theory]
    [InlineData("2", 3, 1, 2)]
    [InlineData("second", 3, 1, 2)]
    [InlineData("the second screen", 3, 1, 2)]
    [InlineData("Screen 2", 2, 1, 2)]
    [InlineData("left", 3, 2, 1)]
    [InlineData("right", 3, 2, 3)]
    [InlineData("last", 2, 1, 2)]
    [InlineData("main", 3, 2, 2)]
    [InlineData("other", 2, 1, 2)]
    [InlineData("secondary", 2, 2, 1)]
    public void ScreenWordsTheModelUses(string choice, int count, int primary, int expected) =>
        Assert.Equal(expected, AskAgent.ScreenNumber(choice, count, primary));

    [Fact]
    public void AScreenThatIsntThereIsNamedAsSuch()
    {
        Assert.Null(AskAgent.ScreenNumber("4", 2, 1));
        Assert.Null(AskAgent.ScreenNumber("third", 2, 1));
        Assert.Null(AskAgent.ScreenNumber("purple", 2, 1));
        Assert.Null(AskAgent.ScreenNumber("other", 3, 1));   // ambiguous with three
    }

    [Fact]
    public void TheAddressBarComesFirst()
    {
        // Browsers show the address without "https://"; a full link in the page body used to win.
        const string screen = "Mother of Learning - Chapter 1\nroyalroad.com/fiction/21220/mother-of-learning/chapter/301778\n" +
                              "Join our Discord: https://discord.gg/abcdef\nThe chapter's text follows here.";
        var links = AskPlanner.ScreenLinks(screen);
        Assert.Equal("https://royalroad.com/fiction/21220/mother-of-learning/chapter/301778", links[0]);
        Assert.Equal("https://discord.gg/abcdef", links[1]);
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public void OnlyTheTextReadOffTheScreenIsSearched()
    {
        const string result = "[1] Screenshot of the screen.\nWeb addresses visible on screen, top first: https://a.example/\nText on screen (OCR):\nexample.org/page\nmore";
        Assert.Equal("example.org/page\nmore", AskAgent.ScreenText(result));
        Assert.Equal("no marker", AskAgent.ScreenText("no marker"));
    }
}
