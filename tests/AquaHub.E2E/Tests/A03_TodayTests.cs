using System.Text.Json.Nodes;

namespace AquaHub.E2E.Tests;

/// <summary>The Today dashboard: brief actions, story rows, card links, market rows, media, apps, scenes, predictions.</summary>
public sealed class A03_TodayTests : E2ETestBase
{
    public A03_TodayTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private AutomationElement Today() => GoTo("today");

    private Dictionary<string, string> AppIdsByName() =>
        (App.Settings.Get("apps") as JsonArray ?? new JsonArray()).OfType<JsonObject>()
        .ToDictionary(a => (string?)a["name"] ?? "", a => (string?)a["id"] ?? "");

    [Fact]
    public void T01_ReadAloudTogglesToStop() => Run(() =>
    {
        var page = Today();
        Check("Today", "Read aloud → journal 'speak' and label becomes Stop", () =>
        {
            var read = Ui.WaitButtonWithText(page, "Read aloud");
            ExpectJournal("speak", () => Ui.Invoke(read));
            Wait.For(() => Ui.ButtonWithText(PageRoot("today"), "Stop") is not null, "button label 'Stop' while speaking");
        });
        Check("Today", "Stop → label returns to Read aloud (no new speech)", () =>
        {
            var stop = Ui.WaitButtonWithText(PageRoot("today"), "Stop");
            var mark = App.Journal.Mark();
            Ui.Invoke(stop);
            var back = Wait.Until(() => Ui.ButtonWithText(PageRoot("today"), "Read aloud") is not null, TimeSpan.FromSeconds(4));
            Expect(App.Journal.Find(mark, "speak") is null, "Stop started a new speech instead of stopping");
            Expect(back, "After Stop the button still says 'Stop' (speech state never resets in dry-run mode)");
        });
    });

    [Fact]
    public void T02_RefreshAndRegenerateBrief() => Run(() =>
    {
        var page = Today();
        Check("Today", "Refresh button runs collectors", () =>
        {
            var mark = App.Log.Mark();
            Ui.Invoke(Ui.WaitButtonWithText(page, "Refresh"));
            App.Log.WaitForLine(mark, l => l.Contains("[agents] weather:") || l.Contains("[agents] news-scout:"), "collector run", TimeSpan.FromSeconds(60));
        });
        Check("Today", "Regenerate ('Write a fresh brief now') runs the Chief of Staff", () =>
        {
            var mark = App.Log.Mark();
            Ui.Invoke(Button(PageRoot("today"), "Write a fresh brief now"));
            App.Log.WaitForLine(mark, l => l.Contains("[agents] briefing:"), "briefing agent run", TimeSpan.FromSeconds(150));
        });
    });

    [Fact]
    public void T03_StoryRowsExpandReadAndAsk() => Run(() =>
    {
        var names = Regions.RowButtonNames(Today(), "All news", "Details");
        Step($"story rows: {names.Count}");
        Check("Today", "story rows present (top + near you)", () => Expect(names.Count >= 3, $"only {names.Count} story rows"));
        var first = true;
        foreach (var title in names)
        {
            Check("Today story", $"expand '{Short(title)}' shows key points + Read/Ask", () =>
            {
                var row = Button(PageRoot("today"), title);
                var before = Ui.Texts(row).Count;
                Ui.Invoke(row);
                Wait.For(() => Ui.ButtonWithText(Button(PageRoot("today"), title), "Read") is not null, "Read button inside the expanded row");
                Ui.WaitButtonWithText(Button(PageRoot("today"), title), "Ask Aqua");
                var after = Ui.Texts(Button(PageRoot("today"), title)).Count;
                Expect(after > before, $"expanded row shows no extra text (before {before}, after {after})");
            });
            if (first)
            {
                first = false;
                Check("Today story", "Read → journal open-url", () =>
                    ExpectJournal("open-url", () => Ui.Invoke(Ui.WaitButtonWithText(Button(PageRoot("today"), title), "Read")),
                        d => d.StartsWith("http", StringComparison.Ordinal)));
                Check("Today story", "Ask Aqua → Ask page with the question", () =>
                {
                    Ui.Invoke(Ui.WaitButtonWithText(Button(PageRoot("today"), title), "Ask Aqua"));
                    ExpectPage("ask");
                    var ask = PageRoot("ask");
                    Wait.For(() => Ui.Texts(ask).Any(t => t.StartsWith("Tell me more about:", StringComparison.Ordinal)), "the story question in the chat");
                    if (Ui.Find(ask, Ui.Id("StopButton")) is { } stop) Ui.Invoke(stop);
                    GoTo("today");
                });
            }
            else
            {
                Check("Today story", $"collapse '{Short(title)}'", () =>
                {
                    Ui.Invoke(Button(PageRoot("today"), title));
                    Wait.For(() => Ui.ButtonWithText(Button(PageRoot("today"), title), "Read") is null, "row collapsed");
                });
            }
        }
    });

