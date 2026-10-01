using System.Text.Json;
using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

/// <summary>Planning: search queries, links, sites, file words and kinds come from the request, not the request pasted whole.</summary>
public class AskPlannerTests
{
    [Fact]
    public void WebQueriesKeepTheSubjectAndDropTheRequest()
    {
        var q = AskPlanner.WebQuery("find the Re:Zero leaker Ice on twitter and link me it");
        Assert.Contains("Re:Zero leaker Ice", q);
        Assert.DoesNotContain("find", q, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("link", q, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SpaceX Starship launch", AskPlanner.WebQuery("Can you please look up SpaceX Starship launch for me?"));
    }

    [Fact]
    public void FindsLinksWithOrWithoutHttps()
    {
        Assert.Equal(new[] { "https://example.com/blog" }, AskPlanner.Links("look through https://example.com/blog, for anything about pricing"));
        Assert.Equal(new[] { "https://arstechnica.com/" }, AskPlanner.Links("search arstechnica.com for starship news"));
        Assert.Equal(new[] { "https://www.youtube.com/@GamersNexus" }, AskPlanner.Links("what's new on www.youtube.com/@GamersNexus?"));
        Assert.Empty(AskPlanner.Links("find my_logo5.png or the report v2.0"));
    }

    [Fact]
    public void KnowsWhichSiteAPlatformMeans()
    {
        Assert.Equal("x.com", AskPlanner.SiteOf("find the Re:Zero leaker Ice on twitter", Array.Empty<string>()));
        Assert.Equal("reddit.com", AskPlanner.SiteOf("what are people saying on reddit about it", Array.Empty<string>()));
        Assert.Equal("example.com", AskPlanner.SiteOf("look through this", new[] { "https://www.example.com/blog" }));
        Assert.Equal("", AskPlanner.SiteOf("give me an example of x", Array.Empty<string>()));
    }

    [Fact]
    public void FileRequestsBecomeNameWordsAKindAndWhatThePictureShows()
    {
        var plan = AskPlanner.Heuristic("Find me my logo photo of a blue flame", Array.Empty<LlmMessage>(), new AskOptions { Computer = true });
        Assert.Equal("files", plan.Intent);
        Assert.Contains("logo", plan.FileTerms);
        Assert.Contains("flame", plan.FileTerms);
        Assert.DoesNotContain("find", plan.FileTerms);
        Assert.DoesNotContain("photo", plan.FileTerms);
        Assert.Equal("image", plan.FileKind);
        Assert.Equal("a blue flame", plan.Looks);
        Assert.Equal(new[] { "PICTURES" }, AskPlanner.FoldersIn("it's in my Pictures folder"));
        Assert.Equal("pdf", AskPlanner.KindOf("where is the invoice pdf"));
    }

    [Fact]
    public void WebAndSiteRequestsBecomeWebPlans()
    {
        var web = AskPlanner.Heuristic("find the Re:Zero leaker Ice on twitter and link me it", Array.Empty<LlmMessage>(), new AskOptions { Web = true });
        Assert.Equal("web", web.Intent);
        Assert.Equal("x.com", web.Site);
        Assert.Contains(web.WebQueries, q => q.EndsWith("site:x.com", StringComparison.Ordinal));
        var site = AskPlanner.Heuristic("look through https://example.com/blog for anything about pricing", Array.Empty<LlmMessage>(), new AskOptions { Web = true });
        Assert.Equal("site", site.Intent);
        Assert.Equal("https://example.com/blog", site.Urls.Single());
    }

    [Fact]
    public void TheModelsPlanIsCheckedAndMergedWithTheRules()
    {
        const string question = "Can you please find out for me what the latest news is regarding the SpaceX Starship launch schedule for next month and whether it was delayed";
        var rules = AskPlanner.Heuristic(question, Array.Empty<LlmMessage>(), new AskOptions { Web = true });
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            intent = "web", web_queries = new[] { question, "Starship launch delay" }, file_terms = Array.Empty<string>(), format = "banana",
            file_kind = "banana", skill = "Nope", site = "https://www.spacex.com/launches",
        }));
        var plan = AskPlanner.Merge(doc.RootElement, rules, question, new AskOptions { Web = true }, new[] { "Focus time" });
        Assert.NotEqual(question, plan.WebQueries[0]);
        Assert.True(plan.WebQueries[0].Length < question.Length);
        Assert.Contains("Starship launch delay", plan.WebQueries);
        Assert.Equal("direct", plan.Format);
        Assert.Equal("any", plan.FileKind);
        Assert.Equal("", plan.Skill);
        Assert.Equal("spacex.com", plan.Site);
        Assert.True(plan.ByModel);

