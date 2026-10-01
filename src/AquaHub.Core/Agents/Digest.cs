using System.Globalization;
using System.Text;
using AquaHub.Core.Analysis;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Core.Agents;

/// <summary>Builds compact, factual text digests of the hub state (inputs for the brief and Ask).</summary>
public static class Digest
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Period(DateTimeOffset now) => now.Hour switch
    {
        < 12 => "morning",
        < 17 => "afternoon",
        _ => "evening",
    };

    public static string Weather(WeatherSnapshot? w)
    {
        if (w?.Now is null) return "";
        var today = w.Days.FirstOrDefault();
        var sb = new StringBuilder();
        var (deg, speed) = w.Units == "imperial" ? ("°F", "mph") : ("°C", "km/h");
        sb.Append(Inv, $"Now {w.Now.Temp:0}{deg} ({WeatherSource.Describe(w.Now.Code)}), feels {w.Now.FeelsLike:0}{deg}, wind {w.Now.Wind:0} {speed}");
        if (today is not null)
            sb.Append(Inv, $". Today {today.Min:0}–{today.Max:0}°, {WeatherSource.Describe(today.Code).ToLowerInvariant()}, rain chance {today.PrecipProb}%, UV {today.Uv:0}");
        var rainyHour = w.Hours.FirstOrDefault(h => h.PrecipProb >= 60 && h.Time > DateTimeOffset.Now);
        if (rainyHour is not null) sb.Append(Inv, $". Rain likely from {rainyHour.Time:HH:mm}");
        return sb.ToString();
    }

    public static string Markets(HubState state, MarketSettings m)
    {
        var sb = new StringBuilder();
        var now = DateTimeOffset.Now;
        var headline = m.Indices.Concat(m.Macro).Select(w => state.Quotes.GetValueOrDefault(w.Symbol)).OfType<Quote>().ToList();
        // Tell the model whether markets are trading, so a Sunday brief doesn't say "stocks rise today" about Friday's close.
        var session = MarketSession.Summary(headline, now);
        if (session.Length > 0) sb.Append('(').Append(session).Append(") ");
        foreach (var q in headline)
            sb.Append(Inv, $"{q.Name} {q.ChangePercent:+0.0;-0.0}%{(q.Kind is InstrumentKind.Crypto or InstrumentKind.Fx || MarketSession.IsLive(q) ? "" : " at " + MarketSession.LastClose(q, now))}; ");
        var movers = m.Watchlist.Select(w => state.Quotes.GetValueOrDefault(w.Symbol)).Where(q => q is not null)
            .OrderByDescending(q => Math.Abs(q!.ChangePercent)).Take(4).ToList();
        if (movers.Count > 0)
            sb.Append("Watchlist: ").Append(string.Join(", ", movers.Select(q => string.Create(Inv, $"{q!.Symbol} {q.ChangePercent:+0.0;-0.0}%"))));
        return sb.ToString().Trim().TrimEnd(';');
    }

    public static string Agenda(IEnumerable<HubEvent> events, TimeSpan window)
    {
        var until = DateTimeOffset.Now + window;
        var list = events.Where(e => e.Start >= DateTimeOffset.Now.AddHours(-1) && e.Start <= until).Take(10).ToList();
        return string.Join("\n", list.Select(e =>
            $"- {e.Start.ToLocalTime().ToString(e.AllDay ? "ddd d MMM" : "ddd HH:mm", Inv)} {e.Title}{(e.Detail is null ? "" : " (" + HtmlText.Truncate(e.Detail, 80) + ")")}"));
    }

    /// <summary>Everything the briefing agent needs, in priority order.</summary>
    public static string ForBrief(HubState state, HubSettings s)
    {
        var sb = new StringBuilder();
        var weather = Weather(state.Weather);
        if (weather.Length > 0) sb.Append("WEATHER: ").Append(weather).Append("\n\n");

        sb.Append("TOP STORIES:\n");
        foreach (var c in state.Stories.Where(c => !c.IsLocal).Take(6))
            sb.Append("- ").Append(c.Summary?.Headline is { Length: > 0 } h ? h : c.Title)
              .Append(" — ").Append(c.Summary?.Tldr ?? "").Append(" (").Append(c.SourceCount).Append(" sources)\n");
        sb.Append("\nLOCAL").Append(s.Location.IsSet ? " (" + s.Location.City + ")" : "").Append(":\n");
        foreach (var c in state.Stories.Where(c => c.IsLocal).Take(4))
            sb.Append("- ").Append(c.Summary?.Headline is { Length: > 0 } h ? h : c.Title).Append(" — ").Append(c.Summary?.Tldr ?? "").Append('\n');

        if (state.Pulse is { } pulse)
        {
            sb.Append("\nSOCIAL PULSE: ").Append(pulse.Overview).Append('\n');
            foreach (var t in pulse.Topics.Take(4)) sb.Append("- ").Append(t.Title).Append(": ").Append(t.Summary).Append('\n');
        }

        var markets = Markets(state, s.Markets);
        if (markets.Length > 0) sb.Append("\nMARKETS: ").Append(markets).Append('\n');
        if (state.MarketBrief is { } mb && mb.Overview.Length > 0) sb.Append("Analyst view: ").Append(mb.Overview).Append('\n');

        var agenda = Agenda(state.Events, TimeSpan.FromHours(36));
        if (agenda.Length > 0) sb.Append("\nAGENDA (next 36h):\n").Append(agenda).Append('\n');

        if (state.Foresight is { } f && f.Overview.Length > 0) sb.Append("\nAHEAD: ").Append(f.Overview).Append('\n');
        foreach (var m in state.Predictions.Take(4))
        {
            var lead = m.Lead;
            if (lead is null) continue;
            sb.Append(Inv, $"- Crowd forecast: {m.Title} → {lead.Label} {lead.Probability * 100:0}%\n");
        }
        return sb.ToString();
    }

    /// <summary>A short "current situation" block for Ask (always included).</summary>
    /// <param name="brief">Include today's brief (left out when the question is about a link, so it's never taken for the page's article).</param>
    public static string Situation(HubState state, HubSettings s, bool brief = true)
    {
        var sb = new StringBuilder();
        sb.Append(Inv, $"Now: {DateTimeOffset.Now:dddd d MMMM yyyy HH:mm}")
          .Append(s.Location.IsSet ? $", location {s.Location.Label}.\n" : " (the user hasn't set their location).\n");
        var w = Weather(state.Weather);
        if (w.Length > 0) sb.Append("Weather: ").Append(w).Append('\n');
        var m = Markets(state, s.Markets);
        if (m.Length > 0) sb.Append("Markets: ").Append(m).Append('\n');
        var agenda = Agenda(state.Events, TimeSpan.FromDays(3));
        if (agenda.Length > 0) sb.Append("Agenda:\n").Append(agenda).Append('\n');
        if (brief && state.Brief is { } b && b.Summary.Length > 0) sb.Append("Today's brief: ").Append(b.Summary).Append('\n');
        return sb.ToString();
    }
}

