using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Markets;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public sealed record StatVM(string Label, string Value, Brush Brush)
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => $"{Label}: {Value}";
}

public sealed record IdeaVM(string Title, string Thesis, string KindLabel, string RiskLabel, Brush RiskBrush, Brush RiskSoft, string Horizon, string SymbolsText)
{
    public override string ToString() => Title;
}

/// <summary>A chart in the row at the top while it's being edited.</summary>
public sealed record BarItemVM(string Symbol, string Name, string PriceText, bool CanMoveLeft, bool CanMoveRight)
{
    public string MoveLeftName => $"Move {Name} left";
    public string MoveRightName => $"Move {Name} right";
    public string RemoveName => $"Remove {Name}";
    public override string ToString() => Name;
}

public sealed record PortfolioLineVM(string Symbol, string Detail, double Weight, string WeightText, string ValueText, string GainText, Brush GainBrush)
{
    public override string ToString() => $"{Symbol}: {ValueText}, {WeightText} of the portfolio" + (GainText.Length > 0 ? $", {GainText}" : "");
}

public sealed class MarketsVM : ObservableObject
{
    public string Subtitle { get; set; } = "";
    public List<QuoteVM> Tiles { get; set; } = new();
    public List<QuoteVM> Watchlist { get; set; } = new();
    public QuoteVM? Detail { get; set; }
    public string DetailExchange { get; set; } = "";
    public string? HoldingText { get; set; }
    public List<SymbolLink> Links { get; set; } = new();
    public bool Editing { get; set; }
    public List<BarItemVM> BarItems { get; set; } = new();
    public bool HasPortfolio { get; set; }
    public string PortfolioValue { get; set; } = "";
    public string PortfolioDay { get; set; } = "";
    public Brush PortfolioDayBrush { get; set; } = Brushes.Gray;
    public string PortfolioGainSeparator { get; set; } = "";
    public string PortfolioGain { get; set; } = "";
    public string PortfolioGainLabel { get; set; } = "";
    public Brush PortfolioGainBrush { get; set; } = Brushes.Gray;
    public List<PortfolioLineVM> PortfolioLines { get; set; } = new();
    public string PortfolioNote { get; set; } = "";
    public Brush ChartBrush { get; set; } = Brushes.DeepSkyBlue;
    public List<StatVM> Stats { get; set; } = new();
    public List<string> Factors { get; set; } = new();
    public string InsightSummary { get; set; } = "";
    public string InsightConfidence { get; set; } = "";
    public double InsightConfidenceValue { get; set; }
    public List<string> InsightRationale { get; set; } = new();
    public List<string> InsightRisks { get; set; } = new();
    public string BriefOverview { get; set; } = "";
    public string BriefBadge { get; set; } = "";
    public string BriefMeta { get; set; } = "";
    public string SessionText { get; set; } = "";
    public List<string> Highlights { get; set; } = new();
    public List<IdeaVM> Ideas { get; set; } = new();
    public void Changed() => RaiseAll();
}

public partial class MarketsPage : UserControl, IPage
{
    private readonly MarketsVM _vm = new();
    private readonly UiThrottle _refresh;
    private readonly Dictionary<string, (DateTime At, ChartData Data)> _charts = new();
    private string _symbol = "";
    private string _range = "1d";
    private string _portfolioRange = "1mo";
    private string _portfolioKey = "";
    private CancellationTokenSource? _searchCts;

    private static readonly (string Id, string Label, string Interval, string Format)[] RangeDefs =
    {
        ("1d", "1D", "5m", "HH:mm"), ("5d", "5D", "15m", "ddd HH:mm"), ("1mo", "1M", "60m", "d MMM"), ("6mo", "6M", "1d", "d MMM"), ("1y", "1Y", "1d", "MMM yy"),
    };

