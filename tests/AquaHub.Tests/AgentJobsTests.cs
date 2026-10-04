using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;

namespace AquaHub.Tests;

/// <summary>Ask having Aqua's own agents refresh things: what counts as asking, how a job runs, and the answer around it.</summary>
[Collection("Brief requests")]
public class AgentJobsTests
{
    /// <summary>Stands in for the agent runtime: records each run and answers as told.</summary>
    private sealed class FakeAgents
    {
        public readonly List<(string Id, TimeSpan Timeout)> Calls = new();
        public Func<string, AgentRunReport?> Answer { get; set; } = id => Done(id, "ok");
        public Func<string, Task>? During { get; set; }

        public async Task<AgentRunReport?> RunAsync(string id, TimeSpan timeout, CancellationToken ct)
        {
            lock (Calls) Calls.Add((id, timeout));
            if (During is not null) await During(id);
            return Answer(id);
        }

        public static AgentRunReport Done(string id, string message, AgentState state = AgentState.Idle, AgentRunEnd end = AgentRunEnd.Finished) =>
            new(end, new AgentStatus { Id = id, Name = NameOf(id), State = state, LastMessage = message, Runs = 1 });

        private static string NameOf(string id) => id switch
        {
            "news-scout" => "News Scout", "curator" => "Story Curator", "market-watch" => "Market Watch", "briefing" => "Chief of Staff", _ => id,
        };
    }

    private static AskRun Run(HubState state, AgentRunner? runner, NullHost? host = null) => new()
    {
        State = state, Settings = new HubSettings(), Book = new SourceBook(), Options = new AskOptions(), Host = host ?? new NullHost(),
        AllowedForChat = new HashSet<string>(), RunAgent = runner,
    };

    private static StoryCluster Story(string id, string title, double importance = 5) => new()
    {
        Id = id, Title = title, Category = "world", Importance = importance,
        Items = { AskFixtures.Item(id + "-1", "RTÉ News", title) },
        FirstSeen = DateTimeOffset.UtcNow.AddHours(-1), Latest = DateTimeOffset.UtcNow.AddHours(-1),
    };

    [Theory]
    [InlineData("Refresh the news", "news")]
    [InlineData("Can you refresh the news and tell me what's new?", "news")]
    [InlineData("please update my markets and the weather", "markets,weather")]
    [InlineData("Hey, refresh my feeds", "news,social")]
    [InlineData("Aqua, refresh the weather then tell me if I need a coat", "weather")]
    [InlineData("Refresh the prediction markets", "predictions")]
    [InlineData("refresh the social pulse", "pulse")]
    [InlineData("Refresh everything", "news,social,markets,predictions,agenda,weather")]
    [InlineData("run all agents", "news,social,markets,predictions,agenda,weather")]
    [InlineData("Rerun the market analyst", "market_analysis")]
    [InlineData("run the market analyst please", "market_analysis")]
    [InlineData("Write me a fresh brief", "brief")]
    [InlineData("redo my morning brief", "brief")]
    [InlineData("give me fresh prices for my watchlist", "markets")]
    [InlineData("Can you also get the latest market prices for my watchlist?", "markets")]
    [InlineData("get me the latest headlines please", "news")]
    [InlineData("Give me an update on the markets", "")]
    [InlineData("update me on the news", "")]
    [InlineData("What's the latest news?", "")]
    [InlineData("Is there a new brief?", "")]
    [InlineData("run the numbers on my portfolio", "")]
    [InlineData("Run a health check", "")]
    [InlineData("Rewrite this story in simpler words", "")]
    [InlineData("Write me a new story about dragons", "")]
    [InlineData("Refresh my memory about the budget news", "")]
    [InlineData("update the app", "")]
    // Asked for in other words, or two at once.
    [InlineData("Refresh Aqua's agents", "news,social,markets,predictions,agenda,weather")]
    [InlineData("Please run a fresh market analysis", "market_analysis")]
    [InlineData("run a new analysis of the market", "market_analysis")]
    [InlineData("I'd like a fresh brief", "brief")]
    [InlineData("New brief please", "brief")]
    [InlineData("update the app and refresh the news", "news")]
    [InlineData("write me a fresh brief and a new market analysis", "brief,market_analysis")]
    [InlineData("Refresh the latest news", "news")]
    // About the user's own things, not what Aqua's agents collect or write.
    [InlineData("give me a fresh analysis of this contract", "")]
    [InlineData("Make me a new analysis of my sales data", "")]
    [InlineData("re-run the analysis on this data", "")]
    [InlineData("redo the analysis", "")]
    [InlineData("run the analysis on the attached file", "")]
    [InlineData("Run the analyst", "")]
    [InlineData("Generate a new digest of these notes", "")]
    [InlineData("write me a new digest of this article", "")]
    [InlineData("Write me a new brief summary of this page", "")]
    [InlineData("update the weather widget code", "")]
    [InlineData("update my news sources", "")]
    [InlineData("update the headlines in my document", "")]
    [InlineData("Can you update the stock prices in my spreadsheet?", "")]
    [InlineData("I want to refresh my knowledge of stocks", "")]
    [InlineData("update my calendar with a dentist visit", "")]
    [InlineData("update the schedule for my team meeting", "")]
    [InlineData("write me some new posts for my blog", "")]
    // "All" is everything only when nothing follows it or it's Aqua's own things (the Phase 3 review's M7).
    [InlineData("update all my documents", "")]
    [InlineData("refresh all my tabs", "")]
    [InlineData("run all the tests", "")]
    [InlineData("update all the files", "")]
    [InlineData("run everything", "")]
    [InlineData("run all", "")]
    [InlineData("Refresh all", "news,social,markets,predictions,agenda,weather")]
    [InlineData("update everything please", "news,social,markets,predictions,agenda,weather")]
    [InlineData("rerun all the agents", "news,social,markets,predictions,agenda,weather")]
    [InlineData("refresh all your data", "news,social,markets,predictions,agenda,weather")]
    [InlineData("refresh all my markets", "markets")]
    [InlineData("Update the app, then refresh the news", "news")]
    [InlineData("first refresh the news", "news")]
    [InlineData("update the files, refresh the weather", "weather")]
    public void RecognisesRequestsToRefresh(string message, string expected) =>
        Assert.Equal(expected, string.Join(",", AgentJobs.Requested(message).Select(j => j.Key)));

