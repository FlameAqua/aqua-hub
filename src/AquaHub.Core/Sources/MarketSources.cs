using System.Globalization;
using System.Text.Json;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Sources;

public sealed record SparkData(string Symbol, long[] Times, double[] Closes, double PreviousClose);

public sealed record ChartData(string Symbol, JsonElementSnapshot Meta, long[] Times, double[] Closes);

/// <summary>Detached copy of the interesting chart metadata fields.</summary>
public sealed record JsonElementSnapshot
{
    public string Name { get; init; } = "";
    public string Currency { get; init; } = "";
    public string Exchange { get; init; } = "";
    public string InstrumentType { get; init; } = "";
    public double Price { get; init; }
    public double PreviousClose { get; init; }
    public double High52 { get; init; }
    public double Low52 { get; init; }
    public double DayHigh { get; init; }
    public double DayLow { get; init; }
    public long Volume { get; init; }
    public long MarketTime { get; init; }
    public long RegularStart { get; init; }
    public long RegularEnd { get; init; }
    public string TimeZone { get; init; } = "";
}

public sealed record SymbolMatch(string Symbol, string Name, string Exchange, string Type);

/// <summary>
/// Yahoo Finance public chart endpoints (no key). Uses the batch "spark" endpoint so a full
/// refresh of every tracked instrument costs a single request.
/// </summary>
public static class YahooFinance
{
    private const string Base = "https://query1.finance.yahoo.com";

