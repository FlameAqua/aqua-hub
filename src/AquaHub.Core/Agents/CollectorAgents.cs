using AquaHub.Core.Analysis;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Core.Agents;

/// <summary>Collects headlines from reliable outlets (wire services, broadcasters, local press).</summary>
public sealed class NewsScoutAgent : Agent
{
    public override string Id => "news-scout";
    public override string Name => "News Scout";
    public override string Description => "Fetches headlines from wire services, broadcasters and local press using conditional requests.";
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(ctx.S.News.RefreshMinutes);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var s = ctx.S;
        var specs = s.News.Sources.Where(x => x.Enabled).Select(x => RssSource.ToSpec(x, s.Location)).ToList();
        if (specs.Count == 0) return AgentResult.Unchanged("No news sources enabled");
        var outcomes = await Task.WhenAll(specs.Select(spec => RssSource.FetchAsync(ctx.Http, spec, ct))).ConfigureAwait(false);
        var items = outcomes.SelectMany(o => o.Items).ToList();
        var inserted = items.Count > 0 ? ctx.Db.UpsertItems(items) : 0;
        var ok = outcomes.Count(o => o.Ok);
        var failed = outcomes.Where(o => !o.Ok).Select(o => o.SourceId).ToList();
        foreach (var f in outcomes.Where(o => !o.Ok)) Log.Warn("news", $"{f.SourceId}: {f.Error}");
        var msg = $"{inserted} new from {ok}/{specs.Count} sources" + (failed.Count > 0 ? $" · unavailable: {string.Join(", ", failed.Take(3))}" : "");
        if (ok == 0) return AgentResult.Fail(ctx.Http.LooksOffline ? "Offline — keeping the last headlines" : "All news sources failed");
        ctx.State.MarkFresh("news");
        return AgentResult.Success(msg, changed: inserted > 0 || ctx.State.Stories.Count == 0);
    }
}

/// <summary>Clusters articles into stories and ranks them.</summary>
public sealed class StoryCuratorAgent : Agent
{
    public override string Id => "curator";
    public override string Name => "Story Curator";
    public override string Role => "curator";
    public override string Description => "Merges coverage of the same story across outlets, then ranks by breadth, reliability, freshness, your interests and locality.";
    public override string[] After => new[] { "news-scout" };
    public override TimeSpan InitialDelay => TimeSpan.FromMilliseconds(300);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(15);

    public override Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var s = ctx.S;
        var since = DateTimeOffset.UtcNow.AddHours(-s.News.MaxAgeHours);
        var items = ctx.Db.GetItems(ItemKind.News, since, 3000);
        if (items.Count == 0) return Task.FromResult(AgentResult.Unchanged("Waiting for headlines"));
        var clusters = StoryClusterer.Build(items, s.News, s.Location).Take(160).ToList();

        // Carry forward summaries: exact cache hit first, else keep the previous one until the editor refreshes it.
        var previous = ctx.State.Stories.ToDictionary(c => c.Id, c => c.Summary);
        foreach (var c in clusters)
        {
            var cached = ctx.Db.GetLlm("story:" + c.ContentKey);
            if (cached is not null) c.Summary = JsonUtil.Deserialize<StorySummary>(cached);
            if (c.Summary is null && previous.TryGetValue(c.Id, out var prev) && prev is not null) c.Summary = prev;
            c.Summary ??= StoryClusterer.ExtractiveSummary(c);
        }
        ctx.State.SetStories(clusters);

        var business = items.Where(i => i.Category is "business" or "markets").OrderByDescending(i => i.Published).Take(30).ToList();
        ctx.State.SetMarketHeadlines(business);

        var multi = clusters.Count(c => c.SourceCount > 1);
        return Task.FromResult(AgentResult.Success($"{clusters.Count} stories ({multi} multi-source) from {items.Count} articles"));
    }
}

