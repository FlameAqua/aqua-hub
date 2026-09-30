using System.Windows.Threading;
using AquaHub.Core;
using AquaHub.Core.Agents;
using AquaHub.Core.Settings;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>Application-wide service registry (composition happens once in <see cref="App"/>).</summary>
public static class Hub
{
    public static HubCore Core { get; set; } = null!;
    public static PlatformServices Platform { get; set; } = null!;
    public static TrayIcon Tray { get; set; } = null!;
    public static HotkeyManager Hotkeys { get; set; } = null!;
    public static SystemMonitor System { get; set; } = null!;
    public static MediaController Media { get; set; } = null!;
    public static VolumeController Volume { get; set; } = null!;
    public static AppCatalog Catalog { get; set; } = null!;
    public static AppLauncher Launcher { get; set; } = null!;
    public static SpeechService Speech { get; set; } = null!;
    public static ThemeService Theme { get; set; } = null!;
    public static ActionExecutor Actions { get; set; } = null!;
    public static WindowManager Windows { get; set; } = null!;
    public static AskSession Ask { get; set; } = null!;
    public static OllamaManager Ollama { get; set; } = null!;
    public static UpdateService Updates { get; set; } = null!;
    public static HealthService Health { get; } = new();
    public static Dispatcher Ui { get; set; } = null!;
    public static bool SnapshotMode { get; set; }

    /// <summary>True once the app is exiting (windows closing now must not say "still running").</summary>
    public static bool Quitting { get; private set; }

    public static void Quit()
    {
        Quitting = true;
        global::System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Windows is signing out or shutting down: the app will exit without an explicit Quit.</summary>
    public static void MarkQuitting() => Quitting = true;

    public static HubState State => Core.State;
    public static HubSettings S => Core.Settings.Current;

    /// <summary>Runs on the UI thread (fire-and-forget) — safe to call from any thread.</summary>
    public static void OnUi(Action action)
    {
        if (Ui.CheckAccess()) action();
        else Ui.BeginInvoke(action, DispatcherPriority.Background);
    }
}

/// <summary>Coalesces bursts of change notifications into one UI refresh.</summary>
public sealed class UiThrottle
{
    private readonly Action _action;
    private readonly DispatcherTimer _timer;

    public UiThrottle(Action action, int milliseconds = 120)
    {
        _action = action;
        _timer = new DispatcherTimer(DispatcherPriority.Background, Hub.Ui) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _action();
        };
    }

    public void Request()
    {
        if (Hub.Ui.CheckAccess()) Restart();
        else Hub.Ui.BeginInvoke(Restart, DispatcherPriority.Background);
    }

    private void Restart()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