    public static async Task<Dictionary<string, SparkData>> GetSparkAsync(HttpFetcher http, IReadOnlyList<string> symbols,
        string range = "1d", string interval = "5m", CancellationToken ct = default)
    {
        var result = new Dictionary<string, SparkData>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in symbols.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(20))
        {
            var url = $"{Base}/v8/finance/spark?symbols={string.Join(",", chunk.Select(Uri.EscapeDataString))}&range={range}&interval={interval}";
            using var doc = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
            if (doc is null) continue;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var e = prop.Value;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var times = e.Longs("timestamp");
                var closes = e.Doubles("close");
                var prev = e.Dbl("previousClose") ?? e.Dbl("chartPreviousClose") ?? 0;
                if (closes.Length == 0) continue;
                result[prop.Name] = new SparkData(prop.Name, times, closes, prev);
            }
        }
        return result;
    }

    public static async Task<ChartData?> GetChartAsync(HttpFetcher http, string symbol, string range, string interval, CancellationToken ct)
    {
        var url = $"{Base}/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range={range}&interval={interval}&includePrePost=false";
        using var doc = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        if (doc is null) return null;
        if (!doc.RootElement.TryProp("chart", out var chart)) return null;
        var res = chart.Arr("result").FirstOrDefault();
        if (res.ValueKind != JsonValueKind.Object || !res.TryProp("meta", out var meta)) return null;
        var times = res.Longs("timestamp");
        var closes = Array.Empty<double>();
        if (res.TryProp("indicators", out var ind))
        {
            var q = ind.Arr("quote").FirstOrDefault();
            if (q.ValueKind == JsonValueKind.Object) closes = q.Doubles("close");
            // Prefer adjusted closes for daily history when present.
            var adj = ind.Arr("adjclose").FirstOrDefault();
            if (interval == "1d" && adj.ValueKind == JsonValueKind.Object)
            {
                var a = adj.Doubles("adjclose");
                if (a.Length == closes.Length && a.Length > 0) closes = a;
            }
        }
        long regStart = 0, regEnd = 0;
        if (meta.TryProp("currentTradingPeriod", out var ctp) && ctp.TryProp("regular", out var reg))
        {
            regStart = reg.Lng("start") ?? 0;
            regEnd = reg.Lng("end") ?? 0;
        }
        var snap = new JsonElementSnapshot
        {
            Name = meta.Str("shortName") ?? meta.Str("longName") ?? symbol,
            Currency = meta.Str("currency") ?? "",
            Exchange = meta.Str("fullExchangeName") ?? meta.Str("exchangeName") ?? "",
            InstrumentType = meta.Str("instrumentType") ?? "",
            Price = meta.Dbl("regularMarketPrice") ?? 0,
            PreviousClose = meta.Dbl("previousClose") ?? meta.Dbl("chartPreviousClose") ?? 0,
            High52 = meta.Dbl("fiftyTwoWeekHigh") ?? 0,
            Low52 = meta.Dbl("fiftyTwoWeekLow") ?? 0,
            DayHigh = meta.Dbl("regularMarketDayHigh") ?? 0,
            DayLow = meta.Dbl("regularMarketDayLow") ?? 0,
            Volume = meta.Lng("regularMarketVolume") ?? 0,
            MarketTime = meta.Lng("regularMarketTime") ?? 0,
            RegularStart = regStart,
            RegularEnd = regEnd,
            TimeZone = meta.Str("exchangeTimezoneName") ?? "",
        };
        return new ChartData(symbol, snap, times, closes);
    }

    public static async Task<List<SymbolMatch>> SearchAsync(HttpFetcher http, string query, CancellationToken ct)
    {
        var url = $"{Base}/v1/finance/search?q={Uri.EscapeDataString(query)}&quotesCount=8&newsCount=0&listsCount=0";
        using var doc = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        var list = new List<SymbolMatch>();
        if (doc is null) return list;
        foreach (var q in doc.RootElement.Arr("quotes"))
        {
            var sym = q.Str("symbol");
            if (string.IsNullOrEmpty(sym)) continue;
            list.Add(new SymbolMatch(sym, q.Str("longname") ?? q.Str("shortname") ?? sym, q.Str("exchDisp") ?? q.Str("exchange") ?? "", q.Str("typeDisp") ?? q.Str("quoteType") ?? ""));
        }
        return list;
    }

    public static InstrumentKind KindOf(WatchSymbol w, string instrumentType = "") => (w.Kind, instrumentType.ToUpperInvariant()) switch
    {
        ("index", _) or (_, "INDEX") => InstrumentKind.Index,
        ("etf", _) or (_, "ETF") => InstrumentKind.Etf,
        ("fx", _) or (_, "CURRENCY") => InstrumentKind.Fx,
        ("crypto", _) or (_, "CRYPTOCURRENCY") => InstrumentKind.Crypto,
        ("commodity", _) or (_, "FUTURE") => InstrumentKind.Commodity,
        _ => InstrumentKind.Equity,
    };

    /// <summary>Builds a live quote from the batch spark data plus cached per-symbol metadata.</summary>
    public static Quote BuildQuote(WatchSymbol w, SparkData spark, JsonElementSnapshot? meta, DateTimeOffset now)
    {
        var valid = new List<double>();
        long lastTime = 0;
        for (var i = 0; i < spark.Closes.Length; i++)
        {
            if (double.IsNaN(spark.Closes[i])) continue;
            valid.Add(spark.Closes[i]);
            if (i < spark.Times.Length) lastTime = spark.Times[i];
        }
        var price = valid.Count > 0 ? valid[^1] : meta?.Price ?? 0;
        var prev = spark.PreviousClose > 0 ? spark.PreviousClose : meta?.PreviousClose ?? 0;
        var kind = KindOf(w, meta?.InstrumentType ?? "");
        var last = lastTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastTime) : now;
        var state = kind == InstrumentKind.Crypto ? "open"
            : (now - last) < TimeSpan.FromMinutes(25) ? "open" : "closed";

        return new Quote
        {
            Symbol = w.Symbol,
            Name = !string.IsNullOrWhiteSpace(w.Name) ? w.Name : meta?.Name ?? w.Symbol,
            Price = price,
            PreviousClose = prev,
            Currency = meta?.Currency ?? "",
            Exchange = meta?.Exchange ?? "",
            MarketState = state,
            Time = last,
            Intraday = Downsample(valid, 72),
            DayHigh = valid.Count > 0 ? valid.Max() : meta?.DayHigh ?? 0,
            DayLow = valid.Count > 0 ? valid.Min() : meta?.DayLow ?? 0,
            High52 = meta?.High52 ?? 0,
            Low52 = meta?.Low52 ?? 0,
            Volume = meta?.Volume ?? 0,
            Kind = kind,
        };
    }

    public static double[] Downsample(IReadOnlyList<double> values, int maxPoints)
    {
        if (values.Count <= maxPoints) return values.ToArray();
        var result = new double[maxPoints];
        var step = (values.Count - 1) / (double)(maxPoints - 1);
        for (var i = 0; i < maxPoints; i++) result[i] = values[(int)Math.Round(i * step)];
        result[^1] = values[^1];
        return result;
    }
}

