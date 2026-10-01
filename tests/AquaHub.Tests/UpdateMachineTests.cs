using System.Net;
using AquaHub.Core.Models;
using AquaHub.Core.Updates;

namespace AquaHub.Tests;

/// <summary>The update flow against a fake updater: every stage, cancel and retry, and failures that mustn't stick.</summary>
public class UpdateMachineTests
{
    private sealed class FakeUpdater : IUpdater
    {
        public UpdateOffer? Pending { get; set; }
        public Func<Task<UpdateOffer?>> Check { get; set; } = () => Task.FromResult<UpdateOffer?>(null);
        public Func<UpdateOffer, Action<int>, CancellationToken, Task> Download { get; set; } = (_, progress, _) =>
        {
            progress(40);
            progress(100);
            return Task.CompletedTask;
        };
        public Exception? ApplyFails { get; set; }
        public List<(UpdateOffer Offer, bool Silent, string[] Args)> Applied { get; } = new();

        public Task<UpdateOffer?> CheckAsync() => Check();
        public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct) => Download(offer, progress, ct);
        public void ApplyAndRestart(UpdateOffer offer, bool silent, string[] restartArgs)
        {
            if (ApplyFails is { } ex) throw ex;
            Applied.Add((offer, silent, restartArgs));
        }
    }

    private static readonly UpdateOffer V2 = new("2.0.0", 15_000_000, "## 2.0.0\n- Faster start");
    private readonly List<HubAlert> _alerts = new();

    private UpdateMachine Machine(FakeUpdater? updater, Action<HubAlert>? announce = null) => new(updater, announce ?? _alerts.Add);

    [Fact]
    public async Task ACopyThatCantUpdateNeverChecks()
    {
        var m = Machine(null);
        Assert.Equal(UpdateStage.Unavailable, m.Stage);
        await m.CheckAsync(userAsked: true);
        await m.DownloadAsync();
        Assert.Equal(UpdateStage.Unavailable, m.Stage);
        Assert.False(m.ApplyAndRestart(false, Array.Empty<string>()));
    }

    [Fact]
    public async Task UpToDateSaysSoAndAnnouncesNothing()
    {
        var m = Machine(new FakeUpdater());
        Assert.Equal(UpdateStage.Idle, m.Stage);
        await m.CheckAsync(userAsked: false);
        Assert.Equal(UpdateStage.UpToDate, m.Stage);
        Assert.NotNull(m.LastChecked);
        Assert.False(m.LastFailed);
        Assert.Empty(_alerts);
    }

    [Fact]
    public async Task AnAutomaticCheckAnnouncesANewVersionACheckYouAskedForShowsItInPlace()
    {
        var automatic = Machine(new FakeUpdater { Check = () => Task.FromResult<UpdateOffer?>(V2) });
        await automatic.CheckAsync(userAsked: false);
        Assert.Equal(UpdateStage.Available, automatic.Stage);
        Assert.Equal("2.0.0", automatic.NewVersion);
        Assert.Equal(15_000_000, automatic.DownloadSize);
        Assert.Contains("Faster start", automatic.Notes);
        Assert.Single(_alerts);
        Assert.Equal(UpdatePolicy.Available("2.0.0", 1, DateTimeOffset.Now).Id, _alerts[0].Id);

        _alerts.Clear();
        var asked = Machine(new FakeUpdater { Check = () => Task.FromResult<UpdateOffer?>(V2) });
        await asked.CheckAsync(userAsked: true);
        Assert.Equal(UpdateStage.Available, asked.Stage);
        Assert.Empty(_alerts);
    }

    [Fact]
    public async Task AFailedCheckSaysWhyAndIsRetriedSooner()
    {
        var m = Machine(new FakeUpdater { Check = () => throw new HttpRequestException("rate limit", null, HttpStatusCode.Forbidden) });
        await m.CheckAsync(userAsked: false);
        Assert.Equal(UpdateStage.Failed, m.Stage);
        Assert.Equal("GitHub is limiting requests for a while", m.Error);
        Assert.True(m.LastFailed);
        Assert.Empty(_alerts);
    }

    [Fact]
    public async Task DownloadingReportsProgressThenIsReadyAndAnnouncesIt()
    {
        var m = Machine(new FakeUpdater { Check = () => Task.FromResult<UpdateOffer?>(V2) });
        await m.CheckAsync(userAsked: true);
        var seen = new List<int>();
        m.Changed += () => { if (m.Stage == UpdateStage.Downloading) seen.Add(m.Percent); };
        await m.DownloadAsync();
        Assert.Equal(UpdateStage.Ready, m.Stage);
        Assert.Contains(40, seen);
        Assert.Contains(100, seen);
        Assert.Equal(UpdatePolicy.Ready("2.0.0", DateTimeOffset.Now).Id, Assert.Single(_alerts).Id);
    }

    [Fact]
    public async Task ACancelledDownloadGoesBackToAvailableWithoutAnError()
    {
        var m = Machine(new FakeUpdater
        {
            Check = () => Task.FromResult<UpdateOffer?>(V2),
            Download = (_, _, ct) => Task.Delay(Timeout.Infinite, ct),
        });
        await m.CheckAsync(userAsked: true);
        var download = m.DownloadAsync();
        Assert.Equal(UpdateStage.Downloading, m.Stage);
        m.CancelDownload();
        await download;
        Assert.Equal(UpdateStage.Available, m.Stage);
        Assert.Null(m.Error);
    }

    [Fact]
    public async Task AFailedDownloadSaysWhyAndCanBeRetried()
    {
        var attempts = 0;
        var updater = new FakeUpdater
        {
            Check = () => Task.FromResult<UpdateOffer?>(V2),
            Download = (_, _, _) => ++attempts == 1 ? throw new HttpRequestException("No such host is known.") : Task.CompletedTask,
        };
        var m = Machine(updater);
        await m.CheckAsync(userAsked: true);
        await m.DownloadAsync();
        Assert.Equal(UpdateStage.Available, m.Stage);
        Assert.Equal("couldn't reach GitHub (are you offline?)", m.Error);
        await m.DownloadAsync();
        Assert.Equal(UpdateStage.Ready, m.Stage);
        Assert.Null(m.Error);
    }

    [Fact]
    public async Task AnAlertThatFailsDoesntUndoACheckOrADownload()
    {
        var m = Machine(new FakeUpdater { Check = () => Task.FromResult<UpdateOffer?>(V2) }, _ => throw new InvalidOperationException("database is locked"));
        await m.CheckAsync(userAsked: false);
        Assert.Equal(UpdateStage.Available, m.Stage);
        Assert.Null(m.Error);
        await m.DownloadAsync();
        Assert.Equal(UpdateStage.Ready, m.Stage);
        Assert.Null(m.Error);
    }

    [Fact]
    public async Task OnceReadyItStaysReadyAndNothingElseRuns()
    {
        var checks = 0;
        var m = Machine(new FakeUpdater { Check = () => { checks++; return Task.FromResult<UpdateOffer?>(V2); } });
        await m.DownloadAsync();
        Assert.Equal(UpdateStage.Idle, m.Stage);   // nothing to download before a check
        await m.CheckAsync(userAsked: true);
        await m.DownloadAsync();
        await m.CheckAsync(userAsked: true);
        await m.DownloadAsync();
        Assert.Equal(UpdateStage.Ready, m.Stage);
        Assert.Equal(1, checks);
    }

    [Fact]
    public void ADownloadFromEarlierIsReadyAtStartAndIsHandedOverOnce()
    {
        var updater = new FakeUpdater { Pending = V2 };
        var m = Machine(updater);
        Assert.Equal(UpdateStage.Ready, m.Stage);
        Assert.Equal("2.0.0", m.NewVersion);
        Assert.True(m.ApplyAndRestart(silent: true, new[] { "--data-dir", @"D:\profile", "--background" }));
        Assert.False(m.ApplyAndRestart(silent: false, Array.Empty<string>()));
        var applied = Assert.Single(updater.Applied);
        Assert.Equal("2.0.0", applied.Offer.Version);
        Assert.True(applied.Silent);
        Assert.Equal(new[] { "--data-dir", @"D:\profile", "--background" }, applied.Args);
    }

    [Fact]
    public void AnUpdaterThatWontStartLeavesItReadyWithTheReason()
    {
        var updater = new FakeUpdater { Pending = V2, ApplyFails = new IOException("Update.exe is missing") };
        var m = Machine(updater);
        Assert.False(m.ApplyAndRestart(silent: false, Array.Empty<string>()));
        Assert.Equal(UpdateStage.Ready, m.Stage);
        Assert.Equal("Update.exe is missing", m.Error);
        updater.ApplyFails = null;
        Assert.True(m.ApplyAndRestart(silent: false, Array.Empty<string>()));
    }

    [Fact]
    public void FailuresAreDescribedInPlainWords()
    {
        Assert.Equal("GitHub is limiting requests for a while", UpdateMachine.Describe(new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)));
        Assert.Equal("couldn't reach GitHub (are you offline?)", UpdateMachine.Describe(new Exception("wrapped", new System.Net.Sockets.SocketException(11001))));
        Assert.Equal("GitHub didn't answer in time", UpdateMachine.Describe(new TaskCanceledException("timed out", new TimeoutException())));
        Assert.Equal("Response status code does not indicate success: 500 (Internal Server Error)",
            UpdateMachine.Describe(new HttpRequestException("Response status code does not indicate success: 500 (Internal Server Error).", null, HttpStatusCode.InternalServerError)));
        Assert.Equal(new string('x', 160) + "…", UpdateMachine.Describe(new Exception(new string('x', 300))));
    }
}