    [Fact]
    public void T04_CardHeaderLinksNavigate() => Run(() =>
    {
        var links = new (string Label, bool ByText, string Page)[]
        {
            ("All news", true, "news"), ("Details", true, "markets"), ("Open social pulse", false, "social"),
            ("Open agenda", false, "upcoming"), ("All predictions", false, "upcoming"), ("Open system view", false, "system"),
            ("Manage apps & scenes", false, "launchpad"),
        };
        foreach (var (label, byText, target) in links)
        {
            Check("Today links", $"'{label}' → {target}", () =>
            {
                var page = Today();
                var link = byText ? Ui.WaitButtonWithText(page, label) : Button(page, label);
                Ui.Invoke(link);
                ExpectPage(target);
            });
        }
        GoTo("today");
    });

    [Fact]
    public void T05_MarketRowsOpenMarketsOnThatSymbol() => Run(() =>
    {
        var names = Regions.RowButtonNames(Today(), "Details", "Open social pulse");
        Check("Today markets", "index + mover rows present", () => Expect(names.Count >= 3, $"only {names.Count} market rows"));
        foreach (var name in names)
        {
            Check("Today markets", $"row '{name}' → Markets detail", () =>
            {
                var row = Button(Today(), name);
                var texts = Ui.Texts(row);
                var symbol = texts.Count > 1 ? texts[1] : "";
                Ui.Invoke(row);
                ExpectPage("markets");
                var markets = PageRoot("markets");
                Wait.For(() => Ui.Texts(markets).Any(t => t.StartsWith(symbol + "  ·", StringComparison.Ordinal)), $"detail header for {symbol}");
            });
        }
        GoTo("today");
    });

    [Fact]
    public void T06_MediaControlsMuteAndVolume() => Run(() =>
    {
        var page = Today();
        foreach (var (name, detail) in new[] { ("Previous", "previous"), ("Play / pause", ""), ("Next", "next") })
        {
            Check("Today media", $"{name} → journal media", () =>
                ExpectJournalAny(MediaActions, () => Ui.Invoke(Button(PageRoot("today"), name)), e => detail.Length == 0 || e.Detail.Contains(detail)));
        }
        Check("Today media", "Mute → journal mute", () => ExpectJournal("mute", () => Ui.Invoke(Button(PageRoot("today"), "Mute"))));
        Check("Today media", "volume slider → journal volume 37", () =>
        {
            var slider = Ui.WaitFind(PageRoot("today"), Ui.Type(ControlType.Slider), "volume slider");
            ExpectJournal("volume", () => Ui.SetRange(slider, 37), d => d == "37");
        });
        _ = page;
    });

