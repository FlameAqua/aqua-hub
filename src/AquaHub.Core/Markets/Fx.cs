namespace AquaHub.Core.Markets;

/// <summary>
/// Converts between currencies with Yahoo's exchange rates ("EURUSD=X" is the dollars one euro buys), keyed here as
/// "EURUSD". Pence and the other hundredths some exchanges quote in (GBp) are handled.
/// </summary>
public sealed class FxRates
{
    private readonly IReadOnlyDictionary<string, double> _rates;

    public FxRates(IReadOnlyDictionary<string, double> rates) => _rates = rates;

    public static FxRates None { get; } = new(new Dictionary<string, double>());

    public IReadOnlyDictionary<string, double> Rates => _rates;

    /// <summary>"GBp" (pence) → ("GBP", 0.01); "eur" → ("EUR", 1); anything that isn't a currency code → ("", 1).</summary>
    public static (string Currency, double Factor) Major(string? currency) => currency switch
    {
        "GBp" or "GBX" => ("GBP", 0.01),
        "ZAc" or "ZAC" => ("ZAR", 0.01),
        "ILA" => ("ILS", 0.01),
        { Length: 3 } c when c.All(char.IsAsciiLetter) => (c.ToUpperInvariant(), 1),
        _ => ("", 1),
    };

    /// <summary>The Yahoo symbol for the rate from one currency to another ("EURUSD=X"), or null when none is needed.</summary>
    public static string? PairSymbol(string? from, string? to)
    {
        var (f, _) = Major(from);
        var (t, _) = Major(to);
        return f.Length > 0 && t.Length > 0 && f != t ? $"{f}{t}=X" : null;
    }

    /// <summary>"EURUSD=X" → "EURUSD"; null for anything else.</summary>
    public static string? PairKey(string symbol) =>
        symbol.Length == 8 && symbol.EndsWith("=X", StringComparison.OrdinalIgnoreCase) ? symbol[..6].ToUpperInvariant() : null;

    public bool TryConvert(double amount, string? from, string? to, out double result)
    {
        result = 0;
        var (f, fromFactor) = Major(from);
        var (t, toFactor) = Major(to);
        if (f.Length == 0 || t.Length == 0 || !TryRate(f, t, out var rate)) return false;
        result = amount * fromFactor * rate / toFactor;
        return true;
    }

    /// <summary>How much of <paramref name="to"/> one unit of <paramref name="from"/> buys (both major currencies).</summary>
    public bool TryRate(string from, string to, out double rate)
    {
        rate = 1;
        if (from == to) return true;
        if (_rates.TryGetValue(from + to, out rate) && rate > 0) return true;
        if (_rates.TryGetValue(to + from, out var inverse) && inverse > 0)
        {
            rate = 1 / inverse;
            return true;
        }
        // Through the dollar: EUR→CHF from EURUSD and USDCHF.
        if (from != "USD" && to != "USD" && TryRate(from, "USD", out var a) && TryRate("USD", to, out var b))
        {
            rate = a * b;
            return true;
        }
        rate = 0;
        return false;
    }
}
