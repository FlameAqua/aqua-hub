using AquaHub.Core.Models;

namespace AquaHub.Core.Analysis;

/// <summary>
/// Transparent, rule-based technical analysis over daily closes. The resulting score and its
/// human-readable factors are shown verbatim in the UI and fed to the market analyst agent, so AI
/// commentary is always grounded in computed numbers rather than model recall.
/// </summary>
public static class TechnicalIndicators
{
    public static double? Sma(IReadOnlyList<double> closes, int period)
    {
        if (closes.Count < period || period <= 0) return null;
        double sum = 0;
        for (var i = closes.Count - period; i < closes.Count; i++) sum += closes[i];
        return sum / period;
    }

    public static double[] Ema(IReadOnlyList<double> values, int period)
    {
        var result = new double[values.Count];
        if (values.Count == 0) return result;
        var k = 2.0 / (period + 1);
        result[0] = values[0];
        for (var i = 1; i < values.Count; i++) result[i] = values[i] * k + result[i - 1] * (1 - k);
        return result;
    }

    /// <summary>Wilder's RSI.</summary>
    public static double? Rsi(IReadOnlyList<double> closes, int period = 14)
    {
        if (closes.Count <= period) return null;
        double gain = 0, loss = 0;
        for (var i = 1; i <= period; i++)
        {
            var d = closes[i] - closes[i - 1];
            if (d >= 0) gain += d; else loss -= d;
        }
        gain /= period; loss /= period;
        for (var i = period + 1; i < closes.Count; i++)
        {
            var d = closes[i] - closes[i - 1];
            gain = (gain * (period - 1) + Math.Max(d, 0)) / period;
            loss = (loss * (period - 1) + Math.Max(-d, 0)) / period;
        }
        if (loss == 0) return 100;
        var rs = gain / loss;
        return 100 - 100 / (1 + rs);
    }

    /// <summary>Annualised volatility (%) of daily log returns over the last <paramref name="period"/> bars.</summary>
    public static double? Volatility(IReadOnlyList<double> closes, int period = 20)
    {
        if (closes.Count < period + 1) return null;
        var rets = new List<double>(period);
        for (var i = closes.Count - period; i < closes.Count; i++)
            rets.Add(Math.Log(closes[i] / closes[i - 1]));
        var mean = rets.Average();
        var variance = rets.Sum(r => (r - mean) * (r - mean)) / (rets.Count - 1);
        return Math.Sqrt(variance) * Math.Sqrt(252) * 100;
    }

    private static double? Return(IReadOnlyList<double> closes, double last, int barsBack)
    {
        if (closes.Count <= barsBack) return null;
        var basis = closes[closes.Count - 1 - barsBack];
        return basis > 0 ? (last / basis - 1) * 100 : null;
    }

