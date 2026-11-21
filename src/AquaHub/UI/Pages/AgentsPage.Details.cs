using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AquaHub.Core.Agents;
using AquaHub.Core.Models;
using AquaHub.Core.Util;
using AquaHub.Services;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public partial class AgentsPage
{
    private sealed class RunRow(RecipeRun run) : ObservableObject
    {
        public RecipeRun Run { get; private set; } = run;
        public string Label => $"{Run.Requested.ToLocalTime():g} · {Run.State} · Revision {Run.Revision}";
        public override string ToString() => Label;
        public void Update(RecipeRun value)
        {
            if (Run == value) return;
            Run = value;
            Raise(nameof(Label));
        }
    }

    private readonly ObservableCollection<RunRow> _recipeRuns = new();
    private Guid? _recipeId;
    private string? _builtInId;
    private Button? _detailEntry;
    private RecipeReview? _review;
    private bool _recipeBusy;
    private bool _syncingHistory;
    private Guid? _stopRequested;
    private Guid? _selectRun;
    private (string Id, DateTimeOffset? LastRun)? _builtInHistoryKey;
    private DateTimeOffset? _lastBuiltInRun;

    private void OnRecipesChanged() => Hub.OnUi(() =>
    {
        if (IsVisible) RefreshInspector();
    });

    private void OnGoToRecipes(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("workbench", "recipes");

    private void OnOpenAgent(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ViewModels.AgentVM agent } entry) return;
        _builtInId = agent.Id;
        _recipeId = null;
        OpenInspector(entry);
    }

    private void OnOpenRecipe(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgentRecipe recipe } entry) OpenRecipe(recipe.Id, entry);
    }

    private void OpenRecipe(Guid id, Button? entry)
    {
        if (!Hub.Core.Recipes.State.Recipes.Any(recipe => recipe.Id == id))
        {
            AgentsFeedback.Show("This recipe is unavailable.", error: true);
            return;
        }
        if (_recipeId != id) _selectRun = null;
        _recipeId = id;
        _builtInId = null;
        OpenInspector(entry);
    }

    private void OpenInspector(Button? entry)
    {
        CloseReview();
        _detailEntry = entry;
        if (RecipeHistory.ItemsSource is null) RecipeHistory.ItemsSource = _recipeRuns;
        AgentDashboard.Visibility = Visibility.Collapsed;
        AgentInspector.Visibility = Visibility.Visible;
        AgentScroll.ScrollToTop();
        RefreshInspector();
        Dispatcher.BeginInvoke(() => { FocusDetailControl(BackToAgents); }, DispatcherPriority.Loaded);
    }

    private void OnBackToAgents(object sender, RoutedEventArgs e) => HideInspector();

    private void HideInspector()
    {
        _builtInId = null;
        _recipeId = null;
        _selectRun = null;
        CloseReview();
        AgentInspector.Visibility = Visibility.Collapsed;
        AgentDashboard.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            if (!AgentDashboard.IsVisible) return;
            var target = _detailEntry is { IsVisible: true, IsLoaded: true } ? _detailEntry : GoToRecipes;
            target.BringIntoView();
            FocusDetailControl(target);
        }, DispatcherPriority.Loaded);
    }

    private void OnInspectorKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_review is not null) CloseReview(focus: true);
        else HideInspector();
        e.Handled = true;
    }

    private void RefreshInspector()
    {
        var state = Hub.Core.Recipes.State;
        if (AgentRecipes.ItemsSource is not ImmutableArray<AgentRecipe> shown || shown != state.Recipes)
            AgentRecipes.ItemsSource = state.Recipes;
        SetVisible(RecipesEmpty, state.Recipes.IsEmpty);
        RecipesEmpty.Text = Hub.Core.Recipes.StoreAvailable ? "Create a recipe in Workbench." : "Saved recipes could not be opened.";
        if (_recipeId is { } id)
        {
            var recipe = state.Recipes.FirstOrDefault(r => r.Id == id);
            if (recipe is null) { HideInspector(); return; }
            ShowRecipe(recipe, state);
        }
        else if (_builtInId is { } key && Hub.Core.Agents.Detail(key) is { } detail)
            ShowBuiltIn(detail);
    }

    private async void ShowBuiltIn(BuiltInAgentDetail detail)
    {
        var status = detail.Status;
        DetailTitle.Text = status.Name;
        DetailPurpose.Text = status.Description;
        DetailState.Text = status.State switch
        {
            AgentState.Running => detail.Paused ? "Running · scheduler paused" : "Running",
            AgentState.Waiting => "Queued",
            _ when !detail.Enabled => "Disabled",
            _ when detail.Paused => "Paused",
            _ when detail.Requested => "Queued",
            AgentState.Paused => "Waiting",
            AgentState.Error => "Failed",
            _ => "Ready",
        };
        DetailAccess.Text = BuiltInAccess(status.Id, status.UsesAi);
        var dependencies = detail.Dependencies.Select(id => _agents.TryGetValue(id, out var agent) ? agent.Name : id).ToArray();
        var interval = detail.Interval.TotalMinutes < 1 ? $"{detail.Interval.TotalSeconds:0} seconds" : $"{detail.Interval.TotalMinutes:0} minutes";
        DetailSchedule.Text = $"Checks every {interval}" + (dependencies.Length > 0 ? $" · Also runs after updates from {string.Join(", ", dependencies)}" : "");
        DetailTiming.Text = BuiltInTiming(status.LastRun ?? (_builtInHistoryKey?.Id == status.Id ? _lastBuiltInRun : null), detail.NextRun);
        DetailNotice.Text = status.LastMessage;
        RecipeSteps.ItemsSource = null;
        SetVisible(RunBuiltIn, detail.CanRun);
        HideRecipeControls();
        SetVisible(BuiltInHistory, true);
        var key = (status.Id, status.LastRun);
        if (_builtInHistoryKey == key)
        {
            SetVisible(DetailEmpty, BuiltInHistory.Items.Count == 0);
            return;
        }
        _builtInHistoryKey = key;
        _lastBuiltInRun = status.LastRun;
        BuiltInHistory.ItemsSource = null;
        SetVisible(DetailEmpty, false);
        try
        {
            var history = await Task.Run(() => Hub.Core.Db.GetRuns(10, status.Id));
            if (_builtInId != status.Id || _builtInHistoryKey != key || !IsVisible) return;
            BuiltInHistory.ItemsSource = history.Select(run => $"{FormatTime(run.Started)} · {run.Outcome} · {run.Duration.TotalSeconds:0.0}s").ToArray();
            _lastBuiltInRun = history.FirstOrDefault()?.Started;
            DetailTiming.Text = BuiltInTiming(_lastBuiltInRun, Hub.Core.Agents.Detail(status.Id)?.NextRun);
            DetailEmpty.Text = "No runs yet.";
            SetVisible(DetailEmpty, history.Count == 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("agents", "Could not read agent history", ex);
            if (_builtInId != status.Id || _builtInHistoryKey != key) return;
            _builtInHistoryKey = null;
            DetailEmpty.Text = "Run history is unavailable.";
            SetVisible(DetailEmpty, true);
        }
    }

    private static string BuiltInAccess(string id, bool usesAi)
    {
        var data = id switch
        {
            "weather" => "Configured location and the weather provider.",
            "news-scout" => "Configured news feeds.",
            "social-scout" => "Configured social feeds and connected accounts.",
            "stream-scout" => "Followed Twitch and saved YouTube channels while Social is open.",
            "market-watch" or "prediction-scout" => "Configured watchlist or market sources.",
            "events-scout" => "Connected read-only calendar feeds and public events.",
            "sentinel" => "Collected stories, quotes, agenda and PC metrics. Can issue Aqua alerts.",
            "keeper" => "Aqua’s local database. Removes expired items according to your retention settings.",
            "model-warden" => "Configured AI endpoint and model availability.",
            _ => "Relevant data already collected in Aqua.",
        };
        return "Access · " + data + (usesAi ? " Uses your configured AI provider." : "");
    }

    private void OnRunBuiltIn(object sender, RoutedEventArgs e)
    {
        if (_builtInId is not { } id || Hub.Core.Agents.Detail(id)?.CanRun != true) return;
        Hub.Core.Agents.RunNow(id);
        RefreshInspector();
        FocusDetailControl(BackToAgents);
    }

    private void ShowRecipe(AgentRecipe recipe, RecipeState state)
    {
        if (_review is { } pending && pending.Recipe.Revision != recipe.Revision)
        {
            CloseReview(focus: true);
            AgentsFeedback.Show("The recipe changed. Review the current revision before running it.");
        }
        var runs = state.Runs.Where(run => run.RecipeId == recipe.Id).ToArray();
        var active = runs.FirstOrDefault(run => run.Active);
        DetailTitle.Text = recipe.Draft.Name;
        DetailPurpose.Text = recipe.Draft.Purpose;
        DetailState.Text = _review is not null ? "Waiting for confirmation" : recipe.Paused ? "Paused" : active is not null
            ? _stopRequested == active.Id ? "Cancelling" : active.State.ToString() : "Ready";
        DetailAccess.Text = "Access · Read-only Windows health checks. Results stay on this PC.";
        DetailSchedule.Text = $"Manual trigger · Revision {recipe.Revision}";
        DetailTiming.Text = $"Last run {FormatTime(runs.FirstOrDefault()?.Requested, "—")} · Next run when you approve";
        DetailNotice.Text = runs.FirstOrDefault() is { } latest ? StopText(latest.Reason) : "";
        var steps = recipe.Draft.Steps.Select((step, i) => $"{i + 1}. {RecipeCapabilities.Label(step.CapabilityId)}"
            + (step.Findings is { } scope ? $" · {ScopeLabel(scope)} from step {step.FromStep}" : "")).ToArray();
        if (RecipeSteps.ItemsSource is not string[] before || !steps.SequenceEqual(before)) RecipeSteps.ItemsSource = steps;
        SetVisible(RunBuiltIn, false);
        SetVisible(BuiltInHistory, false);
        SetVisible(PauseRecipe, true);
        SetVisible(EditRecipeLink, true);
        SetVisible(RunSteps, true);
        SetVisible(CurrentFindings, true);
        SetVisible(ReviewRecipe, !recipe.Paused && active is null && _review is null);
        SetVisible(CancelRecipeRun, active is not null);
        CancelRecipeRun.IsEnabled = active is not null && _stopRequested != active.Id;
        PauseRecipe.Content = recipe.Paused ? "Resume recipe" : "Pause recipe";
        PauseRecipe.IsEnabled = !_recipeBusy;
        ReviewRecipe.IsEnabled = !_recipeBusy;
        var selected = _selectRun ?? (RecipeHistory.SelectedItem as RunRow)?.Run.Id;
        _syncingHistory = true;
        for (var i = _recipeRuns.Count - 1; i >= 0; i--)
            if (!runs.Any(run => run.Id == _recipeRuns[i].Run.Id)) _recipeRuns.RemoveAt(i);
        for (var i = 0; i < runs.Length; i++)
        {
            var existing = _recipeRuns.FirstOrDefault(row => row.Run.Id == runs[i].Id);
            if (existing is null) _recipeRuns.Insert(i, new(runs[i]));
            else existing.Update(runs[i]);
        }
        RecipeHistory.SelectedItem = _recipeRuns.FirstOrDefault(row => row.Run.Id == selected) ?? _recipeRuns.FirstOrDefault();
        _selectRun = null;
        _syncingHistory = false;
        DetailEmpty.Text = "No runs yet.";
        SetVisible(DetailEmpty, runs.Length == 0);
        SetVisible(RecipeHistory, runs.Length > 0);
        SetVisible(ClearRecipeHistory, runs.Any(run => !run.Active));
        ShowRun();
    }

    private void HideRecipeControls()
    {
        SetVisible(ReviewRecipe, false);
        SetVisible(PauseRecipe, false);
        SetVisible(EditRecipeLink, false);
        SetVisible(CancelRecipeRun, false);
        SetVisible(RecipeHistory, false);
        SetVisible(RunOutcome, false);
        SetVisible(RunSteps, false);
        SetVisible(CurrentFindings, false);
        SetVisible(OpenHealthReport, false);
        SetVisible(ClearRecipeHistory, false);
    }

    private void OnRunSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingHistory) ShowRun();
    }

    private void ShowRun()
    {
        var run = (RecipeHistory.SelectedItem as RunRow)?.Run;
        SetVisible(RunOutcome, run is not null);
        SetVisible(OpenHealthReport, run?.Steps.Any(step => step.Result is not null) == true && Hub.Health.Last is not null);
        RunOutcome.Text = run is null ? "" : $"{run.State} · {FormatTime(run.Requested)}"
            + (run.Finished is { } end ? $" → {FormatTime(end)}" : "") + (StopText(run.Reason) is { Length: > 0 } reason ? $" · {reason}" : "");
        RunSteps.ItemsSource = run?.Steps.Select((step, i) => $"{i + 1}. {RecipeCapabilities.Label(step.CapabilityId)} · "
            + (step.State == RecipeStepState.Pending && !run.Active ? "Did not run" : step.State.ToString())
            + (step.Started is { } start ? $" · {start.ToLocalTime():T}" : "")
            + (step.Finished is { } finish ? $" → {finish.ToLocalTime():T}" : "")
            + (step.Result is { } result ? $" · {ResultText(result)}" : "")).ToArray();
        var preview = Hub.Core.Recipes.Preview;
        CurrentFindings.ItemsSource = preview is not null && preview.RunId == run?.Id
            ? preview.Findings.Select(f => $"{f.Area} · {f.Title}")
                .Append(preview.IncludedFindings switch
                {
                    0 => "No findings matched the selected filter.",
                    > RecipeService.PreviewLimit => $"Showing the first {RecipeService.PreviewLimit} findings. Open This PC for the full report.",
                    _ => "",
                }).Where(line => line.Length > 0).ToArray()
            : null;
    }

    private void OnReviewRecipe(object sender, RoutedEventArgs e)
    {
        if (_recipeId is not { } id || _recipeBusy) return;
        try
        {
            _review = Hub.Core.Recipes.Review(id);
            AgentsFeedback.Hide("recipe-start");
            ReviewPanel.Visibility = Visibility.Visible;
            RefreshInspector();
            DismissReview.BringIntoView();
            FocusDetailControl(DismissReview);
        }
        catch (RecipeException ex) { AgentsFeedback.Show(ex.Message, error: true); }
    }

    private void OnDismissReview(object sender, RoutedEventArgs e) => CloseReview(focus: true);

    private void CloseReview(bool focus = false)
    {
        _review = null;
        ReviewPanel.Visibility = Visibility.Collapsed;
        if (!focus) return;
        RefreshInspector();
        FocusDetailControl(ReviewRecipe.IsVisible && ReviewRecipe.IsEnabled ? ReviewRecipe : BackToAgents);
    }

    private async void OnConfirmRecipe(object sender, RoutedEventArgs e)
    {
        if (_review is not { } review) return;
        CloseReview();
        FocusDetailControl(BackToAgents);
        await ChangeRecipeAsync(async () =>
        {
            var id = await Hub.Core.Recipes.StartAsync(review);
            if (_recipeId != review.Recipe.Id) return;
            _selectRun = id;
            RefreshInspector();
            if (IsVisible)
            {
                AgentsFeedback.Show("Recipe run requested.", "recipe-start");
                if (Keyboard.FocusedElement is null || Keyboard.FocusedElement == BackToAgents)
                    FocusDetailControl(CancelRecipeRun.IsVisible ? CancelRecipeRun : BackToAgents);
            }
        });
    }

    private async void OnPauseRecipe(object sender, RoutedEventArgs e)
    {
        if (Hub.Core.Recipes.State.Recipes.FirstOrDefault(r => r.Id == _recipeId) is not { } recipe) return;
        CloseReview(focus: false);
        await ChangeRecipeAsync(() => Hub.Core.Recipes.SetPausedAsync(recipe.Id, recipe.Revision, !recipe.Paused));
    }

    private void OnCancelRecipeRun(object sender, RoutedEventArgs e)
    {
        var run = Hub.Core.Recipes.State.Runs.FirstOrDefault(r => r.RecipeId == _recipeId && r.Active);
        if (run is null || !Hub.Core.Recipes.Cancel(run.Id)) return;
        AgentsFeedback.Hide("recipe-start");
        _stopRequested = run.Id;
        RefreshInspector();
        FocusDetailControl(BackToAgents);
    }

    private void OnEditInWorkbench(object sender, RoutedEventArgs e)
    {
        if (_recipeId is { } id) Hub.Windows.ShowMain("workbench", $"recipe:{id}");
    }

    private void OnOpenHealthReport(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("system", "health-report");

    private async void OnClearRecipeHistory(object sender, RoutedEventArgs e)
    {
        if (_recipeId is not { } id || _recipeBusy) return;
        if (MessageBox.Show(Window.GetWindow(this)!, "Clear the completed run history for this recipe?", "Clear recipe history",
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await ChangeRecipeAsync(() => Hub.Core.Recipes.ClearHistoryAsync(id));
        FocusDetailControl(BackToAgents);
    }

    private async Task ChangeRecipeAsync(Func<Task> action)
    {
        if (_recipeBusy) return;
        _recipeBusy = true;
        RefreshInspector();
        try { await action(); }
        catch (RecipeException ex) { if (IsVisible) AgentsFeedback.Show(ex.Message, error: true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("recipes", "The recipe request failed", ex);
            if (IsVisible) AgentsFeedback.Show("Aqua couldn’t complete the request. Try again.", error: true);
        }
        finally { _recipeBusy = false; if (IsVisible) RefreshInspector(); }
    }

    private static void SetVisible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private void FocusDetailControl(Control control)
    {
        if (control.IsVisible && control.IsEnabled && Window.GetWindow(this)?.IsActive == true) control.Focus();
    }
    private static string BuiltInTiming(DateTimeOffset? last, DateTimeOffset? next) =>
        $"Last run {FormatTime(last)} · Next eligible run {FormatTime(next)}";
    private static string FormatTime(DateTimeOffset? value, string missing = "—") => value is { } time ? $"{time.ToLocalTime():g}" : missing;
    private static string ScopeLabel(RecipeFindingScope scope) => scope == RecipeFindingScope.All ? "All findings" : "Actionable findings";
    private static string ResultText(RecipeResult result) => string.Join(", ",
        Plural.Of(result.IncludedFindings, "finding"), Plural.Of(result.Problems, "problem"),
        Plural.Of(result.Warnings, "warning"), Plural.Of(result.IncompleteChecks, "incomplete check")) + $" · Checked {FormatTime(result.Checked)}";
    private static string StopText(RecipeStopReason reason) => reason switch
    {
        RecipeStopReason.AccessUnavailable => "Health checks are unavailable here.",
        RecipeStopReason.Cancelled => "You cancelled the run.",
        RecipeStopReason.RecipeChanged => "The recipe was changed or paused.",
        RecipeStopReason.Shutdown => "Aqua was closing.",
        RecipeStopReason.Timeout => "The run exceeded its time limit. Review and try again.",
        RecipeStopReason.CheckFailed => "Aqua could not complete the check or save its result. Review and try again.",
        RecipeStopReason.Interrupted => "Aqua closed before this run finished. Review before running it again.",
        _ => "",
    };
}
