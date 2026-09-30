namespace AquaHub.E2E.Tests;

/// <summary>Launchpad: media controls and volume, music search, scene Run buttons, app tiles, Add app picker.</summary>
public sealed class A08_LaunchpadTests : E2ETestBase
{
    public A08_LaunchpadTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private AutomationElement Launchpad() => GoTo("launchpad");

    [Fact]
    public void T01_MediaControlsAndVolume() => Run(() =>
    {
        Launchpad();
        foreach (var (name, detail) in new[] { ("Previous", "previous"), ("Play / pause", ""), ("Next", "next") })
            Check("Launchpad media", $"{name} → journal media", () =>
                ExpectJournalAny(MediaActions, () => Ui.Invoke(Button(PageRoot("launchpad"), name)), e => detail.Length == 0 || e.Detail.Contains(detail)));
        Check("Launchpad media", "Mute → journal mute", () => ExpectJournal("mute", () => Ui.Invoke(Button(PageRoot("launchpad"), "Mute"))));
        Check("Launchpad media", "volume slider → journal volume 64", () =>
        {
            var slider = Ui.WaitFind(PageRoot("launchpad"), Ui.Type(ControlType.Slider), "volume slider");
            ExpectJournal("volume", () => Ui.SetRange(slider, 64), d => d == "64");
            Wait.For(() => Ui.HasText(PageRoot("launchpad"), "64%"), "volume label shows 64%");
        });
    });

    [Fact]
    public void T02_MusicSearchBoxAndButton() => Run(() =>
    {
        Launchpad();
        Check("Launchpad music", "query + Enter → journal music-search", () =>
        {
            var box = Ui.WaitFind(PageRoot("launchpad"), Ui.Id("MusicQuery"), "music query box");
            Ui.SetValue(box, "lofi beats");
            ExpectJournal("music-search", () => FocusAndPress(Main, box, VK.Enter), d => d == "lofi beats");
            Wait.For(() => Ui.ValueOf(box) == "", "query box cleared");
        });
        Check("Launchpad music", "query + search button → journal music-search", () =>
        {
            var box = Ui.WaitFind(PageRoot("launchpad"), Ui.Id("MusicQuery"), "music query box");
            Ui.SetValue(box, "miles davis");
            ExpectJournal("music-search", () => Ui.Invoke(Button(PageRoot("launchpad"), "Search in your music app")), d => d == "miles davis");
        });
        Check("Launchpad music", "empty query does nothing", () =>
        {
            Ui.SetValue(Ui.WaitFind(PageRoot("launchpad"), Ui.Id("MusicQuery"), "box"), "");
            ExpectNoJournal("music-search", () => Ui.Invoke(Button(PageRoot("launchpad"), "Search in your music app")), TimeSpan.FromSeconds(1));
        });
    });

