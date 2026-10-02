using System.Reflection;
using System.Windows.Threading;
using AquaHub.Core.Updates;
using AquaHub.Core.Util;
using AquaHub.UI.ViewModels;
using Velopack;
using Velopack.Sources;

namespace AquaHub.Services;

/// <summary>
/// Updates from the project's GitHub releases, through Velopack. About once a day, an installed copy asks GitHub for
/// the latest release and, if it's newer, only says so (an alert per version). Nothing is downloaded until the user
/// clicks Download in Settings › About; then "Restart now" installs it, or it installs itself the next time Aqua Hub
/// starts. Velopack checks each package against the SHA-256 in the release feed, swaps versions in one step and never
/// touches the profile. The steps themselves are <see cref="UpdateMachine"/> (Core, unit-tested); this class adds
/// Velopack, the daily timer and the card's wording. Source builds and snapshot runs aren't installed and never
/// contact GitHub; in an E2E session every action goes to the journal.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateMachine _machine;
    private readonly string[] _restartArgs;
    private DispatcherTimer? _timer;

    /// <param name="simulated">E2E dry run: Check now is journalled instead of performed.</param>
    /// <param name="restartArgs">Arguments for the copy started after installing (the profile, if not the default).</param>
    /// <param name="demo">"available" or "ready": a pretend release instead of GitHub's (<see cref="DemoAllowed"/> only).</param>
    public UpdateService(bool simulated, string[] restartArgs, string? demo = null)
    {
        _restartArgs = restartArgs;
        var assembly = typeof(UpdateService).Assembly;
        CurrentVersion = assembly.GetName().Version?.ToString(3) ?? "";
        Repository = UpdatePolicy.Repository(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value);
        IUpdater? source = null;
        if (demo is not null && DemoAllowed)
        {
            Demo = true;
            source = new DemoUpdater(ready: demo == "ready");
            Log.Info("update", $"Pretend update ({demo}): nothing is downloaded or installed");
        }
        else Simulated = simulated;
        if (!Demo && !simulated && Repository is not null)
        {
            try
            {
                var manager = new UpdateManager(new GithubSource(Repository, null, false));
                if (manager.IsInstalled)
                {
                    source = new VelopackSource(manager);
                    IsInstalled = !manager.IsPortable;
                    if (manager.CurrentVersion is { } installed) CurrentVersion = installed.ToString();
                }
            }
            catch (Exception ex)
            {
                StartError = UpdateMachine.Describe(ex);
                Log.Warn("update", "The updater isn't available", ex);
            }
        }
        _machine = new UpdateMachine(source, Announce, a => Hub.OnUi(a));
        _machine.Changed += () => Changed?.Invoke();
    }

    /// <summary>https://github.com/owner/repo for release builds (set when the release was built); null otherwise.</summary>
    public string? Repository { get; }

    /// <summary>
    /// Installed with Setup, so this copy owns the Start with Windows entry. A portable copy updates itself too, but
    /// it can be moved or deleted, so it never takes the entry over.
    /// </summary>
    public bool IsInstalled { get; }

    public bool Simulated { get; }
    /// <summary>The pretend release (--demo-update) stands in for GitHub's.</summary>
    public bool Demo { get; }

    /// <summary>
    /// The pretend release is for trying the banner and the card: in Debug builds, and in E2E dry runs (which never
    /// install anything anyway), never in a copy people use.
    /// </summary>
#if DEBUG
    public static bool DemoAllowed => true;
#else
    public static bool DemoAllowed => Sandbox.Enabled;
