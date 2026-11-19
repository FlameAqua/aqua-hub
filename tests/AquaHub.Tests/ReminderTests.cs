using AquaHub.Core.Agents;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Reminders;
using Microsoft.Data.Sqlite;

namespace AquaHub.Tests;

public sealed class ReminderTests
{
    private static DateTimeOffset Utc(int day, int hour = 10, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);
    private static DateTime Wall(int day, int hour = 10, int minute = 0) => Utc(day, hour, minute).DateTime;

    [Fact]
    public async Task OneOffSurvivesReopenAndClearingBellDoesNotRepeatDelivery()
    {
        using var f = new Fixture();
        var item = await f.Service.SaveAsync(new("Buy milk", Wall(19, 10, 1), ReminderRepeat.Once));
        Assert.Equal(item.Id, Assert.Single(f.Service.Snapshot.RowsForDay(new(2026, 11, 19))).Id);
        await f.ReopenAsync();
        f.Time.Now = Utc(19, 10, 4);
        await f.Service.RefreshAsync();
        var due = Assert.Single(f.Service.Snapshot.AllRows());
        Assert.Equal(ReminderStatus.Due, due.Occurrence.Status);
        Assert.Equal(due.AlertId, Assert.Single(f.Db.GetAlerts()).Id);
        Assert.Single(f.Platform.Delivered);
        f.Db.ClearAlerts();
        await f.ReopenAsync();
        f.Time.Now = Utc(19, 10, 5);
        await f.Service.RefreshAsync();
        Assert.Single(f.Platform.Delivered);
        Assert.Empty(f.Db.GetAlerts());
        await f.Service.DoneAsync(Assert.Single(f.Service.Snapshot.AllRows()));
        Assert.Empty(f.Service.Snapshot.Items);
    }

    [Fact]
    public async Task QuickReminderKeepsElapsedTimeAndCanBeRenamedAcrossFallBack()
    {
        using var f = new Fixture();
        f.Time.Zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        f.Time.Now = new(2026, 11, 1, 5, 55, 30, TimeSpan.Zero);
        var item = await f.Service.SaveQuickAsync(15);
        Assert.Equal(f.Time.Now.AddMinutes(15), item.FirstInstant);
        Assert.Equal(new DateTime(2026, 11, 1, 1, 10, 0), item.Draft.LocalTime);
        var renamed = await f.Service.SaveAsync(item.Draft with { Title = "Break" }, item);
        Assert.Equal(item.FirstInstant, renamed.FirstInstant);
        await f.ReopenAsync();
        f.Time.Now = item.FirstInstant;
        await f.Service.RefreshAsync();
        Assert.Equal(15, Assert.Single(f.Service.Snapshot.Items).Draft.QuickMinutes);
        Assert.Equal("Break", Assert.Single(f.Platform.Delivered).Title);
    }

