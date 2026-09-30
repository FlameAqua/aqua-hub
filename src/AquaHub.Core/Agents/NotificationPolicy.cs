using System.Globalization;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;

namespace AquaHub.Core.Agents;

public enum ToastDecision { Show, Drop, Hold }

/// <summary>
/// Whether an alert becomes a toast now, later, or never. Everything is always kept in the bell; this only
/// decides interruptions. Do-not-disturb and disabled kinds drop the toast (the user asked for silence); quiet
/// hours and full-screen games hold it for one summary afterwards; critical alerts always get through.
/// </summary>
public static class NotificationPolicy
{
    /// <param name="holdReason">"quiet" or "game" when the decision is <see cref="ToastDecision.Hold"/>.</param>
    public static ToastDecision Decide(NotificationSettings n, HubAlert alert, DateTime now, bool fullscreenBusy, out string holdReason)
    {
        holdReason = "";
        if (!n.Enabled || n.DoNotDisturb) return ToastDecision.Drop;
        var kindAllowed = alert.Kind switch
        {
            "price" => n.PriceAlerts,
            "news" => n.BigStories,
            "prediction" => n.PredictionSwings,
            "calendar" => n.CalendarReminders,
            "system" => n.SystemWarnings,
            "brief" => n.BriefReady,
            _ => true,
        };
        if (!kindAllowed) return ToastDecision.Drop;
        if (alert.Severity >= AlertSeverity.Critical) return ToastDecision.Show;
        if (InQuietHours(n, now)) { holdReason = "quiet"; return ToastDecision.Hold; }
        if (n.SuppressWhenFullscreen && fullscreenBusy) { holdReason = "game"; return ToastDecision.Hold; }
        return ToastDecision.Show;
    }

    /// <summary>True inside the quiet window (which may cross midnight, e.g. 23:00–07:00).</summary>
    public static bool InQuietHours(NotificationSettings n, DateTime now)
    {
        if (!TimeOnly.TryParse(n.QuietStart, CultureInfo.InvariantCulture, out var start) ||
            !TimeOnly.TryParse(n.QuietEnd, CultureInfo.InvariantCulture, out var end) || start == end) return false;
        var t = TimeOnly.FromDateTime(now);
        return start < end ? t >= start && t < end : t >= start || t < end;
    }

    /// <summary>The one toast that summarises alerts held during quiet hours or a game.</summary>
    public static (string Title, string Body) Summary(IReadOnlyList<HubAlert> held, string reason)
    {
        var top = held.OrderByDescending(a => a.Severity).ThenByDescending(a => a.Created).ToList();
        if (top.Count == 1) return (top[0].Title, top[0].Body);
        var title = $"While you were {(reason == "game" ? "gaming" : "away")}: {top.Count} alerts";
        var body = string.Join(" · ", top.Take(2).Select(a => a.Title)) + (top.Count > 2 ? $" · +{top.Count - 2} more in the bell" : "");
        return (title, body);
    }
}
