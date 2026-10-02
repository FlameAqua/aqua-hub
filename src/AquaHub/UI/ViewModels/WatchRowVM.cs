using System.Globalization;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.UI.ViewModels;

public sealed record CurrencyOption(string Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A watchlist row in Settings: how many you own, what one cost and in which currency, and price alerts. Numbers are
/// kept as typed ("0.5", "0,5") and stored once they read as a number.
/// </summary>
public sealed class WatchRowVM
{
    public static IReadOnlyList<string> CommonCurrencies { get; } = new[] { "EUR", "USD", "GBP", "CHF", "JPY", "CAD", "AUD", "SEK", "NOK", "DKK", "PLN", "HKD", "SGD", "INR", "CNY", "NZD", "ZAR" };

    private readonly WatchSymbol _w;
    private readonly Action _changed;
    private string _shares, _cost, _above, _below;

    /// <param name="priceCurrency">What the symbol is priced in ("USD", "GBp"), or "" before its first quote.</param>
    public WatchRowVM(WatchSymbol w, string priceCurrency, string baseCurrency, Action changed)
    {
        _w = w;
        _changed = changed;
        var culture = CultureInfo.CurrentCulture;
        _shares = Numbers.Format(w.Shares, culture);
        _cost = Numbers.Format(w.CostBasis, culture);
        _above = Numbers.Format(w.AlertAbove, culture);
        _below = Numbers.Format(w.AlertBelow, culture);
        Currencies = new[] { new CurrencyOption("", priceCurrency.Length > 0 ? priceCurrency : "Same as price") }
            .Concat(new[] { baseCurrency, w.CostCurrency }.Concat(CommonCurrencies)
                .Where(c => c.Length == 3 && c != priceCurrency)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c => new CurrencyOption(c, c)))
            .ToList();
    }

    public string Symbol => _w.Symbol;
    public string Name => _w.Name;
    // Screen readers (and the UI tests) know a row as "NVDA, NVIDIA".
    public override string ToString() => _w.ToString();

    public IReadOnlyList<CurrencyOption> Currencies { get; }

    public string Shares { get => _shares; set => Set(ref _shares, value, v => _w.Shares = v); }
    public string Cost { get => _cost; set => Set(ref _cost, value, v => _w.CostBasis = v); }
    public string AlertAbove { get => _above; set => Set(ref _above, value, v => _w.AlertAbove = v); }
    public string AlertBelow { get => _below; set => Set(ref _below, value, v => _w.AlertBelow = v); }

    public string CostCurrency
    {
        get => _w.CostCurrency;
        set
        {
            if ((value ?? "") == _w.CostCurrency) return;
            _w.CostCurrency = value ?? "";
            _changed();
        }
    }

    /// <summary>Keeps the text; stores it when it's empty (none) or a number, and leaves the setting alone otherwise.</summary>
    private void Set(ref string field, string text, Action<double?> store)
    {
        if (field == text) return;
        field = text;
        if (string.IsNullOrWhiteSpace(text)) store(null);
        else if (Numbers.TryParse(text, CultureInfo.CurrentCulture, out var v)) store(v);
        else return;
        _changed();
    }
}