    [Fact]
    public void T07_AppTilesAndContextMenu() => Run(() =>
    {
        var ids = AppIdsByName();
        var tiles = Ui.FindAllWhere(Today(), ControlType.Button, n => n.StartsWith("Open ", StringComparison.Ordinal) && !n.StartsWith("Open social", StringComparison.Ordinal)
                                                                        && !n.StartsWith("Open agenda", StringComparison.Ordinal) && !n.StartsWith("Open system", StringComparison.Ordinal))
            .Select(Ui.NameOf).ToList();
        Check("Today apps", "pinned app tiles present", () => Expect(tiles.Count >= 1, "no app tiles"));
        foreach (var tile in tiles)
        {
            var appName = tile["Open ".Length..];
            var id = ids.GetValueOrDefault(appName, "");
            Check("Today apps", $"'{tile}' → journal launch {id}", () =>
                ExpectJournal("launch", () => Ui.Invoke(Button(PageRoot("today"), tile)), d => id.Length == 0 || d == id));
        }
        if (tiles.Count > 0)
        {
            var tile = tiles[0];
            var id = ids.GetValueOrDefault(tile["Open ".Length..], "");
            Check("Today apps", $"context menu 'Open' on {tile} → journal launch", () =>
                ExpectJournal("launch", () => Ui.Invoke(MenuItem(OpenContextMenu(Main, Button(PageRoot("today"), tile)), "Open")), d => d == id));
            Check("Today apps", $"context menu 'Close app' on {tile} → journal close", () =>
                ExpectJournal("close", () => Ui.Invoke(MenuItem(OpenContextMenu(Main, Button(PageRoot("today"), tile)), "Close app")), d => d == id));
        }
    });

    [Fact]
    public void T08_ScenePillsRunEveryStep() => Run(() =>
    {
        Today();
        Check("Today scenes", "Run scene Focus → DND on, media pause, volume 25, launch vscode", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Button(PageRoot("today"), "Run scene Focus"));
            App.Journal.WaitForAny(mark, MediaActions);
            App.Journal.WaitFor(mark, "volume", d => d == "25");
            App.Journal.WaitFor(mark, "launch", d => d == "vscode");
            App.Settings.WaitForBool("notifications.doNotDisturb", true);
        });
        Check("Today scenes", "Run scene Music time → launch spotify, volume 45, play", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Button(PageRoot("today"), "Run scene Music time"));
            App.Journal.WaitFor(mark, "launch", d => d == "spotify");
            App.Journal.WaitFor(mark, "volume", d => d == "45");
            App.Journal.WaitForAny(mark, MediaActions, null, TimeSpan.FromSeconds(15));
        });
        Check("Today scenes", "Run scene Game mode → unload model, launch steam + discord", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Button(PageRoot("today"), "Run scene Game mode"));
            App.Journal.WaitFor(mark, "unload-model", null, TimeSpan.FromSeconds(20));
            App.Journal.WaitFor(mark, "launch", d => d == "steam", TimeSpan.FromSeconds(20));
            App.Journal.WaitFor(mark, "launch", d => d == "discord", TimeSpan.FromSeconds(20));
        });
        Check("Today scenes", "Run scene Wind down → media pause, DND off, show Today", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Button(PageRoot("today"), "Run scene Wind down"));
            App.Journal.WaitForAny(mark, MediaActions);
            App.Settings.WaitForBool("notifications.doNotDisturb", false, TimeSpan.FromSeconds(20));
            ExpectPage("today", TimeSpan.FromSeconds(20));
        });
    });

    [Fact]
    public void T09_PredictionRowsOpenMarkets() => Run(() =>
    {
        var names = Regions.RowButtonNames(Today(), "All predictions", "Previous");
        Check("Today predictions", "prediction rows present", () => Expect(names.Count >= 1, "no prediction rows"));
        foreach (var name in names)
            Check("Today predictions", $"'{Short(name)}' → journal open-url", () =>
                ExpectJournal("open-url", () => Ui.Invoke(Button(PageRoot("today"), name)), d => d.StartsWith("https://", StringComparison.Ordinal)));
    });

    [Fact]
    public void T10_PlaySomethingLinkWhenNothingPlays() => Run(() =>
    {
        var page = Today();
        var link = Ui.FindByRegex(page, ControlType.Button, "^Play something on ");
        if (link is null)
        {
            Finding("coverage", "'Play something on …' link not testable", "A media session is active on this PC, so the Now playing card shows the track instead of the link.");
            Results.RecordInteraction(TestName, "Today media", "'Play something on …' link (skipped: media playing)", true, "skipped");
            return;
        }
        Check("Today media", "'Play something on …' → play (journal)", () =>
            ExpectJournalAny(new[] { "media", "media-key", "launch" }, () => Ui.Invoke(link)));
    });

    private static string Short(string s) => s.Length > 48 ? s[..48] + "…" : s;
}
