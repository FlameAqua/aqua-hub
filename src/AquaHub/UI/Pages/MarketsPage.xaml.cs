using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public sealed record StatVM(string Label, string Value, Brush Brush);

public sealed record IdeaVM(string Title, string Thesis, string KindLabel, string RiskLabel, Brush RiskBrush, Brush RiskSoft, string Horizon, string SymbolsText);

public sealed class MarketsVM : ObservableObject
{
    public string Subtitle { get; set; } = "";
    public List<QuoteVM> Tiles { get; set; } = new();
    public List<QuoteVM> Watchlist { get; set; } = new();
    public QuoteVM? Detail { get; set; }
    public string DetailExchange { get; set; } = "";
    public string? HoldingText { get; set; }
    public string PortfolioText { get; set; } = "";
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

        var holdings = s.Watchlist.Where(w => w.Shares is > 0 && quotes.ContainsKey(w.Symbol)).ToList();
        if (holdings.Count > 0)
        {
            var value = holdings.Sum(h => quotes[h.Symbol].Price * h.Shares!.Value);
            var day = holdings.Sum(h => quotes[h.Symbol].Change * h.Shares!.Value);
            _vm.PortfolioText = $"Holdings {Fmt.Price(value)}  ({Fmt.Signed(day)} today)";
        }
        else _vm.PortfolioText = "";

        if (quotes.TryGetValue(_symbol, out var q))
        {
            var w = All(s).FirstOrDefault(x => x.Symbol == _symbol);
            var i = ind.GetValueOrDefault(_symbol);
            var insight = Insight(_symbol);
            _vm.Detail = new QuoteVM(q, i, insight, w?.Shares);
            _vm.DetailExchange = string.IsNullOrEmpty(q.Exchange) ? q.Currency : $"{q.Exchange} · {q.Currency}";
            _vm.HoldingText = w?.Shares is > 0 ? $"Your position: {w.Shares:0.##} × {Fmt.Price(q.Price, q.Currency)} = {Fmt.Price(q.Price * w.Shares.Value, q.Currency)}" +
                (w.CostBasis is > 0 ? $"  ·  P/L {Fmt.Pct((q.Price / w.CostBasis.Value - 1) * 100)}" : "") : null;
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
        Hub.Core.Settings.Update(s =>
        {
            if (!s.Markets.Watchlist.Any(w => w.Symbol.Equals(m.Symbol, StringComparison.OrdinalIgnoreCase)))
                s.Markets.Watchlist.Add(new WatchSymbol { Symbol = m.Symbol, Name = m.Name.Length > 28 ? m.Name[..28] : m.Name, Kind = kind });
        });
        SearchPopup.IsOpen = false;
        AddBox.Text = "";
        _symbol = m.Symbol;
        Hub.Core.Agents.RunNow("market-watch");
        _ = LoadChartAsync();
    }
}