    public MarketsPage()
    {
        InitializeComponent();
        DataContext = _vm;
        _refresh = new UiThrottle(Refresh, 250);
        SearchKeys.Attach(AddBox, SearchResults, () => OnPickSymbol(SearchResults, null!), () => SearchPopup.IsOpen = false);
        foreach (var (id, label, _, _) in RangeDefs)
        {
            var chip = new RadioButton { Content = label, Tag = id, Style = (Style)FindResource("Chip"), GroupName = "range", IsChecked = id == "1d", Padding = new Thickness(10, 3, 10, 3) };
            chip.Checked += (_, _) => { _range = id; _ = LoadChartAsync(); };
            Ranges.Children.Add(chip);
        }
        foreach (var (id, label) in new[] { ("1mo", "1M"), ("6mo", "6M"), ("1y", "1Y") })
        {
            var chip = new RadioButton { Content = label, Tag = id, Style = (Style)FindResource("Chip"), GroupName = "portfolio-range", IsChecked = id == _portfolioRange, Padding = new Thickness(10, 3, 10, 3) };
            System.Windows.Automation.AutomationProperties.SetName(chip, $"Portfolio over {label}");
            chip.Checked += (_, _) => { _portfolioRange = id; _ = LoadPortfolioChartAsync(); };
            PortfolioRanges.Children.Add(chip);
        }
    }

    public void OnNavigatedTo(string? arg)
    {
        Hub.State.Changed -= OnChanged; // navigating to the page already shown must not subscribe twice
        Hub.State.Changed += OnChanged;
        if (!string.IsNullOrEmpty(arg)) _symbol = arg.ToUpperInvariant();
        Refresh();
        _ = LoadChartAsync();
    }

    public void OnNavigatedFrom()
    {
        Hub.State.Changed -= OnChanged;
        _refresh.Stop();
    }

    private void OnChanged(string topic)
    {
        if (topic is Topics.Markets or Topics.MarketBrief) _refresh.Request();
    }

    private static IEnumerable<WatchSymbol> All(MarketSettings m) => m.Indices.Concat(m.Macro).Concat(m.Watchlist);

    private void Refresh()
    {
        var s = Hub.S.Markets;
        var quotes = Hub.State.Quotes;
        var ind = Hub.State.Indicators;
        var brief = Hub.State.MarketBrief;
        WatchInsight? Insight(string sym) => brief?.Insights.FirstOrDefault(i => i.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase));

        _vm.Tiles = s.Indices.Concat(s.Macro).Where(w => quotes.ContainsKey(w.Symbol)).Take(9)
            .Select(w => new QuoteVM(quotes[w.Symbol], ind.GetValueOrDefault(w.Symbol))).ToList();
        var bar = MarketBar.Items(s);
        _vm.Editing = _editing;
        var barItems = bar.Select((w, i) => new BarItemVM(w.Symbol, string.IsNullOrWhiteSpace(w.Name) ? w.Symbol : w.Name,
            quotes.TryGetValue(w.Symbol, out var bq) ? Fmt.Price(bq.Price, bq.Currency) : "—", i > 0, i < bar.Count - 1)).ToList();
        // A new price alone doesn't rebuild the editor, which would take the keyboard off the button in use.
        if (!barItems.Select(b => (b.Symbol, b.Name, b.CanMoveLeft, b.CanMoveRight)).SequenceEqual(_vm.BarItems.Select(b => (b.Symbol, b.Name, b.CanMoveLeft, b.CanMoveRight))))
            _vm.BarItems = barItems;
        _vm.Watchlist = s.Watchlist.Where(w => quotes.ContainsKey(w.Symbol))
            .Select(w => new QuoteVM(quotes[w.Symbol], ind.GetValueOrDefault(w.Symbol), Insight(w.Symbol), w.Shares)).ToList();

        if (string.IsNullOrEmpty(_symbol) || !quotes.ContainsKey(_symbol))
            _symbol = s.Watchlist.FirstOrDefault(w => quotes.ContainsKey(w.Symbol))?.Symbol ?? quotes.Keys.FirstOrDefault() ?? "";