        // Links you typed win over the model's idea of the intent.
        var linkRules = AskPlanner.Heuristic("summarise https://example.com/post", Array.Empty<LlmMessage>(), new AskOptions { Web = true });
        using var chat = JsonDocument.Parse("""{"intent":"chat","web_queries":[],"file_terms":[],"format":"direct"}""");
        var merged = AskPlanner.Merge(chat.RootElement, linkRules, "summarise https://example.com/post", new AskOptions { Web = true });
        Assert.Equal("page", merged.Intent);
        Assert.Equal("https://example.com/post", merged.Urls.Single());
    }

    [Fact]
    public void ThePlanningRequestIsFastAndStructured()
    {
        var request = AskPlanner.Request("find it", new[] { new LlmMessage("user", "my logo is blue"), new LlmMessage("assistant", "Which one?") },
            new AskOptions { Computer = true, Web = true }, new PlanContext { Folders = new[] { "Pictures" }, Memories = new[] { "Logos are in Pictures\\Brand" } }, DateTimeOffset.Now);
        Assert.False(request.Think);
        Assert.NotNull(request.Schema);
        var text = request.Messages[0].Content;
        Assert.Contains("my logo is blue", text);
        Assert.Contains("Logos are in Pictures\\Brand", text);
        Assert.Contains("Web: on. Use my PC: on", text);
    }
}

/// <summary>File search: any of the words, your folders first, system and app folders never walked.</summary>
public class FileSearchTests
{
    [Fact]
    public void FileNamesSplitIntoWords()
    {
        Assert.Equal(new[] { "my", "logo", "5" }, LocalFiles.NameTokens("my_logo5"));
        Assert.Equal(new[] { "blue", "flame", "logo" }, LocalFiles.NameTokens("BlueFlameLogo"));
        Assert.Equal(new[] { "tax", "return", "2025" }, LocalFiles.NameTokens("Tax-Return (2025)"));
    }

    [Fact]
    public void EveryWordCountsAndMoreWordsRankHigher()
    {
        var (partial, matched) = LocalFiles.ScoreName("my_logo5.png", "", new[] { "logo", "flame" });
        Assert.True(partial > 0);
        Assert.Equal(new[] { "logo" }, matched);
        var (full, _) = LocalFiles.ScoreName("blue_flame_logo.png", "", new[] { "logo", "flame" });
        Assert.True(full > partial);
        Assert.Equal(0, LocalFiles.ScoreName("notes.txt", "", new[] { "logo" }).Score);
        Assert.True(LocalFiles.ScoreName("flames.png", "", new[] { "flame" }).Score >= 60);
    }

    [Fact]
    public void FindsPicturesAndSkipsSystemAndAppFolders()
    {
        using var dir = new TempDir();
        var root = dir.Path;
        var pictures = Directory.CreateDirectory(Path.Combine(root, "Users", "me", "Pictures")).FullName;
        File.WriteAllBytes(Path.Combine(pictures, "my_logo5.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(pictures, "holiday.jpg"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(root, "logo brief.txt"), "brief");
        foreach (var hidden in new[] { Path.Combine("Windows", "System32"), Path.Combine("node_modules", "pkg"), Path.Combine("Users", "me", "AppData", "Roaming"), ".cache", "Program Files" })
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(root, hidden)).FullName, "logo.png"), new byte[] { 1 });
        var files = new LocalFiles(() => new[] { root }, () => new[] { pictures });

        var hits = files.Find(new FileQuery { Terms = new[] { "logo", "flame" }, Kind = "image" });
        Assert.Equal("my_logo5.png", Assert.Single(hits).Name);

        var any = files.Find(new FileQuery { Terms = new[] { "logo" } });
        Assert.Equal(2, any.Count);
        var relative = any.Select(h => h.Path[root.Length..]).ToList();
        Assert.DoesNotContain(relative, p => p.Contains("Windows") || p.Contains("node_modules") || p.Contains("AppData") || p.Contains(".cache") || p.Contains("Program Files"));

        var recent = files.Find(new FileQuery { Kind = "image" });
        Assert.Equal(2, recent.Count);
        var preferred = files.Find(new FileQuery { Terms = new[] { "logo" }, Prefer = new[] { pictures } });
        Assert.Equal("my_logo5.png", preferred[0].Name);
    }

    [Fact]
    public void IndexResultsAreCheckedAgainstTheFolderRules()
    {
        using var dir = new TempDir();
        var allowed = Directory.CreateDirectory(Path.Combine(dir.Path, "Allowed")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(dir.Path, "Outside")).FullName;
        File.WriteAllText(Path.Combine(allowed, "report.txt"), "x");
        File.WriteAllText(Path.Combine(outside, "report.txt"), "x");
        File.WriteAllText(Path.Combine(allowed, "passwords report.txt"), "x");
        var modules = Directory.CreateDirectory(Path.Combine(allowed, "node_modules", "pkg")).FullName;
        File.WriteAllText(Path.Combine(modules, "report.txt"), "x");
        var files = new LocalFiles(() => new[] { allowed });
        var indexed = new[]
        {
            new IndexedFile(Path.Combine(modules, "report.txt"), DateTime.Now, 1, false),
            new IndexedFile(Path.Combine(allowed, "report.txt"), DateTime.Now, 1, false),
            new IndexedFile(Path.Combine(outside, "report.txt"), DateTime.Now, 1, false),
            new IndexedFile(Path.Combine(allowed, "passwords report.txt"), DateTime.Now, 1, false),
        };
        var hits = files.Find(new FileQuery { Terms = new[] { "report" }, Content = false }, indexed);
        Assert.Equal(Path.Combine(allowed, "report.txt"), Assert.Single(hits).Path);
    }
}

