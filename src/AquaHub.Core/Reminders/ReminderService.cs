using System.Text.Json;
using AquaHub.Core.Agents;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Util;
using Microsoft.Data.Sqlite;

namespace AquaHub.Core.Reminders;

public sealed class ReminderException(string message, string? field = null) : Exception(message)
{
    public string? Field { get; } = field;
}

/// <summary>Owns local reminder state. AgentRuntime drives its due check and joins it on exit.</summary>
public sealed class ReminderService
{
    public const int TitleLimit = 120;
    internal const int Capacity = 200;
    private static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(5);
    private readonly HubDatabase _database;
    private readonly HubState _state;
    private readonly IPlatform _platform;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Reminder[]? _items;
    private ReminderSnapshot _snapshot;
    private long _nextCheck;
    private long _clockRevision;
    private long _checkedRevision;
    private volatile bool _stopping;
    private int _queued;
    private Task _tail = Task.CompletedTask;
    private Task? _check;

    public ReminderService(HubDatabase database, HubState state, IPlatform platform, TimeProvider? time = null)
    {
        _database = database;
        _state = state;
        _platform = platform;
        _time = time ?? TimeProvider.System;
        var zone = _time.LocalTimeZone;
        _snapshot = new([], zone, Day(_time.GetUtcNow(), zone), Loading: true, Error: null);
    }

    public ReminderSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action? Changed;

    public void ClockChanged() => Interlocked.Increment(ref _clockRevision);

    internal void CheckIfDue()
    {
        if (_time.GetUtcNow().UtcTicks < Interlocked.Read(ref _nextCheck)
            && Interlocked.Read(ref _checkedRevision) == Interlocked.Read(ref _clockRevision)) return;
        lock (_gate)
        {
            if (_stopping || _queued >= 8 || _check is { IsCompleted: false }) return;
            _check = Queue(Reconcile, CancellationToken.None);
        }
    }

    public Task RefreshAsync(CancellationToken ct = default) => Queue(Reconcile, ct);

    public Task<Reminder> SaveAsync(ReminderDraft draft, Reminder? original = null, CancellationToken ct = default) =>
        Queue(() => Change((items, now, zone, withdrawn) =>
        {
            var title = draft.Title?.Trim() ?? "";
            if (title.Length is 0 or > TitleLimit || title.Any(char.IsControl))
                throw new ReminderException($"Enter a title of 1–{TitleLimit} characters.", "title");
            if (!ValidSceneId(draft.SceneId))
                throw new ReminderException("Choose a saved scene or leave the scene blank.", "scene");
            draft = draft with { Title = title, LocalTime = DateTime.SpecifyKind(draft.LocalTime, DateTimeKind.Unspecified) };
            Reminder? current = original is null ? null : Find(items, original);
            if (current is not null && current.Draft.LocalTime == draft.LocalTime && current.Draft.Repeat == draft.Repeat)
            {
                if (current.Draft.Title != draft.Title) Withdraw(current, withdrawn);
                var updated = current with { Draft = draft };
                items[items.IndexOf(current)] = updated;
                return updated;
            }
            if (!Enum.IsDefined(draft.Repeat) || draft.LocalTime.Year is < 2 or > 9998
                || (draft.Repeat == ReminderRepeat.Once && zone.IsInvalidTime(draft.LocalTime)))
                throw new ReminderException("Choose a valid date and time on this PC.", "time");
            var instant = ReminderSchedule.ToInstant(draft.LocalTime, zone);
            if (instant <= now || instant > now.AddYears(5))
                throw new ReminderException("Choose a time in the future, within five years.", "time");
            var reminder = new Reminder(current?.Id ?? Guid.NewGuid(), Guid.NewGuid(), draft, instant, draft.LocalTime);
            if (current is null) Add(items, reminder);
            else
            {
                Withdraw(current, withdrawn);
                items[items.IndexOf(current)] = reminder;
            }
            return reminder;
        }), ct);

    public Task<Reminder> SaveQuickAsync(int minutes, CancellationToken ct = default) =>
        Queue(() => Change((items, now, zone, _) =>
        {
            if (minutes is not (5 or 15 or 30))
                throw new ReminderException("Choose a 5-, 15- or 30-minute reminder.");

            var instant = now.AddMinutes(minutes);
            var local = TimeZoneInfo.ConvertTime(instant, zone);
            // Keep the editor's minute precision while retaining the exact elapsed deadline.
            var nominal = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
            var draft = new ReminderDraft($"Quick reminder · {minutes} min", nominal, ReminderRepeat.Once);
            var reminder = new Reminder(Guid.NewGuid(), Guid.NewGuid(), draft, instant, nominal);
            Add(items, reminder);
            return reminder;
        }), ct);

    public Task DeleteAsync(Reminder original, CancellationToken ct = default) =>
        Queue(() => Change((items, _, _, withdrawn) =>
        {
            var current = Find(items, original);
            Withdraw(current, withdrawn);
            return items.Remove(current);
        }), ct);