    [Theory]
    [InlineData("Is anything new on the weather front? Refresh it if you need to.", null, "weather")]
    [InlineData("Refresh it", "What's happening in the markets?", "markets")]
    [InlineData("Can you refresh them?", "Any new posts from my subreddits?", "social")]
    [InlineData("Redo it please", "What does my morning brief say?", "brief")]
    [InlineData("Rewrite it", "Tell me about the bus strike story", "")]
    [InlineData("Refresh it", null, "")]
    public void ARequestForItTakesItsSubjectFromTheChat(string message, string? earlier, string expected) =>
        Assert.Equal(expected, string.Join(",", AgentJobs.Requested(message, earlier).Select(j => j.Key)));

    [Fact]
    public async Task RefreshingTheNewsRunsTheScoutThenTheCuratorAndListsWhatIsNew()
    {
        var state = new HubState(null);
        state.SetStories(new List<StoryCluster> { Story("s-old", "Budget passes the Dáil") });
        var agents = new FakeAgents
        {
            Answer = id => FakeAgents.Done(id, id == "news-scout" ? "12 new articles from 38 sources" : "40 stories (9 multi-source) from 300 articles"),
            During = id =>
            {
                if (id == "news-scout") state.MarkFresh("news"); // as the scout does once it has fetched
                if (id == "curator") state.SetStories(new List<StoryCluster> { Story("s-old", "Budget passes the Dáil"), Story("s-new", "Dublin Bus drivers to strike on Friday", 9) });
                return Task.CompletedTask;
            },
        };
        var run = Run(state, agents.RunAsync);

        var outcome = await AgentJobs.RunAsync(AgentJobs.Find("news")!, run, CancellationToken.None);

        Assert.True(outcome.Ok && outcome.Finished);
        Assert.Equal(new[] { "news-scout", "curator" }, agents.Calls.Select(c => c.Id));
        Assert.All(agents.Calls, c => Assert.InRange(c.Timeout.TotalSeconds, 1, 90));
        Assert.Equal("12 new articles from 38 sources", outcome.Summary);
        Assert.Contains("News Scout: 12 new articles from 38 sources", outcome.Text);
        Assert.Contains("NEW STORIES SINCE THE REFRESH", outcome.Text);
        Assert.Contains("Dublin Bus drivers to strike on Friday", outcome.Text);
        Assert.DoesNotContain("Budget passes", outcome.Text);
        Assert.True(run.SawUntrusted);
        Assert.Equal(new[] { "the news" }, AgentJobs.Ran(run));

        // Asking again in the same answer doesn't run anything twice.
        var again = await AgentJobs.RunAsync(AgentJobs.Find("news")!, run, CancellationToken.None);
        Assert.Equal(2, agents.Calls.Count);
        Assert.Equal(outcome.Text, again.Text);
    }

