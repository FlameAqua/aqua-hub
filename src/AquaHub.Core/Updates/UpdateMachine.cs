using System.Net;
using AquaHub.Core.Models;
using AquaHub.Core.Util;

namespace AquaHub.Core.Updates;

public enum UpdateStage { Unavailable, Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>A version on offer: what a check found, or what was downloaded earlier and not installed yet.</summary>
public sealed record UpdateOffer(string Version, long Size, string? NotesMarkdown = null);

/// <summary>The updater <see cref="UpdateMachine"/> drives: Velopack in the app, a fake in the tests.</summary>
public interface IUpdater
{
    /// <summary>A version downloaded earlier and not installed yet, if any.</summary>
    UpdateOffer? Pending { get; }

    /// <summary>The newest release if it's newer than this copy; null when this copy is up to date.</summary>
    Task<UpdateOffer?> CheckAsync();

    /// <summary>Downloads (and verifies) the offer from the last check, reporting 0–100.</summary>
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct);

    /// <summary>Starts the updater, which waits for this process to exit, installs the download and starts Aqua again.</summary>
    void ApplyAndRestart(UpdateOffer offer, bool silent, string[] restartArgs);
}

/// <summary>
/// The update flow, apart from Velopack and the UI so every step can be tested: a check (an alert for a new version
/// when it was automatic), a download only when asked (progress, cancel, retry), then restart to install, or install
/// at the next start. Nothing downloads or installs without being asked.
/// </summary>
public sealed class UpdateMachine
{
    private readonly IUpdater? _source;
    private readonly Action<HubAlert> _announce;
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _now;
    private UpdateOffer? _offer;
    private UpdateOffer? _pending;
    private CancellationTokenSource? _download;
    private bool _applying;

    /// <param name="source">Null when this copy can't update itself (not installed).</param>
    /// <param name="announce">Raises an alert (once per version); its failures never change the stage.</param>
    /// <param name="post">Runs download progress on the UI thread (default: at once).</param>
    public UpdateMachine(IUpdater? source, Action<HubAlert> announce, Action<Action>? post = null, Func<DateTimeOffset>? now = null)
    {
        _source = source;
        _announce = announce;
        _post = post ?? (a => a());
        _now = now ?? (() => DateTimeOffset.Now);
        Stage = source is null ? UpdateStage.Unavailable : UpdateStage.Idle;
        if (source?.Pending is { } pending)
        {
            _pending = pending;
            NewVersion = pending.Version;
            Stage = UpdateStage.Ready;
        }
    }

    public UpdateStage Stage { get; private set; }
    public string? NewVersion { get; private set; }
    public long DownloadSize { get; private set; }
    /// <summary>The new version's release notes, as plain text.</summary>
    public string Notes { get; private set; } = "";
    public int Percent { get; private set; }
    /// <summary>Why the last check, download or restart didn't work, in a few words; null after a success.</summary>
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public DateTimeOffset? LastAttempt { get; private set; }
    public bool LastFailed { get; private set; }

    /// <summary>Raised whenever the state above changes (on the thread that changed it; progress through post).</summary>
    public event Action? Changed;

    /// <summary>Asks for the latest release. An automatic check announces a new version; nothing is downloaded.</summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (_source is null || Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready) return;
        LastAttempt = _now();
        Error = null;
        Set(UpdateStage.Checking);
        HubAlert? alert = null;
        try
        {
            var offer = await _source.CheckAsync();
            LastFailed = false;
            LastChecked = _now();
            _offer = offer;
            if (offer is null)
            {
                Set(UpdateStage.UpToDate);
                return;
            }
            NewVersion = offer.Version;
            DownloadSize = offer.Size;
            Notes = UpdatePolicy.Notes(offer.NotesMarkdown);
            Log.Info("update", $"Aqua Hub {offer.Version} is available ({UpdatePolicy.Megabytes(offer.Size)})");
            Set(UpdateStage.Available);
            if (!userAsked) alert = UpdatePolicy.Available(offer.Version, offer.Size, _now());
        }
        catch (Exception ex)
        {
            LastFailed = true;
            Error = Describe(ex);
            Log.Warn("update", "Update check failed", ex);
            Set(UpdateStage.Failed);
        }
        if (alert is not null) Announce(alert);
    }

    /// <summary>Downloads the version the check found (the user asked). Cancellable; a failed download can be retried.</summary>
    public async Task DownloadAsync()
    {
        if (Stage != UpdateStage.Available || _offer is not { } offer || _source is null) return;
        var cts = _download = new CancellationTokenSource();
        Percent = 0;
        Error = null;
        Set(UpdateStage.Downloading);
        HubAlert? alert = null;
        try
        {
            await _source.DownloadAsync(offer, p => _post(() => Progress(p)), cts.Token);
            _pending = offer;
            Log.Info("update", $"Aqua Hub {offer.Version} downloaded; it installs on restart");
            Set(UpdateStage.Ready);
            alert = UpdatePolicy.Ready(offer.Version, _now());
        }
        catch (OperationCanceledException)
        {
            Log.Info("update", "Update download cancelled");
            Set(UpdateStage.Available);
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
            Log.Warn("update", "Update download failed", ex);
            Set(UpdateStage.Available);
        }
        finally
        {
            _download = null;
            cts.Dispose();
        }
        if (alert is not null) Announce(alert);
    }

    public void CancelDownload() => _download?.Cancel();

    /// <summary>
    /// Hands the downloaded version to the updater. True when it started and Aqua must now quit (it's started again
    /// once installed); false when there's nothing to install, it's already started, or it couldn't start (see Error).
    /// </summary>
    public bool ApplyAndRestart(bool silent, string[] restartArgs)
    {
        if (Stage != UpdateStage.Ready || _pending is not { } pending || _source is null || _applying) return false;
        try
        {
            _source.ApplyAndRestart(pending, silent, restartArgs);
            _applying = true;
            Log.Info("update", $"Installing Aqua Hub {pending.Version}");
            return true;
        }
        catch (Exception ex)
        {
            Error = Describe(ex);
            Log.Warn("update", "Could not start the updater", ex);
            Changed?.Invoke();
            return false;
        }
    }

    private void Announce(HubAlert alert)
    {
        try { _announce(alert); }
        catch (Exception ex) { Log.Warn("update", "Couldn't raise the update alert", ex); }
    }

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

    /// <summary>A failure in a few plain words for the Updates card (the full one goes to the log).</summary>
    public static string Describe(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }:
                    return "GitHub is limiting requests for a while";
                case HttpRequestException { StatusCode: null } or System.Net.Sockets.SocketException:
                    return "couldn't reach GitHub (are you offline?)";
                case TimeoutException or TaskCanceledException { CancellationToken.IsCancellationRequested: false }:
                    return "GitHub didn't answer in time";
            }
        }
        var inner = ex is AggregateException { InnerException: { } i } ? i : ex;
        var line = inner.Message.Split('\n')[0].Trim().TrimEnd('.');
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
