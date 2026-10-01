using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;

namespace AquaHub.Tests;

/// <summary>Ask's model picker: each model keeps its own Think and Research, and an answer stays on the picked model.</summary>
public class AskModelTests
{
    private static string[] ThinkingUnlessGemma(string model) =>
        model.StartsWith("gemma", StringComparison.Ordinal) ? new[] { "completion", "vision" } : new[] { "completion", "tools", "thinking" };

    private static (AskAgent Agent, LlmClient Llm) Build(FakeOllama server)
    {
        var s = new HubSettings();
        s.Ai.Endpoint = server.Endpoint;
        s.Ask.ReadStoryArticles = false;
        var state = new HubState(null);
        state.SetStories(new List<StoryCluster> { AskFixtures.Starship() });
        var llm = new LlmClient(() => s.Ai, new InMemorySecretStore());
        var agent = new AskAgent(state, null, llm, () => s, new WebSearch(new HttpFetcher(null), () => s, new InMemorySecretStore()), new WebReader());
        return (agent, llm);
    }

    private static Task<AskResult> AskAsync(AskAgent agent, AskOptions options) =>
        agent.RunAsync("What happened with Starship?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), options, new NullHost(), null,
            new HashSet<string>(), CancellationToken.None);

    [Fact]
    public void EachModelKeepsItsOwnSwitches()
    {
        var ask = new AskSettings();
        Assert.False(ask.ModesFor("gemma3:4b").Think);
        Assert.False(ask.ModesFor("gemma3:4b").Research);
        ask.RememberModes("gemma3:4b", think: false, research: true);
        ask.RememberModes("qwen3.5:9b", think: true, research: false);
        Assert.True(ask.ModesFor("gemma3:4b").Research);
        Assert.False(ask.ModesFor("gemma3:4b").Think);
        Assert.True(ask.ModesFor("qwen3.5:9b").Think);
        // A model you haven't used yet starts from Settings › Ask Aqua's default.
        ask.ThinkByDefault = true;
        Assert.True(ask.ModesFor("llama3.2:3b").Think);
        Assert.False(ask.ModesFor("gemma3:4b").Think);
    }

    [Fact]
    public void OnlyAFewDozenModelsAreRemembered()
    {
        var ask = new AskSettings();
        for (var i = 0; i < 60; i++) ask.RememberModes($"model-{i}:1b", think: true, research: false);
        Assert.Equal(AskSettings.MaxModelModes, ask.ModelModes.Count);
        Assert.True(ask.ModelModes.ContainsKey("model-59:1b"));   // the latest is always kept
        ask.RememberModes("", think: true, research: true);
        Assert.False(ask.ModelModes.ContainsKey(""));
    }

    [Fact]
    public void SettingsKeepTheModelChoiceTidy()
    {
        var s = new HubSettings();
        s.Ask.Model = "  gemma3:4b ";
        s.Ask.ModelModes = new() { [" gemma3:4b"] = new AskModelModes { Think = true }, ["gemma3:4b"] = new AskModelModes(), [" "] = new AskModelModes() };
        SettingsStore.Validate(s);
        Assert.Equal("gemma3:4b", s.Ask.Model);
        Assert.Equal("gemma3:4b", Assert.Single(s.Ask.ModelModes).Key);
        s.Ask.ModelModes = null!;
        SettingsStore.Validate(s);
        Assert.Empty(s.Ask.ModelModes);
    }

    [Fact]
    public async Task EveryStepOfAnAnswerUsesThePickedModel()
    {
        using var server = new FakeOllama { Models = new[] { "qwen3.5:9b", "gemma3:4b" }, CapabilitiesFor = ThinkingUnlessGemma };
        server.Script = (_, _) => new[] { FakeOllama.Chunk("Starship flew again [1].", done: true) };
        var (agent, _) = Build(server);

        var result = await AskAsync(agent, new AskOptions { Model = "gemma3:4b" });

        Assert.Equal("gemma3:4b", result.Model);
        Assert.NotEmpty(server.Chats);
        Assert.All(server.Chats.Concat(server.JsonChats), c => Assert.Equal("gemma3:4b", c["model"]?.GetValue<string>()));
    }

    [Fact]
    public async Task APickThatIsNoLongerInstalledFallsBackToTheUsualModel()
    {
        using var server = new FakeOllama { Models = new[] { "qwen3.5:9b" } };
        server.Script = (_, _) => new[] { FakeOllama.Chunk("Starship flew again [1].", done: true) };
        var (agent, _) = Build(server);
        var result = await AskAsync(agent, new AskOptions { Model = "llama9:70b" });
        Assert.Equal("qwen3.5:9b", result.Model);
        Assert.All(server.Chats, c => Assert.Equal("qwen3.5:9b", c["model"]?.GetValue<string>()));
    }

    [Fact]
    public async Task ThinkIsOnlySentToAModelThatCanThink()
    {
        using var server = new FakeOllama { Models = new[] { "qwen3.5:9b", "gemma3:4b" }, CapabilitiesFor = ThinkingUnlessGemma };
        server.Script = (_, _) => new[] { FakeOllama.Chunk("Starship flew again [1].", done: true) };
        var (agent, _) = Build(server);

        await AskAsync(agent, new AskOptions { Model = "gemma3:4b", Think = true });
        Assert.All(server.Chats, c => Assert.Null(c["think"]));   // Gemma 3 has no thinking mode: asking for one is an error

        server.Chats.Clear();
        await AskAsync(agent, new AskOptions { Model = "qwen3.5:9b", Think = true });
        Assert.Contains(server.Chats, c => c["think"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task WhetherAModelCanThinkIsAskedWithoutWakingAnything()
    {
        using var server = new FakeOllama { Models = new[] { "qwen3.5:9b", "gemma3:4b" }, CapabilitiesFor = ThinkingUnlessGemma };
        var (_, llm) = Build(server);
        Assert.Null(await llm.CanThinkAsync("gemma3:4b"));   // the server hasn't been checked yet: unknown, nothing started
        await llm.CheckAsync();
        Assert.False(await llm.CanThinkAsync("gemma3:4b"));
        Assert.True(await llm.CanThinkAsync("qwen3.5:9b"));
        Assert.Null(await llm.CanThinkAsync("not-installed:1b"));
    }
}