/// <summary>Collects posts from social platforms (local subreddits, Mastodon hashtags, Bluesky, HN, YouTube).</summary>
public sealed class SocialScoutAgent : Agent
{
    public override string Id => "social-scout";
    public override string Name => "Social Scout";
    public override string Description => "Reads your local subreddits, Mastodon hashtags, Bluesky trends and accounts, Hacker News and YouTube channels.";
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(3);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(ctx.S.Social.RefreshMinutes);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var s = ctx.S.Social;
        var tasks = new List<Task<FetchOutcome>>();
        if (s.Subreddits.Count > 0)
        {
            // One combined request for all communities (r/a+b+c) — Reddit rate-limits anonymous clients hard.
            var multi = string.Join("+", s.Subreddits);
            tasks.Add(RssSource.FetchAsync(ctx.Http, new FeedSpec
            {
                Id = "reddit:multi", Name = s.Subreddits.Count == 1 ? "r/" + s.Subreddits[0] : "Reddit",
                Url = $"https://www.reddit.com/r/{multi}/hot/.rss?limit={Math.Min(100, 25 * s.Subreddits.Count)}",
                Kind = ItemKind.Social, Platform = "reddit", Category = "social", Tier = 3, Local = true, MaxItems = 100,
            }, ct));
        }
        if (!string.IsNullOrWhiteSpace(s.MastodonInstance))
            foreach (var tag in s.MastodonHashtags)
                tasks.Add(MastodonSource.FetchHashtagAsync(ctx.Http, s.MastodonInstance, tag, ct));
        if (s.BlueskyTrending) tasks.Add(BlueskySource.FetchTrendingAsync(ctx.Http, ct));
        // Your own timelines, when you've connected them in Settings → Social.
        if (s.BlueskyTimeline && s.BlueskyHandle.Length > 0 && ctx.Secrets.Get(SecretKeys.BlueskyAppPassword) is { Length: > 0 } appPassword)
            tasks.Add(BlueskySource.FetchTimelineAsync(ctx.Http, s.BlueskyHandle, appPassword, ct));
        if (s.MastodonHome && !string.IsNullOrWhiteSpace(s.MastodonInstance) && ctx.Secrets.Get(SecretKeys.MastodonToken) is { Length: > 0 } token)
            tasks.Add(MastodonSource.FetchHomeAsync(ctx.Http, s.MastodonInstance, token, ct));
        foreach (var handle in s.BlueskyAccounts) tasks.Add(BlueskySource.FetchAuthorAsync(ctx.Http, handle, ct));
        foreach (var feed in s.BlueskyFeeds) tasks.Add(BlueskySource.FetchFeedAsync(ctx.Http, feed, ct));
        if (s.HackerNews) tasks.Add(HackerNewsSource.FetchFrontPageAsync(ctx.Http, s.HackerNewsCount, ct));
        foreach (var ch in s.YouTubeChannels)
            tasks.Add(RssSource.FetchAsync(ctx.Http, new FeedSpec
            {
                Id = "youtube:" + ch.ChannelId, Name = string.IsNullOrWhiteSpace(ch.Name) ? "YouTube" : ch.Name,
                Url = $"https://www.youtube.com/feeds/videos.xml?channel_id={Uri.EscapeDataString(ch.ChannelId)}",
                Kind = ItemKind.Social, Platform = "youtube", Category = "video", Tier = 3, MaxItems = 10,
            }, ct));
        foreach (var f in s.ExtraFeeds.Where(f => f.Enabled))
            tasks.Add(RssSource.FetchAsync(ctx.Http, new FeedSpec
            {
                Id = "feed:" + f.Id, Name = f.Name, Url = f.Url, Kind = ItemKind.Social, Platform = "rss", Category = f.Category, Tier = 3, Local = f.Local,
            }, ct));

        if (tasks.Count == 0) return AgentResult.Unchanged("No social sources configured");
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        var items = outcomes.SelectMany(o => o.Items).ToList();
        var inserted = items.Count > 0 ? ctx.Db.UpsertItems(items) : 0;
        foreach (var f in outcomes.Where(o => !o.Ok)) Log.Warn("social", $"{f.SourceId}: {f.Error}");

