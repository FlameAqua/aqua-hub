using System.Text.Json.Nodes;

namespace AquaHub.E2E.Tests;

/// <summary>Markets: index tiles, chart ranges, watchlist rows, add-to-watchlist search, remove via context menu.</summary>
public sealed class A06_MarketsTests : E2ETestBase
{
    public A06_MarketsTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private AutomationElement Markets() => GoTo("markets");

    private static readonly System.Text.RegularExpressions.Regex HeaderRx = new(@"^([A-Za-z0-9\^\.\-=]{1,15})  ·  ");

    /// <summary>Symbol shown in the detail header ("NVDA  ·  NasdaqGS · USD").</summary>
    private string HeaderSymbol()
    {
        foreach (var t in Ui.Texts(PageRoot("markets")))
            if (HeaderRx.Match(t) is { Success: true } m) return m.Groups[1].Value;
        return "";
    }

    private Dictionary<string, string> SymbolsByName()
    {
        var map = new Dictionary<string, string>();
        foreach (var list in new[] { "markets.indices", "markets.macro", "markets.watchlist" })
            foreach (var w in (App.Settings.Get(list) as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                map[(string?)w["name"] ?? ""] = (string?)w["symbol"] ?? "";
        return map;
    }

    private List<string> TileNames()
    {
        var flat = TreeSnapshot.Capture(Markets()).Descendants().ToList();
        var range = flat.FindIndex(n => n.Type == ControlType.RadioButton && n.Name == "1D");
        return flat.Take(range < 0 ? flat.Count : range)
            .Where(n => n.Type == ControlType.Button && n.Name.Length > 0 && n.Parent?.Type == ControlType.DataItem
                        && !n.AutomationId.StartsWith("watch-", StringComparison.Ordinal) && !n.AutomationId.StartsWith("link-", StringComparison.Ordinal))
            .Select(n => n.Name).ToList();
    }

    [Fact]
    public void T01_IndexTilesSelectTheDetail() => Run(() =>
    {
        var map = SymbolsByName();
        var tiles = TileNames();
        Check("Markets", "index / macro tiles present", () => Expect(tiles.Count >= 3, $"only {tiles.Count} tiles"));
        foreach (var tile in tiles)
        {
            Check("Markets tiles", $"tile '{tile}' → detail header", () =>
            {
                var before = HeaderSymbol();
                Ui.Invoke(Ui.WaitFind(PageRoot("markets"), Ui.Button(tile), tile));
                if (map.TryGetValue(tile, out var symbol))
                    Wait.For(() => HeaderSymbol() == symbol, $"header shows {symbol} (now {HeaderSymbol()})");
                else
                    Wait.For(() => HeaderSymbol() != before, "header changed");
            });
        }
    });

    [Fact]
    public void T02_ChartRangeChips() => Run(() =>
    {
        Markets();
        foreach (var range in new[] { "1D", "5D", "1M", "6M", "1Y", "1D" })
        {
            Check("Markets chart", $"range {range} loads a chart", () =>
            {
                var chip = Ui.WaitFind(PageRoot("markets"), Ui.And(Ui.Type(ControlType.RadioButton), Ui.Name(range)), range);
                Ui.Select(chip);
                Wait.For(() => Ui.IsSelected(chip), "chip selected");
                var status = Ui.WaitFind(PageRoot("markets"), Ui.Id("ChartStatus"), "chart status");
                Wait.For(() => Ui.NameOf(status) != "Loading chart…", "chart loaded", TimeSpan.FromSeconds(25));
                Expect(!Ui.NameOf(status).Contains("unavailable", StringComparison.OrdinalIgnoreCase), $"chart status: {Ui.NameOf(status)}");
            });
        }
    });

    [Fact]
    public void T03_WatchlistRowsUpdateTheHeader() => Run(() =>
    {
        var rows = Ui.FindAll(Markets(), Ui.Type(ControlType.Button)).Select(Ui.IdOf).Where(id => id.StartsWith("watch-", StringComparison.Ordinal)).ToList();
        Check("Markets", "watchlist rows (watch-SYMBOL) present", () => Expect(rows.Count >= 3, $"only {rows.Count} watchlist rows"));
        foreach (var id in rows)
        {
            var symbol = id["watch-".Length..];
            Check("Markets watchlist", $"row {symbol} → header", () =>
            {
                Ui.Invoke(Ui.WaitFind(PageRoot("markets"), Ui.Id(id), id));
                Wait.For(() => HeaderSymbol() == symbol, $"header shows {symbol} (now {HeaderSymbol()})");
            });
        }
    });

    private AutomationElement? ResultWith(string symbol) =>
        Ui.FindAll(Ui.Find(Main, Ui.Id("SearchResults")) ?? Main, Ui.Type(ControlType.ListItem))
          .FirstOrDefault(li => Ui.Texts(li).Any(t => t.StartsWith(symbol + " · ", StringComparison.Ordinal)));

    [Fact]
    public void T04_AddToWatchlistFromSearch() => Run(() =>
    {
        Markets();
        App.EnsureForeground(Main);
        var box = Ui.WaitFind(PageRoot("markets"), Ui.Id("AddBox"), "add-to-watchlist box");
        Check("Markets search", "typing 'tesla' shows results incl. TSLA", () =>
        {
            Ui.SetValue(box, "tesla");
            Wait.For(() => ResultWith("TSLA"), "TSLA search result", TimeSpan.FromSeconds(20));
        });
        Check("Markets search", "keyboard/UIA pick (select + Enter) adds the symbol", () =>
        {
            var item = Wait.For(() => ResultWith("TSLA"), "TSLA result");
            Ui.Select(item);
            FocusAndPress(Main, item, VK.Enter);
            var added = Wait.Until(() => App.Settings.Symbols("markets.watchlist").Contains("TSLA"), TimeSpan.FromSeconds(3));
            Expect(added, "Selecting a search result with the keyboard (or UIA SelectionItem) and pressing Enter does not add it — only a mouse click works");
        });
        Check("Markets search", "mouse pick adds TSLA to settings.json", () =>
        {
            if (App.Settings.Symbols("markets.watchlist").Contains("TSLA")) return;
            if (ResultWith("TSLA") is null)
            {
                App.EnsureForeground(Main);
                Ui.SetValue(box, "");
                Ui.SetValue(box, "tesla");
            }
            var item = Wait.For(() => ResultWith("TSLA"), "TSLA result", TimeSpan.FromSeconds(20));
            App.EnsureForeground(Main);
            Input.ClickElement(Pid, item);
            Wait.For(() => App.Settings.Symbols("markets.watchlist").Contains("TSLA"), "TSLA in markets.watchlist", E2EConfig.PersistTimeout);
        });
        Check("Markets search", "TSLA row appears and becomes the detail", () =>
        {
            Ui.WaitFind(Main, Ui.Id("watch-TSLA"), "watch-TSLA row", TimeSpan.FromSeconds(40));
            Wait.For(() => HeaderSymbol() == "TSLA", $"header shows TSLA (now {HeaderSymbol()})", TimeSpan.FromSeconds(20));
        });
    });

    private List<string> BarOrder() => App.Settings.Symbols("markets.indices").Concat(App.Settings.Symbols("markets.macro")).ToList();

    [Fact]
    public void T06_TheChartRowCanBeRearranged() => Run(() =>
    {
        Markets();
        var charts = new[] { "markets.indices", "markets.macro" }
            .SelectMany(list => (App.Settings.Get(list) as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Select(w => (Symbol: (string?)w["symbol"] ?? "", Name: (string?)w["name"] is { Length: > 0 } n ? n : (string?)w["symbol"] ?? ""))
            .ToList();
        Check("Markets chart row", "Edit charts shows the editor", () =>
        {
            Expect(charts.Count >= 3, $"only {charts.Count} charts configured");
            Ui.Invoke(Ui.WaitFind(PageRoot("markets"), Ui.Id("markets-edit-bar"), "Edit charts"));
            Ui.WaitFind(PageRoot("markets"), Ui.Id("markets-bar-editor"), "chart row editor");
        });
        Check("Markets chart row", "moving the second chart left and back", () =>
        {
            var (symbol, name) = charts[1];
            Ui.Invoke(Ui.WaitFind(PageRoot("markets"), Ui.Button($"Move {name} left"), "move left"));
            Wait.For(() => BarOrder().FirstOrDefault() == symbol, $"{symbol} first (now {string.Join(" ", BarOrder())})", E2EConfig.PersistTimeout);
            Ui.Invoke(Ui.WaitFind(PageRoot("markets"), Ui.Button($"Move {name} right"), "move right"));
            Wait.For(() => BarOrder().FirstOrDefault() == charts[0].Symbol, "the first two back in order", E2EConfig.PersistTimeout);
        });
        Check("Markets chart row", "removing the last chart", () =>
        {
            var (symbol, name) = charts[^1];
            Ui.Invoke(Ui.WaitFind(PageRoot("markets"), Ui.Button($"Remove {name}"), "remove"));
            Wait.For(() => !BarOrder().Contains(symbol), $"{symbol} removed", E2EConfig.PersistTimeout);
        });
        Check("Markets chart row", "while editing, search adds to the row, not the watchlist", () =>
        {
            var box = Ui.WaitFind(PageRoot("markets"), Ui.Id("AddBox"), "search box");
            Ui.SetValue(box, "nikkei 225");
            var item = Wait.For(() => ResultWith("^N225"), "^N225 result", TimeSpan.FromSeconds(20));
            Ui.Select(item);
            FocusAndPress(Main, item, VK.Enter);
            Wait.For(() => BarOrder().LastOrDefault() == "^N225", "^N225 at the end of the row", E2EConfig.PersistTimeout);
            Expect(!App.Settings.Symbols("markets.watchlist").Contains("^N225"), "^N225 was added to the watchlist as well");
        });
        Check("Markets chart row", "Done closes the editor", () =>
        {
            Ui.Invoke(Ui.WaitFind(PageRoot("markets"), Ui.Id("markets-edit-bar"), "Done"));
            Wait.For(() => Ui.Find(PageRoot("markets"), Ui.Id("markets-bar-editor")) is null, "editor hidden");
        });
    });

    [Fact]
    public void T07_LinksAndEdit() => Run(() =>
    {
        Check("Markets detail", "the Yahoo Finance link opens the symbol's page (dry run)", () =>
        {
            Markets();
            var link = Ui.WaitFind(PageRoot("markets"), Ui.Id("link-Yahoo Finance"), "Yahoo Finance link", TimeSpan.FromSeconds(30));
            ExpectJournal("open-url", () => Ui.Invoke(link), d => d.StartsWith("https://finance.yahoo.com/quote/", StringComparison.Ordinal));
        });
        Check("Markets watchlist", "Edit opens Settings at the holdings editor", () =>
        {
            Ui.Invoke(Ui.WaitFind(PageRoot("markets"), Ui.Id("markets-edit-watchlist"), "Edit watchlist"));
            ExpectPage("settings");
            Ui.WaitFind(PageRoot("settings"), Ui.Id("WatchList"), "holdings editor");
        });
    });

    [Fact]
    public void T05_RemoveFromWatchlistViaContextMenu() => Run(() =>
    {
        Markets();
        var target = App.Settings.Symbols("markets.watchlist").Contains("TSLA") ? "TSLA" : App.Settings.Symbols("markets.watchlist").Last();
        Check("Markets watchlist", $"right-click {target} → Remove from watchlist", () =>
        {
            var row = Ui.WaitFind(PageRoot("markets"), Ui.Id("watch-" + target), target, TimeSpan.FromSeconds(30));
            var menu = OpenContextMenu(Main, row);
            Ui.Invoke(MenuItem(menu, "Remove from watchlist"));
            Wait.For(() => !App.Settings.Symbols("markets.watchlist").Contains(target), $"{target} removed from settings", E2EConfig.PersistTimeout);
            Wait.For(() => Ui.Find(PageRoot("markets"), Ui.Id("watch-" + target)) is null, "row removed");
        });
    });
}