    [Fact]
    public async Task SceneRunClaimRequiresTheCurrentDueOccurrenceAndSurvivesRestart()
    {
        using var f = new Fixture();
        var saved = await f.Service.SaveAsync(new("Review scene", Wall(19, 10, 1), ReminderRepeat.Daily, "scene-a"));
        var future = Assert.Single(f.Service.Snapshot.AllRows());
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.ClaimSceneRunAsync(future));
        f.Time.Now = Utc(19, 10, 1);
        await f.Service.RefreshAsync();
        var stale = Assert.Single(f.Service.Snapshot.AllRows());
        Assert.True(stale.CanRunScene);
        await f.Service.SaveAsync(saved.Draft with { SceneId = "scene-b" }, saved);
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.ClaimSceneRunAsync(stale));
        var eligible = Assert.Single(f.Service.Snapshot.AllRows());
        await f.Service.ClaimSceneRunAsync(eligible);
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.ClaimSceneRunAsync(eligible));

        await f.ReopenAsync();
        await f.Service.RefreshAsync();
        var claimed = Assert.Single(f.Service.Snapshot.AllRows());
        Assert.False(claimed.CanRunScene);
        Assert.True(claimed.Occurrence.SceneRunRequested);
        await f.Service.SnoozeAsync(claimed);
        f.Time.Now = Utc(19, 10, 11);
        await f.Service.RefreshAsync();
        Assert.False(Assert.Single(f.Service.Snapshot.AllRows()).CanRunScene);

        f.Time.Now = Utc(20, 10, 1);
        await f.Service.RefreshAsync();
        var next = Assert.Single(f.Service.Snapshot.AllRows());
        Assert.True(next.CanRunScene);
        Assert.Equal("scene-b", next.Reminder.Draft.SceneId);
    }

    [Theory]
    [InlineData(ReminderRepeat.Daily, 23, 24)]
    [InlineData(ReminderRepeat.Weekly, 20, 27)]
    public async Task LongGapKeepsOnlyLatestMissedOccurrenceAndNextRepeat(ReminderRepeat repeat, int missedDay, int nextDay)
    {
        using var f = new Fixture();
        await f.Service.SaveAsync(new("Check meter", Wall(20), repeat));
        await f.ReopenAsync();
        f.Time.Now = Utc(23, 12);
        await f.Service.RefreshAsync();
        var missed = Assert.Single(f.Service.Snapshot.AllRows());
        Assert.Equal(ReminderStatus.Missed, missed.Occurrence.Status);
        Assert.Equal(Wall(missedDay), missed.Occurrence.Nominal);
        Assert.Equal(Wall(nextDay), missed.Reminder.NextLocal);
        Assert.Empty(f.Platform.Delivered);
        await f.Service.DoneAsync(missed);
        Assert.Equal(ReminderStatus.Scheduled, Assert.Single(f.Service.Snapshot.AllRows()).Occurrence.Status);
    }

    [Theory]
    [InlineData(5, ReminderStatus.Due)]
    [InlineData(6, ReminderStatus.Missed)]
    public async Task WakeUsesFiveMinuteGrace(int lateMinutes, ReminderStatus status)
    {
        using var f = new Fixture();
        await f.Service.SaveAsync(new("Take a break", Wall(19, 10, 10), ReminderRepeat.Once));
        f.Time.Now = Utc(19, 10, 10 + lateMinutes);
        await f.Service.RefreshAsync();
        Assert.Equal(status, Assert.Single(f.Service.Snapshot.AllRows()).Occurrence.Status);
        Assert.Equal(status == ReminderStatus.Due ? 1 : 0, f.Platform.Delivered.Count);
    }

    [Fact]
    public async Task SnoozeSurvivesRestartWithSeparateDeliveryAndKeepsTheRepeat()
    {
        using var f = new Fixture();
        await f.Service.SaveAsync(new("Feed the cat", Wall(19, 10, 1), ReminderRepeat.Daily));
        f.Time.Now = Utc(19, 10, 1);
        await f.Service.RefreshAsync();
        var due = Assert.Single(f.Service.Snapshot.AllRows());
        await f.Service.SnoozeAsync(due);
        Assert.Empty(f.Db.GetAlerts());
        Assert.Equal(due.AlertId, Assert.Single(f.Platform.Withdrawn));
        await Assert.ThrowsAsync<ReminderException>(() => f.Service.DoneAsync(due));
        await f.ReopenAsync();
        f.Time.Now = Utc(19, 10, 10);
        await f.Service.RefreshAsync();
        Assert.Equal(ReminderStatus.Snoozed, Assert.Single(f.Service.Snapshot.AllRows()).Occurrence.Status);
        f.Time.Now = Utc(19, 10, 11);
        await f.Service.RefreshAsync();
        var snoozedDelivery = Assert.Single(f.Service.Snapshot.AllRows());
        Assert.Equal(ReminderStatus.Due, snoozedDelivery.Occurrence.Status);
        Assert.NotEqual(due.AlertId, snoozedDelivery.AlertId);
        Assert.Equal(due.Occurrence.Nominal, snoozedDelivery.Occurrence.Nominal);
        Assert.Equal(Wall(20), snoozedDelivery.Reminder.NextLocal!.Value.AddMinutes(-1));
        Assert.Equal(2, f.Platform.Delivered.Count);
        await f.Service.RefreshAsync();
        Assert.Equal(2, f.Platform.Delivered.Count);
    }

    [Fact]
    public async Task ZoneChangeMovesRepeatAndKeepsOneOffInstant()
    {
        using var f = new Fixture();
        var one = await f.Service.SaveAsync(new("Appointment", Wall(20, 1), ReminderRepeat.Once));
        var repeat = await f.Service.SaveAsync(new("Stretch", Wall(20, 1), ReminderRepeat.Daily));
        f.Time.Zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        f.Service.ClockChanged();
        f.Service.CheckIfDue();
        await f.Service.RefreshAsync();
        var list = f.Service.Snapshot.AllRows().ToDictionary(r => r.Id);
        Assert.Equal(Utc(20, 1), list[one.Id].Occurrence.At);
        Assert.Equal(new DateOnly(2026, 11, 19), f.Service.Snapshot.LocalDay(list[one.Id].Occurrence.At));
        Assert.Equal(Utc(20, 6), list[repeat.Id].Occurrence.At);
        f.Time.Now = Utc(20, 6);
        await f.Service.RefreshAsync();
        f.Time.Zone = TimeZoneInfo.Utc;
        f.Time.Now = Utc(20, 0);
        await f.Service.RefreshAsync();
        f.Time.Now = Utc(20, 1);
        await f.Service.RefreshAsync();
        Assert.Single(f.Platform.Delivered);
        Assert.Equal("Stretch", f.Platform.Delivered[0].Title);
    }

    [Theory]
    [InlineData(3, 7, 2, 30, 8, 7, 30)]
    [InlineData(10, 31, 1, 30, 1, 5, 30)]
    public async Task RepeatFiresOnceAtResolvedDstInstant(int month, int day, int hour, int minute, int nextDay, int utcHour, int utcMinute)
    {
        using var f = new Fixture();
        f.Time.Zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        f.Time.Now = new(2026, month, day, 0, 0, 0, TimeSpan.Zero);
        await f.Service.SaveAsync(new("Morning reminder", new(2026, month, day, hour, minute, 0), ReminderRepeat.Daily));
        var due = new DateTimeOffset(2026, month, day, 0, 0, 0, TimeSpan.Zero).AddDays(1).AddHours(utcHour).AddMinutes(utcMinute);
        Assert.Equal(nextDay, due.Day);
        f.Time.Now = due.AddSeconds(-1);
        await f.Service.RefreshAsync();
        Assert.Empty(f.Platform.Delivered);
        f.Time.Now = due;
        await f.Service.RefreshAsync();
        Assert.Equal(due, Assert.Single(f.Platform.Delivered).Created);
        f.Time.Now = due.AddHours(1);
        await f.Service.RefreshAsync();
        Assert.Single(f.Platform.Delivered);
    }

    [Fact]
    public async Task PendingEditsAndDeletesTakeEffectBeforeFollowingDueCheck()
    {
        using var f = new Fixture();
        await f.Service.SaveAsync(new("Hold notification", Wall(19, 10, 1), ReminderRepeat.Once));
        var change = await f.Service.SaveAsync(new("Move this", Wall(19, 10, 2), ReminderRepeat.Once));
        var remove = await f.Service.SaveAsync(new("Delete this", Wall(19, 10, 2), ReminderRepeat.Once));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Platform.OnNotify = _ =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        };
        f.Time.Now = Utc(19, 10, 1);
        var firstCheck = f.Service.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var moved = f.Service.SaveAsync(change.Draft with { LocalTime = Wall(19, 11) }, change);
            var removed = f.Service.DeleteAsync(remove);
            f.Time.Now = Utc(19, 10, 2);
            var anotherCheck = f.Service.RefreshAsync();
            release.TrySetResult();
            await Task.WhenAll(firstCheck, moved, removed, anotherCheck);
            Assert.NotEqual(change.ScheduleId, (await moved).ScheduleId);
            Assert.Single(f.Platform.Delivered);
            await Assert.ThrowsAsync<ReminderException>(() => f.Service.DeleteAsync(change));
            Assert.DoesNotContain(f.Service.Snapshot.Items, r => r.Id == remove.Id);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ShutdownJoinsActiveWorkAndCancelsQueuedMutation()
    {
        using var f = new Fixture();
        await f.Service.SaveAsync(new("Current", Wall(19, 10, 1), ReminderRepeat.Once));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Platform.OnNotify = _ =>
        {
            entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        };
        f.Time.Now = Utc(19, 10, 1);
        var delivering = f.Service.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = f.Service.SaveAsync(new("Cancelled", Wall(20), ReminderRepeat.Once));
            var stop = f.Service.StopAsync();
            Assert.False(stop.IsCompleted);
            release.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            await delivering;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Single(f.Db.LoadReminders());
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ReadAndWriteFailureRecoverWithoutLosingKnownGoodDataOrDeliveringTwice()
    {
        using var f = new Fixture();
        f.Sql("ALTER TABLE reminder_state RENAME TO unavailable");
        await f.Service.RefreshAsync();
        Assert.NotNull(f.Service.Snapshot.Error);
        f.Sql("ALTER TABLE unavailable RENAME TO reminder_state");
        var item = await f.Service.SaveAsync(new("Keep this", Wall(19, 10, 1), ReminderRepeat.Once));
        f.Sql("CREATE TRIGGER fail_reminder_alert BEFORE INSERT ON alerts BEGIN SELECT RAISE(ABORT, 'test rollback'); END;");
        f.Time.Now = Utc(19, 10, 1);
        await f.Service.RefreshAsync();
        Assert.NotNull(f.Service.Snapshot.Error);
        Assert.Equal(item, Assert.Single(f.Service.Snapshot.Items));
        Assert.Equal(item, Assert.Single(f.Db.LoadReminders()));
        Assert.Empty(f.Platform.Delivered);
        f.Sql("DROP TRIGGER fail_reminder_alert");
        await f.Service.RefreshAsync();
        Assert.Null(f.Service.Snapshot.Error);
        Assert.Single(f.Platform.Delivered);
        Assert.Single(f.Db.GetAlerts());
    }

    [Fact]
    public async Task InvalidInputIsRejectedWithoutWriting()
    {
        using var f = new Fixture();
        var title = await Assert.ThrowsAsync<ReminderException>(() => f.Service.SaveAsync(new("  ", Wall(20), ReminderRepeat.Once)));
        var time = await Assert.ThrowsAsync<ReminderException>(() => f.Service.SaveAsync(new("A task", Wall(18), ReminderRepeat.Once)));
        Assert.Equal("title", title.Field);
        Assert.Equal("time", time.Field);
        Assert.Empty(f.Db.LoadReminders());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Utc(19);
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    private sealed class Platform : IPlatform
    {
        public bool IsFullscreenBusy => false;
        public bool IsUserIdle => false;
        public bool IsOnBattery => false;
        public bool IsUiVisible => true;
        public List<HubAlert> Delivered { get; } = new();
        public List<string> Withdrawn { get; } = new();
        public Action<HubAlert>? OnNotify { get; set; }
        public void Notify(HubAlert alert) { Delivered.Add(alert); OnNotify?.Invoke(alert); }
        public void WithdrawReminderAlerts(IReadOnlyCollection<string> ids) => Withdrawn.AddRange(ids);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _folder = new();
        public HubDatabase Db { get; }
        public Clock Time { get; } = new();
        public Platform Platform { get; } = new();
        public ReminderService Service { get; private set; }

        public Fixture()
        {
            Db = new(Path.Combine(_folder.Path, "hub.db"));
            Service = Start();
        }
        private ReminderService Start() => new(Db, new HubState(Db), Platform, Time);
        public async Task ReopenAsync() { await Service.StopAsync(); Service = Start(); }

        public void Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db.Path, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Service.StopAsync().GetAwaiter().GetResult();
            HubDatabase.ReleaseFile(Db.Path);
            _folder.Dispose();
        }
    }
}
