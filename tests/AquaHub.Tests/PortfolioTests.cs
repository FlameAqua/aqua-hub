using System.Globalization;
using AquaHub.Core.Agents;
using AquaHub.Core.Markets;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

/// <summary>Holdings in your own currency: typed numbers, exchange rates, value, gain, history, and the chart row on Markets.</summary>
public class PortfolioTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-IE");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Theory]
    [InlineData("0.5", 0.5)]
    [InlineData("0,5", 0.5)]
    [InlineData("12.25", 12.25)]
    [InlineData("1,234", 1234)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("1,234.50", 1234.5)]
    [InlineData("1.234,50", 1234.5)]
    [InlineData(" 1 234,5 ", 1234.5)]
    [InlineData("€150", 150)]
    [InlineData("-3.5", -3.5)]
    [InlineData("7", 7)]
    public void NumbersAreReadTheWayTheyreTyped(string typed, double expected)
    {
        Assert.True(Numbers.TryParse(typed, English, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Fact]
    public void ThePlacesSettingsDecideAmbiguousNumbers()
    {
        // "1.234" is a thousand in German and a bit over one in English; "0.5" and "0,5" are a half in both.
        Assert.True(Numbers.TryParse("1.234", German, out var de));
        Assert.Equal(1234, de);
        Assert.True(Numbers.TryParse("1.234", English, out var en));
        Assert.Equal(1.234, en, 6);
        Assert.True(Numbers.TryParse("0.5", German, out var half));
        Assert.Equal(0.5, half);
        Assert.True(Numbers.TryParse("1,234", German, out var deComma));
        Assert.Equal(1.234, deComma, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData(",")]
    [InlineData("1e400")]
    public void NotANumber(string typed) => Assert.False(Numbers.TryParse(typed, English, out _));

    [Fact]
    public void InputBoxesShowPlainNumbers()
    {
        Assert.Equal("0.5", Numbers.Format(0.5, English));
        Assert.Equal("1234.125", Numbers.Format(1234.125, English));
        Assert.Equal("0,5", Numbers.Format(0.5, German));
        Assert.Equal("", Numbers.Format(null, English));
    }

    private static FxRates Rates(params (string Pair, double Rate)[] rates) => new(rates.ToDictionary(r => r.Pair, r => r.Rate));

    [Fact]
    public void RatesWorkBothWaysAndThroughTheDollar()
    {
        var fx = Rates(("EURUSD", 1.25), ("USDCHF", 0.8));
        Assert.True(fx.TryConvert(10, "EUR", "USD", out var usd));
        Assert.Equal(12.5, usd, 6);
        Assert.True(fx.TryConvert(12.5, "USD", "EUR", out var eur));
        Assert.Equal(10, eur, 6);
        Assert.True(fx.TryConvert(10, "EUR", "CHF", out var chf));
        Assert.Equal(10, chf, 6);
        Assert.True(fx.TryConvert(5, "eur", "EUR", out var same));
        Assert.Equal(5, same);
        Assert.False(fx.TryConvert(1, "JPY", "EUR", out _));
        Assert.False(fx.TryConvert(1, "", "EUR", out _));
    }

    [Fact]
    public void PenceArePoundsDividedByAHundred()
    {
        var fx = Rates(("GBPEUR", 1.2));
        Assert.True(fx.TryConvert(1000, "GBp", "EUR", out var eur));
        Assert.Equal(12, eur, 6);
        Assert.True(fx.TryConvert(250, "GBp", "GBP", out var gbp));
        Assert.Equal(2.5, gbp, 6);
        Assert.Equal("GBPEUR=X", FxRates.PairSymbol("GBp", "EUR"));
        Assert.Null(FxRates.PairSymbol("GBp", "GBP"));
        Assert.Null(FxRates.PairSymbol("", "EUR"));
        Assert.Equal("EURUSD", FxRates.PairKey("EURUSD=X"));
        Assert.Null(FxRates.PairKey("BTC-USD"));
    }

    private static Quote Q(string symbol, double price, double previous, string currency) =>
        new() { Symbol = symbol, Price = price, PreviousClose = previous, Currency = currency };

    private static MarketSettings Holdings(string baseCurrency, params WatchSymbol[] watch) =>
        new() { BaseCurrency = baseCurrency, Watchlist = watch.ToList() };

    [Fact]
    public void HoldingsAreValuedInYourCurrency()
    {
        var m = Holdings("EUR",
            new WatchSymbol { Symbol = "NVDA", Name = "NVIDIA", Shares = 2.5, CostBasis = 100, CostCurrency = "EUR" },
            new WatchSymbol { Symbol = "IWDA.AS", Name = "iShares MSCI World", Shares = 10, CostBasis = 80 },
            new WatchSymbol { Symbol = "MSFT", Name = "Microsoft" });   // watched, not held
        var quotes = new Dictionary<string, Quote>
        {
            ["NVDA"] = Q("NVDA", 200, 190, "USD"),
            ["IWDA.AS"] = Q("IWDA.AS", 100, 101, "EUR"),
            ["MSFT"] = Q("MSFT", 400, 400, "USD"),
        };
        var p = Portfolio.Value(m, quotes, Rates(("EURUSD", 1.25)));

        Assert.Equal("EUR", p.Currency);
        Assert.Equal(400 + 1000, p.Value, 6);                    // 2.5 × $200 = $500 = €400; 10 × €100
        Assert.Equal(20 - 10, p.DayChange, 6);                   // +$25 = €20; −€10
        Assert.Equal(new[] { "IWDA.AS", "NVDA" }, p.Lines.Select(l => l.Symbol));
        var nvda = p.Lines.Single(l => l.Symbol == "NVDA");
        Assert.Equal(150, nvda.Gain!.Value, 6);                  // paid €250 in euros
        Assert.Equal(60, nvda.GainPercent!.Value, 6);
        Assert.Equal(400 / 1400.0, nvda.Weight, 6);
        Assert.Equal(350, p.Gain!.Value, 6);                     // €150 + €200
        Assert.Equal(350 / 1050.0 * 100, p.GainPercent!.Value, 6);
        Assert.Empty(p.Unpriced);
    }

    [Fact]
    public void AskIsToldThePortfolioAsTheCardShowsIt()
    {
        var m = Holdings("EUR",
            new WatchSymbol { Symbol = "NVDA", Name = "NVIDIA", Shares = 2.5, CostBasis = 100, CostCurrency = "EUR" },
            new WatchSymbol { Symbol = "IWDA.AS", Name = "iShares MSCI World", Shares = 10, CostBasis = 80 },
            new WatchSymbol { Symbol = "7203.T", Name = "Toyota", Shares = 100 });
        var quotes = new Dictionary<string, Quote>
        {
            ["NVDA"] = Q("NVDA", 200, 190, "USD"),
            ["IWDA.AS"] = Q("IWDA.AS", 100, 101, "EUR"),
            ["7203.T"] = Q("7203.T", 2500, 2480, "JPY"),
        };
        var text = AquaHub.Core.Ai.Assistant.AskAgent.PortfolioText(Portfolio.Value(m, quotes, Rates(("EURUSD", 1.25))));

        Assert.StartsWith("THE USER'S PORTFOLIO (their own holdings from Settings › Markets, valued in EUR", text, StringComparison.Ordinal);
        Assert.Contains("Worth 1,400.00 EUR, today +10.00 EUR (+0.7%), +350.00 EUR (+33.3%) since bought.", text);
        Assert.Contains("- NVDA (NVIDIA): 2.5 shares, 400.00 EUR, today +20.00 EUR, +150.00 EUR (+60.0%) since bought, 29% of the portfolio", text);
        Assert.Contains("- IWDA.AS (iShares MSCI World): 10 shares, 1,000.00 EUR, today −10.00 EUR, +200.00 EUR (+25.0%) since bought, 71% of the portfolio", text);
        Assert.Contains("Not valued yet (no price or exchange rate): 7203.T", text);
    }

    [Fact]
    public void WithoutARateAHoldingWaitsRatherThanBeingAddedInTheWrongCurrency()
    {
        var m = Holdings("EUR", new WatchSymbol { Symbol = "7203.T", Shares = 100 }, new WatchSymbol { Symbol = "SAP.DE", Shares = 1 });
        var quotes = new Dictionary<string, Quote> { ["7203.T"] = Q("7203.T", 2500, 2500, "JPY"), ["SAP.DE"] = Q("SAP.DE", 200, 200, "EUR") };
        var p = Portfolio.Value(m, quotes, FxRates.None);
        Assert.Equal(200, p.Value);
        Assert.Equal(new[] { "7203.T" }, p.Unpriced);
        Assert.Null(p.Gain);
    }

    [Fact]
    public void TheRatesNeededAreAskedFor()
    {
        var m = Holdings("EUR",
            new WatchSymbol { Symbol = "NVDA", Shares = 1, CostBasis = 90, CostCurrency = "GBP" },
            new WatchSymbol { Symbol = "VOD.L", Shares = 100 },
            new WatchSymbol { Symbol = "SAP.DE", Shares = 1 },
            new WatchSymbol { Symbol = "AAPL" });
        var quotes = new Dictionary<string, Quote>
        {
            ["NVDA"] = Q("NVDA", 1, 1, "USD"), ["VOD.L"] = Q("VOD.L", 1, 1, "GBp"), ["SAP.DE"] = Q("SAP.DE", 1, 1, "EUR"), ["AAPL"] = Q("AAPL", 1, 1, "USD"),
        };
        Assert.Equal(new[] { "USDEUR=X", "GBPEUR=X" }, MarketWatchAgent.FxPairs(m, quotes));
    }

    private static long Day(int year, int month, int day) => new DateTimeOffset(year, month, day, 14, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void HistoryValuesTodaysHoldingsAtEachDaysPriceAndRate()
    {
        var m = Holdings("EUR", new WatchSymbol { Symbol = "NVDA", Shares = 2 }, new WatchSymbol { Symbol = "SAP.DE", Shares = 1 });
        var quotes = new Dictionary<string, Quote> { ["NVDA"] = Q("NVDA", 100, 100, "USD"), ["SAP.DE"] = Q("SAP.DE", 50, 50, "EUR") };
        var closes = new Dictionary<string, List<(long, double)>>
        {
            ["NVDA"] = new() { (Day(2026, 9, 1), 100), (Day(2026, 9, 2), 110), (Day(2026, 9, 4), 120) },
            ["SAP.DE"] = new() { (Day(2026, 8, 31), 40), (Day(2026, 9, 2), 45), (Day(2026, 9, 3), 50) },
            ["USDEUR=X"] = new() { (Day(2026, 9, 1), 0.5), (Day(2026, 9, 3), 0.8) },
        };
        var points = Portfolio.History(m, quotes, Rates(("USDEUR", 0.9)), s => closes.GetValueOrDefault(s) ?? new(), new DateOnly(2026, 8, 1), out var missing);

        Assert.Empty(missing);
        // Aug 31 is left out (no NVDA yet); Sept 3 carries NVDA's close forward; the rate is that day's or the last before.
        Assert.Equal(new[] { new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 4) }, points.Select(p => p.Day));
        Assert.Equal(2 * 100 * 0.5 + 40, points[0].Value, 6);
        Assert.Equal(2 * 110 * 0.5 + 45, points[1].Value, 6);
        Assert.Equal(2 * 110 * 0.8 + 50, points[2].Value, 6);
        Assert.Equal(2 * 120 * 0.8 + 50, points[3].Value, 6);

        var fromSept3 = Portfolio.History(m, quotes, Rates(("USDEUR", 0.9)), s => closes.GetValueOrDefault(s) ?? new(), new DateOnly(2026, 9, 3), out _);
        Assert.Equal(2, fromSept3.Count);
    }

    [Fact]
    public void HistoryWithoutStoredRatesUsesTodaysAndNamesWhatItCantShow()
    {
        var m = Holdings("EUR", new WatchSymbol { Symbol = "NVDA", Shares = 1 }, new WatchSymbol { Symbol = "NEW", Shares = 1 });
        var quotes = new Dictionary<string, Quote> { ["NVDA"] = Q("NVDA", 100, 100, "USD"), ["NEW"] = Q("NEW", 5, 5, "USD") };
        var closes = new Dictionary<string, List<(long, double)>> { ["NVDA"] = new() { (Day(2026, 9, 1), 100) } };
        var points = Portfolio.History(m, quotes, Rates(("EURUSD", 1.25)), s => closes.GetValueOrDefault(s) ?? new(), new DateOnly(2026, 1, 1), out var missing);
        Assert.Equal(80, Assert.Single(points).Value, 6);
        Assert.Equal(new[] { "NEW" }, missing);
    }

    private static MarketSettings Bar() => new()
    {
        Indices = new() { new() { Symbol = "^GSPC" }, new() { Symbol = "^FTSE" } },
        Macro = new() { new() { Symbol = "EURUSD=X" }, new() { Symbol = "BTC-USD" } },
    };

    private static string Order(MarketSettings m) => string.Join(" ", MarketBar.Items(m).Select(w => w.Symbol));

    [Fact]
    public void TheChartRowCanBeReordered()
    {
        var m = Bar();
        Assert.True(MarketBar.Move(m, "BTC-USD", -1));
        Assert.Equal("^GSPC ^FTSE BTC-USD EURUSD=X", Order(m));
        // Across the two lists: the pair swaps, so each keeps its length.
        Assert.True(MarketBar.Move(m, "btc-usd", -1));
        Assert.Equal("^GSPC BTC-USD ^FTSE EURUSD=X", Order(m));
        Assert.Equal(2, m.Indices.Count);
        Assert.True(MarketBar.Move(m, "BTC-USD", -1));
        Assert.Equal("BTC-USD ^GSPC ^FTSE EURUSD=X", Order(m));
        Assert.False(MarketBar.Move(m, "BTC-USD", -1));
        Assert.False(MarketBar.Move(m, "EURUSD=X", +1));
        Assert.False(MarketBar.Move(m, "NOPE", +1));
    }

    [Fact]
    public void ChartsCanBeAddedOnceAndRemoved()
    {
        var m = Bar();
        Assert.True(MarketBar.Add(m, new WatchSymbol { Symbol = "GC=F", Name = "Gold", Kind = "commodity" }));
        Assert.False(MarketBar.Add(m, new WatchSymbol { Symbol = "gc=f" }));
        Assert.Equal("^GSPC ^FTSE EURUSD=X BTC-USD GC=F", Order(m));
        Assert.True(MarketBar.Remove(m, "^FTSE"));
        Assert.True(MarketBar.Remove(m, "eurusd=x"));
        Assert.False(MarketBar.Remove(m, "^FTSE"));
        Assert.Equal("^GSPC BTC-USD GC=F", Order(m));
    }

    [Theory]
    [InlineData("NVDA", "NasdaqGS", InstrumentKind.Equity, "NVDA:NASDAQ")]
    [InlineData("IBM", "NYSE", InstrumentKind.Equity, "IBM:NYSE")]
    [InlineData("SPY", "NYSEArca", InstrumentKind.Etf, "SPY:NYSEARCA")]
    [InlineData("IWDA.AS", "Amsterdam", InstrumentKind.Etf, "IWDA:AMS")]
    [InlineData("VOLV-B.ST", "Stockholm", InstrumentKind.Equity, "VOLV-B:STO")]
    [InlineData("^GSPC", "SNP", InstrumentKind.Index, ".INX:INDEXSP")]
    [InlineData("EURUSD=X", "CCY", InstrumentKind.Fx, "EUR-USD")]
    [InlineData("BTC-USD", "CCC", InstrumentKind.Crypto, "BTC-USD")]
    [InlineData("GC=F", "COMEX", InstrumentKind.Commodity, null)]
    [InlineData("^ISEQ", "ISE", InstrumentKind.Index, null)]
    [InlineData("EQNR.OL", "Oslo", InstrumentKind.Equity, null)]
    [InlineData("XYZ", "OTC", InstrumentKind.Equity, null)]
    public void GoogleFinanceIdsMatchTheListing(string symbol, string exchange, InstrumentKind kind, string? expected) =>
        Assert.Equal(expected, SymbolLinks.GoogleId(symbol, exchange, kind));

    [Fact]
    public void EverySymbolLinksToYahooAndUsListingsToTheirExchange()
    {
        var nvda = SymbolLinks.For("NVDA", "NasdaqGS", InstrumentKind.Equity);
        Assert.Equal(new[] { "Yahoo Finance", "Google Finance", "Nasdaq" }, nvda.Select(l => l.Label));
        Assert.Equal("https://www.nasdaq.com/market-activity/stocks/nvda", nvda[2].Url);
        Assert.Equal("https://www.nyse.com/quote/XNYS:IBM", SymbolLinks.For("IBM", "NYSE", InstrumentKind.Equity)[^1].Url);
        Assert.Equal("https://finance.yahoo.com/quote/%5EGSPC/", SymbolLinks.For("^GSPC", "SNP", InstrumentKind.Index)[0].Url);
        Assert.Equal("https://www.google.com/finance/quote/.INX:INDEXSP", SymbolLinks.For("^GSPC", "SNP", InstrumentKind.Index)[1].Url);
        Assert.Single(SymbolLinks.For("GC=F", "COMEX", InstrumentKind.Commodity));
        Assert.All(SymbolLinks.For("IWDA.AS", "Amsterdam", InstrumentKind.Etf), l => Assert.StartsWith("https://", l.Url));
    }

    [Fact]
    public void HoldingsSettingsAreCleanedUp()
    {
        var s = new HubSettings();
        s.Markets.BaseCurrency = "eur";
        s.Markets.Watchlist = new()
        {
            new() { Symbol = "NVDA", Shares = -1, CostBasis = 0, CostCurrency = "euro" },
            new() { Symbol = "SAP.DE", Shares = 0.25, CostBasis = 150.5, CostCurrency = "gbp" },
        };
        SettingsStore.Validate(s);
        Assert.Equal("EUR", s.Markets.BaseCurrency);
        Assert.Null(s.Markets.Watchlist[0].Shares);
        Assert.Null(s.Markets.Watchlist[0].CostBasis);
        Assert.Equal("", s.Markets.Watchlist[0].CostCurrency);
        Assert.Equal(0.25, s.Markets.Watchlist[1].Shares);
        Assert.Equal("GBP", s.Markets.Watchlist[1].CostCurrency);
    }
}
