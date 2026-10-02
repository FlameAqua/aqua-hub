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

        Assert.True(BriefingAgent.ManualFlag.Consume()); // a current brief is rewritten only when asked
        Assert.Equal("briefing", Assert.Single(agents.Calls).Id);
        Assert.InRange(agents.Calls[0].Timeout.TotalMinutes, 3.9, 4);
        Assert.True(outcome.Ok);
        Assert.False(outcome.Finished);
        Assert.Equal("still working", outcome.Summary);
        Assert.Contains("Chief of Staff is still working", outcome.Text);
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
        var modelFree = false;
        // The Chief of Staff writes with the model: it must get it while the answer waits.
        agents.During = async _ =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var lease = await llm.ReserveAsync(cts.Token);
            modelFree = true;
        };
        var host = new NullHost();

        var result = await agent.RunAsync("What does my brief say?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions(), host, null, new HashSet<string>(), CancellationToken.None);

        Assert.True(modelFree);
        Assert.True(BriefingAgent.ManualFlag.Consume());
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
