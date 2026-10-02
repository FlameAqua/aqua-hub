using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AquaHub.Core.Models;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.UI.Shell;

namespace AquaHub.Services;

/// <summary>
/// Owns the main window, taskbar flyout and command palette. The dashboard and quick panel are created on first
/// use and then hidden rather than destroyed: WPF keeps a closed window with a data-bound page alive through an
/// internal handle, so destroying and recreating them grew memory with every open/close. One instance each keeps
/// memory bounded, makes reopening instant, and the idle trim still hands their working set back to Windows.
/// </summary>
public sealed class WindowManager
{
    private MainWindow? _main;
    private FlyoutWindow? _flyout;
    private CommandPalette? _palette;
    private DispatcherTimer? _trim;

    public MainWindow? Main => _main;
    public bool MainVisible => _main is { IsVisible: true } m && m.WindowState != WindowState.Minimized;

    public void ShowMain(string? page = null, string? arg = null)
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closing += (_, e) =>
            {
                if (Hub.Quitting || !Hub.S.General.CloseToTray || Hub.SnapshotMode) return;
                // Close to the tray: hide and keep the window for next time.
                e.Cancel = true;
                _main.Hide();
                ScheduleTrim();
                TrayHint.ShowOnce();
            };
            _main.Closed += (_, _) =>
            {
                _main = null;
                // Only reached when quitting, or with Settings → General → "Closing the window keeps Aqua Hub
                // running" turned off, in which case closing the window quits.
                if (Hub.Quitting || Hub.SnapshotMode) return;
                Log.Info("app", "Window closed with close-to-tray off — quitting");
                Hub.Quit();
            };
        }
        HideFlyout();
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        WindowEffects.ForceForeground(new WindowInteropHelper(_main).Handle);
        if (page is not null) _main.Navigate(page, arg);
        UpdateVisibility();
    }

    public void ShowAlerts()
    {
        ShowMain();
        // Let a freshly created window lay out first so the list opens under the bell.
        _main?.Dispatcher.BeginInvoke(() => _main?.ShowAlerts(), DispatcherPriority.Loaded);
    }

    public void ToggleFlyout() => ToggleFlyout(fromTrayClick: false);

    /// <summary>Opens or closes the quick panel; see <see cref="FlyoutWindow.ShowAnimated"/> for tray clicks.</summary>
    public void ToggleFlyout(bool fromTrayClick)
    {
        if (_flyout is { IsVisible: true }) HideFlyout();
        else ShowFlyout(fromTrayClick);
    }

    public void ShowFlyout(bool fromTrayClick = false)
    {
        _flyout ??= new FlyoutWindow();
        _flyout.ShowAnimated(fromTrayClick);
        UpdateVisibility();
    }

    public void HideFlyout()
    {
        if (_flyout is { IsVisible: true }) _flyout.HideAnimated();
    }

    public void ShowPalette(Window? owner = null)
    {
        if (_palette is { IsVisible: true }) { _palette.Activate(); return; }
        _palette = new CommandPalette();
        var created = _palette;
        created.Closed += (_, _) =>
        {
            if (ReferenceEquals(_palette, created)) _palette = null;
            UpdateVisibility();
        };
        HideFlyout();
        created.ShowNear(owner);
        UpdateVisibility();
    }

    public void TogglePalette()
    {
        if (_palette is { IsVisible: true }) _palette.Hide();
        else ShowPalette(MainVisible ? _main : null);
    }

    /// <summary>
    /// Opens what an alert is about and marks it read, wherever it was clicked: the bell's list, a banner or a Windows
    /// notification. Null is a notification that summarised several alerts; the bell's list opens instead.
    /// </summary>
    public void OpenAlert(HubAlert? alert)
    {
        if (alert is null) { ShowAlerts(); return; }
        _main?.CloseAlerts();
        if (!alert.Read) Hub.State.MarkAlertRead(alert.Id);
        var target = alert.Target ?? "";
        var parts = target.Split(':', 2);
        switch (parts[0])
        {
            case "markets": ShowMain("markets", parts.Length > 1 ? parts[1] : null); break;
            case "news": ShowMain("news", parts.Length > 1 ? parts[1] : null); break;
            case "brief": ShowMain("today"); break;
            case "upcoming": ShowMain("upcoming"); break;
            case "system": ShowMain("system"); break;
            case "settings": ShowMain("settings", parts.Length > 1 ? parts[1] : null); break;
            default:
                if (!AppLauncher.OpenUrl(alert.Url)) ShowMain();
                break;
        }
    }

    public void UpdateVisibility()
    {
        var visible = MainVisible || _flyout is { IsVisible: true } || _palette is { IsVisible: true };
        if (Hub.Platform.IsUiVisible && !visible) _hiddenSince = DateTime.UtcNow;
        Hub.Platform.IsUiVisible = visible;
        var detailed = MainVisible && _main!.CurrentPage is "system";
        // Per-process sampling only while This PC is on screen — and again as soon as it is: bringing the window back
        // doesn't re-open the page, so switching it back on can't be left to the page. (Snapshots draw off screen.)
        Hub.System.DetailedProcesses = detailed || Hub.SnapshotMode;
        Hub.System.SetInterval(visible ? TimeSpan.FromSeconds(detailed ? 1.5 : 2.5) : TimeSpan.FromSeconds(20));
        if (visible) _trim?.Stop();
    }

    /// <summary>After the UI closes, give memory back to Windows.</summary>
    private void ScheduleTrim()
    {
        _trim ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _trim.Tick -= OnTrim;
        _trim.Tick += OnTrim;
        _trim.Start();
    }

    private void OnTrim(object? sender, EventArgs e)
    {
        _trim?.Stop();
        if (Hub.Platform.IsUiVisible) return;
        Trim("UI hidden");
    }

    private DispatcherTimer? _idle;
    private DateTime _hiddenSince = DateTime.UtcNow;
    private long _allocatedAtTrim;

    /// <summary>
    /// Agents allocate in bursts (feeds, JSON, model output). While nothing is on screen, hand that memory back
    /// to Windows a couple of minutes after each burst instead of letting the GC keep it for the next one.
    /// </summary>
    public void StartIdleTrim()
    {
        _idle ??= new DispatcherTimer(TimeSpan.FromSeconds(150), DispatcherPriority.Background, (_, _) =>
        {
            if (Hub.Platform.IsUiVisible || DateTime.UtcNow - _hiddenSince < TimeSpan.FromSeconds(60)) return;
            if (GC.GetTotalAllocatedBytes() - _allocatedAtTrim < 4 * 1048576) return;
            Trim("idle");
        }, Hub.Ui);
        _idle.Start();
    }

    private void Trim(string reason)
    {
        OsSignals.TrimMemory();
        _allocatedAtTrim = GC.GetTotalAllocatedBytes();
        Log.Info("memory", $"{reason}: {MemoryStats.Current()}");
    }
}

/// <summary>Explains once that closing the window keeps Aqua Hub running in the tray.</summary>
public static class TrayHint
{
    public static void ShowOnce()
    {
        if (Hub.Core.Db.TryMarkSeen("hint:tray"))
            Hub.Tray?.Notify("Aqua Hub is still running", Hub.S.General.HotkeyFlyout is { Length: > 0 } hotkey
                ? $"Your agents keep working in the background. Click the tray icon or press {hotkey} for the quick panel."
                : "Your agents keep working in the background. Click the tray icon for the quick panel.", quiet: true);
    }
}