/// <summary>Answers without the model thinking out loud, and reasoning that can't run forever.</summary>
public class AnswerTextTests
{
    [Fact]
    public void NarrationBeforeTheAnswerMovesToTheReasoning()
    {
        var (answer, notes) = AnswerText.Split("The user is asking for their logo. Let me check the Pictures folder. I found my_logo5.png in your Pictures folder [1].");
        Assert.Equal("I found my_logo5.png in your Pictures folder [1].", answer);
        Assert.Contains("The user is asking", notes);
    }

    [Fact]
    public void AThinkingBlockKeepsOnlyTheFinalAnswer()
    {
        var (answer, notes) = AnswerText.Split("Thinking Process:\n\n1. **Analyze the Request:** The user wants the handle.\n2. Answer the user directly.\n\n**Final Answer:** Ice posts as @rezero_ice on X.");
        Assert.Equal("Ice posts as @rezero_ice on X.", answer);
        Assert.Contains("Analyze the Request", notes);
        var (none, all) = AnswerText.Split("Thinking Process:\n1. Look at page two.\n2. Wait, check again.");
        Assert.Equal("", none);
        Assert.Contains("page two", all);
    }

    [Fact]
    public void OrdinaryAnswersAreLeftAlone()
    {
        Assert.Equal(("Let me explain: a leaker shares unreleased details.", ""), AnswerText.Split("Let me explain: a leaker shares unreleased details."));
        Assert.Equal(("Okay, it's in Pictures.", ""), AnswerText.Split("Okay, it's in Pictures."));
    }

    [Fact]
    public void LatexBecomesSymbolsButPathsStayAsTheyAre()
    {
        Assert.Equal("engine issue → orbit burn → splashdown", AnswerText.Tidy(@"engine issue $\rightarrow$ orbit burn $\rightarrow$ splashdown"));
        Assert.Equal("It's 20°C, about 2 × warmer (≈ 5 °C more)", AnswerText.Tidy(@"It's 20$^\circ$C, about 2 \times warmer (\approx 5 $^{\circ}$C more)"));
        Assert.Equal(@"C:\Users\me\tools\to\logo.png", AnswerText.Tidy(@"C:\Users\me\tools\to\logo.png"));
        Assert.Equal("No maths here.", AnswerText.Tidy("No maths here."));
    }

    [Fact]
    public void LongOrCircularReasoningIsStopped()
    {
        Assert.Equal("long", ThinkingGuard.Check(new string('a', 8000), 7000));
        Assert.Null(ThinkingGuard.Check(string.Concat(Enumerable.Repeat("Checking the sources one by one. ", 30)), 7000));
        var circles = string.Concat(Enumerable.Repeat("I need to check page two header for the handle again carefully. Then compare. ", 40));
        Assert.Equal("circles", ThinkingGuard.Check(circles, 14000));
        var dithering = string.Concat(Enumerable.Range(0, 40).Select(i => $"Wait, maybe option {i} is different from what the sources show here. "));
        Assert.Equal("circles", ThinkingGuard.Check(dithering, 14000));
    }
}

/// <summary>The Workbench: memories, skills, matching, filling in and validation.</summary>
public class WorkbenchTests
{
    [Theory]
    [InlineData("remember that my logos are in Pictures", "remember", "my logos are in Pictures")]
    [InlineData("Remember: I prefer bullet points.", "remember", "I prefer bullet points")]
    [InlineData("Aqua, please keep in mind that I'm vegetarian", "remember", "I'm vegetarian")]
    [InlineData("forget about the boiler", "forget", "the boiler")]
    public void MemoryCommands(string text, string kind, string fact)
    {
        var command = Workbench.MemoryCommand(text);
        Assert.NotNull(command);
        Assert.Equal(kind, command.Value.Kind);
        Assert.Equal(fact, command.Value.Fact);
    }

    [Theory]
    [InlineData("remember when we went to Paris?")]
    [InlineData("remember to buy milk")]
    [InlineData("What do you remember about me")]
    public void QuestionsAndRemindersAreNotMemories(string text) => Assert.Null(Workbench.MemoryCommand(text));

    [Fact]
    public void RemembersAndForgets()
    {
        var wb = new Workbench(null);
        var m = wb.Remember("my logos are in Pictures");
        Assert.Equal("My logos are in Pictures", m.Text);
        Assert.Equal(m.Id, wb.Remember("My logos are in Pictures").Id);
        wb.Remember("I prefer bullet points");
        var gone = wb.Forget("logos pictures");
        Assert.Equal("My logos are in Pictures", Assert.Single(gone).Text);
        Assert.Single(wb.Memories());
        Assert.Empty(wb.Forget("weather"));
    }

    [Fact]
    public void SkillsMatchTheirExampleRequests()
    {
        var focus = new AskSkill { Id = "k1", Name = "Focus time", Triggers = new() { "focus time", "help me concentrate" } };
        var standup = new AskSkill { Id = "k2", Name = "Standup", Triggers = new() { "standup" } };
        var off = new AskSkill { Id = "k3", Name = "Price check", Triggers = new() { "how much is" }, Enabled = false };
        var skills = new[] { focus, standup, off };
        Assert.Same(focus, Workbench.Match(skills, "it's focus time!"));
        Assert.Same(focus, Workbench.Match(skills, "Can you help me concentrate"));
        Assert.Same(standup, Workbench.Match(skills, "prep my standup"));
        Assert.Null(Workbench.Match(skills, "how much is Hades 2"));
        Assert.Null(Workbench.Match(skills, "what's the weather"));
    }

