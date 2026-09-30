using System.Reflection;
using System.Windows.Threading;
using AquaHub.Core.Updates;
using AquaHub.Core.Util;
using AquaHub.UI.ViewModels;
using Velopack;
using Velopack.Sources;

namespace AquaHub.Services;

public enum UpdateStage { Unavailable, Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Updates from the project's GitHub releases, through Velopack. About once a day, an installed copy asks GitHub for
/// the latest release and, if it's newer, only says so (an alert, once per version). Nothing is downloaded until the
/// user clicks Download in Settings › About; then "Restart now" installs it, or it installs itself the next time Aqua
/// Hub starts. Velopack checks each package against the SHA-256 in the release feed, swaps versions in one step and
/// never touches the profile. Source builds and snapshot runs aren't installed and never contact GitHub; in an E2E
/// session every action goes to the journal.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager? _manager;
    private readonly string[] _restartArgs;
    private DispatcherTimer? _timer;
    private UpdateInfo? _update;
    private VelopackAsset? _pending;
    private CancellationTokenSource? _download;
    private DateTimeOffset? _lastAttempt;
    private bool _lastFailed;

    /// <param name="simulated">E2E dry run: Check now is journalled instead of performed.</param>
    /// <param name="restartArgs">Arguments for the copy started after installing (the profile, if not the default).</param>
    public UpdateService(bool simulated, string[] restartArgs)
    {
        Simulated = simulated;
        _restartArgs = restartArgs;
        var assembly = typeof(UpdateService).Assembly;
        CurrentVersion = assembly.GetName().Version?.ToString(3) ?? "";
        Repository = UpdatePolicy.Repository(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value);
        Stage = simulated ? UpdateStage.Idle : UpdateStage.Unavailable;
        if (simulated || Repository is null) return;
        try
        {
            var manager = new UpdateManager(new GithubSource(Repository, null, false));
            if (!manager.IsInstalled) return;
            _manager = manager;
            if (manager.CurrentVersion is { } installed) CurrentVersion = installed.ToString();
            Stage = UpdateStage.Idle;
            // Downloaded earlier but not installed yet (normally it installs at start-up, before the app gets here).
            if (manager.UpdatePendingRestart is { } pending)
            {
                _pending = pending;
                NewVersion = pending.Version.ToString();
                Stage = UpdateStage.Ready;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("update", "The updater isn't available", ex);
        }
    }

    /// <summary>https://github.com/owner/repo for release builds (set when the release was built); null otherwise.</summary>
    public string? Repository { get; }

    /// <summary>Installed with Setup, so this copy can update itself.</summary>
    public bool IsInstalled => _manager is not null;

    public bool Simulated { get; }
    public string CurrentVersion { get; }
    public UpdateStage Stage { get; private set; }
    public string? NewVersion { get; private set; }
    public long DownloadSize { get; private set; }
    /// <summary>The new version's release notes, as plain text.</summary>
    public string Notes { get; private set; } = "";
    public int Percent { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Raised on the UI thread whenever the state above changes.</summary>
    public event Action? Changed;

    /// <summary>Automatic checks: the first a few minutes after start-up, then about once a day while Aqua runs.</summary>
    public void Start()
    {
        if (_manager is null) return;
        _timer = new DispatcherTimer { Interval = UpdatePolicy.FirstCheckDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            if (Hub.S.General.CheckForUpdates && Hub.State.Online && UpdatePolicy.CheckDue(_lastAttempt, _lastFailed, DateTimeOffset.Now))
                _ = CheckAsync(userAsked: false);
        };
        _timer.Start();
    }

    /// <summary>
    /// Asks GitHub whether there's a newer version. The automatic check raises an alert the first time it sees a
    /// version; nothing is downloaded either way.
    /// </summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready) return;
        if (Sandbox.Intercept("update-check", userAsked ? "user" : "automatic") || _manager is null) return;
        _lastAttempt = DateTimeOffset.Now;
        Error = null;
        Set(UpdateStage.Checking);
        try
        {
            var manager = _manager;
            var info = await Task.Run(() => manager.CheckForUpdatesAsync());
            _lastFailed = false;
            LastChecked = DateTimeOffset.Now;
            _update = info;
            if (info is null)
            {
                Set(UpdateStage.UpToDate);
                return;
            }
            var target = info.TargetFullRelease;
            NewVersion = target.Version.ToString();
            DownloadSize = target.Size;
            Notes = UpdatePolicy.Notes(target.NotesMarkdown);
            Log.Info("update", $"Aqua Hub {NewVersion} is available ({UpdatePolicy.Megabytes(DownloadSize)})");
            Set(UpdateStage.Available);
            if (!userAsked)
            {
                var alert = UpdatePolicy.Available(NewVersion, DownloadSize, DateTimeOffset.Now);
                Hub.Core.Context.RaiseAlert(alert, alert.Id);
            }
        }
        catch (Exception ex)
        {
            _lastFailed = true;
            Error = Short(ex);
            Log.Warn("update", "Update check failed", ex);
            Set(UpdateStage.Failed);
        }
    }

    /// <summary>Downloads the version the check found (the user asked). Cancellable; a failed download can be retried.</summary>
    public async Task DownloadAsync()
    {
        if (Stage != UpdateStage.Available || _update is not { } update || _manager is not { } manager) return;
        var cts = _download = new CancellationTokenSource();
        Percent = 0;
        Error = null;
        Set(UpdateStage.Downloading);
        try
        {
            await Task.Run(() => manager.DownloadUpdatesAsync(update, p => Hub.OnUi(() => Progress(p)), cts.Token));
            _pending = update.TargetFullRelease;
            Log.Info("update", $"Aqua Hub {NewVersion} downloaded; it installs on restart");
            Set(UpdateStage.Ready);
            var alert = UpdatePolicy.Ready(NewVersion ?? "", DateTimeOffset.Now);
            Hub.Core.Context.RaiseAlert(alert, alert.Id);
        }
        catch (OperationCanceledException)
        {
            Log.Info("update", "Update download cancelled");
            Set(UpdateStage.Available);
        }
        catch (Exception ex)
        {
            Error = Short(ex);
            Log.Warn("update", "Update download failed", ex);
            Set(UpdateStage.Available);
        }
        finally
        {
            _download = null;
            cts.Dispose();
        }
    }

    public void CancelDownload() => _download?.Cancel();

    /// <summary>Quits Aqua Hub; Velopack's updater waits for it to close, installs the update and starts it again.</summary>
    public void RestartToInstall()
    {
        if (Stage != UpdateStage.Ready || _pending is null || _manager is null) return;
        Log.Info("update", $"Restarting to install Aqua Hub {_pending.Version}");
        try
        {
            _manager.WaitExitThenApplyUpdates(_pending, silent: false, restart: true, _restartArgs);
        }
        catch (Exception ex)
        {
            Error = Short(ex);
            Log.Warn("update", "Could not start the updater", ex);
            Changed?.Invoke();
            return;
        }
        Hub.Quit();
    }

    /// <summary>One line for the Updates card (<paramref name="automatic"/>: the automatic-check switch as shown).</summary>
    public string Status(bool automatic) => Simulated
        ? "Checking for updates is simulated in this test session: nothing contacts GitHub."
        : Stage switch
        {
            UpdateStage.Unavailable => Repository is null
                ? "This copy was built from source, so it doesn't update itself. Copies installed with Setup, from the project's GitHub releases, check for new versions."
                : "This copy isn't installed (it runs from a folder), so it can't update itself. Install it with Setup from the GitHub releases to get updates.",
            UpdateStage.Idle => automatic
                ? "Aqua looks for a new version about once a day, and asks before downloading anything."
                : "Automatic checks are off. Check now looks for a new version.",
            UpdateStage.Checking => "Asking GitHub for the latest version…",
            UpdateStage.UpToDate => $"You have the latest version (checked at {Fmt.Clock(LastChecked ?? DateTimeOffset.Now, Hub.S.General.Use24Hour)}).",
            UpdateStage.Available => $"Aqua Hub {NewVersion} is available ({UpdatePolicy.Megabytes(DownloadSize)}). Download it now; it installs when you restart Aqua Hub."
                                     + (Error is null ? "" : $" The download didn't finish: {Error}."),
            UpdateStage.Downloading => $"Downloading Aqua Hub {NewVersion}… {Percent}%",
            UpdateStage.Ready => $"Aqua Hub {NewVersion} is downloaded. Restart now to install it, or it installs itself the next time Aqua Hub starts."
                                 + (Error is null ? "" : $" The updater didn't start: {Error}."),
            UpdateStage.Failed => $"Couldn't check for updates: {Error}. Aqua tries again later.",
            _ => "",
        };

    private void Progress(int percent)
    {
        if (Stage != UpdateStage.Downloading || percent == Percent) return;
        Percent = Math.Clamp(percent, 0, 100);
        Changed?.Invoke();
    }

    private void Set(UpdateStage stage)
    {
        Stage = stage;
        Changed?.Invoke();
    }

    /// <summary>The first line of an error, kept short for the card (the full one is in the log).</summary>
    private static string Short(Exception ex)
    {
        var inner = ex is AggregateException { InnerException: { } i } ? i : ex;
        var line = inner.Message.Split('\n')[0].Trim().TrimEnd('.');
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
