using AquaHub.Core.Models;
using AquaHub.Core.Settings;

namespace AquaHub.Core.Markets;

/// <param name="Value">What the holding is worth now, in the portfolio's currency.</param>
/// <param name="Gain">Value less what it cost, when the cost is known.</param>
/// <param name="Weight">Share of the priced total, 0–1.</param>
public sealed record PortfolioLine(string Symbol, string Name, double Shares, double Value, double DayChange, double? Gain, double? GainPercent, double Weight);

/// <param name="Unpriced">Holdings left out for now: no price yet, or no exchange rate to the portfolio's currency.</param>
public sealed record PortfolioSummary(string Currency, double Value, double DayChange, double? Gain, double? GainPercent,
    IReadOnlyList<PortfolioLine> Lines, IReadOnlyList<string> Unpriced)
{
    public double DayChangePercent => Value - DayChange is > 0 and var before ? DayChange / before * 100 : 0;
}

public sealed record PortfolioPoint(DateOnly Day, double Value);

/// <summary>Your holdings (watchlist entries with shares) valued in your base currency.</summary>
public static class Portfolio
{
    public static IEnumerable<WatchSymbol> Holdings(MarketSettings m) => m.Watchlist.Where(w => w.Shares is > 0);

    /// <summary>What the cost was paid in: the holding's own choice, or else the currency it's priced in.</summary>
    public static string CostCurrency(WatchSymbol w, Quote q) => string.IsNullOrEmpty(w.CostCurrency) ? q.Currency : w.CostCurrency;

    /// <summary>
    /// Today's value, day change and gain of every holding in <see cref="MarketSettings.BaseCurrency"/>. A cost paid in
    /// the base currency is taken as it is; any other is converted at today's rate, so its gain includes the currency's
    /// move since.
    /// </summary>
    public static PortfolioSummary Value(MarketSettings m, IReadOnlyDictionary<string, Quote> quotes, FxRates fx)
    {
        var currency = m.BaseCurrency;
        var priced = new List<(WatchSymbol W, double Value, double Day, double? Cost)>();
        var unpriced = new List<string>();
        foreach (var w in Holdings(m))
        {
            var shares = w.Shares!.Value;
            if (!quotes.TryGetValue(w.Symbol, out var q) || q.Price <= 0 || !fx.TryConvert(q.Price * shares, q.Currency, currency, out var value))
            {
                unpriced.Add(w.Symbol);
                continue;
            }
            fx.TryConvert(q.Change * shares, q.Currency, currency, out var day);
            double? cost = w.CostBasis is > 0 && fx.TryConvert(w.CostBasis.Value * shares, CostCurrency(w, q), currency, out var c) ? c : null;
            priced.Add((w, value, day, cost));
        }
        var total = priced.Sum(p => p.Value);
        var lines = priced
            .Select(p => new PortfolioLine(p.W.Symbol, p.W.Name, p.W.Shares!.Value, p.Value, p.Day,
                p.Cost is { } cost ? p.Value - cost : null,
                p.Cost is > 0 and var paid ? (p.Value / paid - 1) * 100 : null,
                total > 0 ? p.Value / total : 0))
            .OrderByDescending(l => l.Value)
            .ToList();
        var costed = priced.Where(p => p.Cost is not null).ToList();
        var invested = costed.Sum(p => p.Cost!.Value);
        double? gain = costed.Count > 0 ? costed.Sum(p => p.Value) - invested : null;
        double? gainPercent = costed.Count > 0 && invested > 0 ? gain / invested * 100 : null;
        return new PortfolioSummary(currency, total, priced.Sum(p => p.Day), gain, gainPercent, lines, unpriced);
    }

    /// <summary>
    /// What today's holdings were worth on each trading day from <paramref name="from"/>: each one's daily close times
    /// its shares, converted at that day's rate (today's rate where a day's isn't stored). A market that was shut
    /// carries its last close; days on which every market was shut are skipped. Holdings without stored closes are
    /// left out and returned in <paramref name="missing"/>.
    /// </summary>
    /// <param name="closes">Stored daily closes by symbol (oldest first, unix seconds), e.g. HubDatabase.GetCloses.</param>
    public static List<PortfolioPoint> History(MarketSettings m, IReadOnlyDictionary<string, Quote> quotes, FxRates fx,
        Func<string, IReadOnlyList<(long Day, double Close)>> closes, DateOnly from, out List<string> missing)
    {
        missing = new List<string>();
        var series = new List<(Daily Values, double Shares, Daily? Rates, double Rate, double Factor)>();
        foreach (var w in Holdings(m))
        {
            var currency = quotes.GetValueOrDefault(w.Symbol)?.Currency ?? "";
            var (major, factor) = FxRates.Major(currency);
            var (to, toFactor) = FxRates.Major(m.BaseCurrency);
            var history = closes(w.Symbol);
            if (history.Count == 0 || major.Length == 0 || to.Length == 0 || !fx.TryRate(major, to, out var today))
            {
                missing.Add(w.Symbol);
                continue;
            }
            Daily? rates = null;
            if (FxRates.PairSymbol(major, to) is { } pair && closes(pair) is { Count: > 0 } pairHistory) rates = new Daily(pairHistory);
            series.Add((new Daily(history), w.Shares!.Value, rates, today, factor / toFactor));
        }
        if (series.Count == 0) return new List<PortfolioPoint>();

        var days = series.SelectMany(s => s.Values.Days).Where(d => d >= from).Distinct().OrderBy(d => d).ToList();
        var points = new List<PortfolioPoint>(days.Count);
        foreach (var day in days)
        {
            double total = 0;
            var complete = true;
            foreach (var s in series)
            {
                if (s.Values.At(day) is not { } close)
                {
                    complete = false;
                    break;
                }
                var rate = s.Rates?.At(day) ?? s.Rate;
                total += close * s.Shares * rate * s.Factor;
            }
            // Before a holding's first stored close the total would jump when it starts; begin once all have one.
            if (complete) points.Add(new PortfolioPoint(day, total));
        }
        return points;
    }

    /// <summary>Closes by calendar day (UTC), and the last one on or before any day.</summary>
    private sealed class Daily
    {
        private readonly List<DateOnly> _days = new();
        private readonly List<double> _values = new();

        public Daily(IEnumerable<(long Day, double Close)> closes)
        {
            var map = new SortedDictionary<DateOnly, double>();
            foreach (var (day, close) in closes)
                if (close > 0 && double.IsFinite(close)) map[DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(day).UtcDateTime)] = close;
            _days.AddRange(map.Keys);
            _values.AddRange(map.Values);
        }

        public IReadOnlyList<DateOnly> Days => _days;

        public double? At(DateOnly day)
        {
            var i = _days.BinarySearch(day);
            if (i < 0) i = ~i - 1;
            return i >= 0 ? _values[i] : null;
        }
    }
}
