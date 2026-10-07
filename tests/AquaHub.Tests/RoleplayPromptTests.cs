using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Roleplay;

namespace AquaHub.Tests;

public class RoleplayPromptTests
{
    [Fact]
    public void ScenePromptUsesEditedPersonaAndScenarioAsContext()
    {
        var prompt = RoleplayPrompt.BuildSystem("Captain Imani", "A storm has cut off the lighthouse.");

        Assert.Contains("Captain Imani", prompt);
        Assert.Contains("A storm has cut off the lighthouse.", prompt);
        Assert.Contains("do not claim to take real-world actions", prompt);
    }

    [Fact]
    public void BlankSceneFieldsUseUsefulDefaults()
    {
        var prompt = RoleplayPrompt.BuildSystem("  ", null);

        Assert.Contains("a thoughtful scene partner", prompt);
        Assert.Contains("Begin with a quiet, open-ended scene.", prompt);
    }

    [Fact]
    public void CreativeModelIsUsedOnlyForTheLocalRuntimeWhenInstalled()
    {
        var models = new[] { "main-model", "creative-model" };

        Assert.Equal("creative-model", RoleplayPrompt.SelectModel("llamacpp", "creative-model", "main-model", models));
        Assert.Equal("main-model", RoleplayPrompt.SelectModel("llamacpp", "", "main-model", models));
        Assert.Equal("main-model", RoleplayPrompt.SelectModel("ollama", "creative-model", "main-model", models));
    }

    [Fact]
    public void MissingConfiguredCreativeModelExplainsHowToFixIt()
    {
        var error = Assert.Throws<RoleplayModelUnavailableException>(() =>
            RoleplayPrompt.SelectModel("llamacpp", "missing-model", "main-model", new[] { "main-model" }));

        Assert.Contains("missing-model", error.Message);
        Assert.Contains("Settings", error.Message);
    }

    [Fact]
    public void RequestHistoryKeepsRecentCompleteExchangesWithinBudget()
    {
        var history = new[]
        {
            new LlmMessage("user", "old"), new LlmMessage("assistant", "old answer"),
            new LlmMessage("user", "new"), new LlmMessage("assistant", "new answer"),
        };

        var messages = RoleplayPrompt.BuildHistory(history, "current", maxMessages: 3, maxCharacters: 100);

        Assert.Collection(messages,
            user => { Assert.Equal("user", user.Role); Assert.Equal("new", user.Content); },
            assistant => { Assert.Equal("assistant", assistant.Role); Assert.Equal("new answer", assistant.Content); },
            current => { Assert.Equal("user", current.Role); Assert.Equal("current", current.Content); });
    }

    [Fact]
    public void IncompleteExchangeIsOmittedSoNextTurnCannotBecomeConsecutiveUsers()
    {
        var messages = RoleplayPrompt.BuildHistory(new[] { new LlmMessage("user", "cancelled") }, "retry");

        Assert.Collection(messages, only => { Assert.Equal("user", only.Role); Assert.Equal("retry", only.Content); });
    }

    [Fact]
    public void HistoryBudgetRespectsSelectedContextAndAutomaticContext()
    {
        Assert.Equal(RoleplayPrompt.MaxHistoryCharacters, RoleplayPrompt.HistoryBudget(0));
        Assert.InRange(RoleplayPrompt.HistoryBudget(2048), 1200, RoleplayPrompt.MaxHistoryCharacters);
        Assert.True(RoleplayPrompt.HistoryBudget(4096) > RoleplayPrompt.HistoryBudget(2048));
        Assert.Equal(RoleplayPrompt.MaxHistoryCharacters, RoleplayPrompt.HistoryBudget(16000));
    }
}
