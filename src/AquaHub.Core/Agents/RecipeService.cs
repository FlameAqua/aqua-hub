using System.Collections.Immutable;
using System.Text.Json;
using AquaHub.Core.Data;
using AquaHub.Core.Health;
using AquaHub.Core.Util;

namespace AquaHub.Core.Agents;

/// <summary>Owns saved revisions, one manual worker, and a bounded, payload-free journal.</summary>
public sealed class RecipeService
{
    private const string StoreKey = "agents:recipes";
    private const int StoreBytes = 512 * 1024;
    public const int MaxRecipes = 30;
    public const int HistoryLimit = 10;
    public const int PreviewLimit = 8;

    private readonly HubDatabase _db;
    private readonly Func<CancellationToken, Task<HealthReport>>? _checkHealth;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private volatile RecipeState _state;
    private volatile RecipePreview? _preview;
    private ActiveRun? _active;
    private bool _stopping;
    private readonly bool _unreadable;

    private sealed class ActiveRun(Guid id, Guid recipeId)
    {
        public Guid Id { get; } = id;
        public Guid RecipeId { get; } = recipeId;
        public CancellationTokenSource Cancellation { get; } = new();
        public RecipeStopReason StopReason { get; set; }
        public Task Worker { get; set; } = Task.CompletedTask;
        public Task Cancelled { get; set; } = Task.CompletedTask;
    }

