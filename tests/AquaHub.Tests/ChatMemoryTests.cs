using System.Text.Json;
using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;

namespace AquaHub.Tests;

/// <summary>Phase 3.2: a chat's notes (scratchpad), the update_notes tool, and how answers read them.</summary>
public class ChatNotesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AddsNotesOnceAndKnowsTheirKinds()
    {
        var notes = new ChatNotes();
        Assert.True(notes.Add("fact", "Budget for the heat pump: €9,000", "aqua", Now, out _));
        Assert.True(notes.Add("Decided", "Go with the Daikin model", "aqua", Now, out _));
        Assert.True(notes.Add("open questions", "Does the grant cover installation?", "aqua", Now, out _));
        Assert.True(notes.Add("", "- The house is in Cork", "aqua", Now, out _));
        Assert.Equal(new[] { ChatNotes.Fact, ChatNotes.Decision, ChatNotes.Question, ChatNotes.Fact }, notes.Items.Select(n => n.Kind));
        Assert.Equal("The house is in Cork", notes.Items[3].Text); // the bullet is dropped
        // The same note in other words isn't added twice — but a different number is a change, not a repeat.
        Assert.False(notes.Add("fact", "The heat pump budget is €9,000", "aqua", Now, out var why));
        Assert.Contains("already noted", why);
        Assert.True(notes.Add("fact", "The heat pump budget is now €10,000", "aqua", Now, out _));
        notes.Remove("The heat pump budget is now €10,000");
        Assert.False(notes.Add("fact", "   ", "aqua", Now, out _));
        Assert.Equal(4, notes.Count);
    }

    [Fact]
    public void TheNotesHaveALimit()
    {
        var notes = new ChatNotes();
        for (var i = 0; i < ChatNotes.MaxNotes; i++) Assert.True(notes.Add("fact", $"Room {i} needs radiator model R{i * 7919}", "aqua", Now, out _));
        Assert.False(notes.Add("fact", "One more about the attic insulation", "aqua", Now, out var why));
        Assert.Contains("full", why);
        Assert.True(new ChatNotes().Add("fact", new string('x', 5000), "aqua", Now, out _));
        var n = new ChatNotes();
        n.Add("fact", new string('y', 5000), "aqua", Now, out _);
        Assert.True(n.Items[0].Text.Length <= ChatNotes.MaxModelLength);
    }

    [Fact]
    public void RemovesTheNoteItMeans()
    {
        var notes = new ChatNotes(new[]
        {
            new ChatNote(ChatNotes.Question, "Does the grant cover installation?"),
            new ChatNote(ChatNotes.Fact, "The installer is Kelly Heating, phone 021 555 0101"),
        });
        Assert.Equal("Does the grant cover installation?", notes.Remove("does the grant cover installation?")?.Text);
        Assert.Equal("The installer is Kelly Heating, phone 021 555 0101", notes.Remove("Kelly Heating")?.Text);
        Assert.Null(notes.Remove("something else entirely"));
        Assert.Equal(0, notes.Count);
    }

    [Fact]
    public void TextRoundTripsAndKeepsWhoWroteWhat()
    {
        var before = new List<ChatNote>
        {
            new(ChatNotes.Fact, "Budget: €9,000") { By = "aqua", At = Now.AddHours(-2) },
            new(ChatNotes.Decision, "Daikin") { By = "aqua", At = Now.AddHours(-1) },
            new(ChatNotes.Question, "Grant for installation?") { By = "aqua", At = Now.AddHours(-1) },
        };
        var text = ChatNotes.ToText(before);
        Assert.Equal("Facts\n- Budget: €9,000\n\nDecisions\n- Daikin\n\nOpen questions\n- Grant for installation?", text);
        Assert.Equal(before, ChatNotes.FromText(text, Now, before));

        var edited = ChatNotes.FromText("Lives in Cork\nFacts:\n* Budget: €10,000\n## Open questions\nWhen can they start?", Now, before);
        Assert.Equal(new[] { (ChatNotes.Fact, "Lives in Cork"), (ChatNotes.Fact, "Budget: €10,000"), (ChatNotes.Question, "When can they start?") },
            edited.Select(n => (n.Kind, n.Text)));
        Assert.All(edited, n => Assert.Equal("you", n.By));
    }

    [Fact]
    public void AnEditKeepsNotesAquaAddedMeanwhile()
    {
        var notes = new ChatNotes(new[] { new ChatNote(ChatNotes.Fact, "Budget: €9,000") { By = "aqua", At = Now.AddMinutes(-10) } });
        var editStarted = Now.AddMinutes(-1);
        notes.Add("decision", "Go with Daikin", "aqua", Now, out _);   // while you were typing
        notes.ApplyEdit("Facts\n- Budget: €10,000", editStarted, Now);
        Assert.Equal(new[] { "Budget: €10,000", "Go with Daikin" }, notes.Items.Select(n => n.Text));
    }

    [Fact]
    public void ChangesAreAnnounced()
    {
        var notes = new ChatNotes();
        var changes = 0;
        notes.Changed += () => changes++;
        notes.Add("fact", "A", "aqua", Now, out _);
        notes.Replace(notes.Items); // nothing changed
        notes.Remove("A");
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ThePromptHasHeadingsAndARoomLimit()
    {
        var notes = new ChatNotes(new[] { new ChatNote("decision", "Daikin"), new ChatNote("fact", "Cork") });
        Assert.Equal("Facts:\n- Cork\nDecisions:\n- Daikin\n", notes.ForPrompt());
        Assert.Equal("", new ChatNotes().ForPrompt());
        var many = new ChatNotes(Enumerable.Range(0, 50).Select(i => new ChatNote("fact", $"Note {i} " + new string('z', 300))));
        Assert.True(many.ForPrompt().Length <= ChatNotes.PromptChars + 400);
        Assert.Contains("left out", many.ForPrompt());
    }

    private static AskRun Run(ChatNotes? notes) => new()
    {
        State = new HubState(null), Settings = new HubSettings(), Book = new SourceBook(), Options = new AskOptions(), Host = new NullHost(),
        AllowedForChat = new HashSet<string>(), Chat = notes is null ? null : new AskChatContext { Notes = notes }, Now = Now,
    };

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task TheToolAddsAndRemovesNotes()
    {
        var notes = new ChatNotes();
        var run = Run(notes);
        var tool = new UpdateNotesTool();
        var added = await tool.RunAsync(Args(new { add = "Budget: €9,000", kind = "fact" }), run, default);
        Assert.True(added.Ok);
        Assert.Contains("Noted", added.Text);
        Assert.Equal("aqua", notes.Items.Single().By);
        Assert.Contains("already noted", (await tool.RunAsync(Args(new { add = "Budget: €9,000" }), run, default)).Text);
        var removed = await tool.RunAsync(Args(new { remove = "Budget" }), run, default);
        Assert.Contains("Removed “Budget: €9,000”", removed.Text);
        Assert.Equal("Noted “Daikin”", tool.Describe(Args(new { add = "Daikin" })));
        Assert.False((await tool.RunAsync(Args(new { }), run, default)).Ok);
        Assert.False((await tool.RunAsync(Args(new { add = "x" }), Run(null), default)).Ok);
    }

    [Fact]
    public async Task OnlySoManyChangesAnAnswer()
    {
        var run = Run(new ChatNotes());
        var tool = new UpdateNotesTool();
        for (var i = 0; i < UpdateNotesTool.MaxChangesPerAnswer; i++)
            Assert.True((await tool.RunAsync(Args(new { add = $"Fact number {i} about radiator {i * 31}" }), run, default)).Ok);
        Assert.False((await tool.RunAsync(Args(new { add = "One too many about the boiler flue" }), run, default)).Ok);
    }
}