    [Fact]
    public void StepValuesAreFilledIn()
    {
        var args = Workbench.Fill(new Dictionary<string, string> { ["url"] = "https://example.com/search?q={topic}", ["query"] = "{input}", ["name"] = "{missing}" },
            new Dictionary<string, string> { ["topic"] = "rust lang" }, "tell me about rust");
        Assert.Equal("https://example.com/search?q=rust%20lang", args["url"]);
        Assert.Equal("tell me about rust", args["query"]);
        Assert.Equal("", args["name"]);
    }

    [Fact]
    public void DraftedSkillsOnlyUseRealToolsAndArguments()
    {
        var catalogue = new[]
        {
            new ToolInfo("launch_app", "Start an app", ToolAccess.Act, new[] { "name" }, new[] { "name" }, "launchpad"),
            new ToolInfo("web_search", "Search", ToolAccess.Web, new[] { "query", "site", "recent" }, new[] { "query" }, "globe"),
        };
        var skill = new AskSkill
        {
            Name = "  Focus   time ", Instructions = "Do it.",
            Steps = new()
            {
                new SkillStep { Tool = "launch_app", Args = new() { ["name"] = "Notepad", ["bogus"] = "x" } },
                new SkillStep { Tool = "rm_rf", Args = new() { ["path"] = "/" } },
                new SkillStep { Tool = "web_search", Args = new() },
            },
        };
        var notes = Workbench.Validate(skill, catalogue);
        Assert.Equal("Focus time", skill.Name);
        var step = Assert.Single(skill.Steps);
        Assert.Equal("launch_app", step.Tool);
        Assert.False(step.Args.ContainsKey("bogus"));
        Assert.Equal(3, notes.Count);
    }

    [Fact]
    public void ParsesTheDraftersJson()
    {
        using var doc = JsonDocument.Parse("""
            {"name":"Game price","description":"When I ask what a game costs","triggers":["how much is"],"instructions":"Search and compare.",
             "parameters":[{"name":"game","description":"The game"}],
             "steps":[{"tool":"web_search","args":[{"name":"query","value":"{game} price steam"}],"note":"find it"},
                      {"tool":"read_webpage","args":{"url":"https://store.steampowered.com/search/?term={game}"}}]}
            """);
        var skill = SkillDrafter.Parse(doc.RootElement);
        Assert.Equal("Game price", skill.Name);
        Assert.Equal("{game} price steam", skill.Steps[0].Args["query"]);
        Assert.Equal("https://store.steampowered.com/search/?term={game}", skill.Steps[1].Args["url"]);
        Assert.Equal("game", skill.Parameters.Single().Name);
    }
}

/// <summary>The shortcuts Ask may press in other apps.</summary>
public class KeyChordTests
{
    [Fact]
    public void ParsesShortcuts()
    {
        var chords = KeyChords.Parse("ctrl+s, Tab; shift+F5, enter")!;
        Assert.Equal(4, chords.Count);
        Assert.Equal((ushort)'S', chords[0].Key);
        Assert.Equal(new[] { KeyChords.Control }, chords[0].Modifiers);
        Assert.Equal(0x09, chords[1].Key);
        Assert.Equal(0x74, chords[2].Key);
        Assert.Equal(new[] { KeyChords.Shift }, chords[2].Modifiers);
        Assert.Equal(0x0D, chords[3].Key);
    }

    [Theory]
    [InlineData("win+r")]
    [InlineData("alt+f4")]
    [InlineData("ctrl+alt+delete, win")]
    [InlineData("ctrl+ctrl+s")]
    [InlineData("printscreen")]
    [InlineData("f13")]
    [InlineData("a,b,c,d,e,f,g")]
    [InlineData("")]
    public void RefusesWhatIsNotAllowed(string keys) => Assert.Null(KeyChords.Parse(keys));
}

/// <summary>Chat history: many chats, starred ones kept, the rest expiring.</summary>
public class ChatStoreTests
{
    private static SavedChatMessage Q(string text) => new(true, text, null, null, true);
    private static SavedChatMessage A(string text) => new(false, text, null, "on-device", true);

    [Fact]
    public void KeepsManyChatsNewestFirst()
    {
        var store = new ChatStore(null);
        var now = DateTimeOffset.Now;
        store.Save(new ChatSummary { Id = "a", Title = "Older", Created = now.AddHours(-2), Updated = now.AddHours(-2) }, new[] { Q("hi"), A("hello") });
        store.Save(new ChatSummary { Id = "b", Title = "Newer", Created = now, Updated = now }, new[] { Q("boiler?"), A("Your boiler service is due on 14 October.") });
        Assert.Equal(new[] { "b", "a" }, store.List().Select(c => c.Id));
        Assert.Equal(2, store.Load("b").Count);
        Assert.Equal("Your boiler service is due on 14 October.", store.Get("b")!.Preview);
        var found = Assert.Single(store.Search("boiler service"));
        Assert.Equal("b", found.Chat.Id);
        Assert.Contains("boiler", found.Snippet);
        store.Update("a", c => c.Starred = true);
        Assert.True(store.Get("a")!.Starred);
        store.Delete("b");
        Assert.Single(store.List());
    }