    public RecipeService(HubDatabase db, Func<CancellationToken, Task<HealthReport>>? checkHealth, TimeProvider? clock = null)
    {
        _db = db;
        _checkHealth = checkHealth;
        _clock = clock ?? TimeProvider.System;
        _state = new([], []);
        try
        {
            _state = db.GetBoundedJson<RecipeState>(StoreKey, StoreBytes) ?? _state;
            if (_state.Recipes.IsDefault || _state.Runs.IsDefault || _state.Recipes.Length > MaxRecipes
                || _state.Runs.Length > MaxRecipes * HistoryLimit
                || _state.Recipes.Any(recipe => recipe?.Draft is null || recipe.Id == Guid.Empty || recipe.Revision < 1
                    || recipe.Draft.Steps.IsDefaultOrEmpty || recipe.Draft.Steps.Length > 2 || recipe.Draft.Steps.Any(step => step is null))
                || _state.Runs.Any(run => run is null || run.Steps.IsDefaultOrEmpty || run.Steps.Length > 2 || run.Steps.Any(step => step is null)))
                throw new InvalidDataException("The recipe store is incomplete.");
            if (_state.Runs.Any(run => run.Active))
            {
                var now = _clock.GetUtcNow();
                Save(_state with
                {
                    Runs = _state.Runs.Select(run => run.Active
                        ? SetTerminal(run, RecipeRunState.Interrupted, RecipeStopReason.Interrupted, now)
                        : run).ToImmutableArray(),
                });
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            _unreadable = true;
            _state = new([], []);
            Log.Warn("recipes", "Could not open the recipe store", ex);
        }
    }

    public RecipeState State => _state;
    public RecipePreview? Preview => _preview;
    public bool StoreAvailable => !_unreadable;
    public event Action? Changed;

    public Task<AgentRecipe> SaveAsync(Guid? id, int? revision, RecipeDraft draft) => ChangeAsync(() =>
    {
        var validated = RecipeCapabilities.Validate(draft);
        var current = id is { } key ? Current(key, revision) : null;
        if (current is null && _state.Recipes.Length >= MaxRecipes)
            throw new RecipeException($"Aqua can keep up to {MaxRecipes} recipes.");
        var recipe = current is null
            ? new AgentRecipe(Guid.NewGuid(), 1, false, validated)
            : current with { Revision = checked(current.Revision + 1), Draft = validated };
        Save(_state with { Recipes = current is null ? _state.Recipes.Add(recipe) : _state.Recipes.Replace(current, recipe) });
        RequestCancellation(recipe.Id, RecipeStopReason.RecipeChanged);
        return recipe;
    });

    public Task SetPausedAsync(Guid id, int revision, bool paused) => ChangeAsync(() =>
    {
        var recipe = Current(id, revision);
        if (recipe.Paused == paused) return false;
        Save(_state with
        {
            Recipes = _state.Recipes.Replace(recipe, recipe with { Paused = paused, Revision = checked(recipe.Revision + 1) }),
        });
        if (paused) RequestCancellation(id, RecipeStopReason.RecipeChanged);
        return true;
    });

    public Task DeleteAsync(Guid id, int revision) => ChangeAsync(() =>
    {
        var recipe = Current(id, revision);
        Save(new(_state.Recipes.Remove(recipe), _state.Runs.RemoveAll(run => run.RecipeId == id)));
        RequestCancellation(id, RecipeStopReason.RecipeChanged);
        if (_preview is { } preview && FindRun(preview.RunId) is null) _preview = null;
        return true;
    });

    public Task ClearHistoryAsync(Guid id) => ChangeAsync(() =>
    {
        Current(id);
        Save(_state with { Runs = _state.Runs.RemoveAll(run => run.RecipeId == id && !run.Active) });
        if (_preview is { } preview && FindRun(preview.RunId) is null) _preview = null;
        return true;
    });

    public RecipeReview Review(Guid id)
    {
        lock (_gate)
        {
            EnsureAvailable();
            var recipe = Current(id);
            if (recipe.Paused) throw new RecipeException("Resume the recipe before running it.");
            RecipeCapabilities.Validate(recipe.Draft);
            return new(recipe);
        }
    }

    public Task<Guid> StartAsync(RecipeReview review) => ChangeAsync(() =>
    {
        if (review.RunId != Guid.Empty) return review.RunId;
        var recipe = Current(review.Recipe.Id, review.Recipe.Revision);
        if (recipe.Paused) throw new RecipeException("Resume the recipe before running it.");
        var draft = RecipeCapabilities.Validate(recipe.Draft);
        if (_active is { } active)
        {
            if (active.RecipeId == recipe.Id && FindRun(active.Id) is { Active: true } running && running.Revision == recipe.Revision)
                return review.RunId = active.Id;
            throw new RecipeException("A recipe is already running. Wait for it to finish.");
        }

        var run = new RecipeRun(Guid.NewGuid(), recipe.Id, recipe.Revision, _clock.GetUtcNow(), RecipeRunState.Queued,
            draft.Steps.Select(step => new RecipeStepRun(step.CapabilityId, RecipeStepState.Pending)).ToImmutableArray());
        var old = _state.Runs.Where(r => r.RecipeId == recipe.Id).Skip(HistoryLimit - 1).Select(r => r.Id).ToHashSet();
        Save(_state with { Runs = _state.Runs.RemoveAll(r => old.Contains(r.Id)).Insert(0, run) });
        if (_preview is { } preview && old.Contains(preview.RunId)) _preview = null;
        review.RunId = run.Id;
        var owner = new ActiveRun(run.Id, recipe.Id);
        _active = owner;
        owner.Worker = Task.Run(() => RunAsync(owner, recipe));
        return run.Id;
    });

    public bool Cancel(Guid runId)
    {
        lock (_gate)
            return _active is { } active && active.Id == runId && FindRun(runId)?.Active == true
                && RequestCancellation(active.RecipeId, RecipeStopReason.Cancelled);
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_active is not { } active) return Task.CompletedTask;
            RequestCancellation(active.RecipeId, RecipeStopReason.Shutdown);
            return active.Worker;
        }
    }

    internal Task JoinCurrentRunAsync()
    {
        lock (_gate) return _active?.Worker ?? Task.CompletedTask;
    }

    private async Task RunAsync(ActiveRun owner, AgentRecipe recipe)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40), _clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Cancellation.Token, deadline.Token);
        var ct = cancellation.Token;
        var state = RecipeRunState.Completed;
        var reason = RecipeStopReason.None;
        HealthReport? health = null;
        RecipePreview? preview = null;
        try
        {
            for (var i = 0; i < recipe.Draft.Steps.Length; i++)
            {
                var step = recipe.Draft.Steps[i];
                UpdateStep(owner, recipe, i, ct, row => row with { State = RecipeStepState.Running, Started = _clock.GetUtcNow() });
                if (step.CapabilityId == RecipeCapabilities.CheckHealth)
                {
                    if (_checkHealth is null)
                    {
                        state = RecipeRunState.Blocked;
                        reason = RecipeStopReason.AccessUnavailable;
                        break;
                    }
                    ct.ThrowIfCancellationRequested();
                    health = await _checkHealth(ct).ConfigureAwait(false);
                }
                var report = health ?? throw new InvalidOperationException("The health check did not return a report.");
                var findings = step.Findings == RecipeFindingScope.Actionable
                    ? report.Findings.Where(finding => finding.Level is HealthLevel.Warning or HealthLevel.Problem)
                    : report.Findings;
                var result = new RecipeResult(report.Facts.Checked, report.Problems, report.Warnings,
                    report.Facts.IncompleteChecks, findings.Count());
                if (step.CapabilityId == RecipeCapabilities.BuildReport)
                    preview = new(owner.Id, findings.Take(PreviewLimit)
                        .Select(f => new RecipeFinding(HtmlText.Truncate(f.Area, 40), f.Level, HtmlText.Truncate(f.Title, 140)))
                        .ToImmutableArray(), result.IncludedFindings);
                UpdateStep(owner, recipe, i, ct, row => row with { State = RecipeStepState.Completed, Finished = _clock.GetUtcNow(), Result = result });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            state = deadline.IsCancellationRequested ? RecipeRunState.Failed : RecipeRunState.Cancelled;
            reason = deadline.IsCancellationRequested ? RecipeStopReason.Timeout : RecipeStopReason.Cancelled;
        }
        catch (UnauthorizedAccessException)
        {
            state = RecipeRunState.Blocked;
            reason = RecipeStopReason.AccessUnavailable;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            state = RecipeRunState.Failed;
            reason = RecipeStopReason.CheckFailed;
            Log.Warn("recipes", "Recipe worker failed", ex);
        }
        finally
        {
            Task cancelled;
            lock (_gate)
            {
                if (owner.StopReason != RecipeStopReason.None)
                {
                    state = RecipeRunState.Cancelled;
                    reason = owner.StopReason;
                }
                if (FindRun(owner.Id) is { } run)
                {
                    var finished = SetTerminal(run, state, reason, _clock.GetUtcNow());
                    var next = _state with { Runs = _state.Runs.Replace(run, finished) };
                    try { Save(next); }
                    catch (Microsoft.Data.Sqlite.SqliteException ex)
                    {
                        Log.Warn("recipes", "Could not save the final run outcome", ex);
                        _state = next with { Runs = next.Runs.Replace(finished, finished with
                        {
                            State = RecipeRunState.Failed, Reason = RecipeStopReason.CheckFailed,
                        }) };
                        state = RecipeRunState.Failed;
                    }
                    if (state == RecipeRunState.Completed && preview is not null) _preview = preview;
                }
                cancelled = owner.Cancelled;
            }
            try { await cancelled.ConfigureAwait(false); }
            catch (AggregateException ex) { Log.Warn("recipes", "A cancellation callback failed", ex); }
            finally
            {
                cancellation.Dispose();
                owner.Cancellation.Dispose();
                lock (_gate) _active = null;
                Publish();
            }
        }
    }

    private void UpdateStep(ActiveRun owner, AgentRecipe recipe, int i, CancellationToken ct, Func<RecipeStepRun, RecipeStepRun> update)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            // Edits, pause and delete withdraw authority under the same gate as step admission.
            if (owner.StopReason != RecipeStopReason.None) throw new OperationCanceledException(ct);
            Current(recipe.Id, recipe.Revision);
            var run = FindRun(owner.Id) ?? throw new RecipeException("The run was removed.");
            Save(_state with { Runs = _state.Runs.Replace(run, run with
            {
                State = RecipeRunState.Running, Steps = run.Steps.SetItem(i, update(run.Steps[i])),
            }) });
        }
        Publish();
    }

    private static RecipeRun SetTerminal(RecipeRun run, RecipeRunState state, RecipeStopReason reason, DateTimeOffset now)
    {
        var stepState = state switch
        {
            RecipeRunState.Cancelled => RecipeStepState.Cancelled,
            RecipeRunState.Blocked => RecipeStepState.Blocked,
            RecipeRunState.Interrupted => RecipeStepState.Interrupted,
            _ => RecipeStepState.Failed,
        };
        var steps = run.Steps.Select(step => step.State == RecipeStepState.Running
            ? step with { State = stepState, Reason = reason, Finished = now }
            : step).ToImmutableArray();
        return run with { State = state, Reason = reason, Steps = steps, Finished = now };
    }

    private AgentRecipe Current(Guid id, int? revision = null)
    {
        var recipe = _state.Recipes.FirstOrDefault(r => r.Id == id);
        if (recipe is null) throw new RecipeException("This recipe was removed.");
        if (revision is { } expected && expected != recipe.Revision)
            throw new RecipeException("The recipe changed. Open its current revision and review it again.");
        return recipe;
    }

    private RecipeRun? FindRun(Guid id) => _state.Runs.FirstOrDefault(run => run.Id == id);

    private bool RequestCancellation(Guid recipeId, RecipeStopReason reason)
    {
        if (_active is not { } active || active.RecipeId != recipeId || active.StopReason != RecipeStopReason.None
            || FindRun(active.Id) is { Active: false }) return false;
        active.StopReason = reason;
        active.Cancelled = active.Cancellation.CancelAsync();
        return true;
    }

    private Task<T> ChangeAsync<T>(Func<T> change) => Task.Run(() =>
    {
        T result;
        lock (_gate)
        {
            EnsureAvailable();
            result = change();
        }
        Publish();
        return result;
    });

    private void EnsureAvailable()
    {
        if (_stopping) throw new RecipeException("Aqua is closing.");
        if (_unreadable) throw new RecipeException("The saved recipes could not be opened.");
    }

    private void Save(RecipeState state)
    {
        _db.PutJson(StoreKey, state);
        _state = state;
    }

    private void Publish()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Warn("recipes", "Could not publish recipe status", ex); }
    }
}
