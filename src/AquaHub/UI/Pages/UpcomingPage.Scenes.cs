using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AquaHub.Core.Reminders;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.UI.Pages;

public partial class UpcomingPage
{
    private sealed record SceneChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
    private (ReminderRow Row, ReminderSceneReview Review, Button Entry)? _sceneReview;

    private void LoadSceneChoices(string? currentId)
    {
        var choices = new List<SceneChoice> { new(null, "No scene") };
        choices.AddRange(Hub.S.Scenes.GroupBy(scene => scene.Id)
            .Where(group => group.Count() == 1 && group.First().Steps.Count > 0)
            .Select(group => new SceneChoice(group.Key, group.First().Name))
            .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase));
        var current = choices.Find(choice => choice.Id == currentId);
        if (current is null)
        {
            current = new(currentId, "Unavailable scene");
            choices.Add(current);
        }
        ReminderSceneBox.ItemsSource = choices;
        ReminderSceneBox.SelectedItem = current;
    }

    private void OnReviewScene(object sender, RoutedEventArgs e)
    {
        if (_busy || ReminderEditor.Visibility == Visibility.Visible
            || sender is not Button { DataContext: AgendaEntry { Reminder: { CanRunScene: true } row } } button) return;

        var settings = Hub.S;
        if (ReminderSceneReview.FindScene(settings, row.Reminder.Draft.SceneId) is not { } scene)
        {
            _commandError = "This scene is unavailable. Edit the reminder to choose another scene.";
            RefreshAgenda();
            return;
        }
        var review = new ReminderSceneReview(scene, settings);
        _sceneReview = (row, review, button);
        SceneReviewTitle.Text = $"Run {scene.Name}?";
        SceneReviewSteps.ItemsSource = review.Steps;
        SceneReviewPanel.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() =>
        {
            if (_sceneReview is null) return;
            SceneReviewPanel.BringIntoView();
            CancelSceneReview.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void CloseSceneReview(bool focus)
    {
        var entry = _sceneReview?.Entry;
        _sceneReview = null;
        SceneReviewSteps.ItemsSource = null;
        SceneReviewPanel.Visibility = Visibility.Collapsed;
        if (focus) (entry is { IsVisible: true } ? entry : AddReminder).Focus();
    }

    private void OnCancelSceneReview(object sender, RoutedEventArgs e) => CloseSceneReview(focus: true);

    private void OnSceneReviewKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseSceneReview(focus: true);
        e.Handled = true;
    }

    private async void OnConfirmScene(object sender, RoutedEventArgs e)
    {
        if (_sceneReview is not { } confirmed || _visible is null) return;
        if (Hub.Actions.ReminderSceneRunning)
        {
            SetStatus(SceneRunStatus, "A scene is already running.");
            return;
        }
        CloseSceneReview(focus: false);
        SetStatus(SceneRunStatus, $"Running {confirmed.Review.Scene.Name}…");
        CancelSceneRun.IsEnabled = true;
        try
        {
            var running = Hub.Actions.RunReminderSceneAsync(_reminders, confirmed.Row, confirmed.Review);
            RefreshAgenda();
            CancelSceneRun.Focus();
            var success = await running;
            SetStatus(SceneRunStatus, success ? "Scene run finished." : "Scene run finished with one or more unsuccessful steps.");
        }
        catch (OperationCanceledException) { SetStatus(SceneRunStatus, "Scene run stopped."); }
        catch (ReminderException ex) { SetStatus(SceneRunStatus, ex.Message); }
        catch (Exception ex)
        {
            Log.Warn("scene", "Reminder scene run failed", ex);
            SetStatus(SceneRunStatus, "Aqua couldn’t run the scene.");
        }
        finally
        {
            RefreshAgenda();
            if (_visible is not null && (Keyboard.FocusedElement is null || CancelSceneRun.IsKeyboardFocused)) AddReminder.Focus();
        }
    }

    private void OnStopScene(object sender, RoutedEventArgs e)
    {
        CancelSceneRun.IsEnabled = false;
        SetStatus(SceneRunStatus, "Stopping scene…");
        Hub.Actions.CancelReminderScene();
    }
}
