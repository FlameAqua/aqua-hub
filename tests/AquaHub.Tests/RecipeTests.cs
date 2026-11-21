using System.Collections.Immutable;
using AquaHub.Core.Agents;
using AquaHub.Core.Data;
using AquaHub.Core.Health;
using AquaHub.Core.Models;
using Microsoft.Data.Sqlite;

namespace AquaHub.Tests;

public sealed class RecipeTests
{
    private const string PrivateTitle = "Private diagnostic name";
    private static readonly RecipeDraft Draft = new("PC briefing", "Check the PC",
        [new(RecipeCapabilities.CheckHealth), new(RecipeCapabilities.BuildReport, 1, RecipeFindingScope.Actionable)]);

    private static HealthReport Report() => new(new HealthFacts
    {
        Readings = [new("Firewall", HealthReadingState.Denied)],
    }, [new("Storage", HealthLevel.Warning, PrivateTitle, "Private path"), new("Storage", HealthLevel.Good, "Space available", "")], null);

    [Theory]
    [InlineData("shell", 1, RecipeFindingScope.Actionable)]
    [InlineData(RecipeCapabilities.BuildReport, 2, RecipeFindingScope.Actionable)]
    [InlineData(RecipeCapabilities.BuildReport, 1, (RecipeFindingScope)42)]
    [InlineData(RecipeCapabilities.BuildReport, 1, null)]
    [InlineData(RecipeCapabilities.CheckHealth, null, null)]
    public async Task UnsupportedCapabilitiesOrInvalidBindingsCannotBeSaved(string capability, int? source, RecipeFindingScope? scope)
    {
        using var fixture = new Fixture(_ => Task.FromResult(Report()));
        var bad = Draft with { Steps = Draft.Steps.SetItem(1, new(capability, source, scope)) };
        await Assert.ThrowsAsync<RecipeException>(() => fixture.Service.SaveAsync(null, null, bad));
        Assert.Empty(fixture.Service.State.Recipes);
    }

