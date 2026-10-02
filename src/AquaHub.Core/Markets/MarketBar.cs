using AquaHub.Core.Settings;

namespace AquaHub.Core.Markets;

/// <summary>
/// The row of charts at the top of Markets: your indices, then currencies, crypto and commodities, shown as one row
/// you can reorder. Each chart stays in its list; a move across the two swaps a pair, so both keep their length.
/// </summary>
public static class MarketBar
{
    public static List<WatchSymbol> Items(MarketSettings m) => m.Indices.Concat(m.Macro).ToList();

    /// <summary>Moves a chart one place left (<paramref name="step"/> −1) or right (+1).</summary>
    public static bool Move(MarketSettings m, string symbol, int step)
    {
        var all = Items(m);
        var i = all.FindIndex(w => Same(w, symbol));
        var j = i + Math.Sign(step);
        if (i < 0 || step == 0 || j < 0 || j >= all.Count) return false;
        (all[i], all[j]) = (all[j], all[i]);
        var split = m.Indices.Count;
        m.Indices = all.Take(split).ToList();
        m.Macro = all.Skip(split).ToList();
        return true;
    }

    public static bool Remove(MarketSettings m, string symbol) =>
        m.Indices.RemoveAll(w => Same(w, symbol)) + m.Macro.RemoveAll(w => Same(w, symbol)) > 0;

    /// <summary>Adds a chart at the end of the row, unless it's already there.</summary>
    public static bool Add(MarketSettings m, WatchSymbol chart)
    {
        if (Items(m).Any(w => Same(w, chart.Symbol))) return false;
        m.Macro.Add(chart);
        return true;
    }

    private static bool Same(WatchSymbol w, string symbol) => w.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase);
}