    [Fact]
    public void T03_SceneRunButtons() => Run(() =>
    {
        Launchpad();
        Check("Launchpad scenes", "Run Focus → volume 25 + launch vscode", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Button(PageRoot("launchpad"), "Run Focus"));
            App.Journal.WaitFor(mark, "volume", d => d == "25");
            App.Journal.WaitFor(mark, "launch", d => d == "vscode");
        });
        Check("Launchpad scenes", "Run Music time → launch spotify + volume 45", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Button(PageRoot("launchpad"), "Run Music time"));
            App.Journal.WaitFor(mark, "launch", d => d == "spotify");
            App.Journal.WaitFor(mark, "volume", d => d == "45");
        });
        Check("Launchpad scenes", "Run Game mode → unload-model + launch steam/discord", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Button(PageRoot("launchpad"), "Run Game mode"));
            App.Journal.WaitFor(mark, "unload-model", null, TimeSpan.FromSeconds(20));
            App.Journal.WaitFor(mark, "launch", d => d == "discord", TimeSpan.FromSeconds(20));
        });
        Check("Launchpad scenes", "Run Wind down → media pause + DND off + Today", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Button(PageRoot("launchpad"), "Run Wind down"));
            App.Journal.WaitForAny(mark, MediaActions);
            App.Settings.WaitForBool("notifications.doNotDisturb", false, TimeSpan.FromSeconds(20));
            ExpectPage("today", TimeSpan.FromSeconds(20));
        });
    });

    [Fact]
    public void T04_AppTilesLaunchAndCloseFromMenu() => Run(() =>
    {
        var grid = Ui.WaitFind(Launchpad(), Ui.Id("AppGrid"), "app grid");
        var tiles = Ui.FindAllWhere(grid, ControlType.Button, n => n.StartsWith("Open ", StringComparison.Ordinal)).Select(Ui.NameOf).ToList();
        Check("Launchpad apps", "all configured apps shown", () => Expect(tiles.Count == App.Settings.Count("apps"), $"{tiles.Count} tiles vs {App.Settings.Count("apps")} apps"));
        var ids = (App.Settings.Get("apps") as System.Text.Json.Nodes.JsonArray)!.OfType<System.Text.Json.Nodes.JsonObject>()
            .ToDictionary(a => (string?)a["name"] ?? "", a => (string?)a["id"] ?? "");
        foreach (var tile in tiles)
        {
            var id = ids.GetValueOrDefault(tile["Open ".Length..], "");
            Check("Launchpad apps", $"'{tile}' → journal launch {id}", () =>
                ExpectJournal("launch", () => Ui.Invoke(Ui.WaitFind(Ui.WaitFind(PageRoot("launchpad"), Ui.Id("AppGrid"), "grid"), Ui.Button(tile), tile)), d => d == id));
        }
        var last = tiles.Last();
        var lastId = ids.GetValueOrDefault(last["Open ".Length..], "");
        Check("Launchpad apps", $"context menu 'Close app' on {last} → journal close", () =>
        {
            var tile = Ui.WaitFind(Ui.WaitFind(PageRoot("launchpad"), Ui.Id("AppGrid"), "grid"), Ui.Button(last), last);
            ExpectJournal("close", () => Ui.Invoke(MenuItem(OpenContextMenu(Main, tile), "Close app")), d => d == lastId);
        });
    });

    [Fact]
    public void T05_AddAppPicker() => Run(() =>
    {
        var before = App.Settings.Count("apps");
        string? chosen = null;
        Check("Launchpad add app", "'Add app' opens the picker with installed apps", () =>
        {
            App.EnsureForeground(Main);
            Ui.Invoke(Ui.WaitButtonWithText(Launchpad(), "Add app"));
            var list = Ui.WaitFind(PageRoot("launchpad"), Ui.Id("PickerList"), "picker list");
            Wait.For(() => Ui.FindAll(list, Ui.Type(ControlType.ListItem)).Count > 0, "catalog items", TimeSpan.FromSeconds(30));
        });
        Check("Launchpad add app", "search filters the catalog", () =>
        {
            var search = Ui.WaitFind(PageRoot("launchpad"), Ui.Id("PickerSearch"), "picker search");
            foreach (var q in new[] { "Notepad", "Paint", "Snipping", "Clock", "Photos", "Camera" })
            {
                Ui.SetValue(search, q);
                Thread.Sleep(400);
                var items = Ui.FindAll(Ui.WaitFind(PageRoot("launchpad"), Ui.Id("PickerList"), "list"), Ui.Type(ControlType.ListItem));
                var match = items.Select(i => Ui.Texts(i).FirstOrDefault() ?? "").FirstOrDefault(t => t.Contains(q, StringComparison.OrdinalIgnoreCase));
                if (match is not null) { chosen = match; break; }
            }
            Expect(chosen is not null, "none of the probe apps is installed / available in the picker");
        });
        if (chosen is null) return;
        AutomationElement Item() => Wait.For(() => Ui.FindAll(Ui.WaitFind(PageRoot("launchpad"), Ui.Id("PickerList"), "list"), Ui.Type(ControlType.ListItem))
            .FirstOrDefault(i => Ui.Texts(i).FirstOrDefault() == chosen), $"picker item {chosen}");
        Check("Launchpad add app", "keyboard/UIA pick (select + Enter) adds the app", () =>
        {
            var item = Item();
            Ui.Select(item);
            FocusAndPress(Main, item, VK.Enter);
            var added = Wait.Until(() => App.Settings.Count("apps") > before, TimeSpan.FromSeconds(3));
            Expect(added, "Selecting an app in the picker with the keyboard (or UIA SelectionItem) and pressing Enter does not add it — only a mouse click works");
        });
        Check("Launchpad add app", $"mouse pick adds '{chosen}' to settings.json apps", () =>
        {
            if (App.Settings.Count("apps") == before)
            {
                App.EnsureForeground(Main);
                Input.ClickElement(Pid, Item());
            }
            Wait.For(() => App.Settings.Count("apps") == before + 1, "apps count +1", E2EConfig.PersistTimeout);
            Expect(App.Settings.Get("apps")!.AsArray().Any(a => (string?)a?["name"] == chosen), $"{chosen} not in apps");
            Ui.WaitFind(Ui.WaitFind(PageRoot("launchpad"), Ui.Id("AppGrid"), "grid"), Ui.Button("Open " + chosen), "new tile");
        });
        Check("Launchpad add app", "picker Close button hides the picker", () =>
        {
            if (Ui.Find(PageRoot("launchpad"), Ui.Id("PickerSearch")) is null) Ui.Invoke(Ui.WaitButtonWithText(PageRoot("launchpad"), "Add app"));
            Ui.Invoke(Ui.WaitFind(PageRoot("launchpad"), Ui.Button("Close"), "picker close button"));
            Wait.For(() => Ui.Find(PageRoot("launchpad"), Ui.Id("PickerSearch")) is null, "picker hidden");
        });
    });
}