        var recent = ctx.Db.GetItems(ItemKind.Social, DateTimeOffset.UtcNow.AddHours(-36), 800);
        var now = DateTimeOffset.UtcNow;
        var ranked = recent
            .OrderByDescending(i => Math.Log10(2 + Math.Max(0, i.Score)) + Math.Log10(2 + i.Comments) * 0.5 - (now - i.Published).TotalHours / 10.0)
            .Take(240).ToList();
        ctx.State.SetSocial(ranked);
        var ok = outcomes.Count(o => o.Ok);
        if (ok == 0) return AgentResult.Fail(ctx.Http.LooksOffline ? "Offline — keeping the last posts" : "All social sources failed");
        ctx.State.MarkFresh("social");
        return AgentResult.Success($"{Plural.Of(inserted, "new post")} from {ok}/{tasks.Count} sources", changed: inserted > 0 || ctx.State.Pulse is null);
    }
}

/// <summary>Live quotes (one batched request) plus daily history for technical indicators.</summary>
public sealed class MarketWatchAgent : Agent
{
    public override string Id => "market-watch";
    public override string Name => "Market Watch";
    public override string Description => "Streams delayed quotes for indices, FX, commodities and your watchlist in a single batched request; keeps daily history for indicators.";
    public override bool Stretchable => false;
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(2);

    public override TimeSpan Interval(HubContext ctx)
    {
        var anyOpen = ctx.State.Quotes.Values.Any(q => q.MarketState == "open" && q.Kind is not InstrumentKind.Crypto and not InstrumentKind.Fx);
        return anyOpen ? TimeSpan.FromSeconds(ctx.S.Markets.RefreshSeconds) : TimeSpan.FromMinutes(15);
    }

    public static IEnumerable<WatchSymbol> AllSymbols(MarketSettings m) => m.Indices.Concat(m.Macro).Concat(m.Watchlist);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var m = ctx.S.Markets;
        var all = AllSymbols(m).GroupBy(w => w.Symbol, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (all.Count == 0) return AgentResult.Unchanged("No symbols configured");

        // 1) Daily history + metadata (refreshed at most once every ~20h per symbol, a few per run).
        var refreshed = 0;
        foreach (var w in all.Where(w => w.Kind is not "fx"))
        {
            if (refreshed >= 16) break;
            var metaKey = "meta:" + w.Symbol;
            var meta = ctx.Db.GetJson<MetaCache>(metaKey);
            if (meta is not null && DateTimeOffset.UtcNow - meta.Fetched < TimeSpan.FromHours(20)) continue;
            var chart = await YahooFinance.GetChartAsync(ctx.Http, w.Symbol, "1y", "1d", ct).ConfigureAwait(false);
            if (chart is null) continue;
            refreshed++;
            ctx.Db.PutJson(metaKey, new MetaCache { Fetched = DateTimeOffset.UtcNow, Meta = chart.Meta });
            var bars = chart.Times.Zip(chart.Closes, (t, c) => (t, c)).Where(x => !double.IsNaN(x.c)).ToList();
            ctx.Db.UpsertCloses(w.Symbol, bars);
        }

        // 2) Live quotes via one batched spark request.
        var spark = await YahooFinance.GetSparkAsync(ctx.Http, all.Select(w => w.Symbol).ToList(), ct: ct).ConfigureAwait(false);
        if (spark.Count == 0) return AgentResult.Fail(ctx.Http.LooksOffline ? "Offline — keeping the last quotes" : "Quote service unavailable");
        var now = DateTimeOffset.UtcNow;
        var quotes = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in all)
        {
            if (!spark.TryGetValue(w.Symbol, out var data)) continue;
            var meta = ctx.Db.GetJson<MetaCache>("meta:" + w.Symbol)?.Meta;
            quotes[w.Symbol] = YahooFinance.BuildQuote(w, data, meta, now);
        }
        ctx.State.SetQuotes(quotes);