    public static Indicators Compute(string symbol, IReadOnlyList<(long Day, double Close)> history, double? livePrice = null,
        double? previousClose = null)
    {
        var closes = history.Select(h => h.Close).Where(c => c > 0 && !double.IsNaN(c)).ToList();
        if (closes.Count == 0)
            return new Indicators { Symbol = symbol, Last = livePrice ?? 0, Factors = new() { "Not enough price history yet." } };

        var last = livePrice is > 0 ? livePrice.Value : closes[^1];
        // Treat the live price as today's bar if the last stored bar is older than today.
        var series = new List<double>(closes);
        if (livePrice is > 0 && history.Count > 0 &&
            DateTimeOffset.FromUnixTimeSeconds(history[^1].Day).UtcDateTime.Date < DateTime.UtcNow.Date)
            series.Add(livePrice.Value);
        else if (livePrice is > 0)
            series[^1] = livePrice.Value;

        var sma20 = Sma(series, 20);
        var sma50 = Sma(series, 50);
        var sma200 = Sma(series, 200);
        var rsi = Rsi(series);
        var vol = Volatility(series);
        var window = series.Skip(Math.Max(0, series.Count - 252)).ToList();
        var hi = window.Max();
        var lo = window.Min();
        double? pos52 = hi > lo ? (last - lo) / (hi - lo) : null;
        double? fromHigh = hi > 0 ? (last / hi - 1) * 100 : null;

        double? macdHist = null;
        if (series.Count >= 35)
        {
            var e12 = Ema(series, 12);
            var e26 = Ema(series, 26);
            var macd = e12.Zip(e26, (a, b) => a - b).ToList();
            var signal = Ema(macd, 9);
            macdHist = macd[^1] - signal[^1];
        }

        double? ytd = null;
        var year = DateTime.UtcNow.Year;
        var firstThisYear = history.Select((h, i) => (h, i)).FirstOrDefault(x => DateTimeOffset.FromUnixTimeSeconds(x.h.Day).Year == year);
        if (firstThisYear.i > 0) ytd = (last / history[firstThisYear.i - 1].Close - 1) * 100;

        double? ret1d = previousClose is > 0 ? (last / previousClose.Value - 1) * 100 : Return(series, last, 1);

        // ── Composite score with explicit factors ──
        var factors = new List<string>();
        var score = 0.0;
        var trend = "sideways";
        if (sma50 is not null && sma200 is not null)
        {
            if (last > sma50 && sma50 > sma200) { score += 30; trend = "uptrend"; factors.Add("Price above rising 50- and 200-day averages (uptrend)"); }
            else if (last < sma50 && sma50 < sma200) { score -= 30; trend = "downtrend"; factors.Add("Price below falling 50- and 200-day averages (downtrend)"); }
            else if (last > sma200) { score += 10; trend = "recovering"; factors.Add("Above the 200-day average but trend is mixed"); }
            else { score -= 10; trend = "weakening"; factors.Add("Below the 200-day average; trend is mixed"); }
        }
        else if (sma50 is not null)
        {
            if (last > sma50) { score += 12; trend = "uptrend"; factors.Add("Above the 50-day average"); }
            else { score -= 12; trend = "downtrend"; factors.Add("Below the 50-day average"); }
        }

        var r3m = Return(series, last, 63);
        if (r3m is not null)
        {
            score += Math.Clamp(r3m.Value, -30, 30) * 0.8;
            factors.Add($"3-month momentum {r3m.Value:+0.0;-0.0}%");
        }

        if (rsi is not null)
        {
            if (rsi > 70) { score -= 10; factors.Add($"RSI {rsi:0} — overbought, pullback risk"); }
            else if (rsi < 30) { score += 10; factors.Add($"RSI {rsi:0} — oversold, rebound potential"); }
            else if (rsi >= 50) { score += 5; factors.Add($"RSI {rsi:0} — positive momentum"); }
            else { score -= 5; factors.Add($"RSI {rsi:0} — soft momentum"); }
        }

        if (macdHist is not null)
        {
            score += macdHist > 0 ? 8 : -8;
            factors.Add(macdHist > 0 ? "MACD above signal line" : "MACD below signal line");
        }

        if (pos52 is not null)
        {
            if (pos52 > 0.9) { score += 5; factors.Add("Trading near its 52-week high"); }
            else if (pos52 < 0.1) { score -= 5; factors.Add("Trading near its 52-week low"); }
        }

        if (vol is > 45) factors.Add($"High volatility ({vol:0}% annualised)");

        var tech = (int)Math.Round(Math.Clamp(score, -100, 100));
        return new Indicators
        {
            Symbol = symbol,
            Last = last,
            Sma20 = sma20,
            Sma50 = sma50,
            Sma200 = sma200,
            Rsi14 = rsi,
            Volatility20 = vol,
            Ret1D = ret1d,
            Ret5D = Return(series, last, 5),
            Ret1M = Return(series, last, 21),
            Ret3M = r3m,
            RetYtd = ytd,
            Ret1Y = Return(series, last, 252),
            Position52 = pos52,
            FromHigh = fromHigh,
            MacdHist = macdHist,
            Trend = trend,
            TechScore = tech,
            Signal = tech >= 25 ? TechSignal.Bullish : tech <= -25 ? TechSignal.Bearish : TechSignal.Neutral,
            Factors = factors,
        };
    }
}
