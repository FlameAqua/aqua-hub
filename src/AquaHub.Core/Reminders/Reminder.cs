namespace AquaHub.Core.Reminders;

public enum ReminderRepeat { Once, Daily, Weekly }
public enum ReminderStatus { Scheduled, Due, Missed, Snoozed }

public sealed record ReminderDraft(string Title, DateTime LocalTime, ReminderRepeat Repeat, string? SceneId = null, int? QuickMinutes = null);
public sealed record ReminderOccurrence(DateTime Nominal, DateTimeOffset At, ReminderStatus Status, int Delivery = 0,
    bool SceneRunRequested = false);
public sealed record Reminder(Guid Id, Guid ScheduleId, ReminderDraft Draft, DateTimeOffset FirstInstant,
    DateTime? NextLocal, ReminderOccurrence? Pending = null);

public sealed record ReminderRow(Reminder Reminder, ReminderOccurrence Occurrence)
{
    public Guid Id => Reminder.Id;
    public bool CanRunScene => Reminder.Draft.SceneId is not null && !Occurrence.SceneRunRequested
        && Occurrence.Status is ReminderStatus.Due or ReminderStatus.Missed;
    public string AlertId => FormattableString.Invariant($"reminder:{Reminder.ScheduleId:N}:{Occurrence.Nominal.Ticks}:{Occurrence.Delivery}");
}

public sealed record ReminderSnapshot(IReadOnlyList<Reminder> Items, TimeZoneInfo Zone, DateOnly Today, bool Loading, string? Error)
{
    public IEnumerable<ReminderRow> AllRows()
    {
        foreach (var item in Items)
        {
            if (item.Pending is { } pending) yield return new(item, pending);
            else if (item.NextLocal is { } next)
                yield return new(item, new(next, ReminderSchedule.At(item, next, Zone), ReminderStatus.Scheduled));
        }
    }

    public IEnumerable<ReminderRow> RowsForDay(DateOnly day)
    {
        foreach (var item in Items)
        {
            if (item.Pending is { } pending && LocalDay(pending.At) == day)
                yield return new(item, pending);
            if (ReminderSchedule.OnDay(item, day, Zone) is { } scheduled)
                yield return new(item, scheduled);
        }
    }

    public DateOnly LocalDay(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);
}

internal static class ReminderSchedule
{
    internal static int Interval(ReminderRepeat repeat) => repeat == ReminderRepeat.Weekly ? 7 : 1;

    internal static DateTimeOffset ToInstant(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            // Compare offsets outside the gap. This also handles transitions shorter than one hour.
            wall += zone.GetUtcOffset(wall.AddDays(1)) - zone.GetUtcOffset(wall.AddDays(-1));
        }
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset).ToUniversalTime();
    }

    internal static DateTimeOffset At(Reminder reminder, DateTime nominal, TimeZoneInfo zone) =>
        reminder.Draft.Repeat == ReminderRepeat.Once ? reminder.FirstInstant : ToInstant(nominal, zone);

    internal static ReminderOccurrence? OnDay(Reminder reminder, DateOnly day, TimeZoneInfo zone)
    {
        if (reminder.NextLocal is not { } first) return null;
        if (reminder.Draft.Repeat == ReminderRepeat.Once)
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reminder.FirstInstant, zone).DateTime) == day
                ? new(first, reminder.FirstInstant, ReminderStatus.Scheduled) : null;

        // A late-night DST gap can move an occurrence onto the following day.
        for (var previous = 0; previous <= 1; previous++)
        {
            if (day.DayNumber < previous) break;
            var nominal = day.AddDays(-previous).ToDateTime(TimeOnly.FromDateTime(first));
            if (nominal < first || (nominal.Date - first.Date).Days % Interval(reminder.Draft.Repeat) != 0) continue;
            var instant = ToInstant(nominal, zone);
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime) == day)
                return new(nominal, instant, ReminderStatus.Scheduled);
        }
        return null;
    }

    internal static DateTime LatestDue(Reminder item, DateTime first, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (item.Draft.Repeat == ReminderRepeat.Once) return first;
        var step = Interval(item.Draft.Repeat);
        var localToday = TimeZoneInfo.ConvertTime(now, zone).Date;
        var days = Math.Max(0, (localToday - first.Date).Days / step) * step;
        var candidate = first.AddDays(days);
        return days > 0 && ToInstant(candidate, zone) > now ? candidate.AddDays(-step) : candidate;
    }
}