    [Fact]
    public async Task SaveAndReviewDoNotRunAndOneConfirmationClaimsOneRun()
    {
        var called = Signal();
        var release = Signal();
        using var fixture = new Fixture(async token =>
        {
            called.TrySetResult();
            await release.Task.WaitAsync(token);
            return Report();
        });
        var service = fixture.Service;
        var recipe = await service.SaveAsync(null, null, Draft);
        var review = service.Review(recipe.Id);
        Assert.Empty(service.State.Runs);
        Assert.False(called.Task.IsCompleted);
        var ids = await Task.WhenAll(service.StartAsync(review), service.StartAsync(review), service.StartAsync(service.Review(recipe.Id)));
        Assert.All(ids, id => Assert.Equal(ids[0], id));
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = Terminal(service, ids[0]);
        release.SetResult();
        var run = await completed;
        Assert.Equal(RecipeRunState.Completed, run.State);
        Assert.All(run.Steps, step => Assert.Equal(RecipeStepState.Completed, step.State));
        Assert.Equal(2, run.Steps[0].Result!.IncludedFindings);
        Assert.Equal(1, run.Steps[1].Result!.IncludedFindings);
        Assert.Equal(1, run.Steps[1].Result!.IncompleteChecks);
        Assert.Equal(PrivateTitle, Assert.Single(service.Preview!.Findings).Title);
        Assert.Equal(ids[0], await service.StartAsync(review));
        var reopened = new RecipeService(fixture.Database, null);
        Assert.Null(reopened.Preview);
        Assert.Equal(ids[0], Assert.Single(reopened.State.Runs).Id);
        using var c = new SqliteConnection($"Data Source={fixture.Database.Path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key='agents:recipes';";
        Assert.DoesNotContain("Private", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task PausingAfterReviewRevokesThatApprovalAndMissingAccessBlocksTheStep()
    {
        using var fixture = new Fixture(null);
        var service = fixture.Service;
        var recipe = await service.SaveAsync(null, null, Draft);
        var stale = service.Review(recipe.Id);
        await service.SetPausedAsync(recipe.Id, recipe.Revision, true);
        await Assert.ThrowsAsync<RecipeException>(() => service.StartAsync(stale));
        Assert.Throws<RecipeException>(() => service.Review(recipe.Id));
        await service.SetPausedAsync(recipe.Id, recipe.Revision + 1, false);
        await Assert.ThrowsAsync<RecipeException>(() => service.StartAsync(stale));
        var current = await service.StartAsync(service.Review(recipe.Id));
        var run = await Terminal(service, current);
        Assert.Equal(RecipeRunState.Blocked, run.State);
        Assert.Equal(RecipeStopReason.AccessUnavailable, run.Steps[0].Reason);
        Assert.Equal(RecipeStepState.Pending, run.Steps[1].State);
    }

    [Theory]
    [InlineData("cancel", RecipeStopReason.Cancelled)]
    [InlineData("edit", RecipeStopReason.RecipeChanged)]
    [InlineData("pause", RecipeStopReason.RecipeChanged)]
    [InlineData("shutdown", RecipeStopReason.Shutdown)]
    public async Task RevocationStopsTheRemainingSteps(string action, RecipeStopReason reason)
    {
        var called = Signal();
        using var fixture = new Fixture(async token =>
        {
            called.SetResult();
            await Signal().Task.WaitAsync(token);
            return Report();
        });
        var service = fixture.Service;
        var recipe = await service.SaveAsync(null, null, Draft);
        var id = await service.StartAsync(service.Review(recipe.Id));
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = Terminal(service, id);
        switch (action)
        {
            case "cancel": Assert.True(service.Cancel(id)); break;
            case "edit": await service.SaveAsync(recipe.Id, recipe.Revision, Draft with { Name = "Edited" }); break;
            case "pause": await service.SetPausedAsync(recipe.Id, recipe.Revision, true); break;
            case "shutdown": await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); break;
        }
        var run = await completed;
        Assert.Equal(RecipeRunState.Cancelled, run.State);
        Assert.Equal(reason, run.Reason);
        Assert.Equal(RecipeStepState.Cancelled, run.Steps[0].State);
        Assert.Equal(RecipeStepState.Pending, run.Steps[1].State);
        Assert.Null(service.Preview);
    }

    [Fact]
    public async Task DeleteDoesNotAllowAnInFlightResultToRecreateHistory()
    {
        var called = Signal();
        var release = Signal();
        using var fixture = new Fixture(async _ => { called.SetResult(); await release.Task; return Report(); });
        var service = fixture.Service;
        var recipe = await service.SaveAsync(null, null, Draft);
        await service.StartAsync(service.Review(recipe.Id));
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DeleteAsync(recipe.Id, recipe.Revision);
        var stopped = service.StopAsync();
        Assert.False(stopped.IsCompleted);
        release.SetResult();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(service.State.Recipes);
        Assert.Empty(service.State.Runs);
        Assert.Empty(new RecipeService(fixture.Database, null).State.Runs);
    }

    [Fact]
    public async Task HistoryIsBoundedAndCanBeCleared()
    {
        using var fixture = new Fixture(_ => Task.FromResult(Report()));
        var service = fixture.Service;
        var recipe = await service.SaveAsync(null, null, Draft with { Steps = ImmutableArray.Create(Draft.Steps[0]) });
        for (var i = 0; i < 12; i++)
        {
            var id = await service.StartAsync(service.Review(recipe.Id));
            await Terminal(service, id);
            await service.JoinCurrentRunAsync();
        }
        Assert.Equal(RecipeService.HistoryLimit, service.State.Runs.Length);
        Assert.Equal(RecipeService.HistoryLimit, service.State.Runs.Select(run => run.Id).Distinct().Count());
        await service.ClearHistoryAsync(recipe.Id);
        Assert.Empty(new RecipeService(fixture.Database, null).State.Runs);
    }

    [Fact]
    public void RestartMarksAbandonedWorkWithoutReplayingIt()
    {
        using var fixture = new Fixture(null);
        var recipe = new AgentRecipe(Guid.NewGuid(), 1, false, Draft);
        var first = new RecipeStepRun(RecipeCapabilities.CheckHealth, RecipeStepState.Completed);
        var second = new RecipeStepRun(RecipeCapabilities.BuildReport, RecipeStepState.Running);
        var abandoned = new RecipeRun(Guid.NewGuid(), recipe.Id, 1, DateTimeOffset.UtcNow, RecipeRunState.Running, [first, second]);
        fixture.Database.PutJson("agents:recipes", new RecipeState([recipe], [abandoned]));
        var reopened = new RecipeService(fixture.Database, _ => throw new InvalidOperationException("Unexpected replay"));
        var run = Assert.Single(reopened.State.Runs);
        Assert.Equal(RecipeRunState.Interrupted, run.State);
        Assert.Equal(RecipeStepState.Completed, run.Steps[0].State);
        Assert.Equal(RecipeStepState.Interrupted, run.Steps[1].State);
        var reread = Assert.Single(new RecipeService(fixture.Database, null).State.Runs);
        Assert.Equal(run.Id, reread.Id);
        Assert.Equal(RecipeRunState.Interrupted, reread.State);
    }

    [Fact]
    public void LegacyAgentHistoryMigratesAndCanBeFilteredWithTypedOutcomes()
    {
        using var folder = new TempDir();
        var path = Path.Combine(folder.Path, "legacy.db");
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE agent_runs(id INTEGER PRIMARY KEY, agent TEXT, started INTEGER, duration_ms INTEGER, ok INTEGER, message TEXT);
                INSERT INTO agent_runs VALUES (1,'news',5,10,1,'legacy'), (2,'other',5,10,0,'legacy failure');
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        var db = new HubDatabase(path);
        db.AddRun(new("news", DateTimeOffset.UtcNow, TimeSpan.Zero, true, "waiting") { Outcome = BuiltInRunOutcome.Waiting });
        var history = new HubDatabase(path).GetRuns(10, "news");
        Assert.Equal(new[] { BuiltInRunOutcome.Waiting, BuiltInRunOutcome.Finished }, history.Select(row => row.Outcome));
        Assert.Equal(BuiltInRunOutcome.Failed, Assert.Single(db.GetRuns(10, "other")).Outcome);
        HubDatabase.ReleaseFile(path);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<RecipeRun> Terminal(RecipeService service, Guid id)
    {
        var ready = new TaskCompletionSource<RecipeRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            if (service.State.Runs.FirstOrDefault(run => run.Id == id) is { Active: false } ended) ready.TrySetResult(ended);
        }
        service.Changed += Changed;
        try { Changed(); return await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { service.Changed -= Changed; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _folder = new();
        public HubDatabase Database { get; }
        public RecipeService Service { get; }

        public Fixture(Func<CancellationToken, Task<HealthReport>>? check)
        {
            Database = new(Path.Combine(_folder.Path, "hub.db"));
            Service = new(Database, check);
        }

        public void Dispose()
        {
            Service.StopAsync().GetAwaiter().GetResult();
            HubDatabase.ReleaseFile(Database.Path);
            _folder.Dispose();
        }
    }
}
