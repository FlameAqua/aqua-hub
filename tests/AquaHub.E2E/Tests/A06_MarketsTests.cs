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
            .Where(n => n.Type == ControlType.Button && n.Name.Length > 0 && n.Parent?.Type == ControlType.DataItem && !n.AutomationId.StartsWith("watch-", StringComparison.Ordinal))
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
