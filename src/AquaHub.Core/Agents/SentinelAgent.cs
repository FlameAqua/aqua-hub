using System.Globalization;
using AquaHub.Core.Analysis;
using AquaHub.Core.Models;
using AquaHub.Core.Util;

namespace AquaHub.Core.Agents;

/// <summary>
/// Watches everything the other agents produce and raises alerts that are worth an interruption:
/// big price moves / price targets, fast-developing multi-source stories, your tracked keywords,
/// prediction-market swings, upcoming calendar items and PC health. Every alert is de-duplicated.
/// </summary>
public sealed class SentinelAgent : Agent
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private int _ramHotChecks;
    private DateTimeOffset _started = DateTimeOffset.Now;

    public override string Id => "sentinel";
    public override string Name => "Sentinel";
    public override string Role => "sentinel";
    public override string Description => "Raises de-duplicated alerts for big market moves, price targets, breaking multi-source stories, your keywords, prediction swings, reminders and PC health.";
    public override bool Stretchable => false;
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(20);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(1);

    public override Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var n = ctx.S.Notifications;
        var raised = new List<HubAlert>();
        var now = DateTimeOffset.Now;
        var day = now.ToString("yyyyMMdd", Inv);

        void Raise(string key, HubAlert alert)
        {
            if (!ctx.Db.TryMarkSeen(key)) return;
            if (ctx.Db.AddAlert(alert)) raised.Add(alert);
        }

        // ── Markets ──
        if (n.PriceAlerts)
        {
            var threshold = ctx.S.Markets.AlertMovePercent;
            foreach (var w in ctx.S.Markets.Watchlist.Concat(ctx.S.Markets.Indices))
            {
                if (!ctx.State.Quotes.TryGetValue(w.Symbol, out var q) || q.PreviousClose <= 0) continue;
                var move = q.ChangePercent;
                if (Math.Abs(move) >= threshold && q.MarketState == "open")
                {
                    var dir = move > 0 ? "up" : "down";
                    Raise($"move:{w.Symbol}:{day}:{dir}", new HubAlert
                    {
                        Id = Hash.Short("move", w.Symbol, day, dir),
                        Kind = "price",
                        Severity = Math.Abs(move) >= threshold * 2 ? AlertSeverity.Important : AlertSeverity.Notice,
                        Title = string.Create(Inv, $"{q.Name} {move:+0.0;-0.0}% today"),
                        Body = string.Create(Inv, $"{q.Symbol} at {q.Price:0.##} {q.Currency} (prev. close {q.PreviousClose:0.##})."),
                        Target = "markets:" + w.Symbol,
                        Created = now,
                    });
                }
                if (w.AlertAbove is { } above && q.Price >= above)
                    Raise($"above:{w.Symbol}:{above}:{day}", new HubAlert
                    {
                        Id = Hash.Short("above", w.Symbol, day), Kind = "price", Severity = AlertSeverity.Important,
                        Title = string.Create(Inv, $"{q.Name} crossed above {above:0.##}"),
                        Body = string.Create(Inv, $"Now {q.Price:0.##} {q.Currency}."), Target = "markets:" + w.Symbol, Created = now,
                    });
                if (w.AlertBelow is { } below && q.Price > 0 && q.Price <= below)
                    Raise($"below:{w.Symbol}:{below}:{day}", new HubAlert
                    {
                        Id = Hash.Short("below", w.Symbol, day), Kind = "price", Severity = AlertSeverity.Important,
                        Title = string.Create(Inv, $"{q.Name} fell below {below:0.##}"),
                        Body = string.Create(Inv, $"Now {q.Price:0.##} {q.Currency}."), Target = "markets:" + w.Symbol, Created = now,
                    });
            }
        }

        // ── Breaking, widely covered stories (skip the first minutes after launch to avoid a burst) ──
        if (n.BigStories && now - _started > TimeSpan.FromMinutes(3))
        {
            foreach (var c in ctx.State.Stories.Take(20))
            {
                var fresh = now - c.FirstSeen < TimeSpan.FromHours(2);
                if (!fresh || c.SourceCount < 4 || c.Items.Min(i => i.Tier) > 1) continue;
                Raise("story:" + c.Id, new HubAlert
                {
                    Id = Hash.Short("story", c.Id), Kind = "news", Severity = AlertSeverity.Notice,
                    Title = "Developing: " + HtmlText.Truncate(c.Summary?.Headline is { Length: > 0 } h ? h : c.Title, 90),
                    Body = $"Covered by {string.Join(", ", c.SourceNames.Take(4))}.",
                    Url = c.Url, Target = "news:" + c.Id, Created = now,
                });
            }
        }

        // ── Your keywords ──
        if (n.Keywords.Count > 0)
        {
            var recent = ctx.State.Stories.SelectMany(c => c.Items).Concat(ctx.State.Social)
                .Where(i => now - i.Published < TimeSpan.FromHours(3));
            foreach (var item in recent)
            {
                var text = TextTools.Fold(item.Title + " " + item.Summary).ToLowerInvariant();
                var hit = n.Keywords.FirstOrDefault(k => TextTools.ContainsPhrase(text, k));
                if (hit is null) continue;
                Raise($"kw:{hit}:{Hash.Short(item.Title)}", new HubAlert
                {
                    Id = Hash.Short("kw", item.Id), Kind = "keyword", Severity = AlertSeverity.Notice,
                    Title = $"“{hit}” — {HtmlText.Truncate(item.Title, 90)}",
                    Body = $"{item.SourceName} · {TimeText.AgoPhrase(item.Published, now)}", Url = item.Url ?? item.CommentsUrl, Created = now,
                });
            }
        }

        // ── Prediction swings ──
        if (n.PredictionSwings)
        {
            var points = ctx.S.Predictions.SwingAlertPoints / 100.0;
            foreach (var m in ctx.State.Predictions)
            {
                var o = m.Outcomes.OrderByDescending(x => Math.Abs(x.Change24h ?? 0)).FirstOrDefault();
                if (o?.Change24h is not { } ch || Math.Abs(ch) < points) continue;
                Raise($"pm:{m.Id}:{o.Label}:{day}", new HubAlert
                {
                    Id = Hash.Short("pm", m.Id, day), Kind = "prediction", Severity = AlertSeverity.Notice,
                    Title = string.Create(Inv, $"Odds shift: {HtmlText.Truncate(m.Title, 70)}"),
                    Body = string.Create(Inv, $"{o.Label} now {o.Probability * 100:0}% ({ch * 100:+0;-0} pts in 24h) on {m.Source}."),
                    Url = m.Url, Target = "upcoming", Created = now,
                });
            }
        }

        // ── Reminders ──
        if (n.CalendarReminders && ctx.S.Events.ReminderMinutes > 0)
        {
            var lead = TimeSpan.FromMinutes(ctx.S.Events.ReminderMinutes);
            foreach (var e in ctx.State.Events.Where(e => !e.AllDay && e.Kind is EventKind.Calendar or EventKind.Economic))
            {
                var until = e.Start - now;
                if (until <= TimeSpan.Zero || until > lead) continue;
                if (e.Kind == EventKind.Economic && e.Importance < 3) continue;
                Raise("rem:" + e.Id, new HubAlert
                {
                    Id = Hash.Short("rem", e.Id), Kind = "calendar", Severity = AlertSeverity.Important,
                    Title = $"{e.Title} {TimeText.Until(e.Start, now)}",
                    Body = string.Join(" · ", new[] { e.Start.ToLocalTime().ToString("HH:mm", Inv), e.Location, e.Detail }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    Url = e.Url, Target = "upcoming", Created = now,
                });
            }
        }

        // ── PC health ──
        if (n.SystemWarnings && ctx.State.System is { } sys)
        {
            foreach (var d in sys.Disks.Where(d => d.TotalGb > 20 && d.FreeGb / d.TotalGb < 0.08))
                Raise($"disk:{d.Name}:{day}", new HubAlert
                {
                    Id = Hash.Short("disk", d.Name, day), Kind = "system", Severity = AlertSeverity.Important,
                    Title = string.Create(Inv, $"Drive {d.Name} is almost full"),
                    Body = string.Create(Inv, $"{d.FreeGb:0.#} GB free of {d.TotalGb:0} GB."), Target = "system", Created = now,
                });
            _ramHotChecks = sys.RamPercent >= 92 ? _ramHotChecks + 1 : 0;
            if (_ramHotChecks >= 3)
                Raise($"ram:{now:yyyyMMddHH}", new HubAlert
                {
                    Id = Hash.Short("ram", now.ToString("yyyyMMddHH", Inv)), Kind = "system", Severity = AlertSeverity.Notice,
                    Title = string.Create(Inv, $"Memory pressure: {sys.RamPercent:0}% in use"),
                    Body = sys.TopProcesses.Count > 0 ? $"Top: {string.Join(", ", sys.TopProcesses.OrderByDescending(p => p.MemoryMb).Take(3).Select(p => p.Name))}" : "",
                    Target = "system", Created = now,
                });
        }

        if (raised.Count > 0)
        {
            ctx.State.ReloadAlerts();
            foreach (var a in raised) ctx.Platform.Notify(a);
        }
        return Task.FromResult(raised.Count > 0 ? AgentResult.Success($"{raised.Count} new alert(s)") : AgentResult.Unchanged("All quiet"));
    }
}
