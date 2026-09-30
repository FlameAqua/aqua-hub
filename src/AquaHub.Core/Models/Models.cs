using AquaHub.Core.Util;

namespace AquaHub.Core.Models;

// ───────────────────────────── Feeds (news + social) ─────────────────────────────

public enum ItemKind { News, Social }

public sealed record FeedItem
{
    public required string Id { get; init; }
    public required ItemKind Kind { get; init; }
    public required string SourceId { get; init; }
    public required string SourceName { get; init; }
    /// <summary>rss | reddit | bluesky | mastodon | hackernews | youtube</summary>
    public string Platform { get; init; } = "rss";
    public string Category { get; init; } = "general";
    /// <summary>1 = wire / public broadcaster / paper of record, 2 = reputable outlet, 3 = aggregator.</summary>
    public int Tier { get; init; } = 2;
    public required string Title { get; init; }
    public string Summary { get; init; } = "";
    public string? Url { get; init; }
    public string? ImageUrl { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset Published { get; init; }
    public int Score { get; init; }
    public int Comments { get; init; }
    public string? CommentsUrl { get; init; }
    public bool IsLocal { get; init; }
    public bool Saved { get; init; }
}

public sealed record StorySummary
{
    public string Headline { get; init; } = "";
    public string Tldr { get; init; } = "";
    public List<string> KeyPoints { get; init; } = new();
    public string WhyItMatters { get; init; } = "";
    public bool IsAi { get; init; }
    public string? Model { get; init; }
}

public sealed class StoryCluster
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public string Category { get; set; } = "general";
    public List<FeedItem> Items { get; set; } = new();
    public double Importance { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset Latest { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsLocal { get; set; }
    public List<string> Matches { get; set; } = new();
    public StorySummary? Summary { get; set; }
    /// <summary>Hash of member ids; used as the LLM cache key so summaries refresh when coverage grows.</summary>
    public string ContentKey { get; set; } = "";

    /// <summary>One name per publisher: "TheJournal.ie" and "The Journal" (via Google News) are the same outlet.</summary>
    public IReadOnlyList<string> SourceNames => Items.GroupBy(i => Publishers.Key(i.SourceName)).Select(g => g.First().SourceName).ToList();
    public int SourceCount => Items.Select(i => Publishers.Key(i.SourceName)).Distinct().Count();
    public string? Url => Items.OrderBy(i => i.Tier).ThenByDescending(i => i.Published).FirstOrDefault()?.Url;
}

// ───────────────────────────── Markets ─────────────────────────────

public enum InstrumentKind { Index, Equity, Etf, Fx, Crypto, Commodity, Other }

public sealed record Quote
{
    public required string Symbol { get; init; }
    public string Name { get; init; } = "";
    public double Price { get; init; }
    public double PreviousClose { get; init; }
    public double Change => Price - PreviousClose;
    public double ChangePercent => PreviousClose > 0 ? (Price - PreviousClose) / PreviousClose * 100.0 : 0;
    public string Currency { get; init; } = "";
    public string Exchange { get; init; } = "";
    /// <summary>open | closed | pre | post</summary>
    public string MarketState { get; init; } = "closed";
    public DateTimeOffset Time { get; init; }
    public double[] Intraday { get; init; } = Array.Empty<double>();
    public double DayHigh { get; init; }
    public double DayLow { get; init; }
    public double High52 { get; init; }
    public double Low52 { get; init; }
    public long Volume { get; init; }
    public InstrumentKind Kind { get; init; } = InstrumentKind.Equity;
}

public enum TechSignal { Bearish, Neutral, Bullish }

public sealed record Indicators
{
    public required string Symbol { get; init; }
    public double Last { get; init; }
    public double? Sma20 { get; init; }
    public double? Sma50 { get; init; }
    public double? Sma200 { get; init; }
    public double? Rsi14 { get; init; }
    public double? Volatility20 { get; init; }
    public double? Ret1D { get; init; }
    public double? Ret5D { get; init; }
    public double? Ret1M { get; init; }
    public double? Ret3M { get; init; }
    public double? RetYtd { get; init; }
    public double? Ret1Y { get; init; }
    public double? Position52 { get; init; }
    public double? FromHigh { get; init; }
    public double? MacdHist { get; init; }
    public string Trend { get; init; } = "sideways";
    /// <summary>-100 (strongly bearish) … +100 (strongly bullish) — transparent rule-based composite.</summary>
    public int TechScore { get; init; }
    public TechSignal Signal { get; init; } = TechSignal.Neutral;
    public List<string> Factors { get; init; } = new();
}

public sealed record WatchInsight
{
    public string Symbol { get; init; } = "";
    /// <summary>bullish | neutral | bearish | watch</summary>
    public string Stance { get; init; } = "neutral";
    public double Confidence { get; init; }
    public string Summary { get; init; } = "";
    public List<string> Rationale { get; init; } = new();
    public List<string> Risks { get; init; } = new();
}

public sealed record InvestmentIdea
{
    public string Title { get; init; } = "";
    public string Thesis { get; init; } = "";
    public List<string> Symbols { get; init; } = new();
    /// <summary>low | medium | high</summary>
    public string Risk { get; init; } = "medium";
    public string Horizon { get; init; } = "";
    /// <summary>opportunity | caution | diversify | rebalance</summary>
    public string Kind { get; init; } = "opportunity";
}

public sealed record MarketBrief
{
    public string Overview { get; init; } = "";
    public List<string> Highlights { get; init; } = new();
    public List<WatchInsight> Insights { get; init; } = new();
    public List<InvestmentIdea> Ideas { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; }
    public bool IsAi { get; init; }
    public string? Model { get; init; }
}

// ───────────────────────────── Predictions ─────────────────────────────

public sealed record PredictionOutcome(string Label, double Probability, double? Change24h);

public sealed record PredictionMarket
{
    public required string Id { get; init; }
    public string Source { get; init; } = "Polymarket";
    public required string Title { get; init; }
    public string Category { get; init; } = "";
    public List<PredictionOutcome> Outcomes { get; init; } = new();
    public double Volume24h { get; init; }
    public double TotalVolume { get; init; }
    public DateTimeOffset? EndDate { get; init; }
    public string? Url { get; init; }
    public List<string> Tags { get; init; } = new();
    public bool IsBinary { get; init; }

    /// <summary>The most likely outcome (outcome lists may be in date order).</summary>
    public PredictionOutcome? Lead => Outcomes.Count > 0 ? Outcomes.MaxBy(o => o.Probability) : null;
    public double MaxSwing => Outcomes.Count == 0 ? 0 : Outcomes.Max(o => Math.Abs(o.Change24h ?? 0));
}

// ───────────────────────────── Events ─────────────────────────────

public enum EventKind { Calendar, Holiday, Economic, Earnings, Reminder }

public sealed record HubEvent
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public bool AllDay { get; init; }
    public string? Location { get; init; }
    public EventKind Kind { get; init; }
    public string Source { get; init; } = "";
    /// <summary>0 = low … 3 = critical.</summary>
    public int Importance { get; init; } = 1;
    public string? Url { get; init; }
    public string? Detail { get; init; }
    public string? Color { get; init; }
}

// ───────────────────────────── Weather ─────────────────────────────

public sealed record WeatherNow(double Temp, double FeelsLike, int Humidity, double Precip, int Code, double Wind, bool IsDay);
public sealed record WeatherHour(DateTimeOffset Time, double Temp, int PrecipProb, int Code, bool IsDay);
public sealed record WeatherDay(DateOnly Date, int Code, double Max, double Min, int PrecipProb, DateTimeOffset? Sunrise, DateTimeOffset? Sunset, double Uv);

public sealed record WeatherSnapshot
{
    public string Location { get; init; } = "";
    public WeatherNow? Now { get; init; }
    public List<WeatherHour> Hours { get; init; } = new();
    public List<WeatherDay> Days { get; init; } = new();
    public DateTimeOffset UpdatedAt { get; init; }
    public string Units { get; init; } = "metric";
}

// ───────────────────────────── AI digests ─────────────────────────────

public sealed record PulseTopic
{
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    /// <summary>positive | neutral | negative | mixed</summary>
    public string Sentiment { get; init; } = "neutral";
    public int Heat { get; init; } = 1;
    public List<string> Platforms { get; init; } = new();
    public List<string> ItemIds { get; init; } = new();
}

public sealed record SocialPulse
{
    public string Overview { get; init; } = "";
    public List<PulseTopic> Topics { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; }
    public bool IsAi { get; init; }
    public string? Model { get; init; }
    public int PostCount { get; init; }
}

public sealed record BriefSection
{
    public string Title { get; init; } = "";
    /// <summary>news | local | markets | agenda | weather | predictions | tech</summary>
    public string Icon { get; init; } = "news";
    public List<string> Bullets { get; init; } = new();
}

public sealed record DailyBrief
{
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<BriefSection> Sections { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; }
    public bool IsAi { get; init; }
    public string? Model { get; init; }
    public string Period { get; init; } = "now";
}

public sealed record ForesightItem
{
    public string Title { get; init; } = "";
    public string When { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>event | prediction | market</summary>
    public string Kind { get; init; } = "event";
    public int Importance { get; init; } = 1;
}

public sealed record Foresight
{
    public string Overview { get; init; } = "";
    public List<ForesightItem> Items { get; init; } = new();
    public DateTimeOffset GeneratedAt { get; init; }
    public bool IsAi { get; init; }
    public string? Model { get; init; }
}

// ───────────────────────────── Alerts & agents ─────────────────────────────

public enum AlertSeverity { Info, Notice, Important, Critical }

public sealed record HubAlert
{
    public required string Id { get; init; }
    /// <summary>price | news | keyword | prediction | calendar | system | brief | ai</summary>
    public string Kind { get; init; } = "info";
    public AlertSeverity Severity { get; init; } = AlertSeverity.Info;
    public required string Title { get; init; }
    public string Body { get; init; } = "";
    public string? Url { get; init; }
    /// <summary>In-app navigation target, e.g. "markets:NVDA" or "news:{clusterId}".</summary>
    public string? Target { get; init; }
    public DateTimeOffset Created { get; init; }
    public bool Read { get; init; }
}

public enum AgentState { Idle, Running, Waiting, Paused, Error, Disabled }

public sealed record AgentStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string Role { get; init; } = "collector";
    public AgentState State { get; init; } = AgentState.Idle;
    public DateTimeOffset? LastRun { get; init; }
    public TimeSpan? LastDuration { get; init; }
    public DateTimeOffset? NextRun { get; init; }
    public string LastMessage { get; init; } = "";
    public int Runs { get; init; }
    public int Errors { get; init; }
    public bool UsesAi { get; init; }
}

public sealed record AgentRunRecord(string Agent, DateTimeOffset Started, TimeSpan Duration, bool Ok, string Message);

// ───────────────────────────── System ─────────────────────────────

public sealed record DiskInfo(string Name, string Label, double TotalGb, double FreeGb)
{
    public double UsedPercent => TotalGb <= 0 ? 0 : (TotalGb - FreeGb) / TotalGb * 100.0;
}

public sealed record GpuInfo(string Name, double Utilization, double VramUsedGb, double VramTotalGb);

public sealed record BatteryInfo(int Percent, bool Charging, TimeSpan? Remaining);

public sealed record ProcInfo(int Pid, string Name, double Cpu, double MemoryMb);

public sealed record SystemSnapshot
{
    public double CpuPercent { get; init; }
    public string CpuName { get; init; } = "";
    public int LogicalCores { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double RamPercent => RamTotalGb <= 0 ? 0 : RamUsedGb / RamTotalGb * 100;
    public List<DiskInfo> Disks { get; init; } = new();
    public double NetDownBps { get; init; }
    public double NetUpBps { get; init; }
    public GpuInfo? Gpu { get; init; }
    public BatteryInfo? Battery { get; init; }
    public TimeSpan Uptime { get; init; }
    public string OsName { get; init; } = "";
    public string MachineName { get; init; } = "";
    public List<ProcInfo> TopProcesses { get; init; } = new();
    public DateTimeOffset Time { get; init; }
}

/// <summary>Plain-language trading-session labels ("Live", "Fri close"), shared by the UI and the AI digests.</summary>
public static class MarketSession
{
    public static bool IsLive(Quote q) => q.MarketState == "open";

