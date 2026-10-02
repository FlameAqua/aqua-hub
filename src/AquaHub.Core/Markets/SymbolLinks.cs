using AquaHub.Core.Models;

namespace AquaHub.Core.Markets;

public sealed record SymbolLink(string Label, string Url)
{
    public override string ToString() => Label;
}

/// <summary>Where to read more about a symbol: Yahoo Finance always, Google Finance and the exchange when the listing is known.</summary>
public static class SymbolLinks
{
    /// <summary>Yahoo's suffix → Google Finance's exchange code.</summary>
    private static readonly Dictionary<string, string> GoogleExchanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["L"] = "LON", ["AS"] = "AMS", ["PA"] = "EPA", ["DE"] = "ETR", ["F"] = "FRA", ["MI"] = "BIT", ["SW"] = "SWX",
        ["MC"] = "BME", ["BR"] = "EBR", ["LS"] = "ELI", ["ST"] = "STO", ["CO"] = "CPH", ["HE"] = "HEL",
        ["TO"] = "TSE", ["AX"] = "ASX", ["T"] = "TYO", ["NS"] = "NSE",
    };

    private static readonly Dictionary<string, string> GoogleIndices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["^GSPC"] = ".INX:INDEXSP", ["^IXIC"] = ".IXIC:INDEXNASDAQ", ["^DJI"] = ".DJI:INDEXDJX", ["^FTSE"] = "UKX:INDEXFTSE",
        ["^GDAXI"] = "DAX:INDEXDB", ["^STOXX50E"] = "SX5E:INDEXSTOXX", ["^N225"] = "NI225:INDEXNIKKEI", ["^FCHI"] = "PX1:INDEXEURO",
    };

    public static IReadOnlyList<SymbolLink> For(string symbol, string exchange, InstrumentKind kind)
    {
        var links = new List<SymbolLink> { new("Yahoo Finance", "https://finance.yahoo.com/quote/" + Uri.EscapeDataString(symbol) + "/") };
        if (GoogleId(symbol, exchange, kind) is { } google)
            links.Add(new("Google Finance", "https://www.google.com/finance/quote/" + Uri.EscapeDataString(google).Replace("%3A", ":")));
        if (kind is InstrumentKind.Equity or InstrumentKind.Etf && !symbol.Contains('.') && !symbol.Contains('='))
        {
            var lower = Uri.EscapeDataString(symbol.ToLowerInvariant());
            if (exchange.StartsWith("Nasdaq", StringComparison.OrdinalIgnoreCase))
                links.Add(new("Nasdaq", $"https://www.nasdaq.com/market-activity/{(kind == InstrumentKind.Etf ? "etf" : "stocks")}/{lower}"));
            else if (exchange.Equals("NYSE", StringComparison.OrdinalIgnoreCase))
                links.Add(new("NYSE", "https://www.nyse.com/quote/XNYS:" + Uri.EscapeDataString(symbol.ToUpperInvariant())));
        }
        return links;
    }

    /// <summary>"IWDA.AS" → "IWDA:AMS", "NVDA" on Nasdaq → "NVDA:NASDAQ", "EURUSD=X" → "EUR-USD"; null when unknown.</summary>
    public static string? GoogleId(string symbol, string exchange, InstrumentKind kind)
    {
        if (GoogleIndices.TryGetValue(symbol, out var index)) return index;
        if (kind == InstrumentKind.Fx || symbol.EndsWith("=X", StringComparison.OrdinalIgnoreCase))
            return symbol.Length == 8 ? $"{symbol[..3]}-{symbol[3..6]}".ToUpperInvariant() : null;
        if (kind == InstrumentKind.Crypto) return symbol.Contains('-') ? symbol.ToUpperInvariant() : null;
        if (kind is not (InstrumentKind.Equity or InstrumentKind.Etf) || symbol.StartsWith('^') || symbol.Contains('=')) return null;
        var dot = symbol.LastIndexOf('.');
        if (dot > 0) return GoogleExchanges.TryGetValue(symbol[(dot + 1)..], out var code) ? $"{symbol[..dot].ToUpperInvariant()}:{code}" : null;
        var us = exchange.ToUpperInvariant() switch
        {
            var e when e.StartsWith("NASDAQ", StringComparison.Ordinal) => "NASDAQ",
            "NYSE" => "NYSE",
            "NYSEARCA" or "NYSE ARCA" => "NYSEARCA",
            "NYSE AMERICAN" or "NYSEAMERICAN" => "NYSEAMERICAN",
            _ => null,
        };
        return us is null ? null : $"{symbol.ToUpperInvariant()}:{us}";
    }
}