        // Name the live indices you follow ("FTSE 100, ISEQ") rather than Yahoo's exchange codes ("FTSE Index, Irish").
        var live = s.Indices.Select(w => quotes.GetValueOrDefault(w.Symbol)).OfType<Quote>().Where(MarketSession.IsLive)
            .Select(q => string.IsNullOrWhiteSpace(q.Name) ? q.Symbol : q.Name).Distinct().ToList();
        var session = MarketSession.Summary(quotes.Values, DateTimeOffset.Now);
        _vm.Subtitle = quotes.Count == 0 ? (Hub.State.Online ? "Fetching quotes…" : "Offline — quotes will load when you're back online")
            : live.Count > 0 ? $"Live now: {string.Join(", ", live.Take(3))}  ·  quotes may be delayed ~15 min"
            : session.Length > 0 ? session
            : "Crypto and currencies trade around the clock";

        var portfolio = Portfolio.Holdings(s).Any() ? Portfolio.Value(s, quotes, Hub.State.Fx) : null;
        ShowPortfolio(portfolio);

        if (quotes.TryGetValue(_symbol, out var q))
        {
            var w = All(s).FirstOrDefault(x => x.Symbol == _symbol);
            var i = ind.GetValueOrDefault(_symbol);
            var insight = Insight(_symbol);
            _vm.Detail = new QuoteVM(q, i, insight, w?.Shares);
            _vm.DetailExchange = string.IsNullOrEmpty(q.Exchange) ? q.Currency : $"{q.Exchange} · {q.Currency}";
            var links = SymbolLinks.For(q.Symbol, q.Exchange, q.Kind);
            if (!links.SequenceEqual(_vm.Links)) _vm.Links = links.ToList();
            _vm.HoldingText = w?.Shares is > 0 ? HoldingText(w.Shares.Value, q, portfolio?.Lines.FirstOrDefault(l => l.Symbol == _symbol), portfolio?.Currency ?? "") : null;
            _vm.ChartBrush = Fmt.UpDown(q.ChangePercent);
            _vm.Stats = BuildStats(q, i);
            _vm.Factors = i?.Factors ?? new List<string> { "Building price history…" };
            _vm.InsightSummary = insight?.Summary ?? "";
            _vm.InsightConfidence = insight is null ? "" : $"{insight.Confidence * 100:0}%";
            _vm.InsightConfidenceValue = insight?.Confidence ?? 0;
            _vm.InsightRationale = insight?.Rationale ?? new();
            _vm.InsightRisks = insight?.Risks ?? new();
        }