    /// <summary>"today's close", "yesterday's close" or e.g. "Fri close" for the quote's last trade.</summary>
    public static string LastClose(Quote q, DateTimeOffset now)
    {
        var day = q.Time.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        return day == today ? "today's close" : day == today.AddDays(-1) ? "yesterday's close" : day.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture) + " close";
    }

    public static string Label(Quote q, DateTimeOffset now) =>
        q.Kind == InstrumentKind.Crypto ? "24/7" : IsLive(q) ? "Live" : "Closed · " + LastClose(q, now);

    /// <summary>Summary for a set of stock-market quotes: "Markets open", or "Markets closed · figures from Fri close".</summary>
    public static string Summary(IEnumerable<Quote> quotes, DateTimeOffset now)
    {
        var stocks = quotes.Where(q => q.Kind is InstrumentKind.Index or InstrumentKind.Equity or InstrumentKind.Etf).ToList();
        if (stocks.Count == 0) return "";
        if (stocks.Any(IsLive)) return stocks.All(IsLive) ? "Markets open · quotes delayed" : "Some markets open · quotes delayed";
        var latest = stocks.OrderByDescending(q => q.Time).First();
        return "Markets closed · figures from " + LastClose(latest, now);
    }

    /// <summary>Card-subtitle form of <see cref="Summary"/>: "Open · delayed ~15m", "Partly open" or "Closed · Fri close".</summary>
    public static string Short(IEnumerable<Quote> quotes, DateTimeOffset now)
    {
        var stocks = quotes.Where(q => q.Kind is InstrumentKind.Index or InstrumentKind.Equity or InstrumentKind.Etf).ToList();
        if (stocks.Count == 0) return "";
        if (stocks.Any(IsLive)) return stocks.All(IsLive) ? "Open · delayed ~15m" : "Partly open";
        var day = stocks.Max(q => q.Time).ToLocalTime().Date;
        return "Closed · " + (day == now.ToLocalTime().Date ? "today's close" : day.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture) + " close");
    }
}
