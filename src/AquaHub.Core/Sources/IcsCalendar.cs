using System.Globalization;
using System.Text;
using AquaHub.Core.Models;
using AquaHub.Core.Util;

namespace AquaHub.Core.Sources;

/// <summary>
/// Minimal RFC 5545 (iCalendar) reader: VEVENTs with TZID / UTC / all-day times, DURATION, EXDATE,
/// RECURRENCE-ID overrides and the common RRULE subset (DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL,
/// COUNT, UNTIL, BYDAY, BYMONTHDAY). Recurrences are expanded in the event's own time zone so they
/// stay correct across daylight-saving changes.
/// </summary>
public static class IcsCalendar
{
    private sealed class Prop
    {
        public string Name = "";
        public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
        public string Value = "";
    }

    private sealed class VEvent
    {
        public string Uid = "";
        public string Summary = "";
        public string? Location;
        public string? Description;
        public string? Url;
        public DateTime Start;
        public DateTime? End;
        public TimeSpan? Duration;
        public TimeZoneInfo Zone = TimeZoneInfo.Local;
        public bool AllDay;
        public string? RRule;
        public List<DateTime> ExDates = new();
        public DateTime? RecurrenceId;
        public bool Cancelled;
    }

    public static List<HubEvent> Parse(string ics, string sourceName, string? color, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var events = new List<VEvent>();
        VEvent? current = null;
        foreach (var prop in ReadProps(ics))
        {
            if (prop.Name == "BEGIN" && prop.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) { current = new VEvent(); continue; }
            if (prop.Name == "END" && prop.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null && current.Start != default) events.Add(current);
                current = null;
                continue;
            }
            if (current is null) continue;
            switch (prop.Name)
            {
                case "UID": current.Uid = prop.Value; break;
                case "SUMMARY": current.Summary = Unescape(prop.Value); break;
                case "LOCATION": current.Location = Unescape(prop.Value); break;
                case "DESCRIPTION": current.Description = Unescape(prop.Value); break;
                case "URL": current.Url = prop.Value; break;
                case "STATUS": current.Cancelled = prop.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "RRULE": current.RRule = prop.Value; break;
                case "DURATION": current.Duration = ParseDuration(prop.Value); break;
                case "DTSTART":
                    {
                        var (dt, zone, allDay) = ParseDate(prop);
                        current.Start = dt; current.Zone = zone; current.AllDay = allDay;
                        break;
                    }
                case "DTEND":
                    current.End = ParseDate(prop).Time;
                    break;
                case "EXDATE":
                    foreach (var part in prop.Value.Split(','))
                        current.ExDates.Add(ParseDate(new Prop { Name = "EXDATE", Params = prop.Params, Value = part }).Time);
                    break;
                case "RECURRENCE-ID":
                    current.RecurrenceId = ParseDate(prop).Time;
                    break;
            }
        }