        // 3) Indicators for everything except FX.
        var indicators = new Dictionary<string, Indicators>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in all.Where(w => w.Kind is not "fx"))
        {
            var history = ctx.Db.GetCloses(w.Symbol, 400);
            if (history.Count < 20) continue;
            quotes.TryGetValue(w.Symbol, out var q);
            indicators[w.Symbol] = TechnicalIndicators.Compute(w.Symbol, history, q?.Price, q?.PreviousClose);
        }
        ctx.State.SetIndicators(indicators);
        ctx.State.MarkFresh("markets");

        var open = quotes.Values.Count(q => q.MarketState == "open");
        return AgentResult.Success($"{quotes.Count} quotes ({open} live), {indicators.Count} with indicators" + (refreshed > 0 ? $", refreshed history for {refreshed}" : ""));
    }

    public sealed class MetaCache
    {
        public DateTimeOffset Fetched { get; set; }
        public JsonElementSnapshot? Meta { get; set; }
    }
}

public sealed class PredictionScoutAgent : Agent
{
    public override string Id => "prediction-scout";
    public override string Name => "Prediction Scout";
    public override string Description => "Tracks the most-traded prediction markets (Polymarket, optionally Kalshi) on the topics you follow.";
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(5);
    public override bool IsEnabled(HubContext ctx) => ctx.S.Predictions.Polymarket || ctx.S.Predictions.Kalshi;
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(ctx.S.Predictions.RefreshMinutes);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var p = ctx.S.Predictions;
        var list = new List<PredictionMarket>();
        if (p.Polymarket) list.AddRange(await PredictionSources.FetchPolymarketAsync(ctx.Http, p.PolymarketTags, p.MinVolume24h, ct).ConfigureAwait(false));
        if (p.Kalshi) list.AddRange(await PredictionSources.FetchKalshiAsync(ctx.Http, p.KalshiCategories, p.MinVolume24h, ct).ConfigureAwait(false));
        if (list.Count == 0) return AgentResult.Fail(ctx.Http.LooksOffline ? "Offline — keeping the last markets" : "No prediction markets returned");
        var top = list.OrderByDescending(m => m.Volume24h).Take(p.MaxItems).ToList();
        ctx.State.SetPredictions(top);
        ctx.State.MarkFresh("predictions");
        return AgentResult.Success($"{top.Count} markets tracked");
    }
}

public sealed class EventsScoutAgent : Agent
{
    public override string Id => "events-scout";
    public override string Name => "Agenda Scout";
    public override string Description => "Merges your calendars (ICS), public holidays, high-impact economic releases and watchlist earnings into one agenda.";
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(4);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(30);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var e = ctx.S.Events;
        var from = DateTimeOffset.Now.AddHours(-2);
        var to = DateTimeOffset.Now.AddDays(e.LookaheadDays);
        var all = new List<HubEvent>();
        var notes = new List<string>();

        foreach (var cal in e.Calendars.Where(c => c.Enabled))
        {
            try
            {
                string? text = null;
                if (Path.IsPathFullyQualified(cal.Url) && File.Exists(cal.Url))
                {
                    var info = new FileInfo(cal.Url);
                    if (info.Length < 20 * 1024 * 1024) text = await File.ReadAllTextAsync(cal.Url, ct).ConfigureAwait(false);
                }
                else
                {
                    var url = cal.Url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ? "https://" + cal.Url[9..] : cal.Url;
                    var res = await ctx.Http.GetAsync(url, maxBytes: 20 * 1024 * 1024, ct: ct).ConfigureAwait(false);
                    if (res.Ok) text = res.Text; else notes.Add($"{cal.Name}: {res.Error}");
                }
                if (text is not null) all.AddRange(IcsCalendar.Parse(text, cal.Name, cal.Color, from, to));
            }
            catch (Exception ex) { notes.Add($"{cal.Name}: {ex.Message}"); }
        }

