using AquaHub.Core.Reminders;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;

namespace AquaHub.Services;

internal sealed class ReminderSceneReview
{
    internal Scene Scene { get; }
    internal HubSettings Settings { get; }
    internal IReadOnlyList<string> Steps { get; }
    private readonly string _version;

    internal ReminderSceneReview(Scene scene, HubSettings settings)
    {
        Scene = scene;
        Settings = settings;
        Steps = scene.Steps.Select(step => DescribeStep(step, settings)).ToArray();
        _version = Version(scene, settings);
    }

    internal bool IsCurrent(HubSettings settings) =>
        FindScene(settings, Scene.Id) is { } scene && Version(scene, settings) == _version;

    internal static Scene? FindScene(HubSettings settings, string? id)
    {
        if (id is null) return null;
        var matches = settings.Scenes.Where(scene => scene.Id == id).Take(2).ToArray();
        return matches is [{ Steps.Count: > 0 } scene] ? scene : null;
    }

    private static AppEntry? TargetApp(SceneStep step, HubSettings settings) => step.Action switch
    {
        "launch" or "close" or "focus" => settings.Apps.FirstOrDefault(app => app.Id == step.Target),
        "music-search" => settings.Apps.FirstOrDefault(app => app.IsMusicPlayer),
        "media" when step.Value == "play" => settings.Apps.FirstOrDefault(app => app.IsMusicPlayer),
        _ => null,
    };

    private static string Version(Scene scene, HubSettings settings) =>
        JsonUtil.Serialize(new { scene.Name, scene.Steps, Targets = scene.Steps.Select(step => TargetApp(step, settings)) });

    private static string DescribeStep(SceneStep step, HubSettings settings)
    {
        var description = ActionExecutor.Describe(step, settings);
        var app = TargetApp(step, settings);
        return step.Action switch
        {
            "launch" when app is not null => $"{description} · {app.Target} {app.Args}".TrimEnd(),
            "close" or "focus" when app is not null => $"{description} · {AppLauncher.ProcessNameOf(app)}",
            "launch" or "close" or "focus" => $"{description} · app unavailable",
            _ => description,
        };
    }
}

public sealed partial class ActionExecutor
{
    private CancellationTokenSource? _reminderSceneCancellation;
    private Task<bool>? _reminderSceneRun;
    private bool _stopReminderScenes;

    internal bool ReminderSceneRunning => _reminderSceneRun is { IsCompleted: false };

    internal Task<bool> RunReminderSceneAsync(ReminderService reminders, ReminderRow row, ReminderSceneReview review)
    {
        Hub.Ui.VerifyAccess();
        if (_stopReminderScenes || Hub.Quitting) throw new OperationCanceledException();
        if (ReminderSceneRunning) throw new ReminderException("A reminder scene is already running.");
        return _reminderSceneRun = RunReviewedSceneAsync(reminders, row, review);
    }

    private async Task<bool> RunReviewedSceneAsync(ReminderService reminders, ReminderRow row, ReminderSceneReview review)
    {
        using var cancellation = new CancellationTokenSource();
        _reminderSceneCancellation = cancellation;
        void EnsureCurrent()
        {
            if (row.Reminder.Draft.SceneId != review.Scene.Id || !review.IsCurrent(Hub.S))
                throw new ReminderException("The saved scene changed or was removed. Review its current steps.");
        }

        try
        {
            EnsureCurrent();
            await reminders.ClaimSceneRunAsync(row, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!review.IsCurrent(Hub.S))
                throw new ReminderException("The scene changed before it could start. Review and run it from Launchpad.");

            bool StillDue() => reminders.Snapshot.Items.Any(item =>
                item.Id == row.Id && item.ScheduleId == row.Reminder.ScheduleId && item.Draft.SceneId == review.Scene.Id
                && item.Pending is { SceneRunRequested: true } pending
                && pending.Nominal == row.Occurrence.Nominal && pending.Delivery == row.Occurrence.Delivery);

            return await RunSceneAsync(review.Scene, review.Settings, progress: null, cancellation.Token, StillDue);
        }
        finally { _reminderSceneCancellation = null; }
    }

    internal void CancelReminderScene() => _reminderSceneCancellation?.Cancel();

    internal async Task StopReminderSceneAsync()
    {
        _stopReminderScenes = true;
        CancelReminderScene();
        if (_reminderSceneRun is not { } run) return;
        try { await run; }
        catch (OperationCanceledException) { }
        catch (ReminderException) { }
        catch (Exception ex) { Log.Warn("scene", "Could not join the reminder scene", ex); }
    }
}
