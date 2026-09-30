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
        // Even after the bell was cleared, the same version isn't announced again.
        t.Ctx.State.ClearAlerts();
        Assert.False(t.Ctx.RaiseAlert(alert, alert.Id));
        Assert.Single(t.Platform.Delivered);
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
