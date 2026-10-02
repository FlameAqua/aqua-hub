using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Updates;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData("https://github.com/someone/aqua-hub", "https://github.com/someone/aqua-hub")]
    [InlineData("https://github.com/someone/aqua-hub/", "https://github.com/someone/aqua-hub")]
    [InlineData("https://github.com/someone/aqua-hub.git", "https://github.com/someone/aqua-hub")]
    [InlineData("  https://github.com/Some-One/Aqua.Hub_2 ", "https://github.com/Some-One/Aqua.Hub_2")]
    public void AcceptsAGithubRepository(string url, string expected) => Assert.Equal(expected, UpdatePolicy.Repository(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://github.com/someone/aqua-hub")]
    [InlineData("https://gitlab.com/someone/aqua-hub")]
    [InlineData("https://github.com.example.net/someone/aqua-hub")]
    [InlineData("https://github.com/someone")]
    [InlineData("https://github.com/someone/aqua-hub/releases")]
    [InlineData("https://github.com/someone/aqua-hub?tab=readme")]
    [InlineData("https://github.com/-someone/aqua-hub")]
    [InlineData("https://github.com/someone/..")]
    [InlineData("https://user@github.com/someone/aqua-hub")]
    [InlineData("javascript:alert(1)")]
    public void RefusesAnythingElse(string? url) => Assert.Null(UpdatePolicy.Repository(url));

    [Fact]
    public void ChecksAboutDailyAndRetriesSoonerAfterAFailure()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        Assert.True(UpdatePolicy.CheckDue(null, false, now));
        Assert.False(UpdatePolicy.CheckDue(now.AddHours(-19), false, now));
        Assert.True(UpdatePolicy.CheckDue(now.AddHours(-21), false, now));
        Assert.False(UpdatePolicy.CheckDue(now.AddHours(-1), true, now));
        Assert.True(UpdatePolicy.CheckDue(now.AddHours(-3), true, now));
    }

    [Fact]
    public void AlertsLeadToSettingsAboutAndNameTheVersion()
    {
        var now = DateTimeOffset.Now;
        var available = UpdatePolicy.Available("1.2.0", 12_345_678, now);
        Assert.Equal("update:1.2.0", available.Id);
        Assert.Equal("update", available.Kind);
        Assert.Equal("settings:about:updates", available.Target);
        Assert.Contains("1.2.0", available.Title);
        Assert.Contains("11.8 MB", available.Body);
        var ready = UpdatePolicy.Ready("1.2.0", now);
        Assert.NotEqual(available.Id, ready.Id);
        Assert.Equal(UpdatePolicy.Target, ready.Target);
        Assert.Contains("restart", ready.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUpdateAlertIsNotifiedLikeAnyOther()
    {
        var alert = UpdatePolicy.Available("1.2.0", 1, DateTimeOffset.Now);
        var noon = new DateTime(2026, 9, 30, 12, 0, 0);
        Assert.Equal(ToastDecision.Show, NotificationPolicy.Decide(new NotificationSettings(), alert, noon, false, out _));
        Assert.Equal(ToastDecision.Drop, NotificationPolicy.Decide(new NotificationSettings { DoNotDisturb = true }, alert, noon, false, out _));
    }

    [Fact]
    public void AnInstalledVersionsAlertsAreSettled()
    {
        var now = DateTimeOffset.Now;
        Assert.True(UpdatePolicy.IsSettled(UpdatePolicy.Available("1.1.0", 1, now), "1.1.0"));
        Assert.True(UpdatePolicy.IsSettled(UpdatePolicy.Ready("1.1.0", now), "1.1.0"));
        Assert.True(UpdatePolicy.IsSettled(UpdatePolicy.Available("1.0.9", 1, now), "1.1.0"));
        Assert.False(UpdatePolicy.IsSettled(UpdatePolicy.Available("1.2.0", 1, now), "1.1.0"));
        Assert.False(UpdatePolicy.IsSettled(UpdatePolicy.Ready("1.1.10", now), "1.1.9"));
        Assert.False(UpdatePolicy.IsSettled(new HubAlert { Id = "price:1.1", Kind = "price", Title = "x" }, "9.9.9"));
        Assert.False(UpdatePolicy.IsSettled(UpdatePolicy.Available("1.1.0", 1, now), ""));   // a copy without a version keeps them
    }

    [Fact]
    public void AfterAnUpdateTheBellNoLongerOffersIt()
    {
        using var t = new TestContext();
        var now = DateTimeOffset.Now;
        t.Ctx.RaiseAlert(UpdatePolicy.Available("1.1.0", 1, now), "update:1.1.0");
        t.Ctx.RaiseAlert(UpdatePolicy.Ready("1.1.0", now));
        t.Ctx.RaiseAlert(new HubAlert { Id = "price:NVDA", Kind = "price", Title = "NVDA crossed 200", Created = now });
        Assert.Equal(3, t.Ctx.State.UnreadAlerts);
        var topics = new List<string>();
        t.Ctx.State.Changed += topics.Add;

        t.Ctx.State.RemoveAlerts(a => UpdatePolicy.IsSettled(a, "1.1.0"));
        Assert.Equal("price:NVDA", Assert.Single(t.Ctx.State.Alerts).Id);
        Assert.Equal(new[] { Topics.Alerts }, topics);
        t.Ctx.State.RemoveAlerts(a => UpdatePolicy.IsSettled(a, "1.1.0"));   // nothing left to remove: nobody is told
        Assert.Single(topics);
        Assert.False(t.Ctx.RaiseAlert(UpdatePolicy.Available("1.1.0", 1, now), "update:1.1.0"));   // and it isn't raised again
    }

    [Fact]
    public void SizesReadAsMegabytes()
    {
        Assert.Equal("size unknown", UpdatePolicy.Megabytes(0));
        Assert.Equal("11.8 MB", UpdatePolicy.Megabytes(12_345_678));
        Assert.Equal("0.5 MB", UpdatePolicy.Megabytes(524_288));
    }

    [Fact]
    public void ReleaseNotesBecomePlainText()
    {
        var md = "## What's new\r\n\r\n* **Faster** start-up\n- Notifications: [clicking one](https://example.com/x) marks it read\n\n\n\n`code` and <b>tags</b> go";
        var text = UpdatePolicy.Notes(md);
        Assert.Equal("What's new\n\n• Faster start-up\n• Notifications: clicking one marks it read\n\ncode and tags go", text);
        Assert.Equal("", UpdatePolicy.Notes(null));
        var long_ = string.Join("\n", Enumerable.Range(1, 200).Select(i => "• item " + i));
        var cut = UpdatePolicy.Notes(long_, 100);
        Assert.True(cut.Length <= 102);
        Assert.EndsWith(" …", cut);
    }

    [Fact]
    public void TheBannerFollowsAnUpdateFromOfferToRestart()
    {
        Assert.Null(UpdatePolicy.BannerFor(UpdateStage.Idle, null, 0, 0, null));
        Assert.Null(UpdatePolicy.BannerFor(UpdateStage.UpToDate, null, 0, 0, null));
        Assert.Null(UpdatePolicy.BannerFor(UpdateStage.Failed, null, 0, 0, "GitHub didn't answer in time"));

        var offer = UpdatePolicy.BannerFor(UpdateStage.Available, "1.2.0", 12_345_678, 0, null)!;
        Assert.Equal("Aqua Hub 1.2.0 is available (11.8 MB). Nothing downloads until you choose to.", offer.Text);
        Assert.Equal("Download", offer.Action);
        Assert.True(offer.Notes);
        Assert.False(offer.Progress);
        var retry = UpdatePolicy.BannerFor(UpdateStage.Available, "1.2.0", 12_345_678, 40, "couldn't reach GitHub (are you offline?)")!;
        Assert.Equal("Try again", retry.Action);
        Assert.Contains("The download didn't finish: couldn't reach GitHub", retry.Text);

        var downloading = UpdatePolicy.BannerFor(UpdateStage.Downloading, "1.2.0", 12_345_678, 42, null)!;
        Assert.Equal("Downloading Aqua Hub 1.2.0… 42%", downloading.Text);
        Assert.Equal("Cancel", downloading.Action);
        Assert.True(downloading.Progress);

        var ready = UpdatePolicy.BannerFor(UpdateStage.Ready, "1.2.0", 12_345_678, 100, null)!;
        Assert.Equal("Restart now", ready.Action);
        Assert.StartsWith("Aqua Hub 1.2.0 is ready. Restart now to install it", ready.Text);
        Assert.Contains("the updater didn't start", UpdatePolicy.BannerFor(UpdateStage.Ready, "1.2.0", 0, 100, "access denied")!.Text);
    }

    [Fact]
    public void TheDiagnosticsSayWhereTheUpdaterStands()
    {
        Assert.Equal("can't update itself", UpdatePolicy.Describe(UpdateStage.Unavailable, null, null, null));
        Assert.Equal("up to date (checked 2026-10-02 09:30)",
            UpdatePolicy.Describe(UpdateStage.UpToDate, null, null, new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(1))));
        Assert.Equal("1.2.0 ready to install, the updater didn't start (access denied)", UpdatePolicy.Describe(UpdateStage.Ready, "1.2.0", "access denied", null));
        Assert.Equal("the last check failed (GitHub didn't answer in time)", UpdatePolicy.Describe(UpdateStage.Failed, null, "GitHub didn't answer in time", null));
    }

    [Fact]
    public void NotNowLastsUntilTheNextVersionOrStep()
    {
        // Putting away the offer keeps it away while it downloads; ready to install, or a newer version, shows again.
        Assert.Equal(UpdatePolicy.BannerKey(UpdateStage.Available, "1.2.0"), UpdatePolicy.BannerKey(UpdateStage.Downloading, "1.2.0"));
        Assert.NotEqual(UpdatePolicy.BannerKey(UpdateStage.Available, "1.2.0"), UpdatePolicy.BannerKey(UpdateStage.Ready, "1.2.0"));
        Assert.NotEqual(UpdatePolicy.BannerKey(UpdateStage.Available, "1.2.0"), UpdatePolicy.BannerKey(UpdateStage.Available, "1.3.0"));
        Assert.Null(UpdatePolicy.BannerKey(UpdateStage.UpToDate, "1.2.0"));
    }
}

public class AlertReadTests
{
    private static HubAlert Alert(string id) => new()
    {
        Id = id, Kind = "price", Title = "Alert " + id, Target = "markets:NVDA", Created = DateTimeOffset.Now,
    };

    [Fact]
    public void OpeningAnAlertMarksOnlyThatOneRead()
    {
        using var t = new TestContext();
        var ctx = t.Ctx;
        Assert.True(ctx.RaiseAlert(Alert("a")));
        Assert.True(ctx.RaiseAlert(Alert("b")));
        Assert.Equal(2, ctx.State.UnreadAlerts);
        Assert.Equal(2, t.Platform.Delivered.Count);

        var topics = new List<string>();
        ctx.State.Changed += topics.Add;
        ctx.State.MarkAlertRead("a");
        Assert.Equal(1, ctx.State.UnreadAlerts);
        Assert.True(ctx.State.Alerts.Single(x => x.Id == "a").Read);
        Assert.False(ctx.State.Alerts.Single(x => x.Id == "b").Read);
        Assert.Equal(new[] { Topics.Alerts }, topics);

        // Already read, or never stored (e.g. the brief's notification): nothing changes and nobody is told.
        ctx.State.MarkAlertRead("a");
        ctx.State.MarkAlertRead("not-stored");
        Assert.Single(topics);
        Assert.False(ctx.Db.MarkAlertRead("a"));
    }

    [Fact]
    public void AnAlertWithAKeyIsRaisedOnce()
    {
        using var t = new TestContext();
        var alert = UpdatePolicy.Available("1.2.0", 1, DateTimeOffset.Now);
        Assert.True(t.Ctx.RaiseAlert(alert, alert.Id));
        Assert.False(t.Ctx.RaiseAlert(alert, alert.Id));
        // Clearing the bell doesn't bring it back while the key is remembered (keys expire after 10 days, so a
        // version that's still not installed is announced again after that).
        t.Ctx.State.ClearAlerts();
        Assert.False(t.Ctx.RaiseAlert(alert, alert.Id));
        Assert.Single(t.Platform.Delivered);
    }

    [Fact]
    public void AKeyIsOnlyUsedUpWhenItsAlertIsStored()
    {
        using var t = new TestContext();
        var alert = UpdatePolicy.Available("1.2.0", 1, DateTimeOffset.Now);
        Assert.True(t.Ctx.RaiseAlert(alert));
        // The same alert again under a new key isn't stored (it's already there), so the key stays unused.
        Assert.False(t.Ctx.RaiseAlert(alert, "update-reminder:1.2.0"));
        Assert.False(t.Ctx.Db.HasSeen("update-reminder:1.2.0"));
        t.Ctx.State.ClearAlerts();
        Assert.True(t.Ctx.RaiseAlert(alert, "update-reminder:1.2.0"));
        Assert.True(t.Ctx.Db.HasSeen("update-reminder:1.2.0"));
    }
}

public class RunEntryTests
{
    private const string Installed = @"C:\Users\someone\AppData\Local\AquaHub.App\current\AquaHub.exe";
    private const string Source = @"C:\code\aqua-hub\src\AquaHub\bin\Release\net9.0-windows10.0.19041.0\AquaHub.exe";

    [Theory]
    [InlineData("\"C:\\Program Files\\Aqua\\AquaHub.exe\" --background", @"C:\Program Files\Aqua\AquaHub.exe")]
    [InlineData("C:\\Aqua\\AquaHub.exe --background --data-dir \"D:\\p\"", @"C:\Aqua\AquaHub.exe")]
    [InlineData("  C:\\Aqua\\AquaHub.exe  ", @"C:\Aqua\AquaHub.exe")]
    public void FindsTheProgramInACommandLine(string command, string program) => Assert.Equal(program, RunEntry.ProgramOf(command));

    [Fact]
    public void TheInstalledCopyTakesOverFromASourceBuild()
    {
        var entry = $"\"{Source}\" --background";
        Assert.True(RunEntry.ShouldTakeOver(entry, Installed, installed: true, exists: _ => true));
        // A source build leaves an installed copy's entry alone, but repairs one whose program is gone.
        var installedEntry = $"\"{Installed}\" --background";
        Assert.False(RunEntry.ShouldTakeOver(installedEntry, Source, installed: false, exists: _ => true));
        Assert.True(RunEntry.ShouldTakeOver(installedEntry, Source, installed: false, exists: _ => false));
        // Its own entry (any letter case) needs nothing.
        Assert.False(RunEntry.ShouldTakeOver(installedEntry.ToUpperInvariant(), Installed, installed: true, exists: _ => true));
    }
}

public class ContextWindowTests
{
    [Fact]
    public void AutomaticGrowsUpTo128K()
    {
        var s = new HubSettings();
        var question = new List<LlmMessage> { new("user", "hi") };
        Assert.Equal(65536, AskAgent.ContextFor(new string('x', 150_000), question, s));
        Assert.Equal(98304, AskAgent.ContextFor(new string('x', 250_000), question, s));
        Assert.Equal(131072, AskAgent.ContextFor(new string('x', 320_000), question, s));
        Assert.Equal(131072, AskAgent.ContextFor(new string('x', 900_000), question, s));
    }

    [Fact]
    public void AutomaticStopsAt64KUnlessTheGraphicsCardHas24GB()
    {
        Assert.Equal(65536, AskAgent.AutomaticLimit(0));   // unknown card
        Assert.Equal(65536, AskAgent.AutomaticLimit(12));
        Assert.Equal(131072, AskAgent.AutomaticLimit(24));
        var s = new HubSettings();
        var question = new List<LlmMessage> { new("user", "hi") };
        Assert.Equal(65536, AskAgent.ContextFor(new string('x', 320_000), question, s, limit: 65536));
        Assert.Equal(49152, AskAgent.ContextFor(new string('x', 120_000), question, s, limit: 65536));
    }

    [Fact]
    public void PastTheLimitAutomaticTrimsInsteadOfOverflowing()
    {
        var s = new HubSettings();
        var messages = new List<LlmMessage> { new("user", new string('a', 50_000)), new("assistant", new string('b', 50_000)), new("user", "and now?") };
        var (window, dropped, context) = AskAgent.FitToWindow("instructions", new string('c', 400_000), messages, s, 1800, automaticLimit: 65536);
        Assert.Equal(65536, window);
        Assert.Equal(2, dropped);   // the oldest messages go first…
        Assert.Contains("left out to fit the context window", context);   // …then the end of the material
        Assert.True(("instructions".Length + context.Length + "and now?".Length) / 3.2 + 1800 <= 65536 * 0.92);

        // Within the limit nothing is cut.
        var (smallWindow, none, whole) = AskAgent.FitToWindow("instructions", new string('c', 100_000), new List<LlmMessage> { new("user", "hi") }, s, 1800, automaticLimit: 65536);
        Assert.Equal(49152, smallWindow);
        Assert.Equal(0, none);
        Assert.Equal(100_000, whole.Length);
    }

    [Fact]
    public void A128KWindowIsAValidChoice()
    {
        var s = new HubSettings();
        s.Ask.ContextWindow = 131072;
        SettingsStore.Validate(s);
        Assert.Equal(131072, s.Ask.ContextWindow);
        s.Ask.ContextWindow = 100_000;
        SettingsStore.Validate(s);
        Assert.Equal(0, s.Ask.ContextWindow);
    }
}