/// <summary>Phase 3.2 and 3.3 in an answer: notes and the compressed summary reach the model, which can update the notes.</summary>
public class ChatMemoryAgentTests
{
    private static (AskAgent Agent, HubSettings Settings) Build(FakeOllama server)
    {
        var s = new HubSettings();
        s.Ai.Endpoint = server.Endpoint;
        s.Ask.ReadStoryArticles = false;
        var state = new HubState(null);
        state.SetStories(new List<Core.Models.StoryCluster> { AskFixtures.Starship() });
        var llm = new LlmClient(() => s.Ai, new InMemorySecretStore());
        var agent = new AskAgent(state, null, llm, () => s, new WebSearch(new HttpFetcher(null), () => s, new InMemorySecretStore()), new WebReader());
        return (agent, s);
    }

    [Fact]
    public async Task NotesAndTheSummaryAreReadAndTheModelCanAddANote()
    {
        using var server = new FakeOllama
        {
            Script = (req, turn) => turn == 1
                ? new[] { FakeOllama.Chunk("", done: true, calls: FakeOllama.Call("update_notes", new JsonObject { ["add"] = "Prefers a quiet outdoor unit", ["kind"] = "fact" })) }
                : new[] { FakeOllama.Chunk("Noted — the Daikin is quiet.", done: true) },
        };
        var (agent, _) = Build(server);
        var notes = new ChatNotes(new[] { new ChatNote(ChatNotes.Decision, "Go with the Daikin heat pump") { By = "you" } });
        var host = new NullHost();
        var chat = new AskChatContext { Notes = notes, Summary = "- Asked about heat pump grants; SEAI pays €6,500.", SummaryCovers = 12 };

        var result = await agent.RunAsync("It should be quiet too", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions(), host, null,
            new HashSet<string>(), CancellationToken.None, chat: chat);

        var system = server.Chats[0]["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("THIS CHAT SO FAR (Aqua's summary of its first 12 messages", system);
        Assert.Contains("SEAI pays €6,500", system);
        Assert.Contains("NOTES FOR THIS CHAT", system);
        Assert.Contains("- Go with the Daikin heat pump", system);
        Assert.Contains("update_notes keeps this chat's notes", system);
        Assert.True(system.IndexOf("NOTES FOR THIS CHAT", StringComparison.Ordinal) < system.IndexOf("THE USER'S SITUATION", StringComparison.Ordinal));
        var offered = server.Chats[0]["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("update_notes", offered);
        Assert.Equal(new[] { "Go with the Daikin heat pump", "Prefers a quiet outdoor unit" }, notes.Items.Select(n => n.Text));
        Assert.Contains(host.Steps, s => s.StartsWith("Noted “Prefers a quiet outdoor unit”", StringComparison.Ordinal));
        Assert.Equal("Noted — the Daikin is quiet.", result.Text);
    }

    [Fact]
    public async Task WithoutAChatThereIsNoNotesTool()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("Hi.", done: true) } };
        var (agent, _) = Build(server);
        await agent.RunAsync("Hello", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions(), new NullHost(), null, new HashSet<string>(), CancellationToken.None);
        var offered = server.Chats[0]["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("update_notes", offered);
        Assert.DoesNotContain("NOTES FOR THIS CHAT", server.Chats[0]["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ThePlannerSeesTheSummary()
    {
        var request = AskPlanner.Request("and how much would that cost?", Array.Empty<LlmMessage>(), new AskOptions { Web = true },
            new PlanContext { ChatSummary = "- Talked about the Daikin Altherma 3 heat pump." }, new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        Assert.Contains("Before that, in this chat (a summary): - Talked about the Daikin Altherma 3 heat pump.", request.Messages[0].Content);
    }
}

/// <summary>Notes, compression and pictures are kept with a chat and go with it.</summary>
public class ChatStoreExtrasTests : IDisposable
{
    private readonly string _media = Path.Combine(Path.GetTempPath(), "aquahub-media-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_media)) Directory.Delete(_media, true);
    }

    private static ChatSummary NewChat(ChatStore store, string id)
    {
        var summary = new ChatSummary { Id = id, Title = "Heat pumps", Created = DateTimeOffset.Now, Updated = DateTimeOffset.Now };
        store.Save(summary, new[] { new SavedChatMessage(true, "Which heat pump?", null, null, true) });
        return summary;
    }

    [Fact]
    public void NotesAndCompressionAreKeptWithTheChat()
    {
        var store = new ChatStore(null, _media);
        NewChat(store, "c1");
        store.SaveNotes("c1", new[] { new ChatNote(ChatNotes.Fact, "Budget €9,000") });
        store.SaveCompression("c1", new ChatCompression { Summary = "- Earlier", Covers = 4, Fingerprint = "f" });
        Assert.Equal("Budget €9,000", store.LoadNotes("c1").Single().Text);
        Assert.Equal(4, store.LoadCompression("c1")?.Covers);
        store.SaveCompression("c1", null); // Undo
        Assert.Null(store.LoadCompression("c1"));
        // Nothing is kept for a chat that isn't in the list.
        store.SaveNotes("nope", new[] { new ChatNote(ChatNotes.Fact, "x") });
        Assert.Empty(store.LoadNotes("nope"));
    }

    [Fact]
    public void DeletingAChatDeletesEverythingItKept()
    {
        var store = new ChatStore(null, _media);
        NewChat(store, "c1");
        NewChat(store, "c2");
        store.SaveNotes("c1", new[] { new ChatNote(ChatNotes.Fact, "Budget €9,000") });
        store.SaveCompression("c1", new ChatCompression { Summary = "- Earlier", Covers = 4, Fingerprint = "f" });
        Directory.CreateDirectory(store.MediaFolder("c1")!);
        File.WriteAllBytes(Path.Combine(store.MediaFolder("c1")!, "s1.png"), new byte[] { 1, 2, 3 });
        Directory.CreateDirectory(store.MediaFolder("c2")!);

        store.Delete("c1");
        Assert.Empty(store.LoadNotes("c1"));
        Assert.Null(store.LoadCompression("c1"));
        Assert.False(Directory.Exists(Path.Combine(_media, "c1")));
        Assert.True(Directory.Exists(Path.Combine(_media, "c2")));

        store.DeleteAll();
        Assert.False(Directory.Exists(_media));
    }

    [Fact]
    public void PicturesOfChatsThatAreGoneAreTidiedAndIdsMustBePlainNames()
    {
        var store = new ChatStore(null, _media);
        NewChat(store, "c1");
        Directory.CreateDirectory(Path.Combine(_media, "c1"));
        Directory.CreateDirectory(Path.Combine(_media, "c-orphan"));
        Assert.Equal(1, store.PruneMedia());
        Assert.True(Directory.Exists(Path.Combine(_media, "c1")));
        Assert.Null(store.MediaFolder(@"..\..\Windows"));
        Assert.Null(store.MediaFolder("a/b"));
        Assert.Null(new ChatStore(null).MediaFolder("c1"));
    }
}

/// <summary>Phase 3.3: compressing the start of a long chat.</summary>
public class ChatCompressorTests
{
    private static List<ChatTurn> Chat(int exchanges, int answerLength = 200) =>
        Enumerable.Range(0, exchanges).SelectMany(i => new[]
        {
            new ChatTurn(true, $"Question {i} about heat pumps?"),
            new ChatTurn(false, $"Answer {i}: " + new string('a', answerLength), new[] { new Citation(1, "SEAI grants", "seai.ie", "https://www.seai.ie/grants") }),
        }).ToList();

    [Fact]
    public void CompressesWholeExchangesAndKeepsTheLatestOnes()
    {
        var chat = Chat(6); // 12 messages
        Assert.Equal(8, ChatCompressor.CoverCount(chat, 0));           // all but the last four
        Assert.Equal(0, ChatCompressor.CoverCount(chat, 8));           // nothing new to compress
        Assert.Equal(6, ChatCompressor.CoverCount(chat.Take(11).ToList(), 0)); // ends after an answer, not mid-exchange
        Assert.Equal(0, ChatCompressor.CoverCount(Chat(2), 0));        // too short to be worth it
    }

    [Fact]
    public void AVeryLongChatIsCompressedInSteps()
    {
        var chat = Chat(40, answerLength: 3000);
        var first = ChatCompressor.CoverCount(chat, 0);
        Assert.InRange(first, 2, 79);
        Assert.False(chat[first - 1].User);
        Assert.True(ChatCompressor.CoverCount(chat, first) > first);
    }

    [Fact]
    public void ASummaryOnlyAppliesToTheMessagesItWasMadeFrom()
    {
        var chat = Chat(6);
        var compression = new ChatCompression { Summary = "- Earlier", Covers = 8, Fingerprint = ChatCompressor.Fingerprint(chat, 8) };
        Assert.Same(compression, ChatCompressor.Valid(compression, chat));
        var edited = chat.ToList();
        edited[2] = new ChatTurn(true, "A different question");
        Assert.Null(ChatCompressor.Valid(compression, edited));
        Assert.Null(ChatCompressor.Valid(compression, chat.Take(6).ToList()));
        Assert.Null(ChatCompressor.Valid(null, chat));
    }

    [Fact]
    public void CompressesOnItsOwnWhenMessagesWouldBeLeftOut()
    {
        Assert.True(ChatCompressor.ShouldCompress(Chat(6), 0, historyMessages: 10, contextUsed: 2000, contextLimit: 32768));
        Assert.False(ChatCompressor.ShouldCompress(Chat(5), 0, historyMessages: 10, contextUsed: 2000, contextLimit: 32768));
        Assert.True(ChatCompressor.ShouldCompress(Chat(5), 0, historyMessages: 40, contextUsed: 27000, contextLimit: 32768));
        Assert.False(ChatCompressor.ShouldCompress(Chat(6), 8, historyMessages: 10, contextUsed: 2000, contextLimit: 32768));
        Assert.False(ChatCompressor.ShouldCompress(Chat(2), 0, historyMessages: 2, contextUsed: 32000, contextLimit: 32768));
    }

    [Fact]
    public void TheSummaryIsCleanedAndMustBeShorter()
    {
        Assert.Equal("- What was asked: grants for heat pumps.", ChatCompressor.Clean("**Summary:**\n- What was asked: grants for heat pumps.", 500));
        Assert.Equal("- What was asked: grants for heat pumps.", ChatCompressor.Clean("```\n- What was asked: grants for heat pumps.\n```", 500));
        Assert.Null(ChatCompressor.Clean("Too short.", 500));
        Assert.Null(ChatCompressor.Clean(new string('w', 2000), 300));
    }

    [Fact]
    public void TheRequestCarriesTheMessagesTheirSourcesAndTheSummarySoFar()
    {
        var request = ChatCompressor.Request("- Earlier: asked about solar panels.", Chat(2), "qwen3.5:9b", interactive: true, new HubSettings(), 16384);
        var text = request.Messages.Single().Content;
        Assert.Contains("SUMMARY OF THE CHAT BEFORE THESE MESSAGES:\n- Earlier: asked about solar panels.", text);
        Assert.Contains("User: Question 0 about heat pumps?", text);
        Assert.Contains("Sources: [1] SEAI grants <https://www.seai.ie/grants>", text);
        Assert.StartsWith("<<<DATA", text);
        Assert.Contains("name, number, date", request.System);
        Assert.Equal(LlmPriority.Interactive, request.Priority);
        Assert.Equal(16384, request.ContextTokens); // the chat's own window: no reload
        Assert.Equal("qwen3.5:9b", request.Model);
        Assert.False(request.Think);
    }

    [Fact]
    public async Task CompressesThroughTheModelAndCarriesOnFromTheSummary()
    {
        using var server = new FakeOllama
        {
            Script = (_, _) => new[] { FakeOllama.Chunk("- What was asked: heat pump grants.\n- What Aqua found: SEAI pays up to €6,500 [seai.ie].", done: true) },
        };
        var s = new HubSettings();
        s.Ai.Endpoint = server.Endpoint;
        var llm = new LlmClient(() => s.Ai, new InMemorySecretStore());
        var chat = Chat(8, answerLength: 600);
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

        var first = await ChatCompressor.CompressAsync(llm, s, null, null, chat, 8, automatic: false, window: 0, now, default);
        Assert.NotNull(first);
        Assert.Equal(8, first!.Covers);
        Assert.Equal(ChatCompressor.Fingerprint(chat, 8), first.Fingerprint);
        Assert.True(first.TokensBefore > first.TokensAfter);
        Assert.True(first.Saved > 0);

        var second = await ChatCompressor.CompressAsync(llm, s, null, first, chat, 12, automatic: true, window: 0, now, default);
        Assert.Equal(12, second!.Covers);
        Assert.True(second.Automatic);
        var request = server.Chats[^1]["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();
        Assert.Contains("SEAI pays up to €6,500", request);       // the summary so far
        Assert.Contains("Question 4 about heat pumps?", request); // and only the messages after it
        Assert.DoesNotContain("Question 3 about heat pumps?", request);
    }
}