    [Fact]
    public async Task ASwitchedOffOrFailedAgentStopsTheJobAndSaysWhy()
    {
        var off = new FakeAgents { Answer = id => FakeAgents.Done(id, "Disabled in settings", AgentState.Disabled, AgentRunEnd.Disabled) };
        var outcome = await AgentJobs.RunAsync(AgentJobs.Find("news")!, Run(new HubState(null), off.RunAsync), CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal("switched off in Settings", outcome.Summary);
        Assert.Contains("News Scout is switched off in Settings", outcome.Text);
        Assert.Single(off.Calls); // the curator had nothing new to sort

        var failed = new FakeAgents { Answer = id => FakeAgents.Done(id, "No connection", AgentState.Error) };
        outcome = await AgentJobs.RunAsync(AgentJobs.Find("news")!, Run(new HubState(null), failed.RunAsync), CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.Equal("failed: No connection", outcome.Summary);
        Assert.StartsWith("Couldn't refresh the news", outcome.Text);
        Assert.Single(failed.Calls);

        var paused = new FakeAgents { Answer = id => FakeAgents.Done(id, "", end: AgentRunEnd.Paused) };
        outcome = await AgentJobs.RunAsync(AgentJobs.Find("markets")!, Run(new HubState(null), paused.RunAsync), CancellationToken.None);
        Assert.Equal("agents are paused", outcome.Summary);
        Assert.Contains("Resume all", outcome.Text);

        outcome = await AgentJobs.RunAsync(AgentJobs.Find("markets")!, Run(new HubState(null), null), CancellationToken.None);
        Assert.False(outcome.Ok);
    }

    [Fact]
    public async Task AFreshBriefAsksTheChiefOfStaffToRewriteAndMayStillBeWorking()
    {
        var agents = new FakeAgents { Answer = id => FakeAgents.Done(id, "Working…", AgentState.Running, AgentRunEnd.StillRunning) };
        var outcome = await AgentJobs.RunAsync(AgentJobs.Find("brief")!, Run(new HubState(null), agents.RunAsync), CancellationToken.None);

        Assert.True(BriefingAgent.ManualFlag.Consume()); // a current brief is rewritten only when asked; the run under way keeps the request
        Assert.Equal("briefing", Assert.Single(agents.Calls).Id);
        Assert.InRange(agents.Calls[0].Timeout.TotalMinutes, 3.9, 4);
        Assert.True(outcome.Ok);
        Assert.False(outcome.Finished);
        Assert.Equal("still working", outcome.Summary);
        Assert.Contains("Chief of Staff is still working", outcome.Text);
    }

    [Fact]
    public async Task ACollectorThatFetchedNothingHasntRefreshed()
    {
        // The weather agent ran, but with no place chosen it had nothing to fetch (it marks the weather fresh only once it has).
        var agents = new FakeAgents { Answer = id => new AgentRunReport(AgentRunEnd.Finished, new AgentStatus { Id = id, Name = "Weather", LastMessage = "No place chosen yet", Runs = 1, NothingNew = true }) };
        var state = new HubState(null);
        var run = Run(state, agents.RunAsync);
        var outcome = await AgentJobs.RunAsync(AgentJobs.Find("weather")!, run, CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.StartsWith("Couldn't refresh the weather", outcome.Text);
        Assert.Contains("No place chosen yet", outcome.Text);
        Assert.Empty(AgentJobs.Ran(run));

        // Fetched with nothing new is still a refresh.
        var fetched = new FakeAgents { Answer = id => FakeAgents.Done(id, "Clear, 14°"), During = _ => { state.MarkFresh("weather"); return Task.CompletedTask; } };
        outcome = await AgentJobs.RunAsync(AgentJobs.Find("weather")!, Run(state, fetched.RunAsync), CancellationToken.None);
        Assert.True(outcome.Ok);
        Assert.StartsWith("Refreshed the weather just now", outcome.Text);
    }

    [Theory]
    [InlineData("Compact brief only · the model server is offline", AgentState.Paused, false)]
    [InlineData("Brief is current", AgentState.Idle, true)]
    public async Task ABriefThatWasntRewrittenIsntCalledFreshAndTheRequestIsTakenBack(string message, AgentState state, bool nothingNew)
    {
        var agents = new FakeAgents
        {
            Answer = id => new AgentRunReport(AgentRunEnd.Finished, new AgentStatus { Id = id, Name = "Chief of Staff", State = state, LastMessage = message, Runs = 1, NothingNew = nothingNew }),
            During = _ => { BriefingAgent.ManualFlag.Consume(); return Task.CompletedTask; },
        };
        var outcome = await AgentJobs.RunAsync(AgentJobs.Find("brief")!, Run(new HubState(null), agents.RunAsync), CancellationToken.None);
        Assert.False(outcome.Ok);
        Assert.StartsWith("Couldn't refresh your brief", outcome.Text);
        Assert.Contains(message, outcome.Text);
        Assert.False(BriefingAgent.ManualFlag.Pending);
    }

    /// <summary>
    /// The request is for this answer: when no run took it up (the agent was paused, or kept the brief it had), it's
    /// taken back, so a later scheduled run doesn't rewrite the brief for nobody. The agent here never looks at it, so
    /// only the taking back can clear it (the audit's TM6a).
    /// </summary>
    [Fact]
    public async Task ARequestNoRunTookUpIsTakenBack()
    {
        var agents = new FakeAgents { Answer = id => FakeAgents.Done(id, "Brief is current") };
        await AgentJobs.RunAsync(AgentJobs.Find("brief")!, Run(new HubState(null), agents.RunAsync), CancellationToken.None);
        Assert.NotEmpty(agents.Calls);
        Assert.False(BriefingAgent.ManualFlag.Pending);
    }

    [Fact]
    public async Task ABriefRunUnderWayThatMissedTheRequestIsFollowedByOneThatTakesItUp()
    {
        var took = new List<bool>();
        var agents = new FakeAgents
        {
            // The first run had looked before the request came; the second takes it up and rewrites.
            Answer = id => FakeAgents.Done(id, took.Count == 1 ? "Brief is current" : "Brief written · 5 sections"),
            During = _ => { took.Add(took.Count > 0 && BriefingAgent.ManualFlag.Consume()); return Task.CompletedTask; },
        };
        var outcome = await AgentJobs.RunAsync(AgentJobs.Find("brief")!, Run(new HubState(null), agents.RunAsync), CancellationToken.None);
        Assert.Equal(new[] { false, true }, took);
        Assert.True(outcome.Ok);
        Assert.Contains("Brief written", outcome.Text);
        Assert.False(BriefingAgent.ManualFlag.Pending);
    }

    [Fact]
    public async Task AskedForSeveralAiJobsTheAnswerWaitsForThemOnlySoLong()
    {
        var agents = new FakeAgents
        {
            Answer = id => FakeAgents.Done(id, "Written"),
            During = async _ => await Task.Delay(120),
        };
        var run = Run(new HubState(null), agents.RunAsync);

        var text = await AskAgent.RefreshFirstAsync("write me a fresh brief and a new market analysis", Array.Empty<LlmMessage>(), run, CancellationToken.None,
            budget: TimeSpan.FromMilliseconds(60));

        Assert.Equal("briefing", Assert.Single(agents.Calls).Id); // the analysis wasn't started once the time was up
        Assert.Contains("Didn't start the market analysis", text);
        BriefingAgent.ManualFlag.Withdraw();
    }

    [Fact]
    public async Task AJobThatThrowsDoesntSinkTheAnswer()
    {
        using var server = new FakeOllama
        {
            Script = (_, turn) => turn == 1
                ? new[] { FakeOllama.Chunk("", done: true, calls: FakeOllama.Call("run_agent", new JsonObject { ["job"] = "news" })) }
                : new[] { FakeOllama.Chunk("The news couldn't be refreshed.", done: true) },
        };
        var agents = new FakeAgents { Answer = _ => throw new InvalidOperationException("the runtime is shutting down") };
        var (agent, _, _) = Build(server, agents);
        var host = new NullHost();

        var result = await agent.RunAsync("Refresh the news", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions(), host, null, new HashSet<string>(), CancellationToken.None);

        Assert.Equal("The news couldn't be refreshed.", result.Text);
        Assert.Contains(host.Steps, st => st.StartsWith("Couldn't refresh the news", StringComparison.Ordinal));
        var tool = server.Chats[1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Contains("the runtime is shutting down", tool["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AMessageWithAttachmentsDoesntRunAgentsFirst()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("Here's the analysis of your notes.", done: true) } };
        var agents = new FakeAgents();
        var (agent, _, _) = Build(server, agents);
        var notes = new AskAttachment { Name = "notes.txt", Kind = AttachmentKind.Document, Text = "Q3 sales: 40k" };

        // Asked in so many words, but with a file attached: the model decides (it can still call run_agent).
        Assert.Equal("news", Assert.Single(AgentJobs.Requested("Refresh the news and summarise the attached notes")).Key);
        await agent.RunAsync("Refresh the news and summarise the attached notes", Array.Empty<LlmMessage>(), new[] { notes },
            new AskOptions(), new NullHost(), null, new HashSet<string>(), CancellationToken.None);

        Assert.Empty(agents.Calls);
    }

    [ReadableFolderFact]
    public async Task AMessageWithAFolderDoesntRunAgentsFirst()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("Here's a fresh analysis of the project.", done: true) } };
        var agents = new FakeAgents();
        var (agent, _, _) = Build(server, agents);
        // Outside %TEMP%: a folder in AppData is never attached at all.
        using var dir = new TempDir(AppContext.BaseDirectory);
        // "A fresh analysis" here is of the folder's files, not the market analyst's (the audit's TM7, AG6).
        await agent.RunAsync("Give me a fresh analysis and refresh the news", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions { Folders = new[] { dir.Path } }, new NullHost(), null, new HashSet<string>(), CancellationToken.None);
        Assert.Empty(agents.Calls);
    }

    [Fact]
    public async Task TheAgendaIsFreshOnceItsEventsAre()
    {
        var state = new HubState(null);
        var agents = new FakeAgents { Answer = id => FakeAgents.Done(id, "3 events"), During = _ => { state.MarkFresh("events"); return Task.CompletedTask; } };
        var outcome = await AgentJobs.RunAsync(AgentJobs.Find("agenda")!, Run(state, agents.RunAsync), CancellationToken.None);
        Assert.True(outcome.Ok);
        Assert.StartsWith("Refreshed your agenda just now", outcome.Text);
    }

    [Fact]
    public async Task WithNoModelTheCollectorsAskedForStillRun()
    {
        using var server = new FakeOllama();
        var agents = new FakeAgents();
        var (agent, _, state) = Build(server, agents);
        agents.During = id => { if (id == "market-watch") state.MarkFresh("markets"); return Task.CompletedTask; };
        var host = new NullHost();
        const string question = "Refresh the markets, then write me a fresh brief";
        Assert.Equal(new[] { "markets", "brief" }, AgentJobs.Requested(question).Select(j => j.Key));

        var said = await agent.RefreshWithoutModelAsync(question, null, host, CancellationToken.None);

        // Market Watch needs no model; the brief does, so it waits for one rather than being asked for and not written.
        Assert.Equal(new[] { "market-watch" }, agents.Calls.Select(c => c.Id));
        Assert.Equal("Refreshed market prices (ok). Your brief needs the local model, so it wasn't rewritten.", said);
        Assert.Equal("Refreshed market prices · ok", Assert.Single(host.Steps));
        Assert.Equal("", await agent.RefreshWithoutModelAsync("What's the weather like?", null, host, CancellationToken.None));
    }

    // ───────────────────────────── In an answer ─────────────────────────────

    private static (AskAgent Agent, LlmClient Llm, HubState State) Build(FakeOllama server, FakeAgents? agents)
    {
        var s = new HubSettings();
        s.Ai.Endpoint = server.Endpoint;
        s.Ask.ReadStoryArticles = false;
        var state = new HubState(null);
        state.SetStories(new List<StoryCluster> { AskFixtures.Starship() });
        var llm = new LlmClient(() => s.Ai, new InMemorySecretStore());
        var agent = new AskAgent(state, null, llm, () => s, new WebSearch(new HttpFetcher(null), () => s, new InMemorySecretStore()), new WebReader())
        {
            RunAgent = agents is null ? null : agents.RunAsync,
        };
        return (agent, llm, state);
    }

    private static List<string> Offered(JsonNode chat) => chat["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task AskingToRefreshTheNewsRunsTheAgentsBeforeTheAnswer()
    {
        // The model asks for the refresh again anyway: it isn't run twice, nor listed twice.
        using var server = new FakeOllama
        {
            Script = (_, turn) => turn == 1
                ? new[] { FakeOllama.Chunk("", done: true, calls: FakeOllama.Call("run_agent", new JsonObject { ["job"] = "news" })) }
                : new[] { FakeOllama.Chunk("Fresh in: Dublin Bus drivers strike on Friday [1].", done: true) },
        };
        var agents = new FakeAgents();
        var (agent, _, state) = Build(server, agents);
        agents.Answer = id => FakeAgents.Done(id, id == "news-scout" ? "3 new articles from 30 sources" : "2 stories");
        agents.During = id =>
        {
            if (id == "news-scout") state.MarkFresh("news");
            if (id == "curator") state.SetStories(new List<StoryCluster> { AskFixtures.Starship(), Story("s-bus", "Dublin Bus drivers to strike on Friday", 9) });
            return Task.CompletedTask;
        };
        var host = new NullHost();

        var result = await agent.RunAsync("Refresh the news and tell me what's new", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions(), host, null, new HashSet<string>(), CancellationToken.None);

        Assert.Equal(new[] { "news-scout", "curator" }, agents.Calls.Select(c => c.Id));
        var system = server.Chats[0]["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("WHAT AQUA'S AGENTS DID FOR THIS MESSAGE", system);
        Assert.Contains("Dublin Bus drivers to strike on Friday", system);
        Assert.Contains("run_agent", Offered(server.Chats[0]));
        Assert.Equal("Refreshed the news · 3 new articles from 30 sources", Assert.Single(host.Steps, st => st.StartsWith("Refreshed the news", StringComparison.Ordinal)));
        var tool = server.Chats[1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Contains("Refreshed the news just now", tool["content"]!.GetValue<string>());
        Assert.Single(result.Citations);
    }

    [Fact]
    public async Task TheModelCanAskForAFreshBriefAndLetsGoOfTheModelMeanwhile()
    {
        using var server = new FakeOllama
        {
            Script = (_, turn) => turn == 1
                ? new[] { FakeOllama.Chunk("", done: true, calls: FakeOllama.Call("run_agent", new JsonObject { ["job"] = "brief" })) }
                : new[] { FakeOllama.Chunk("Your new brief leads with the bus strike.", done: true) },
        };
        var agents = new FakeAgents { Answer = id => FakeAgents.Done(id, "Brief written · 5 sections") };
        var (agent, llm, _) = Build(server, agents);
        bool modelFree = false, asked = false;
        // The Chief of Staff writes with the model: it must get it while the answer waits.
        agents.During = async _ =>
        {
            asked = BriefingAgent.ManualFlag.Consume(); // as the agent does: a current brief is rewritten only when asked
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var lease = await llm.ReserveAsync(cts.Token);
            modelFree = true;
        };
        var host = new NullHost();

        var result = await agent.RunAsync("What does my brief say?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions(), host, null, new HashSet<string>(), CancellationToken.None);

        Assert.True(modelFree);
        Assert.True(asked);
        Assert.False(BriefingAgent.ManualFlag.Pending);
        var tool = server.Chats[1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Contains("Refreshed your brief just now", tool["content"]!.GetValue<string>());
        Assert.Contains(host.Steps, st => st == "Refreshed your brief · Brief written · 5 sections");
        Assert.Equal("Your new brief leads with the bus strike.", result.Text);
    }

    [Fact]
    public async Task WithoutTheRuntimeNothingIsRunOrOffered()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("I can't refresh from here.", done: true) } };
        var (agent, _, _) = Build(server, null);

        await agent.RunAsync("Refresh the news", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions(), new NullHost(), null, new HashSet<string>(), CancellationToken.None);

        Assert.DoesNotContain("run_agent", Offered(server.Chats[0]));
        Assert.DoesNotContain("WHAT AQUA'S AGENTS DID", server.Chats[0]["messages"]![0]!["content"]!.GetValue<string>());
    }
}
