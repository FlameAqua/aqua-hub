using System.Text.Json.Nodes;

namespace AquaHub.E2E.Tests;

/// <summary>Title bar: command box, refresh, alerts bell (Mark all read / Clear / open), do-not-disturb.</summary>
public sealed class A02_TitleBarTests : E2ETestBase
{
    public A02_TitleBarTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    /// <summary>
    /// Seeds a price-target alert (NVDA "alert below" far above the price) so the Sentinel raises a fresh alert
    /// ~20 s after start, and lets toasts through while a full-screen app runs (deterministic journal entry).
    /// </summary>
    protected override SessionOptions Options => new()
    {
        EditSettings = root =>
        {
            if (root["markets"]?["watchlist"] is JsonArray list)
                foreach (var w in list.OfType<JsonObject>())
                    if ((string?)w["symbol"] == "NVDA") w["alertBelow"] = 1_000_000;
            if (root["notifications"] is JsonObject n)
            {
                n["suppressWhenFullscreen"] = false;
                n["quietStart"] = "03:00";
                n["quietEnd"] = "03:01";
            }
        },
    };

    private AutomationElement Bell => Ui.WaitFind(Main, Ui.Id("BellButton"), "bell button");

    /// <summary>The bell's unread count: "9+" reads as 10, no badge as 0.</summary>
    private int Unread() => Ui.Find(Main, Ui.Id("BellCount")) is { } c
        ? Ui.NameOf(c) is "9+" ? 10 : int.TryParse(Ui.NameOf(c), out var n) ? n : 0
        : 0;
    private AutomationElement Dnd => Ui.WaitFind(Main, Ui.Id("dnd-toggle"), "do-not-disturb button");

    /// <summary>The list under the bell (rows are searched only there: an alert's corner banner has the same button).</summary>
    private AutomationElement AlertsList => Ui.WaitFind(Main, Ui.Id("alerts-popup"), "alerts popup");
    private AutomationElement? PriceRow() =>
        Ui.Find(Main, Ui.Id("alerts-popup")) is { } list ? Ui.FindWhere(list, ControlType.Button, n => n.Contains("fell below", StringComparison.OrdinalIgnoreCase)) : null;
    private static string DndName(bool on) => on ? "Do not disturb: on" : "Do not disturb: off";