/// <summary>Deterministic, model-free versions of every AI digest so the hub stays useful without a model.</summary>
public static class Fallbacks
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static SocialPulse Pulse(IReadOnlyList<FeedItem> posts, IEnumerable<string>? exclude = null)
    {
        // Feed names (#dublin, r/ireland) trivially dominate their own feeds, so they are excluded as topics.
        var sourceWords = posts.Select(p => p.SourceName.TrimStart('#').Replace("r/", "", StringComparison.OrdinalIgnoreCase));
        var terms = TextTools.TopTerms(posts.Select(p => p.Title + " " + p.Summary), 6, sourceWords.Concat(exclude ?? Enumerable.Empty<string>()));
        var topics = new List<PulseTopic>();
        foreach (var (term, _) in terms)
        {
            var matches = posts.Where(p => TextTools.ContainsPhrase(TextTools.Fold(p.Title + " " + p.Summary).ToLowerInvariant(), term)).ToList();
            if (matches.Count < 2) continue;
            var top = matches.OrderByDescending(p => p.Score + p.Comments).First();
            topics.Add(new PulseTopic
            {
                Title = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(term),
                Summary = top.Title,
                Sentiment = "neutral",
                Heat = Math.Clamp(matches.Count / 2, 1, 5),
                Platforms = matches.Select(p => p.Platform).Distinct().ToList(),
                ItemIds = matches.Take(6).Select(p => p.Id).ToList(),
            });
            if (topics.Count >= 5) break;
        }
        var overview = topics.Count > 0
            ? "Most discussed right now: " + string.Join(", ", topics.Take(3).Select(t => t.Title.ToLowerInvariant())) + "."
            : "Quiet on your feeds right now.";
        return new SocialPulse { Overview = overview, Topics = topics, GeneratedAt = DateTimeOffset.Now, IsAi = false, PostCount = posts.Count };
    }

    public static MarketBrief Market(HubState state, MarketSettings m)
    {
        var overview = Digest.Markets(state, m);
        var insights = new List<WatchInsight>();
        foreach (var w in m.Watchlist)
        {
            if (!state.Indicators.TryGetValue(w.Symbol, out var ind)) continue;
            var stance = ind.Signal switch { TechSignal.Bullish => "bullish", TechSignal.Bearish => "bearish", _ => "neutral" };
            var risks = new List<string>();
            if (ind.Rsi14 > 70) risks.Add("Overbought on RSI — pullback risk");
            if (ind.Volatility20 > 45) risks.Add(string.Create(Inv, $"High volatility ({ind.Volatility20:0}% annualised)"));
            if (ind.Trend == "downtrend") risks.Add("Trend is down — wait for stabilisation");
            insights.Add(new WatchInsight
            {
                Symbol = w.Symbol,
                Stance = stance,
                Confidence = Math.Round(Math.Min(1, Math.Abs(ind.TechScore) / 100.0 + 0.2), 2),
                Summary = string.Create(Inv, $"Technical score {ind.TechScore:+0;-0} ({ind.Trend})."),
                Rationale = ind.Factors.Take(3).ToList(),
                Risks = risks.Take(2).ToList(),
            });
        }
        var movers = m.Watchlist.Select(w => state.Quotes.GetValueOrDefault(w.Symbol)).Where(q => q is not null)
            .OrderByDescending(q => Math.Abs(q!.ChangePercent)).Take(3)
            .Select(q => string.Create(Inv, $"{q!.Name} {q.ChangePercent:+0.0;-0.0}% today")).ToList();
        return new MarketBrief
        {
            Overview = overview.Length > 0 ? overview : "Waiting for market data.",
            Highlights = movers,
            Insights = insights,
            GeneratedAt = DateTimeOffset.Now,
            IsAi = false,
        };
    }

    public static Foresight Foresight(IReadOnlyList<HubEvent> events, IReadOnlyList<PredictionMarket> markets)
    {
        var items = new List<ForesightItem>();
        foreach (var e in events.Where(e => e.Start > DateTimeOffset.Now && e.Start < DateTimeOffset.Now.AddDays(7) && e.Importance >= 2).Take(5))
            items.Add(new ForesightItem
            {
                Title = e.Title,
                When = e.Start.ToLocalTime().ToString(e.AllDay ? "ddd d MMM" : "ddd HH:mm", CultureInfo.CurrentCulture),
                Detail = e.Detail ?? e.Source,
                Kind = "event",
                Importance = e.Importance,
            });
        foreach (var m in markets.Take(3))
        {
            var lead = m.Lead;
            if (lead is null) continue;
            items.Add(new ForesightItem
            {
                Title = m.Title,
                When = m.EndDate?.ToString("d MMM", CultureInfo.CurrentCulture) ?? "",
                Detail = string.Create(Inv, $"Crowd: {lead.Label} {lead.Probability * 100:0}%"),
                Kind = "prediction",
                Importance = 2,
            });
        }
        return new Foresight
        {
            Overview = items.Count == 0 ? "Nothing major on the horizon." : $"{items.Count(i => i.Kind == "event")} key dates this week and {items.Count(i => i.Kind == "prediction")} closely watched forecasts.",
            Items = items,
            GeneratedAt = DateTimeOffset.Now,
            IsAi = false,
        };
    }

    private static string Move(Quote q) => string.Create(Inv, $"{q.Name} {q.ChangePercent:+0.0;-0.0;0.0}%");

    /// <summary>
    /// Readable market lines for the quick-digest brief (the model gets <see cref="Digest.Markets"/> instead):
    /// the main indices with when the figures are from, other assets, then the biggest watchlist moves.
    /// </summary>
    public static List<string> MarketBullets(HubState state, MarketSettings m, DateTimeOffset now)
    {
        var bullets = new List<string>();
        Quote? Find(WatchSymbol w) => state.Quotes.GetValueOrDefault(w.Symbol);
        var indices = m.Indices.Select(Find).OfType<Quote>().Take(5).ToList();
        if (indices.Count > 0)
        {
            var list = string.Join(", ", indices.Select(Move));
            var live = indices.Count(MarketSession.IsLive);
            bullets.Add(live == indices.Count ? $"{list} so far today."
                : live > 0 ? $"{list} — some exchanges are still trading."
                : $"{list} at {MarketSession.LastClose(indices.MaxBy(q => q.Time)!, now)}.");
        }
        var other = m.Macro.Select(Find).OfType<Quote>().Take(5).ToList();
        if (other.Count > 0) bullets.Add("Elsewhere: " + string.Join(", ", other.Select(Move)) + ".");
        var movers = m.Watchlist.Select(Find).OfType<Quote>().OrderByDescending(q => Math.Abs(q.ChangePercent)).Take(4).ToList();
        if (movers.Count > 0)
            bullets.Add("Your watchlist: " + string.Join(", ", movers.Select(q => string.Create(Inv, $"{q.Symbol} {q.ChangePercent:+0.0;-0.0;0.0}%"))) + ".");
        return bullets;
    }

    /// <summary>Readable weather lines for the quick-digest brief: now, the rest of today, and when rain arrives.</summary>
    public static List<string> WeatherBullets(WeatherSnapshot? w, DateTimeOffset now)
    {
        var bullets = new List<string>();
        if (w?.Now is null) return bullets;
        bullets.Add(string.Create(Inv, $"{w.Now.Temp:0}° and {WeatherSource.Describe(w.Now.Code).ToLowerInvariant()} now, feels like {w.Now.FeelsLike:0}°."));
        if (w.Days.FirstOrDefault() is { } today)
        {
            var rain = w.Hours.FirstOrDefault(h => h.PrecipProb >= 60 && h.Time > now && h.Time < now.AddHours(18));
            var line = string.Create(Inv, $"Today {today.Min:0}–{today.Max:0}°, {WeatherSource.Describe(today.Code).ToLowerInvariant()}");
            line += rain is not null ? string.Create(Inv, $"; rain likely from {rain.Time.ToLocalTime():HH:mm}")
                : today.PrecipProb >= 40 ? string.Create(Inv, $"; {today.PrecipProb}% chance of rain")
                : today.PrecipProb >= 20 ? "; a low chance of rain" : "; staying dry";
            bullets.Add(line + (today.Uv >= 6 ? string.Create(Inv, $"; high UV ({today.Uv:0}).") : "."));
        }
        return bullets;
    }

    public static DailyBrief Brief(HubState state, HubSettings s)
    {
        var period = Digest.Period(DateTimeOffset.Now);
        var sections = new List<BriefSection>();
        var weather = WeatherBullets(state.Weather, DateTimeOffset.Now);
        if (weather.Count > 0) sections.Add(new BriefSection { Title = "Weather", Icon = "weather", Bullets = weather });

        var world = state.Stories.Where(c => !c.IsLocal).Take(4)
            .Select(c => (c.Summary?.Headline is { Length: > 0 } h ? h : c.Title)).ToList();
        if (world.Count > 0) sections.Add(new BriefSection { Title = "Top stories", Icon = "news", Bullets = world });

        var local = state.Stories.Where(c => c.IsLocal).Take(3).Select(c => c.Title).ToList();
        if (local.Count > 0) sections.Add(new BriefSection { Title = s.Location.LocalTitle, Icon = "local", Bullets = local });

        var markets = MarketBullets(state, s.Markets, DateTimeOffset.Now);
        if (markets.Count > 0) sections.Add(new BriefSection { Title = "Markets", Icon = "markets", Bullets = markets });

        var agenda = state.Events.Where(e => e.Start > DateTimeOffset.Now && e.Start < DateTimeOffset.Now.AddHours(36)).Take(4)
            .Select(e => $"{e.Start.ToLocalTime().ToString(e.AllDay ? "ddd" : "ddd HH:mm", CultureInfo.CurrentCulture)} · {e.Title}").ToList();
        if (agenda.Count > 0) sections.Add(new BriefSection { Title = "Agenda", Icon = "agenda", Bullets = agenda });

        var name = string.IsNullOrWhiteSpace(s.General.UserName) ? "" : ", " + s.General.UserName;
        return new DailyBrief
        {
            Title = $"Your {period} brief",
            Summary = $"Good {period}{name}. " + (world.Count > 0 ? $"Leading today: {world[0]}." : "Your feeds are warming up."),
            Sections = sections,
            GeneratedAt = DateTimeOffset.Now,
            IsAi = false,
            Period = period,
        };
    }
}