        foreach (var country in e.HolidayCountries)
            all.AddRange(await CalendarFeeds.FetchHolidaysAsync(ctx.Http, country, from, to, ct).ConfigureAwait(false));

        if (e.Economic)
            all.AddRange(await CalendarFeeds.FetchEconomicAsync(ctx.Http, e.EconomicCurrencies, e.EconomicMinImpact, ct).ConfigureAwait(false));

        if (e.Earnings)
        {
            var key = ctx.Secrets.Get(SecretKeys.FinnhubApiKey);
            if (!string.IsNullOrEmpty(key))
                all.AddRange(await CalendarFeeds.FetchEarningsAsync(ctx.Http, key, ctx.S.Markets.Watchlist.Select(w => w.Symbol).ToList(), from, to, ct).ConfigureAwait(false));
            else notes.Add("add a free Finnhub key for earnings dates");
        }

        var events = all.Where(x => x.Start >= from && x.Start <= to)
            .GroupBy(x => x.Id).Select(g => g.First())
            .OrderBy(x => x.Start).ToList();
        ctx.State.SetEvents(events);
        if (!ctx.Http.LooksOffline) ctx.State.MarkFresh("events");
        return AgentResult.Success($"{Plural.Of(events.Count, "upcoming item")}" + (notes.Count > 0 ? " · " + string.Join("; ", notes.Take(2)) : ""));
    }
}

public sealed class WeatherAgent : Agent
{
    public override string Id => "weather";
    public override string Name => "Weather";
    public override string Description => "Hyperlocal forecast from Open-Meteo (no key, no tracking).";
    public override TimeSpan InitialDelay => TimeSpan.FromMilliseconds(500);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(30);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var w = await WeatherSource.FetchAsync(ctx.Http, ctx.S.Location, ct).ConfigureAwait(false);
        if (w?.Now is null) return AgentResult.Fail(ctx.Http.LooksOffline ? "Offline — keeping the last forecast" : "Weather service unavailable");
        ctx.State.SetWeather(w);
        ctx.State.MarkFresh("weather");
        return AgentResult.Success($"{w.Now.Temp:0}° {WeatherSource.Describe(w.Now.Code)} in {w.Location}");
    }
}

/// <summary>Keeps an eye on the local model server and publishes its health.</summary>
public sealed class ModelWardenAgent : Agent
{
    public override string Id => "model-warden";
    public override string Name => "Model Warden";
    public override string Role => "keeper";
    public override string Description => "Checks the local model server, picks the best installed model and pauses AI work while you game.";
    public override bool Stretchable => false;
    public override TimeSpan InitialDelay => TimeSpan.Zero;
    public override TimeSpan Interval(HubContext ctx) => ctx.State.Ai?.Available == true ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(45);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var before = ctx.State.Ai;
        var health = await ctx.Llm.CheckAsync(ct).ConfigureAwait(false);
        ctx.State.SetAi(health);
        if (!health.Enabled) return AgentResult.Unchanged("AI disabled");
        if (!health.Available) return AgentResult.Unchanged(health.Error ?? "Model server offline");
        var changed = before is null || before.Available != health.Available || before.ActiveModel != health.ActiveModel;
        return new AgentResult(true, $"{health.ActiveModel} ready" + (health.Paused ? $" · paused ({health.PauseReason})" : ""), changed);
    }
}

/// <summary>Housekeeping: retention, FTS optimisation, incremental vacuum.</summary>
public sealed class KeeperAgent : Agent
{
    public override string Id => "keeper";
    public override string Name => "Housekeeper";
    public override string Role => "keeper";
    public override string Description => "Applies your retention policy and keeps the local database compact and fast.";
    public override TimeSpan InitialDelay => TimeSpan.FromMinutes(3);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromHours(6);

    public override Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct) =>
        Task.FromResult(AgentResult.Unchanged(ctx.Db.Prune(ctx.S.Privacy.RetentionDays)));
}