        _vm.SessionText = MarketSession.Summary(Hub.State.Quotes.Values, DateTimeOffset.Now);
        if (brief is not null)
        {
            _vm.BriefOverview = brief.Overview;
            _vm.BriefBadge = brief.IsAi ? "AI" : "";
            _vm.BriefMeta = (brief.IsAi ? brief.Model + " · " : "Indicator view · ") + Core.Util.TimeText.Stamp(brief.GeneratedAt, Hub.S.General.Use24Hour);
            _vm.Highlights = brief.Highlights;
            _vm.Ideas = brief.Ideas.Select(idea => new IdeaVM(idea.Title, idea.Thesis,
                CultureInfo.CurrentCulture.TextInfo.ToTitleCase(idea.Kind),
                CultureInfo.CurrentCulture.TextInfo.ToTitleCase(idea.Risk) + " risk",
                Fmt.Res(idea.Risk switch { "low" => "B.Up", "high" => "B.Down", _ => "B.Warn" }),
                Fmt.Res(idea.Risk switch { "low" => "B.UpSoft", "high" => "B.DownSoft", _ => "B.WarnSoft" }),
                idea.Horizon, idea.Symbols.Count > 0 ? string.Join("  ·  ", idea.Symbols) : "")).ToList();
        }
        _vm.Changed();
    }

    /// <summary>"You own 2.5 · $577.15 (€513.89) · +105.6% since you bought": the gain is in your currency, like the cost.</summary>
    private static string HoldingText(double shares, Quote q, PortfolioLine? line, string currency)
    {
        var text = $"You own {shares.ToString("0.####", CultureInfo.CurrentCulture)} · {Fmt.PriceExact(q.Price * shares, q.Currency)}";
        if (line is not null && !string.Equals(FxRates.Major(q.Currency).Currency, currency, StringComparison.OrdinalIgnoreCase))
            text += $" ({Fmt.PriceExact(line.Value, currency)})";
        if (line?.GainPercent is { } gain) text += $" · {Fmt.Pct(gain, 1)} since you bought";
        return text;
    }

    // ───────────── Portfolio ─────────────
    private void ShowPortfolio(PortfolioSummary? p)
    {
        _vm.HasPortfolio = p is not null;
        if (p is null) return;
        var cur = p.Currency;
        _vm.PortfolioValue = p.Lines.Count > 0 ? Fmt.PriceExact(p.Value, cur) : "—";
        _vm.PortfolioDay = $"{SignedMoney(p.DayChange, cur)} ({Fmt.Pct(p.DayChangePercent)})";
        _vm.PortfolioDayBrush = Fmt.UpDown(p.DayChange);
        _vm.PortfolioGainSeparator = p.Gain is null ? "" : "  ·  ";
        _vm.PortfolioGain = p.Gain is { } gain ? $"{SignedMoney(gain, cur)} ({Fmt.Pct(p.GainPercent ?? 0)})" : "";
        _vm.PortfolioGainLabel = p.Gain is null ? "" : " since you bought";
        _vm.PortfolioGainBrush = Fmt.UpDown(p.Gain ?? 0);
        _vm.PortfolioLines = p.Lines.Select(l => new PortfolioLineVM(l.Symbol,
            $"{l.Shares.ToString("0.####", CultureInfo.CurrentCulture)} · {l.Name}",
            l.Weight, $"{l.Weight * 100:0}%", Fmt.PriceExact(l.Value, cur),
            l.GainPercent is { } g ? Fmt.Pct(g, 1) : "", Fmt.UpDown(l.Gain ?? 0))).ToList();
        _vm.PortfolioNote = p.Unpriced.Count == 0 ? ""
            : $"Not counted until a price and {cur} exchange rate arrive: {string.Join(", ", p.Unpriced)}.";
        _ = LoadPortfolioChartAsync();
    }

    private static string SignedMoney(double v, string currency) => (v >= 0 ? "+" : "−") + Fmt.PriceExact(Math.Abs(v), currency);

    /// <summary>Today's holdings valued on each day of the chosen range, worked out from the stored daily closes.</summary>
    private async Task LoadPortfolioChartAsync()
    {
        var s = Hub.S.Markets;
        var holdings = Portfolio.Holdings(s).ToList();
        if (holdings.Count == 0) return;
        var days = _portfolioRange switch { "6mo" => 183, "1y" => 366, _ => 31 };
        var fx = Hub.State.Fx;
        var quotes = Hub.State.Quotes;
        // Worked out again when the range, holdings or rates change, and once an hour for new closes.
        var key = string.Join("|", _portfolioRange, s.BaseCurrency, DateTime.UtcNow.ToString("yyyyMMddHH", CultureInfo.InvariantCulture), fx.Rates.Count,
            string.Join(",", holdings.Select(h => $"{h.Symbol}:{h.Shares}:{quotes.GetValueOrDefault(h.Symbol)?.Currency}")));
        if (key == _portfolioKey) return;
        _portfolioKey = key;
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-days));
        List<PortfolioPoint> points;
        try
        {
            points = await Task.Run(() => Portfolio.History(s, quotes, fx, symbol => Hub.Core.Db.GetCloses(symbol, 400), from, out _));
        }
        catch (Exception ex)
        {
            Core.Util.Log.Warn("markets", "Portfolio history failed", ex);
            _portfolioKey = "";
            return;
        }
        if (key != _portfolioKey) return;
        if (points.Count < 2)
        {
            PortfolioChart.Values = null;
            PortfolioChartStatus.Text = "The chart fills in once a few days of prices are stored.";
            return;
        }
        PortfolioChartStatus.Text = "";
        PortfolioChart.TimeFormat = days > 40 ? "MMM yy" : "d MMM";
        PortfolioChart.Decimals = 2;
        PortfolioChart.Currency = s.BaseCurrency;
        PortfolioChart.Baseline = double.NaN;
        PortfolioChart.Times = points.Select(p => new DateTimeOffset(p.Day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero).ToUnixTimeSeconds()).ToList();
        PortfolioChart.Values = points.Select(p => p.Value).ToList();
        PortfolioChart.Stroke = Fmt.UpDown(points[^1].Value - points[0].Value);
    }

    private void OnEditHoldings(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings", "markets:watchlist");

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) AquaHub.Platform.AppLauncher.OpenUrl(url);
    }

    // ───────────── The chart row ─────────────
    private bool _editing;

    private void OnEditBar(object sender, RoutedEventArgs e)
    {
        _editing = !_editing;
        EditBarText.Text = _editing ? "Done" : "Edit charts";
        System.Windows.Automation.AutomationProperties.SetName(EditBarButton, EditBarText.Text);
        EditBarIcon.Kind = _editing ? "check" : "edit";
        UiProps.SetPlaceholder(AddBox, _editing ? "Add an index, currency or coin…" : "Add a company or ticker…");
        AddBox.ToolTip = _editing ? "Search for a chart to add to the row" : "Search for a company or ticker to add it to your watchlist";
        Refresh();
    }

    private void OnMoveChartLeft(object sender, RoutedEventArgs e) => MoveChart(sender, -1);
    private void OnMoveChartRight(object sender, RoutedEventArgs e) => MoveChart(sender, +1);

    private void MoveChart(object sender, int step)
    {
        if (sender is not FrameworkElement { Tag: string symbol }) return;
        Hub.Core.Settings.Update(s => MarketBar.Move(s.Markets, symbol, step));
        Refresh();
        // Keep the keyboard on the chart that moved.
        Dispatcher.BeginInvoke(() => FocusBarButton(symbol, step < 0 ? 0 : 1), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnRemoveChart(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string symbol }) return;
        Hub.Core.Settings.Update(s => MarketBar.Remove(s.Markets, symbol));
        Refresh();
    }

    /// <summary>Focuses a chart's move-left (0) or move-right (1) button, or the nearest one that's enabled.</summary>
    private void FocusBarButton(string symbol, int which)
    {
        var buttons = FindAll<Button>(this).Where(b => b.Tag is string t && t == symbol && b.IsVisible).ToList();
        (buttons.ElementAtOrDefault(which) is { IsEnabled: true } b ? b : buttons.FirstOrDefault(x => x.IsEnabled))?.Focus();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }

    private static List<StatVM> BuildStats(Quote q, Indicators? i)
    {
        var text = Fmt.Res("B.Text");
        Brush Signed(double? v) => v is null ? text : Fmt.UpDown(v.Value);
        string Pct(double? v) => v is null ? "—" : Fmt.Pct(v.Value, 1);
        var list = new List<StatVM>
        {
            new("Day range", q.DayLow > 0 ? $"{Fmt.Price(q.DayLow)} – {Fmt.Price(q.DayHigh)}" : "—", text),
            new("52-week range", q.Low52 > 0 ? $"{Fmt.Price(q.Low52)} – {Fmt.Price(q.High52)}" : "—", text),
            new("1 month", Pct(i?.Ret1M), Signed(i?.Ret1M)),
            new("Year to date", Pct(i?.RetYtd), Signed(i?.RetYtd)),
            new("RSI (14)", i?.Rsi14 is { } r ? r.ToString("0", CultureInfo.CurrentCulture) : "—", i?.Rsi14 is > 70 ? Fmt.Res("B.Warn") : i?.Rsi14 is < 30 ? Fmt.Res("B.Info") : text),
            new("vs 50-day avg", i?.Sma50 is { } s50 && s50 > 0 ? Fmt.Pct((q.Price / s50 - 1) * 100, 1) : "—", i?.Sma50 is { } a ? Fmt.UpDown(q.Price - a) : text),
            new("vs 200-day avg", i?.Sma200 is { } s200 && s200 > 0 ? Fmt.Pct((q.Price / s200 - 1) * 100, 1) : "—", i?.Sma200 is { } b ? Fmt.UpDown(q.Price - b) : text),
            new("Volatility (ann.)", i?.Volatility20 is { } v ? $"{v:0}%" : "—", text),
        };
        return list;
    }

    private async Task LoadChartAsync()
    {
        if (string.IsNullOrEmpty(_symbol)) return;
        var def = RangeDefs.First(r => r.Id == _range);
        var key = _symbol + "|" + _range;
        ChartStatus.Text = "";
        if (!_charts.TryGetValue(key, out var cached) || DateTime.UtcNow - cached.At > TimeSpan.FromMinutes(5))
        {
            ChartStatus.Text = "Loading chart…";
            var data = await YahooFinance.GetChartAsync(Hub.Core.Http, _symbol, _range, def.Interval, CancellationToken.None);
            if (data is null)
            {
                ChartStatus.Text = "Chart unavailable right now";
                Chart.Values = null;
                return;
            }
            cached = (DateTime.UtcNow, data);
            _charts[key] = cached;
        }
        ChartStatus.Text = "";
        var d = cached.Data;
        Chart.TimeFormat = def.Format;
        Chart.Decimals = d.Meta.Price >= 1000 ? 0 : d.Meta.Price >= 1 ? 2 : 4;
        // Indices are points, not money; everything else hovers with its currency.
        Chart.Currency = d.Meta.InstrumentType.Equals("INDEX", StringComparison.OrdinalIgnoreCase) || _symbol.StartsWith('^') ? "" : d.Meta.Currency;
        Chart.Times = d.Times;
        Chart.Values = d.Closes;
        Chart.Baseline = _range == "1d" ? d.Meta.PreviousClose : double.NaN;
        var first = d.Closes.FirstOrDefault(v => !double.IsNaN(v));
        var last = d.Closes.LastOrDefault(v => !double.IsNaN(v));
        Chart.Stroke = Fmt.UpDown(_range == "1d" ? last - d.Meta.PreviousClose : last - first);
    }

    private void OnSelectRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sym })
        {
            _symbol = sym;
            Refresh();
            _ = LoadChartAsync();
        }
    }

    private void OnRemoveSymbol(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sym })
        {
            Hub.Core.Settings.Update(s => s.Markets.Watchlist.RemoveAll(w => w.Symbol == sym));
            Refresh();
        }
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var q = AddBox.Text.Trim();
        if (q.Length < 2) { SearchPopup.IsOpen = false; return; }
        try
        {
            await Task.Delay(350, cts.Token);
            var results = await YahooFinance.SearchAsync(Hub.Core.Http, q, cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchResults.ItemsSource = results;
            SearchPopup.IsOpen = results.Count > 0;
        }
        catch (OperationCanceledException) { }
    }

    private void OnPickSymbol(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is not SymbolMatch m) return;
        var kind = m.Type.ToLowerInvariant() switch { "etf" => "etf", "index" => "index", "cryptocurrency" => "crypto", "currency" => "fx", "futures" => "commodity", _ => "equity" };
        var picked = new WatchSymbol { Symbol = m.Symbol, Name = m.Name.Length > 28 ? m.Name[..28] : m.Name, Kind = kind };
        Hub.Core.Settings.Update(s =>
        {
            if (_editing) MarketBar.Add(s.Markets, picked);
            else if (!s.Markets.Watchlist.Any(w => w.Symbol.Equals(m.Symbol, StringComparison.OrdinalIgnoreCase)))
                s.Markets.Watchlist.Add(picked);
        });
        SearchPopup.IsOpen = false;
        AddBox.Text = "";
        _symbol = m.Symbol;
        Hub.Core.Agents.RunNow("market-watch");
        _ = LoadChartAsync();
    }
}
