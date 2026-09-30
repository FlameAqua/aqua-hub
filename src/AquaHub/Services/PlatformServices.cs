using System.Globalization;
using AquaHub.Core.Agents;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>Windows implementation of the core's platform hooks, including notification policy.</summary>
public sealed class PlatformServices : IPlatform
{
    private readonly Func<HubSettings> _settings;
    private TrayIcon? _tray;
    private bool _fullscreen;
    private DateTime _fullscreenChecked = DateTime.MinValue;

    public PlatformServices(Func<HubSettings> settings) => _settings = settings;

    public void AttachTray(TrayIcon tray) => _tray = tray;

    /// <summary>Raised on any thread when an alert is produced (the UI shows an in-app banner).</summary>
    public event Action<HubAlert>? AlertRaised;

    /// <summary>Raised when the user clicks a notification that showed an alert; null for a summary of several.</summary>
    public event Action<HubAlert?>? ToastClicked;

    public bool IsFullscreenBusy
    {
        get
        {
            if (DateTime.UtcNow - _fullscreenChecked > TimeSpan.FromSeconds(4))
            {
                _fullscreen = OsSignals.IsFullscreenBusy();
                _fullscreenChecked = DateTime.UtcNow;
            }
            return _fullscreen;
        }
    }

    public bool IsUserIdle => OsSignals.IdleTime() > TimeSpan.FromMinutes(10);
    public bool IsOnBattery => OsSignals.IsOnBattery();
    public bool IsUiVisible { get; set; }

    private readonly List<HubAlert> _held = new();
    private string _holdReason = "";

    public void Notify(HubAlert alert)
    {
        try { AlertRaised?.Invoke(alert); } catch { }
        switch (Decide(alert, out var reason))
        {
            case ToastDecision.Show:
                _tray?.Notify(alert.Title, alert.Body, quiet: alert.Severity < AlertSeverity.Important, onClick: () => ToastClicked?.Invoke(alert));
                break;
            case ToastDecision.Hold:
                // Quiet hours / full-screen: keep it and deliver one summary afterwards (it's in the bell meanwhile).
                lock (_held)
                {
                    if (_held.Count < 50) _held.Add(alert);
                    _holdReason = reason;
                }
                break;
        }
    }

    /// <summary>Called on every system sample: once quiet hours or the game end, deliver one summary of what was held.</summary>
    public void FlushHeld()
    {
        List<HubAlert> held;
        string reason;
        lock (_held)
        {
            if (_held.Count == 0) return;
            var n = _settings().Notifications;
            if (NotificationPolicy.InQuietHours(n, DateTime.Now) || (n.SuppressWhenFullscreen && IsFullscreenBusy)) return;
            held = _held.ToList();
            reason = _holdReason;
            _held.Clear();
        }
        var n2 = _settings().Notifications;
        if (!n2.Enabled || n2.DoNotDisturb) return;
        var single = held.Count == 1 ? held[0] : null;
        var (title, body) = NotificationPolicy.Summary(held, reason);
        _tray?.Notify(title, body, quiet: true, onClick: () => ToastClicked?.Invoke(single));
    }

    public bool ShouldToast(HubAlert alert) => Decide(alert, out _) == ToastDecision.Show;

    public ToastDecision Decide(HubAlert alert, out string holdReason) =>
        NotificationPolicy.Decide(_settings().Notifications, alert, DateTime.Now, IsFullscreenBusy, out holdReason);

    private readonly Queue<double> _gameGpu = new();
    private readonly Queue<(DateTime At, double Load)> _otherGpu = new();

    /// <summary>Feed GPU utilisation samples taken while our own model is idle (i.e. load from other apps/games).</summary>
    public void RecordGpu(double utilisation, bool ownModelBusy)
    {
        FlushHeld();
        if (ownModelBusy) return;
        lock (_gameGpu)
        {
            _gameGpu.Enqueue(utilisation);
            while (_gameGpu.Count > 6) _gameGpu.Dequeue();
            var now = DateTime.UtcNow;
            _otherGpu.Enqueue((now, utilisation));
            while (_otherGpu.Count > 0 && now - _otherGpu.Peek().At > TimeSpan.FromSeconds(75)) _otherGpu.Dequeue();
        }
    }

    /// <summary>
    /// Something else needs the GPU (null = nothing): a full-screen game with the GPU busy, or — when the model
    /// server has no model loaded, so the load can't be the server answering another app — any app keeping the GPU
    /// above 60% for the last minute. Used to stop the local model server.
    /// </summary>
    public string? GpuContention(bool serverHasModelLoaded)
    {
        List<double> recent;
        lock (_gameGpu) recent = _otherGpu.Where(x => DateTime.UtcNow - x.At <= TimeSpan.FromSeconds(65)).Select(x => x.Load).ToList();
        if (IsFullscreenBusy && recent.Count > 0 && recent.Average() >= 40) return "a full-screen game";
        if (!serverHasModelLoaded && recent.Count >= 3 && recent.Min() >= 60) return "another app using the GPU";
        return null;
    }

    /// <summary>
    /// Why background AI is paused right now (null = free to run). Steps aside only when a full-screen
    /// game/presentation is running AND the GPU is genuinely busy — a light game or a paused one doesn't block briefs.
    /// </summary>
    public string? AiPauseReason()
    {
        if (!IsFullscreenBusy) return null;
        double avg;
        lock (_gameGpu) avg = _gameGpu.Count == 0 ? 100 : _gameGpu.Average();
        return avg >= 55 ? "a full-screen game is using the GPU" : null;
    }
}