    [Fact]
    public void UnstarredChatsExpireAndStarredOnesStay()
    {
        var store = new ChatStore(null);
        var now = DateTimeOffset.Now;
        store.Save(new ChatSummary { Id = "old", Title = "Old", Updated = now.AddDays(-40) }, new[] { Q("x") });
        store.Save(new ChatSummary { Id = "kept", Title = "Kept", Updated = now.AddDays(-40), Starred = true }, new[] { Q("y") });
        store.Save(new ChatSummary { Id = "new", Title = "New", Updated = now.AddDays(-2) }, new[] { Q("z") });
        Assert.Equal(0, store.Prune(now, 0));
        Assert.Equal(1, store.Prune(now, 30));
        Assert.Equal(new[] { "new", "kept" }, store.List().Select(c => c.Id));
        Assert.Empty(store.Load("old"));
    }

    [Fact]
    public void TitlesComeFromTheFirstQuestion()
    {
        Assert.StartsWith("SpaceX Starship reaches orbit", ChatStore.TitleFrom("Tell me more about: SpaceX Starship reaches orbit for first time despite engine failure"));
        Assert.Equal("Find my tax documents", ChatStore.TitleFrom("hey aqua, can you find my tax documents? thanks"));
        Assert.Equal("New chat", ChatStore.TitleFrom("   "));
    }

    [Fact]
    public void TheSingleOldConversationIsImportedOnce()
    {
        using var dir = new TempDir();
        var db = new HubDatabase(Path.Combine(dir.Path, "hub.db"));
        db.PutJson("ask:conversation", new List<SavedChatMessage> { Q("What's the weather?"), A("Dry and mild.") });
        var store = new ChatStore(db);
        var chat = store.ImportLegacy(DateTimeOffset.Now);
        Assert.NotNull(chat);
        Assert.Equal("What's the weather", chat!.Title);
        Assert.Equal(2, store.Load(chat.Id).Count);
        Assert.Null(store.ImportLegacy(DateTimeOffset.Now));
        Assert.Single(new ChatStore(db).List());
    }
}

public class AskSettingsTests
{
    [Fact]
    public void PicturesIsAddedOnlyForTheOriginalFolders()
    {
        var defaults = new HubSettings { Version = 2 };
        defaults.Ask.Folders = new() { "%DOCUMENTS%", "%DESKTOP%", "%DOWNLOADS%" };
        Assert.True(SettingsStore.Migrate(defaults));
        Assert.Contains("%PICTURES%", defaults.Ask.Folders);

        var custom = new HubSettings { Version = 2 };
        custom.Ask.Folders = new() { "C:\\", "D:\\" };
        SettingsStore.Migrate(custom);
        Assert.Equal(new[] { "C:\\", "D:\\" }, custom.Ask.Folders);
        Assert.Contains("%PICTURES%", new HubSettings().Ask.Folders);
    }

    [Fact]
    public void NewAskSettingsAreKeptInRange()
    {
        var s = new HubSettings();
        s.Ask.ContextWindow = 12345; s.Ask.HistoryMessages = 100; s.Ask.Voice = "loud"; s.Ask.ChatRetentionDays = -5;
        SettingsStore.Validate(s);
        Assert.Equal(0, s.Ask.ContextWindow);
        Assert.Equal(40, s.Ask.HistoryMessages);
        Assert.Equal("offline", s.Ask.Voice);
        Assert.Equal(0, s.Ask.ChatRetentionDays);
        s.Ask.ContextWindow = 32768;
        SettingsStore.Validate(s);
        Assert.Equal(32768, s.Ask.ContextWindow);
    }
}

/// <summary>The agent end to end against a scripted Ollama: planning, files, pictures, reasoning, memory and skills.</summary>
public class AskAgentBehaviourTests
{
    private static (AskAgent Agent, HubSettings Settings) Build(FakeOllama server, HubSettings? settings = null, Workbench? workbench = null,
        IEnumerable<StoryCluster>? moreStories = null)
    {
        var s = settings ?? new HubSettings();
        s.Ai.Endpoint = server.Endpoint;
        s.Ask.ReadStoryArticles = false;
        var state = new HubState(null);
        state.SetStories(new List<StoryCluster> { AskFixtures.Starship() }.Concat(moreStories ?? Array.Empty<StoryCluster>()).ToList());
        var llm = new LlmClient(() => s.Ai, new InMemorySecretStore());
        var agent = new AskAgent(state, null, llm, () => s, new WebSearch(new HttpFetcher(null), () => s, new InMemorySecretStore()), new WebReader(), workbench);
        return (agent, s);
    }

    private static string System(JsonNode chat) => chat["messages"]![0]!["content"]!.GetValue<string>();