#endif

    public string CurrentVersion { get; }
    /// <summary>Set when an installed copy's updater couldn't start (rather than this copy not being installed).</summary>
    public string? StartError { get; }

    /// <summary>How this copy came to be on the PC, as the diagnostics summary reports it.</summary>
    public string InstallKind =>
        Demo ? "pretend release (--demo-update)"
        : Simulated ? "test session (updates simulated)"
        : StartError is not null ? "installed, but its updater couldn't start"
        : _machine.Stage == UpdateStage.Unavailable ? (Repository is null ? "built from source" : "a folder copy (not installed)")
        : IsInstalled ? "installed with Setup" : "portable (updates itself)";
    public UpdateStage Stage => Simulated ? UpdateStage.Idle : _machine.Stage;
    public string? NewVersion => _machine.NewVersion;
    public long DownloadSize => _machine.DownloadSize;
    /// <summary>The new version's release notes, as plain text.</summary>
    public string Notes => _machine.Notes;
    public int Percent => _machine.Percent;
    public string? Error => _machine.Error;
    public DateTimeOffset? LastChecked => _machine.LastChecked;

    /// <summary>Raised on the UI thread whenever the state above changes.</summary>
    public event Action? Changed;

    /// <summary>Automatic checks: the first a few minutes after start-up, then about once a day while Aqua runs.</summary>
    public void Start()
    {
        // After an update the "available" and "ready" alerts for this version would still be in the bell, unread.
        try { Hub.State.RemoveAlerts(a => UpdatePolicy.IsSettled(a, CurrentVersion)); }
        catch (Exception ex) { Log.Warn("update", "Couldn't tidy the update alerts", ex); }
        if (Simulated || _machine.Stage == UpdateStage.Unavailable) return;
        if (Demo)
        {
            // The pretend release turns up straight away, as an automatic check would find it (with its alert).
            if (_machine.Stage == UpdateStage.Idle) _ = _machine.CheckAsync(userAsked: false);
            return;
        }
        _timer = new DispatcherTimer { Interval = UpdatePolicy.FirstCheckDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            if (Hub.S.General.CheckForUpdates && Hub.State.Online && UpdatePolicy.CheckDue(_machine.LastAttempt, _machine.LastFailed, DateTimeOffset.Now))
                _ = _machine.CheckAsync(userAsked: false);
        };
        _timer.Start();
    }

    /// <summary>Asks GitHub whether there's a newer version (the automatic check also raises an alert for it).</summary>
    public Task CheckAsync(bool userAsked) =>
        !Demo && Sandbox.Intercept("update-check", userAsked ? "user" : "automatic") ? Task.CompletedTask : _machine.CheckAsync(userAsked);

    /// <summary>Downloads the version the check found (the user asked). Cancellable; a failed download can be retried.</summary>
    public Task DownloadAsync() => _machine.DownloadAsync();

    public void CancelDownload() => _machine.CancelDownload();

    /// <summary>
    /// At start-up, in the first instance only: a version downloaded earlier installs now. True when the updater has
    /// started and Aqua Hub must exit straight away; false to carry on as usual (nothing pending, or it didn't start).
    /// </summary>
    /// <param name="background">Started by Start with Windows: the updater stays quiet and restarts us in the tray.</param>
    public bool ApplyPendingAtStartup(bool background) =>
        !Simulated && !Demo && _machine.ApplyAndRestart(silent: background, background ? _restartArgs.Append("--background").ToArray() : _restartArgs);

    /// <summary>Quits Aqua Hub; Velopack's updater waits for it to close, installs the update and starts it again.</summary>
    public void RestartToInstall()
    {
        if (!Simulated && _machine.ApplyAndRestart(silent: false, _restartArgs)) Hub.Quit();
    }

    /// <summary>One line for the Updates card (<paramref name="automatic"/>: the automatic-check switch as shown).</summary>
    public string Status(bool automatic) => Simulated
        ? "Checking for updates is simulated in this test session: nothing contacts GitHub."
        : Stage switch
        {
            UpdateStage.Unavailable => StartError is not null
                ? $"This copy is installed, but its updater couldn't start ({StartError}). Reinstalling it with Setup fixes that."
                : Repository is null
                    ? "This copy was built from source, so it doesn't update itself. Copies installed with Setup do."
                    : "This copy runs from a folder, so it can't update itself. Install it with Setup for updates.",
            UpdateStage.Idle => automatic
                ? "Aqua checks for a new version about once a day."
                : "Automatic checks are off. Check now looks for a new version.",
            UpdateStage.Checking => "Asking GitHub for the latest version…",
            UpdateStage.UpToDate => $"You have the latest version (checked at {Fmt.Clock(LastChecked ?? DateTimeOffset.Now, Hub.S.General.Use24Hour)}).",
            UpdateStage.Available => $"Aqua Hub {NewVersion} is available ({UpdatePolicy.Megabytes(DownloadSize)})."
                                     + (Error is null ? "" : $" The download didn't finish: {Error}."),
            UpdateStage.Downloading => $"Downloading Aqua Hub {NewVersion}… {Percent}%",
            UpdateStage.Ready => $"Aqua Hub {NewVersion} is ready. Restart now to install it, or it installs at the next start."
                                 + (Error is null ? "" : $" The updater didn't start: {Error}."),
            UpdateStage.Failed => $"Couldn't check for updates: {Error}." + (automatic ? " Aqua tries again later." : ""),
            _ => "",
        };

    private static void Announce(Core.Models.HubAlert alert) => Hub.Core.Context.RaiseAlert(alert, alert.Id);

    /// <summary>
    /// A pretend release (--demo-update) for trying the banner, the progress and Restart now: it "downloads" in a few
    /// seconds and never installs anything (Restart now says so; in an E2E dry run the click goes to the journal).
    /// </summary>
    private sealed class DemoUpdater(bool ready) : IUpdater
    {
        private static readonly UpdateOffer Offer = new("9.9.9", 96L * 1024 * 1024,
            "- A pretend release, for trying out how updates look.\n- Nothing is downloaded or installed.");

        public UpdateOffer? Pending { get; } = ready ? Offer : null;

        public Task<UpdateOffer?> CheckAsync() => Task.FromResult<UpdateOffer?>(Offer);

        public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
        {
            for (var p = 0; p <= 100; p += 4)
            {
                progress(p);
                await Task.Delay(120, ct);
            }
        }

        public void ApplyAndRestart(UpdateOffer offer, bool silent, string[] restartArgs)
        {
            Sandbox.Record("update-restart", offer.Version);
            throw new InvalidOperationException("this is a pretend update, so there's nothing to install");
        }
    }

    /// <summary>Velopack behind <see cref="IUpdater"/>; it keeps Velopack's own objects for what it last reported.</summary>
    private sealed class VelopackSource(UpdateManager manager) : IUpdater
    {
        private UpdateInfo? _info;
        private VelopackAsset? _downloaded = manager.UpdatePendingRestart;

        public UpdateOffer? Pending => _downloaded is { } d ? new UpdateOffer(d.Version.ToString(), d.Size, d.NotesMarkdown) : null;

        public async Task<UpdateOffer?> CheckAsync()
        {
            var info = await Task.Run(() => manager.CheckForUpdatesAsync());
            _info = info;
            return info?.TargetFullRelease is { } t ? new UpdateOffer(t.Version.ToString(), t.Size, t.NotesMarkdown) : null;
        }

        public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
        {
            var info = _info ?? throw new InvalidOperationException("Nothing to download: check for updates first.");
            await Task.Run(() => manager.DownloadUpdatesAsync(info, progress, ct), ct);
            _downloaded = info.TargetFullRelease;
        }

        public void ApplyAndRestart(UpdateOffer offer, bool silent, string[] restartArgs) =>
            manager.WaitExitThenApplyUpdates(_downloaded ?? throw new InvalidOperationException("Nothing has been downloaded."), silent, restart: true, restartArgs);
    }
}
