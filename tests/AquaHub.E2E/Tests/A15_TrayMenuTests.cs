namespace AquaHub.E2E.Tests;

/// <summary>Tray context menu (opened with --tray-menu): every item except Quit (covered by the lifecycle tests).</summary>
public sealed class A15_TrayMenuTests : E2ETestBase
{
    public A15_TrayMenuTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private AutomationElement Menu()
    {
        App.Command("tray-menu");
        return Wait.For(() => AppWindows.Menu(Pid, "tray-menu"), "tray menu (#tray-menu)");
    }

    private void Choose(string item) => Ui.Invoke(MenuItem(Menu(), item));

    [Fact]
    public void T01_EveryTrayMenuItem() => Run(() =>
    {
        Check("Tray menu", "menu lists all items", () =>
        {
            var m = Menu();
            var items = Ui.FindAll(m, Ui.Type(ControlType.MenuItem)).Select(Ui.NameOf).ToList();
            foreach (var expected in new[] { "Open Aqua Hub", "Quick panel", "Ask or command…", "Read my brief", "Refresh everything", "Settings", "Quit Aqua Hub" })
                Expect(items.Contains(expected), $"missing '{expected}' in [{string.Join(", ", items)}]");
            Expect(items.Contains("Do not disturb") || items.Contains("Turn notifications on"), "DND item missing");
            Expect(items.Contains("Pause AI & free VRAM") || items.Contains("Resume AI"), "AI item missing");
            App.EnsureForeground(App.Main);
            Input.Press(Pid, VK.Escape);
            DismissTransients();
        });
        Check("Tray menu", "Open Aqua Hub → main window shown", () =>
        {
            GoTo("news");
            Choose("Open Aqua Hub");
            Wait.For(() => App.TryMain() is not null, "main window");
            ExpectPage("news");
        });
        Check("Tray menu", "Quick panel → quick panel shown", () =>
        {
            Choose("Quick panel");
            Wait.For(App.TryFlyout, "quick panel");
            App.Command("flyout");
            Wait.For(() => App.TryFlyout() is null, "quick panel hidden");
        });
        Check("Tray menu", "Ask or command… → palette shown", () =>
        {
            Choose("Ask or command…");
            var p = Wait.For(App.TryPalette, "palette");
            App.EnsureForeground(p, allowActivateCommand: false);
            Input.Press(Pid, VK.Escape);
            Wait.For(() => App.TryPalette() is null, "palette closed");
        });
        Check("Tray menu", "Ask about my screen → journal screenshot, Ask page with the question", () =>
        {
            ExpectJournal("screenshot", () => Choose("Ask about my screen"));
            ExpectPage("ask");
            var ask = PageRoot("ask");
            Wait.For(() => Ui.AllTexts(ask).Any(t => t.StartsWith("What's on my screen?", StringComparison.Ordinal)), "the screen question in the chat");
            if (Ui.Find(ask, Ui.Id("StopButton")) is { } stop) Ui.Invoke(stop);
        });
        Check("Tray menu", "Read my brief → journal speak", () => ExpectJournal("speak", () => Choose("Read my brief")));
        Check("Tray menu", "Refresh everything → collectors run", () =>
        {
            var mark = App.Log.Mark();
            Choose("Refresh everything");
            App.Log.WaitForLine(mark, l => l.Contains("[agents] weather:") || l.Contains("[agents] news-scout:"), "collector run", TimeSpan.FromSeconds(60));
        });
        Check("Tray menu", "Do not disturb → DND on; item becomes 'Turn notifications on' → DND off", () =>
        {
            var start = App.Settings.GetBool("notifications.doNotDisturb") ?? false;
            Choose(start ? "Turn notifications on" : "Do not disturb");
            App.Settings.WaitForBool("notifications.doNotDisturb", !start);
            Choose(!start ? "Turn notifications on" : "Do not disturb");
            App.Settings.WaitForBool("notifications.doNotDisturb", start);
        });
        Check("Tray menu", "Pause AI & free VRAM → journal unload-model; then Resume AI", () =>
        {
            ExpectJournal("unload-model", () => Choose("Pause AI & free VRAM"));
            Wait.For(() => Ui.NameOf(Ui.WaitFind(App.Main, Ui.Id("AiState"), "AI state")).Contains("Paused by you"), "AI status 'Paused by you'");
            Choose("Resume AI");
            Wait.For(() => !Ui.NameOf(Ui.WaitFind(App.Main, Ui.Id("AiState"), "AI state")).Contains("Paused by you"), "AI no longer paused by user");
        });
        Check("Tray menu", "Settings → Settings page", () =>
        {
            GoTo("today");
            Choose("Settings");
            ExpectPage("settings");
        });
        GoTo("today");
    });
}
