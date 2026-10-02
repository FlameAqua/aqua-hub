namespace AquaHub.E2E.Tests;

/// <summary>Start-up in dry-run mode, and reaching every page through the nav rail and the keyboard shortcuts.</summary>
public sealed class A01_StartupNavigationTests : E2ETestBase
{
    public A01_StartupNavigationTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    [Fact]
    [Trait("Category", "Smoke")]
    public void T01_StartsInDryRunModeOnToday() => Run(() =>
    {
        Check("Startup", "process running with main window 'Aqua Hub'", () =>
        {
            Expect(App.IsAlive, "process is not running");
            Expect(Ui.NameOf(Main) == "Aqua Hub", "main window title");
        });
        Check("Startup", "journal records dry-run mode", () =>
        {
            var first = App.Journal.ReadAll().FirstOrDefault();
            Expect(first is { Action: "e2e" } && first.Detail.Contains("dry-run"), $"first journal entry: {first}");
        });
        Check("Startup", "log reports dry-run start", () =>
            App.Log.WaitForLine(0, l => l.Contains("Aqua Hub started (dry-run E2E mode)"), "start line"));
        Check("Startup", "Today page root (page-today) shown", () => PageRoot("today"));
        Check("Startup", "nav rail exposes all 10 items with AutomationIds", () =>
        {
            foreach (var p in AllPages) NavItem(p);
        });
        Check("Startup", "nav-today is selected", () => Expect(Ui.IsSelected(NavItem("today")), "nav-today not selected"));
        Check("Startup", "title bar controls present", () =>
        {
            Ui.WaitFind(Main, Ui.Id("CommandBox"), "command box");
            var refresh = Ui.WaitFind(Main, Ui.Id("RefreshButton"), "refresh");
            // Its tooltip lists each area's freshness; the accessible name must stay short and stable.
            Expect(Ui.NameOf(refresh) == "Refresh everything", $"refresh button announced as '{Ui.NameOf(refresh)}'");
            Ui.WaitFind(Main, Ui.Id("BellButton"), "bell");
            Ui.WaitFind(Main, Ui.Id("dnd-toggle"), "dnd");
            Ui.WaitFind(Main, Ui.Id("AiStatus"), "AI status");
        });
    });

    [Fact]
    [Trait("Category", "Smoke")]
    public void T02_EveryPageViaNavRail() => Run(() =>
    {
        foreach (var page in AllPages.Skip(1).Append("today"))
        {
            Check("Navigation", $"nav rail → {page}", () =>
            {
                Wait.Retry(() => Ui.Select(NavItem(page)), "select nav item");
                ExpectPage(page);
                Expect(Ui.IsSelected(NavItem(page)), $"nav-{page} should be selected");
                var others = AllPages.Where(p => p != page && Ui.IsSelected(NavItem(p))).ToList();
                Expect(others.Count == 0, "other nav items still selected: " + string.Join(",", others));
            });
        }
    });

    [Fact]
    public void T03_KeyboardShortcutsCtrl1To9AndCtrlComma() => Run(() =>
    {
        GoTo("settings");
        var digits = new[] { VK.D1, VK.D2, VK.D3, VK.D4, VK.D5, VK.D6, VK.D7, VK.D8, VK.D9 };
        for (var i = 0; i < 9; i++)
        {
            var page = AllPages[i];
            var key = digits[i];
            Check("Navigation", $"Ctrl+{i + 1} → {page}", () =>
            {
                KeysToMain(VK.Control, key);
                ExpectPage(page);
                Expect(Ui.IsSelected(NavItem(page)), $"nav-{page} should follow the keyboard navigation");
            });
        }
        Check("Navigation", "Ctrl+, → settings", () =>
        {
            KeysToMain(VK.Control, VK.OemComma);
            ExpectPage("settings");
        });
        GoTo("today");
    });

    [Fact]
    public void T04_AiStatusButtonOpensLocalAiPopup() => Run(() =>
    {
        GoTo("today");
        Check("Navigation", "AI status (nav footer) → local AI popup (status, Ollama on/off, Agents, Settings)", () =>
        {
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("AiStatus"), "AI status button"));
            Ui.WaitFind(Main, Ui.Id("ai-popup"), "local AI popup");
        });
        Check("Navigation", "local AI popup → Agents", () =>
        {
            Ui.Invoke(Button(Main, "Agents"));
            ExpectPage("agents");
        });
        GoTo("today");
    });
}