    public Task DoneAsync(ReminderRow row, CancellationToken ct = default) => ChangeOccurrenceAsync(row, snooze: false, ct);
    public Task SnoozeAsync(ReminderRow row, CancellationToken ct = default) => ChangeOccurrenceAsync(row, snooze: true, ct);

    public Task ClaimSceneRunAsync(ReminderRow row, CancellationToken ct = default) =>
        Queue(() => Change((items, _, _, _) =>
        {
            var current = Find(items, row.Reminder);
            if (!row.CanRunScene || current.Draft.SceneId != row.Reminder.Draft.SceneId || current.Pending != row.Occurrence)
                throw new ReminderException("This scene reminder changed. Open it again to review it.");

            items[items.IndexOf(current)] = current with { Pending = row.Occurrence with { SceneRunRequested = true } };
            return true;
        }), ct);

    private Task ChangeOccurrenceAsync(ReminderRow row, bool snooze, CancellationToken ct) =>
        Queue(() => Change((items, now, _, withdrawn) =>
        {
            var current = Find(items, row.Reminder);
            if (current.Pending is not { } pending || pending != row.Occurrence)
                throw new ReminderException("This occurrence has changed. Select it again.");
            if (snooze && pending.Status == ReminderStatus.Snoozed)
                throw new ReminderException("This occurrence is already snoozed.");
            Withdraw(current, withdrawn);
            if (snooze)
            {
                pending = pending with { At = now.AddMinutes(10), Status = ReminderStatus.Snoozed, Delivery = pending.Delivery + 1 };
                items[items.IndexOf(current)] = current with { Pending = pending };
            }
            else if (current.NextLocal is null) items.Remove(current);
            else items[items.IndexOf(current)] = current with { Pending = null };
            return true;
        }), ct);

    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return _tail;
        }
    }

    private Task<T> Queue<T>(Func<T> work, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_stopping) return Task.FromCanceled<T>(new CancellationToken(true));
            if (_queued >= 8) return Task.FromException<T>(new ReminderException("Aqua is busy saving reminders. Try again."));
            _queued++;
            var previous = _tail;
            var next = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (_stopping) throw new OperationCanceledException();
                // Once work starts, finish the transaction and publication together.
                return work();
            });
            _tail = ObserveAsync(next);
            return next;
        }
    }

    private async Task ObserveAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ReminderException) { }
        catch (Exception ex) { Log.Warn("reminders", $"Unexpected reminder operation failure ({ex.GetType().Name})."); }
        finally { lock (_gate) _queued--; }
    }

    private T Change<T>(Func<List<Reminder>, DateTimeOffset, TimeZoneInfo, List<string>, T> mutate) => InStore((now, zone) =>
    {
        var items = _items!.ToList();
        var withdrawn = new List<string>();
        var result = mutate(items, now, zone, withdrawn);
        Commit(items.ToArray(), withdrawn, [], now, zone);
        return result;
    });

    private bool Reconcile()
    {
        var revision = Interlocked.Read(ref _clockRevision);
        try
        {
            return InStore((now, zone) =>
            {
                var items = (Reminder[])_items!.Clone();
                var withdrawn = new List<string>();
                var alerts = new List<HubAlert>();
                for (var index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    if (item.NextLocal is { } first && ReminderSchedule.At(item, first, zone) <= now)
                    {
                        Withdraw(item, withdrawn);
                        var nominal = ReminderSchedule.LatestDue(item, first, now, zone);
                        var at = ReminderSchedule.At(item, nominal, zone);
                        item = item with
                        {
                            NextLocal = item.Draft.Repeat == ReminderRepeat.Once ? null : nominal.AddDays(ReminderSchedule.Interval(item.Draft.Repeat)),
                            Pending = new(nominal, at, ReminderStatus.Scheduled),
                        };
                    }
                    if (item.Pending is { Status: ReminderStatus.Scheduled or ReminderStatus.Snoozed } pending && pending.At <= now)
                    {
                        pending = pending with { Status = now - pending.At <= CatchUp ? ReminderStatus.Due : ReminderStatus.Missed };
                        item = item with { Pending = pending };
                        if (pending.Status == ReminderStatus.Due) alerts.Add(Alert(item, pending, now));
                    }
                    items[index] = item;
                }
                if (!items.SequenceEqual(_items!)) Commit(items, withdrawn, alerts, now, zone);
                else Publish(now, zone);
                return true;
            });
        }
        catch (ReminderException) { return false; }
        finally { Interlocked.Exchange(ref _checkedRevision, revision); }
    }

    private T InStore<T>(Func<DateTimeOffset, TimeZoneInfo, T> operation)
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        try
        {
            if (_items is null)
            {
                var saved = _database.LoadReminders();
                if (saved.Length > Capacity || saved.Any(item => !IsValid(item))
                    || saved.Select(item => item.Id).Distinct().Count() != saved.Length
                    || saved.Select(item => item.ScheduleId).Distinct().Count() != saved.Length)
                    throw new JsonException();
                _items = saved;
            }
            return operation(now, zone);
        }
        catch (Exception ex) when (ex is SqliteException or JsonException)
        {
            Log.Warn("reminders", $"Local reminder storage unavailable ({ex.GetType().Name}).");
            Interlocked.Exchange(ref _nextCheck, now.AddSeconds(30).UtcTicks);
            SetSnapshot(now, zone, "Aqua couldn’t access saved reminders. Try again.");
            throw new ReminderException("Aqua couldn’t access saved reminders. Try again.");
        }
    }

    private static bool IsValid(Reminder? item) => item is
    {
        Id: var id, ScheduleId: var schedule, Draft: { Title.Length: > 0 and <= TitleLimit, LocalTime.Year: >= 2 and <= 9998 } draft,
    } && id != Guid.Empty && schedule != Guid.Empty && Enum.IsDefined(draft.Repeat) && !draft.Title.Any(char.IsControl)
        && ValidSceneId(draft.SceneId)
        && (item.NextLocal is null || item.NextLocal.Value.Year is >= 2 and <= 9998)
        && (item.Pending is null || item.Pending is { Nominal.Year: >= 2 and <= 9998, Delivery: >= 0 and < int.MaxValue }
            && item.Pending.Status is ReminderStatus.Due or ReminderStatus.Missed or ReminderStatus.Snoozed)
        && (item.NextLocal is not null || item.Pending is not null);

    private static bool ValidSceneId(string? id) =>
        id is null || (id.Length <= 128 && !string.IsNullOrWhiteSpace(id) && !id.Any(char.IsControl));

    private void Commit(Reminder[] items, List<string> withdrawn, List<HubAlert> alerts, DateTimeOffset now, TimeZoneInfo zone)
    {
        var inserted = _database.SaveReminders(items, withdrawn, alerts);
        _items = items;
        Publish(now, zone);
        if (withdrawn.Count == 0 && inserted.Count == 0) return;
        try
        {
            _platform.WithdrawReminderAlerts(withdrawn);
            _state.ReloadAlerts();
        }
        catch (Exception ex) { Log.Warn("reminders", $"Could not refresh the bell ({ex.GetType().Name})."); }
        foreach (var alert in inserted)
        {
            try { _platform.Notify(alert); }
            catch (Exception ex) { Log.Warn("reminders", $"Could not request a notification ({ex.GetType().Name})."); }
        }
    }

    private void Publish(DateTimeOffset now, TimeZoneInfo zone)
    {
        var tomorrow = Day(now, zone).AddDays(1).ToDateTime(TimeOnly.MinValue);
        var next = ReminderSchedule.ToInstant(tomorrow, zone);
        foreach (var item in _items!)
        {
            if (item.NextLocal is { } start) next = Min(next, ReminderSchedule.At(item, start, zone));
            if (item.Pending is { Status: ReminderStatus.Snoozed } pending) next = Min(next, pending.At);
        }
        Interlocked.Exchange(ref _nextCheck, next.UtcTicks);
        SetSnapshot(now, zone, null);
    }

    private void SetSnapshot(DateTimeOffset now, TimeZoneInfo zone, string? error)
    {
        var current = Snapshot;
        var today = Day(now, zone);
        if (!current.Loading && current.Error == error && current.Today == today && current.Zone.Equals(zone)
            && (_items is null ? current.Items.Count == 0 : current.Items.SequenceEqual(_items))) return;
        Volatile.Write(ref _snapshot, new(_items is null ? [] : Array.AsReadOnly(_items), zone, today, Loading: false, error));
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Warn("reminders", $"Could not publish reminder state ({ex.GetType().Name})."); }
    }

    private static DateOnly Day(DateTimeOffset now, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static Reminder Find(List<Reminder> items, Reminder original) =>
        items.Find(item => item.Id == original.Id && item.ScheduleId == original.ScheduleId)
        ?? throw new ReminderException("This reminder changed or was deleted. Open it again.");

    private static void Add(List<Reminder> items, Reminder reminder)
    {
        if (items.Count >= Capacity)
            throw new ReminderException($"You have {Capacity} reminders. Delete or finish one to add another.");
        items.Add(reminder);
    }

    private static void Withdraw(Reminder item, List<string> withdrawn)
    {
        if (item.Pending is { } pending) withdrawn.Add(new ReminderRow(item, pending).AlertId);
    }

    private static HubAlert Alert(Reminder item, ReminderOccurrence pending, DateTimeOffset now) => new()
    {
        Id = new ReminderRow(item, pending).AlertId, Kind = "reminder", Severity = AlertSeverity.Notice,
        Title = item.Draft.Title, Body = "Reminder due", Target = $"upcoming:reminder:{item.Id:N}", Created = now,
    };
}