    [Fact]
    [Trait("Category", "Smoke")]
    public void T01_CommandBoxAndCtrlKOpenThePalette() => Run(() =>
    {
        GoTo("today");
        Check("Title bar", "command box opens the palette", () =>
        {
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("CommandBox"), "command box"));
            var palette = Wait.For(App.TryPalette, "command palette window");
            Ui.WaitFind(palette, Ui.Id("Input"), "palette input");
            App.EnsureForeground(palette, allowActivateCommand: false);
            Input.Press(Pid, VK.Escape);
            Wait.For(() => App.TryPalette() is null, "palette closed with Esc");
        });
        Check("Title bar", "Ctrl+K opens the palette", () =>
        {
            KeysToMain(VK.Control, VK.K);
            var palette = Wait.For(App.TryPalette, "command palette window");
            App.EnsureForeground(palette, allowActivateCommand: false);
            Input.Press(Pid, VK.Escape);
            Wait.For(() => App.TryPalette() is null, "palette closed with Esc");
        });
    });

    [Fact]
    public void T02_RefreshButtonAndF5RunTheCollectors() => Run(() =>
    {
        GoTo("today");
        Check("Title bar", "Refresh everything runs collectors", () =>
        {
            var mark = App.Log.Mark();
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("RefreshButton"), "refresh button"));
            App.Log.WaitForLine(mark, l => l.Contains("[agents] weather:"), "a weather collector run", TimeSpan.FromSeconds(30));
            App.Log.WaitForLine(mark, l => l.Contains("[agents] news-scout:"), "a news-scout run", TimeSpan.FromSeconds(60));
        });
        Check("Title bar", "F5 runs collectors", () =>
        {
            Thread.Sleep(1500);
            var mark = App.Log.Mark();
            PressInMain(VK.F5);
            App.Log.WaitForLine(mark, l => l.Contains("[agents] weather:") || l.Contains("[agents] market-watch:"), "a collector run after F5", TimeSpan.FromSeconds(60));
        });
        Check("Title bar", "sync status text updates", () =>
            Wait.For(() => Ui.NameOf(Ui.WaitFind(Main, Ui.Id("sync-status"), "sync text")).Contains("updated", StringComparison.OrdinalIgnoreCase), "'News updated …' text"));
    });

    [Fact]
    public void T03_AlertsBellMarkAllReadOpenAndClear() => Run(() =>
    {
        GoTo("today");
        JournalEntry? toast = null;
        Check("Alerts", "seeded price-target alert raised and toasted (journal 'toast')", () =>
        {
            toast = App.Journal.WaitFor(0, "toast", d => d.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || d.Contains("fell below"), TimeSpan.FromSeconds(90), "price alert toast");
        });
        Check("Alerts", "unread badge shows a count", () =>
            Wait.For(() => Ui.Find(Main, Ui.Id("BellCount")) is { } c && Ui.NameOf(c).Length > 0, "bell unread count", TimeSpan.FromSeconds(30)));

        Check("Alerts", "bell opens the alerts popup with rows", () =>
        {
            Ui.Invoke(Bell);
            Button(AlertsList, "Mark all read");
            Button(AlertsList, "Clear");
            Wait.For(() => PriceRow() is not null, "the price alert row");
        });
        Check("Alerts", "opening an alert marks it read (the unread count drops) and closes the list", () =>
        {
            var before = Unread();
            var row = Wait.For(PriceRow, "alert row");
            Ui.Invoke(row);
            ExpectPage("markets");
            Wait.For(() => Ui.Find(Main, Ui.Id("alerts-popup")) is null, "alerts list closed after opening an alert");
            // "9+" can't show a drop of one; the badge must still not grow.
            if (before < 10) Wait.For(() => Unread() < before, $"unread count below {before}");
            else Expect(Unread() <= before, "unread count did not grow");
            GoTo("today");
            Ui.Invoke(Bell);
            Button(Main, "Mark all read");
        });
        Check("Alerts", "Mark all read clears the unread badge", () =>
        {
            Ui.Invoke(Button(AlertsList, "Mark all read"));
            Wait.For(() => Ui.Find(Main, Ui.Id("alerts-popup")) is null, "popup closed after Mark all read");
            Wait.For(() => Ui.Find(Main, Ui.Id("BellCount")) is null, "unread badge hidden");
        });
        Check("Alerts", "alert row opens its target (Markets, NVDA)", () =>
        {
            Ui.Invoke(Bell);
            var row = Wait.For(PriceRow, "alert row");
            Ui.Invoke(row);
            ExpectPage("markets");
            var page = PageRoot("markets");
            Wait.For(() => Ui.Texts(page).Any(t => t.StartsWith("NVDA  ·", StringComparison.Ordinal)), "NVDA detail header");
        });
        Check("Alerts", "Clear empties the list ('You're all caught up.') and removes the alert's corner banner", () =>
        {
            GoTo("today");
            Ui.Invoke(Bell);
            Ui.Invoke(Button(AlertsList, "Clear"));
            Wait.For(() => Ui.Find(Main, Ui.Id("alerts-popup")) is null, "popup closed after Clear");
            Expect(Ui.Find(Main, Ui.Id("alert-banner")) is null, "an alert banner is still showing after Clear");
            Ui.Invoke(Bell);
            Ui.WaitFind(AlertsList, Ui.Text("You're all caught up."), "empty state text");
            Expect(PriceRow() is null, "alert rows still listed after Clear");
            // Close the popup again (the popup is modeless; Mark all read closes it).
            Ui.Invoke(Button(AlertsList, "Mark all read"));
        });
        Check("Alerts", "Esc closes the alerts popup (keyboard users)", () =>
        {
            Ui.Invoke(Bell);
            _ = AlertsList;
            PressInMain(VK.Escape);
            var closed = Wait.Until(() => Ui.Find(Main, Ui.Id("alerts-popup")) is null, TimeSpan.FromSeconds(3));
            if (!closed)
            {
                Ui.Invoke(Button(AlertsList, "Mark all read"));
                throw new Xunit.Sdk.XunitException("The alerts popup does not close with Esc");
            }
        });
    });

    [Fact]
    public void T04_DoNotDisturbTogglesAndPersists() => Run(() =>
    {
        GoTo("today");
        var initial = App.Settings.GetBool("notifications.doNotDisturb") ?? false;
        Check("Title bar", "DND button → notifications.doNotDisturb flips", () =>
        {
            Ui.Invoke(Dnd);
            App.Settings.WaitForBool("notifications.doNotDisturb", !initial);
        });
        Check("Title bar", "DND button tooltip/name reflects the state", () =>
        {
            var expected = DndName(!initial);
            Wait.For(() => Ui.NameOf(Dnd) == expected, $"DND button named '{expected}' (now '{Ui.NameOf(Dnd)}')");
        });
        Check("Title bar", "DND button again → restored", () =>
        {
            Ui.Invoke(Dnd);
            App.Settings.WaitForBool("notifications.doNotDisturb", initial);
            Wait.For(() => Ui.NameOf(Dnd) == DndName(initial), "DND button name restored");
        });
    });
}