/// <summary>Polymarket (Gamma API) and Kalshi public market data.</summary>
public static class PredictionSources
{
    private static readonly HashSet<string> SportsTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "sports", "nfl", "nba", "mlb", "nhl", "soccer", "football", "tennis", "esports", "cricket", "golf", "ufc", "f1", "boxing",
    };

    public static async Task<List<PredictionMarket>> FetchPolymarketAsync(HttpFetcher http, IEnumerable<string> tags, double minVolume,
        CancellationToken ct)
    {
        var byId = new Dictionary<string, PredictionMarket>();
        foreach (var tag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var url = $"https://gamma-api.polymarket.com/events?limit=12&active=true&closed=false&order=volume24hr&ascending=false&tag_slug={Uri.EscapeDataString(tag)}";
            using var doc = await http.GetJsonAsync(url, maxBytes: 12 * 1024 * 1024, ct: ct).ConfigureAwait(false);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var ev in doc.RootElement.EnumerateArray())
            {
                var parsed = ParsePolymarketEvent(ev, tag);
                if (parsed is null || parsed.Volume24h < minVolume) continue;
                byId.TryAdd(parsed.Id, parsed);
            }
        }
        return byId.Values.OrderByDescending(m => m.Volume24h).ToList();
    }

    internal static PredictionMarket? ParsePolymarketEvent(JsonElement ev, string tag)
    {
        var id = ev.Str("id");
        var title = ev.Str("title");
        if (id is null || title is null) return null;
        var tags = ev.Arr("tags").Select(t => t.Str("slug") ?? t.Str("label") ?? "").Where(t => t.Length > 0).ToList();
        if (tags.Any(SportsTags.Contains)) return null;
        // Skip novelty markets (tweet counts, "what will X say") — noise for a briefing.
        if (tags.Any(t => t.Contains("tweet", StringComparison.OrdinalIgnoreCase) || t.Contains("mention", StringComparison.OrdinalIgnoreCase) ||
                          t.Equals("pop-culture", StringComparison.OrdinalIgnoreCase)) ||
            System.Text.RegularExpressions.Regex.IsMatch(title, @"\b(tweets?|# of|mentions?|say\b|post on X)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;
        var end = TimeText.ParseLenient(ev.Str("endDate"));
        if (end is not null && end < DateTimeOffset.UtcNow) return null;

        var outcomes = new List<PredictionOutcome>();
        var openMarkets = ev.Arr("markets").Where(m => m.Bool("closed") != true && m.Bool("active") != false).ToList();
        if (openMarkets.Count == 0) return null;
        var binary = openMarkets.Count == 1;
        foreach (var m in openMarkets)
        {
            var labels = ParseStringArray(m.Str("outcomes"));
            var prices = ParseStringArray(m.Str("outcomePrices")).Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToList();
            if (prices.Count == 0 || double.IsNaN(prices[0])) continue;
            var change = m.Dbl("oneDayPriceChange");
            if (binary)
            {
                for (var i = 0; i < Math.Min(labels.Count, prices.Count); i++)
                    outcomes.Add(new PredictionOutcome(labels[i], prices[i], i == 0 ? change : -change));
            }
            else
            {
                var label = m.Str("groupItemTitle") ?? m.Str("question") ?? "?";
                outcomes.Add(new PredictionOutcome(label, prices[0], change));
            }
        }
        if (outcomes.Count == 0) return null;
        outcomes = TidyOutcomes(outcomes, binary, DateTimeOffset.Now);
        if (outcomes.Count == 0 || IsEffectivelyResolved(outcomes)) return null;
        return new PredictionMarket
        {
            Id = "pm" + id,
            Source = "Polymarket",
            Title = title,
            Category = tag,
            Outcomes = outcomes,
            Volume24h = ev.Dbl("volume24hr") ?? 0,
            TotalVolume = ev.Dbl("volume") ?? 0,
            EndDate = end,
            Url = $"https://polymarket.com/event/{ev.Str("slug")}",
            Tags = tags.Take(6).ToList(),
            IsBinary = binary,
        };
    }

    public static async Task<List<PredictionMarket>> FetchKalshiAsync(HttpFetcher http, IReadOnlyCollection<string> categories, double minVolume,
        CancellationToken ct)
    {
        var list = new List<PredictionMarket>();
        using var doc = await http.GetJsonAsync("https://api.elections.kalshi.com/trade-api/v2/events?limit=200&status=open&with_nested_markets=true",
            maxBytes: 12 * 1024 * 1024, ct: ct).ConfigureAwait(false);
        if (doc is null) return list;
        var cats = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        foreach (var ev in doc.RootElement.Arr("events"))
        {
            var category = ev.Str("category") ?? "";
            if (cats.Count > 0 && !cats.Contains(category)) continue;
            var markets = ev.Arr("markets").ToList();
            var outcomes = markets.Select(m =>
            {
                var p = m.Dbl("last_price_dollars") ?? (m.Dbl("last_price") / 100.0) ?? 0;
                var prev = m.Dbl("previous_price_dollars") ?? (m.Dbl("previous_price") / 100.0);
                return (Label: m.Str("yes_sub_title") ?? m.Str("title") ?? "Yes", P: p, Change: prev is null ? (double?)null : p - prev.Value,
                        Vol: m.Dbl("volume_24h_fp") ?? m.Dbl("volume_24h") ?? 0);
            }).ToList();
            var volume = outcomes.Sum(o => o.Vol);
            if (volume < minVolume / 10) continue; // Kalshi volumes are in contracts, typically smaller.
            var series = (ev.Str("series_ticker") ?? "").ToLowerInvariant();
            list.Add(new PredictionMarket
            {
                Id = "ks" + ev.Str("event_ticker"),
                Source = "Kalshi",
                Title = ev.Str("title") ?? "",
                Category = category,
                Outcomes = TidyOutcomes(outcomes.Select(o => new PredictionOutcome(o.Label, o.P, o.Change)).ToList(), markets.Count == 1, DateTimeOffset.Now),
                Volume24h = volume,
                Url = series.Length > 0 ? $"https://kalshi.com/markets/{series}" : "https://kalshi.com",
                IsBinary = markets.Count == 1,
            });
        }
        return list.OrderByDescending(m => m.Volume24h).ToList();
    }

    /// <summary>A market whose leading outcome is ~certain has in effect resolved — noise for a briefing.</summary>
    internal static bool IsEffectivelyResolved(IReadOnlyList<PredictionOutcome> outcomes) =>
        outcomes.Count > 0 && outcomes.Max(o => o.Probability) >= 0.985;

    /// <summary>
    /// Makes outcome lists readable: date-style outcomes ("September 30", "by Oct 15") in date order without dates that
    /// have already passed; everything else most-likely first. Near-zero outcomes are dropped.
    /// </summary>
    internal static List<PredictionOutcome> TidyOutcomes(List<PredictionOutcome> outcomes, bool binary, DateTimeOffset now)
    {
        if (binary) return outcomes;
        var live = outcomes.Where(o => o.Probability >= 0.005).ToList();
        var dated = live.Select(o => (Outcome: o, Date: OutcomeDate(o.Label, now))).ToList();
        var datedCount = dated.Count(d => d.Date is not null);
        if (datedCount >= 2 && datedCount * 5 >= dated.Count * 3)
            return dated.Where(d => d.Date is null || d.Date.Value >= now.LocalDateTime.Date)
                .OrderBy(d => d.Date ?? DateTime.MaxValue).Select(d => d.Outcome).Take(5).ToList();
        return live.OrderByDescending(o => o.Probability).Take(5).ToList();
    }

    private static readonly string[] MonthNames = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

    /// <summary>"September 21", "Sept. 21", "by Oct 15, 2026", "October" (end of month) → a date; null for non-date labels.</summary>
    internal static DateTime? OutcomeDate(string label, DateTimeOffset now)
    {
        var m = System.Text.RegularExpressions.Regex.Match(label,
            @"\b(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?(?:\s+(\d{1,2})(?:st|nd|rd|th)?)?(?:,?\s+(\d{4}))?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var month = Array.IndexOf(MonthNames, m.Groups[1].Value[..3].ToLowerInvariant()) + 1;
        if (month <= 0) return null;
        var today = now.LocalDateTime.Date;
        // No year: the next occurrence within ~10 months, so "January" in November means next January.
        var year = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
            : month < today.Month - 2 ? today.Year + 1 : today.Year;
        var day = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : DateTime.DaysInMonth(year, month);
        return day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, day) : null;
    }

    private static List<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }
}