    [Fact]
    public async Task PlansAFileSearchAndLooksAtThePicturesItFinds()
    {
        using var dir = new TempDir();
        var pictures = Directory.CreateDirectory(Path.Combine(dir.Path, "Pictures")).FullName;
        File.WriteAllBytes(Path.Combine(pictures, "my_logo5.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2 });
        File.WriteAllText(Path.Combine(dir.Path, "shopping list.txt"), "milk");
        using var server = new FakeOllama
        {
            Json = req => req["messages"]!.AsArray().Any(m => m!["content"]!.GetValue<string>().Contains("Plan how Aqua"))
                ? """{"intent":"files","web_queries":[],"file_terms":["logo","flame"],"file_kind":"image","looks":"a blue flame","format":"direct"}"""
                : """{"images":[{"n":1,"shows":"a blue flame logo","match":true}]}""",
            Script = (_, _) => new[] { FakeOllama.Chunk("It's my_logo5.png in your Pictures folder [1].", done: true) },
        };
        var s = new HubSettings();
        s.Ask.Folders = new() { dir.Path };
        var (agent, _) = Build(server, s);
        var host = new NullHost();

        var result = await agent.RunAsync("Find me my logo photo of a blue flame", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions { Computer = true }, host, new StubPlatform(), new HashSet<string>(), CancellationToken.None);

        Assert.Equal(2, server.JsonChats.Count); // the plan, then looking at the picture
        Assert.NotNull(server.JsonChats[1]["messages"]!.AsArray().Last()!["images"]);
        var system = System(server.Chats[0]);
        Assert.Contains("my_logo5.png", system);
        Assert.Contains("MATCHES “a blue flame”", system);
        Assert.Contains("Use my PC is ON", system);
        Assert.Contains(host.Steps, st => st.StartsWith("Plan: look for pictures named like", StringComparison.Ordinal));
        Assert.Contains(host.Steps, st => st.StartsWith("Searched your files for “logo”, “flame”", StringComparison.Ordinal));
        Assert.Contains(host.Steps, st => st.StartsWith("Looked at 1 picture · 1 match", StringComparison.Ordinal));
        Assert.Equal("It's my_logo5.png in your Pictures folder [1].", result.Text);
        Assert.Equal("file", Assert.Single(result.Citations).Kind);
    }

    [Fact]
    public async Task ReasoningThatGoesInCirclesIsCutShortAndAnsweredDirectly()
    {
        var loop = string.Concat(Enumerable.Repeat("Wait, I need to check page two again for the handle. ", 80));
        using var server = new FakeOllama
        {
            Script = (_, turn) => turn == 1
                ? loop.Chunk(200).Select(c => FakeOllama.Chunk("", thinking: new string(c))).Append(FakeOllama.Chunk("", done: true)).ToArray()
                : new[] { FakeOllama.Chunk("The handle is @rezero_ice.", done: true) },
        };
        var (agent, _) = Build(server);
        var result = await agent.RunAsync("Who is Ice?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions { Think = true },
            new NullHost(), null, new HashSet<string>(), CancellationToken.None);
        Assert.True(result.ReasoningCut);
        Assert.Equal("The handle is @rezero_ice.", result.Text);
        Assert.Equal(2, server.Chats.Count);
        Assert.False(server.Chats[1]["think"]!.GetValue<bool>());
        Assert.Contains("stopped the reasoning", result.Thinking);
    }

    [Fact]
    public async Task AnEmptyAnswerIsWrittenAgainDirectly()
    {
        using var server = new FakeOllama
        {
            Script = (_, turn) => turn == 1 ? new[] { FakeOllama.Chunk("", done: true) } : new[] { FakeOllama.Chunk("Starship reached orbit [1].", done: true) },
        };
        var (agent, _) = Build(server);
        var result = await agent.RunAsync("Did Starship reach orbit?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions(),
            new NullHost(), null, new HashSet<string>(), CancellationToken.None);
        Assert.Equal("Starship reached orbit [1].", result.Text);
        Assert.Contains("answer my question directly", server.Chats[1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task NarrationIsMovedOutOfTheAnswer()
    {
        using var server = new FakeOllama
        {
            Script = (_, _) => new[] { FakeOllama.Chunk("The user is asking about Starship. Let me check the feeds. Starship reached orbit despite an engine failure [1].", done: true) },
        };
        var (agent, _) = Build(server);
        var result = await agent.RunAsync("What happened with Starship?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions(),
            new NullHost(), null, new HashSet<string>(), CancellationToken.None);
        Assert.Equal("Starship reached orbit despite an engine failure [1].", result.Text);
        Assert.Contains("The user is asking", result.Thinking);
    }

    [Fact]
    public async Task MemoriesAndATaughtSkillShapeTheAnswer()
    {
        var wb = new Workbench(null);
        wb.Remember("My logos are in Pictures\\Brand");
        var skill = wb.SaveSkill(new AskSkill
        {
            Name = "Starship check", Description = "Starship questions", Instructions = "Always mention the launch site.",
            Steps = new() { new SkillStep { Tool = "search_hub", Args = new() { ["query"] = "{input}" } } },
        });
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("From Starbase, it reached orbit [1].", done: true) } };
        var (agent, _) = Build(server, workbench: wb);
        var host = new NullHost();
        var result = await agent.RunAsync("Starship orbit", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions { SkillId = skill.Id },
            host, null, new HashSet<string>(), CancellationToken.None);
        var system = System(server.Chats[0]);
        Assert.Contains("THE USER ASKED YOU TO REMEMBER", system);
        Assert.Contains("My logos are in Pictures\\Brand", system);
        Assert.Contains("Always mention the launch site.", system);
        Assert.Contains("RESULTS OF THE SKILL “Starship check”", system);
        Assert.Contains(host.Steps, st => st.StartsWith("Searched your feeds for “Starship orbit”", StringComparison.Ordinal));
        Assert.Equal("Starship check", result.Skill);
        Assert.Equal(1, wb.Skills().Single().Uses);
    }

    [Fact]
    public async Task WhenWebIsOffItSaysHowToTurnItOn()
    {
        using var server = new FakeOllama
        {
            Json = _ => """{"intent":"web","web_queries":["Re:Zero leaker Ice twitter"],"site":"x.com","file_terms":[],"format":"direct"}""",
            Script = (_, _) => new[] { FakeOllama.Chunk("Switch on Web to look this up.", done: true) },
        };
        var s = new HubSettings();
        s.Ask.Folders = new() { Path.GetTempPath() };
        var (agent, _) = Build(server, s);
        await agent.RunAsync("find the Re:Zero leaker Ice on twitter", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions { Computer = true },
            new NullHost(), new StubPlatform(), new HashSet<string>(), CancellationToken.None);
        var system = System(server.Chats[0]);
        Assert.Contains("Web is OFF", system);
        Assert.Contains("NOTE: Web is off, so nothing was looked up online", system);
    }

    [Fact]
    public void AFixedContextWindowLeavesOutTheOldestMessagesFirst()
    {
        var s = new HubSettings();
        s.Ask.ContextWindow = 8192;
        var messages = Enumerable.Range(0, 10).Select(i => new LlmMessage(i % 2 == 0 ? "user" : "assistant", new string('m', 2000))).Append(new LlmMessage("user", "now?")).ToList();
        var (window, dropped, context) = AskAgent.FitToWindow(new string('i', 3000), new string('c', 10000), messages, s, 2000);
        Assert.Equal(8192, window);
        Assert.True(dropped > 0);
        Assert.Equal("now?", messages.Last().Content);
        Assert.Equal(10000, context.Length);

        var few = new List<LlmMessage> { new("user", "hi") };
        var (_, _, shortened) = AskAgent.FitToWindow(new string('i', 3000), new string('c', 60000), few, s, 2000);
        Assert.Contains("left out to fit the context window", shortened);
        Assert.True(shortened.Length < 20000);
    }

    /// <summary>A platform with no PC tools that prepares images as they are.</summary>
    [Fact]
    public async Task AFollowUpUsesWhatTheChatReadBefore()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("It says grants go up to 6,500 euro [1].", done: true) } };
        var (agent, _) = Build(server);
        var earlier = new[] { new ChatSource("Heat pump grants", "seai.ie", "https://seai.ie/grants", "web", "Heat pump grants go up to 6,500 euro for homes built before 2021.") };

        var result = await agent.RunAsync("How much was the heat pump grant again?", new[] { new LlmMessage("user", "Read seai.ie/grants"), new LlmMessage("assistant", "Done.") },
            Array.Empty<AskAttachment>(), new AskOptions(), new NullHost(), new StubPlatform(), new HashSet<string>(), CancellationToken.None, earlier);

        var system = System(server.Chats[0]);
        Assert.Contains("FROM EARLIER IN THIS CHAT", system);
        Assert.Contains("6,500 euro", system);
        Assert.Equal("https://seai.ie/grants", Assert.Single(result.Citations).Url);
        Assert.Equal("Heat pump grants", Assert.Single(result.Sources).Title); // and it stays with the chat
    }