        var overrides = events.Where(e => e.RecurrenceId is not null)
            .ToLookup(e => e.Uid);
        var result = new List<HubEvent>();
        foreach (var ev in events.Where(e => e.RecurrenceId is null && !e.Cancelled))
        {
            var length = ev.End is { } end ? end - ev.Start : ev.Duration ?? (ev.AllDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1));
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            var replaced = overrides[ev.Uid].Where(o => o.RecurrenceId is not null).Select(o => o.RecurrenceId!.Value).ToHashSet();
            foreach (var occurrence in Occurrences(ev, windowStart, windowEnd, length))
            {
                if (ev.ExDates.Any(x => Math.Abs((x - occurrence).TotalMinutes) < 1)) continue;
                if (replaced.Contains(occurrence)) continue;
                result.Add(ToHubEvent(ev, occurrence, length, sourceName, color));
            }
        }
        foreach (var ov in overrides.SelectMany(g => g).Where(o => !o.Cancelled))
        {
            var length = ov.End is { } end ? end - ov.Start : ov.Duration ?? TimeSpan.FromHours(1);
            var start = ToOffset(ov.Start, ov.Zone, ov.AllDay);
            if (start + length >= windowStart && start <= windowEnd)
                result.Add(ToHubEvent(ov, ov.Start, length, sourceName, color));
        }
        return result.OrderBy(e => e.Start).ToList();
    }

    private static HubEvent ToHubEvent(VEvent ev, DateTime localStart, TimeSpan length, string source, string? color)
    {
        var start = ToOffset(localStart, ev.Zone, ev.AllDay);
        return new HubEvent
        {
            Id = "cal" + Hash.Short(ev.Uid, localStart.ToString("o", CultureInfo.InvariantCulture)),
            Title = string.IsNullOrWhiteSpace(ev.Summary) ? "(untitled event)" : ev.Summary,
            Start = start,
            End = start + length,
            AllDay = ev.AllDay,
            Location = ev.Location,
            Detail = ev.Description is null ? null : HtmlText.Truncate(ev.Description.Replace('\n', ' '), 200),
            Url = ev.Url is not null && ev.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? ev.Url : null,
            Kind = EventKind.Calendar,
            Source = source,
            Importance = 2,
            Color = color,
        };
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone, bool allDay)
    {
        if (allDay) zone = TimeZoneInfo.Local;
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    private static IEnumerable<DateTime> Occurrences(VEvent ev, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeSpan length)
    {
        bool InWindow(DateTime local)
        {
            var s = ToOffset(local, ev.Zone, ev.AllDay);
            return s + length >= windowStart && s <= windowEnd;
        }

        if (string.IsNullOrEmpty(ev.RRule))
        {
            if (InWindow(ev.Start)) yield return ev.Start;
            yield break;
        }

        var rule = ev.RRule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        var freq = rule.GetValueOrDefault("FREQ", "DAILY").ToUpperInvariant();
        var interval = int.TryParse(rule.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(rule.GetValueOrDefault("COUNT"), out var cnt) ? cnt : null;
        DateTime? until = rule.TryGetValue("UNTIL", out var u) ? ParseDate(new Prop { Name = "UNTIL", Value = u }).Time : null;
        var byDay = rule.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(ParseByDay).Where(x => x.Day is not null).ToList() : new();
        var byMonthDay = rule.TryGetValue("BYMONTHDAY", out var bmd)
            ? bmd.Split(',').Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v != 0).ToList() : new();

        var endLocal = TimeZoneInfo.ConvertTime(windowEnd, ev.Zone).DateTime;
        var produced = 0;
        var guard = 0;

        IEnumerable<DateTime> Candidates()
        {
            var period = 0;
            while (guard++ < 5000)
            {
                switch (freq)
                {
                    case "DAILY":
                        yield return ev.Start.AddDays((double)period * interval);
                        break;
                    case "WEEKLY":
                        {
                            var weekStart = ev.Start.Date.AddDays(-(((int)ev.Start.DayOfWeek + 6) % 7)).AddDays(7.0 * period * interval);
                            if (byDay.Count == 0) yield return ev.Start.AddDays(7.0 * period * interval);
                            else
                                foreach (var d in byDay.Select(b => b.Day!.Value).OrderBy(d => ((int)d + 6) % 7))
                                    yield return weekStart.AddDays(((int)d + 6) % 7) + ev.Start.TimeOfDay;
                            break;
                        }
                    case "MONTHLY":
                        {
                            var month = new DateTime(ev.Start.Year, ev.Start.Month, 1).AddMonths(period * interval);
                            var days = new List<DateTime>();
                            if (byMonthDay.Count > 0)
                            {
                                var dim = DateTime.DaysInMonth(month.Year, month.Month);
                                foreach (var md in byMonthDay)
                                {
                                    var day = md > 0 ? md : dim + md + 1;
                                    if (day >= 1 && day <= dim) days.Add(month.AddDays(day - 1) + ev.Start.TimeOfDay);
                                }
                            }
                            else if (byDay.Count > 0)
                            {
                                foreach (var b in byDay) days.AddRange(NthWeekdays(month, b.Day!.Value, b.Nth).Select(d => d + ev.Start.TimeOfDay));
                            }
                            else if (ev.Start.Day <= DateTime.DaysInMonth(month.Year, month.Month))
                            {
                                days.Add(month.AddDays(ev.Start.Day - 1) + ev.Start.TimeOfDay);
                            }
                            foreach (var d in days.OrderBy(x => x)) yield return d;
                            break;
                        }
                    case "YEARLY":
                        {
                            var year = ev.Start.Year + period * interval;
                            if (ev.Start.Month == 2 && ev.Start.Day == 29 && !DateTime.IsLeapYear(year)) break;
                            yield return new DateTime(year, ev.Start.Month, ev.Start.Day) + ev.Start.TimeOfDay;
                            break;
                        }
                    default:
                        yield break;
                }
                period++;
            }
        }

        foreach (var occ in Candidates())
        {
            if (occ < ev.Start) continue;
            if (until is not null && occ > until.Value) yield break;
            if (occ > endLocal) yield break;
            produced++;
            if (count is not null && produced > count) yield break;
            if (InWindow(occ)) yield return occ;
        }
    }

    private static IEnumerable<DateTime> NthWeekdays(DateTime month, DayOfWeek day, int nth)
    {
        var all = Enumerable.Range(0, DateTime.DaysInMonth(month.Year, month.Month))
            .Select(i => month.AddDays(i)).Where(d => d.DayOfWeek == day).ToList();
        if (nth == 0) return all;
        var idx = nth > 0 ? nth - 1 : all.Count + nth;
        return idx >= 0 && idx < all.Count ? new[] { all[idx] } : Array.Empty<DateTime>();
    }

    private static (DayOfWeek? Day, int Nth) ParseByDay(string token)
    {
        token = token.Trim().ToUpperInvariant();
        if (token.Length < 2) return (null, 0);
        var code = token[^2..];
        var num = token[..^2];
        DayOfWeek? day = code switch
        {
            "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
        };
        return (day, int.TryParse(num, out var n) ? n : 0);
    }

    private static (DateTime Time, TimeZoneInfo Zone, bool AllDay) ParseDate(Prop p)
    {
        var value = p.Value.Trim();
        var zone = TimeZoneInfo.Local;
        if (p.Params.TryGetValue("TZID", out var tzid))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(tzid.Trim('"')); }
            catch { zone = TimeZoneInfo.Local; }
        }
        if (value.Length == 8 && DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (date, TimeZoneInfo.Local, true);
        var utc = value.EndsWith('Z');
        var raw = utc ? value[..^1] : value;
        if (DateTime.TryParseExact(raw, new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            if (utc)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeZoneInfo.Local);
                return (DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local, false);
            }
            return (dt, zone, false);
        }
        return (default, zone, false);
    }

    private static TimeSpan? ParseDuration(string value)
    {
        // P1D, PT1H30M, P1W, -PT15M
        var s = value.Trim().ToUpperInvariant();
        var negative = s.StartsWith('-');
        s = s.TrimStart('-', '+');
        if (!s.StartsWith('P')) return null;
        var total = TimeSpan.Zero;
        var num = new StringBuilder();
        var inTime = false;
        foreach (var ch in s[1..])
        {
            if (char.IsDigit(ch)) { num.Append(ch); continue; }
            if (ch == 'T') { inTime = true; continue; }
            var n = num.Length > 0 ? int.Parse(num.ToString(), CultureInfo.InvariantCulture) : 0;
            num.Clear();
            total += ch switch
            {
                'W' => TimeSpan.FromDays(7 * n),
                'D' => TimeSpan.FromDays(n),
                'H' => TimeSpan.FromHours(n),
                'M' when inTime => TimeSpan.FromMinutes(n),
                'S' => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero,
            };
        }
        return negative ? -total : total;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static IEnumerable<Prop> ReadProps(string ics)
    {
        // Unfold continuation lines (RFC 5545 §3.1).
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && lines.Count > 0) lines[^1] += raw[1..];
            else lines.Add(raw);
        }
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var colon = FindValueColon(line);
            if (colon < 0) continue;
            var head = line[..colon];
            var prop = new Prop { Value = line[(colon + 1)..] };
            var parts = head.Split(';');
            prop.Name = parts[0].ToUpperInvariant();
            foreach (var param in parts.Skip(1))
            {
                var eq = param.IndexOf('=');
                if (eq > 0) prop.Params[param[..eq]] = param[(eq + 1)..];
            }
            yield return prop;
        }
    }

    private static int FindValueColon(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }
}
