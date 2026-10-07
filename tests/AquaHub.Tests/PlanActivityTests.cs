using AquaHub.Core.Ai.Assistant;

namespace AquaHub.Tests;

public sealed class PlanActivityTests
{
    [Fact]
    public void DirectChatHasNoInventedRetrievalActivity() => Assert.Empty(new AskPlan().Describe());

    [Fact]
    public void PreparationNamesActualSources()
    {
        Assert.Contains("attachments", new AskPlan { AboutAttachments = true }.Describe());
        Assert.Contains("collected feeds", new AskPlan { Intent = "feeds" }.Describe());
        var web = new AskPlan { WebQueries = new() { "Dublin weather" } }.Describe();
        Assert.Contains("Dublin weather", web);
        Assert.DoesNotContain("already knows", web);
    }
}