    [Fact]
    public async Task NearMeLeadsWithLocalStoriesAndSaysWhereOthersAre()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("Dublin Bus drivers strike on Friday [1].", done: true) } };
        var s = new HubSettings();
        s.Location.City = "Dublin";
        var local = new StoryCluster
        {
            Id = "s-bus", Title = "Dublin Bus drivers to strike on Friday", Category = "local", IsLocal = true,
            Items = { AskFixtures.Item("b1", "RTÉ News", "Dublin Bus drivers to strike on Friday", "Services across the city will stop for 24 hours.") },
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-3), Latest = DateTimeOffset.UtcNow.AddHours(-2),
        };
        var (agent, _) = Build(server, s, moreStories: new[] { local });

        await agent.RunAsync("What's happening near me today?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions(), new NullHost(), new StubPlatform(), new HashSet<string>(), CancellationToken.None);

        var system = System(server.Chats[0]);
        Assert.Contains("NEAR THE USER: they are in Dublin", system);
        Assert.Contains("(local story · RTÉ News", system);
        Assert.Contains("not a source: never cite it", system); // Aqua's own situation summary is labelled
    }

    [Fact]
    public async Task NearMeWithoutAPlaceSaysItDoesntKnowWhere()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("I don't know where you are yet.", done: true) } };
        var (agent, _) = Build(server, new HubSettings());

        await agent.RunAsync("What's happening near me today?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions(), new NullHost(), new StubPlatform(), new HashSet<string>(), CancellationToken.None);

        var system = System(server.Chats[0]);
        Assert.Contains("you don't know where the user is", system);
        Assert.Contains("the user hasn't said where they are", system);
        Assert.DoesNotContain("they are in", system);
    }

    [Fact]
    public async Task AnAttachedDocumentIsAnsweredWithoutSearchingThePc()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "meeting notes.txt"), "private: nothing to do with this");
        using var server = new FakeOllama
        {
            // The planner wrongly asks for a file search: the rules overrule it.
            Json = _ => """{"intent":"files","web_queries":[],"file_terms":["notes","summarise","attached","bullets"],"format":"list"}""",
            Script = (_, _) => new[] { FakeOllama.Chunk("- The budget was approved\n- The launch moved to May", done: true) },
        };
        var s = new HubSettings();
        s.Ask.Folders = new() { dir.Path };
        var (agent, _) = Build(server, s);
        var host = new NullHost();
        var notes = new AskAttachment { Name = "notes.txt", Kind = AttachmentKind.Document, Text = "The budget was approved. The launch moved to May." };

        await agent.RunAsync("Summarise the attached notes in two bullets", Array.Empty<LlmMessage>(), new[] { notes },
            new AskOptions { Computer = true }, host, new StubPlatform(), new HashSet<string>(), CancellationToken.None);

        Assert.DoesNotContain(host.Steps, st => st.Contains("your files", StringComparison.OrdinalIgnoreCase));
        var system = System(server.Chats[0]);
        Assert.Contains("FILES THE USER ATTACHED", system);
        Assert.DoesNotContain("meeting notes.txt", system);
    }

    [Fact]
    public async Task ALinkIsOpenedOnlyWithYourOkOnceYourFilesWereUsed()
    {
        using var server = new FakeOllama
        {
            Json = _ => """{"intent":"page","web_queries":[],"urls":["https://example.com/post"],"file_terms":[],"format":"direct"}""",
            Script = (_, _) => new[] { FakeOllama.Chunk("I didn't open the link, so here's just the notes.", done: true) },
        };
        var (agent, _) = Build(server);
        var host = new NullHost { Approve = _ => false };
        var notes = new AskAttachment { Name = "notes.txt", Kind = AttachmentKind.Document, Text = "Budget: 40k" };

        await agent.RunAsync("Compare https://example.com/post with the attached notes", Array.Empty<LlmMessage>(), new[] { notes },
            new AskOptions { Web = true }, host, new StubPlatform(), new HashSet<string>(), CancellationToken.None);

        Assert.Contains(host.Asked, a => a.Tool == "web-after-private" && a.Title.Contains("https://example.com/post", StringComparison.Ordinal));
        Assert.Contains(host.Steps, st => st.StartsWith("Didn't open example.com", StringComparison.Ordinal));
    }

    /// <summary>A screenshot whose text shows a browser on a web novel chapter.</summary>
    private sealed class ScreenWithABrowser : AskTool
    {
        public List<string> Calls { get; } = new();
        public override string Name => "take_screenshot";
        public override string Description => "screen";
        public override System.Text.Json.Nodes.JsonObject Parameters => new();
        public override ToolAccess Access => ToolAccess.Private;
        public override string Describe(JsonElement args) => "Take a screenshot";
        public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
        {
            Calls.Add(args.GetRawText());
            run.SawPrivate = true;
            return Task.FromResult(new ToolResult("[1] Screenshot of screen 2.\nText on screen (OCR):\nwww.royalroad.com/fiction/12345/re-zero/chapter/12\nChapter 12: The Return", "captured"));
        }
    }

    [Fact]
    public async Task APageOnScreenIsReadForTheQuestionWithYourOk()
    {
        using var server = new FakeOllama
        {
            Json = _ => """{"intent":"screen","web_queries":[],"file_terms":[],"screen":true,"format":"direct"}""",
            Script = (_, _) => new[] { FakeOllama.Chunk("I didn't open the page, so here's what's on screen.", done: true) },
        };
        var (agent, _) = Build(server);
        var screen = new ScreenWithABrowser();
        var host = new NullHost { Approve = a => a.Tool != "web-after-private" };

        await agent.RunAsync("Summarise the chapter I'm reading on my second screen", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions { Computer = true, Web = true }, host, new StubPlatform(screen), new HashSet<string>(), CancellationToken.None);

        Assert.Equal("{\"screen\":\"2\"}", Assert.Single(screen.Calls));
        var ask = Assert.Single(host.Asked, a => a.Tool == "web-after-private"); // it came from your screen: it asks first
        Assert.Contains("https://www.royalroad.com/fiction/12345/re-zero/chapter/12", ask.Title);
        Assert.Contains(host.Steps, st => st.StartsWith("Didn't open royalroad.com", StringComparison.Ordinal));
    }

    private sealed class StubPlatform(params AskTool[] tools) : IAskPlatform
    {
        public IEnumerable<AskTool> ComputerTools() => tools;
        public Task<(string Text, int Pages, int Total)> ReadPdfAsync(byte[] pdf, int maxPages, CancellationToken ct) => Task.FromResult(("", 0, 0));
        public Task<string> ReadImageTextAsync(byte[] image, CancellationToken ct) => Task.FromResult("");
        public byte[] PrepareImage(byte[] image, int maxEdge = 1600) => image;
        public string? KnownFolder(string token) => null;
        public Task<IReadOnlyList<IndexedFile>> SearchIndexAsync(IReadOnlyList<string> terms, IReadOnlyList<string> scopes, bool content, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IndexedFile>>(Array.Empty<IndexedFile>());
    }
}
