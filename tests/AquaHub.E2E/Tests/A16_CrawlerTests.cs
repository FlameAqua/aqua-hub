namespace AquaHub.E2E.Tests;

/// <summary>
/// Generic crawler over every page (each Settings section separately), the title bar, the quick panel and the palette:
/// activates every enabled control once and asserts the app survives. The last test reports accessibility gaps.
/// </summary>
public sealed class A16_CrawlerTests : E2ETestBase
{
    public A16_CrawlerTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private Crawler NewCrawler() => new(App, Step);

    /// <summary>Closes whatever an activated control opened (palette, quick panel, menus, the bell popup, dialogs).</summary>
    private void Cleanup(bool keepFlyout = false, bool keepPalette = false)
    {
        if (!keepPalette && App.TryPalette() is { } p)
        {
            try { App.EnsureForeground(p, allowActivateCommand: false); Input.Press(Pid, VK.Escape); } catch { }
            Wait.Until(() => App.TryPalette() is null, TimeSpan.FromSeconds(2));
        }
        if (!keepFlyout && App.TryFlyout() is not null)
        {
            App.Command("flyout");
            Wait.Until(() => App.TryFlyout() is null, TimeSpan.FromSeconds(2));
        }
        foreach (var w in AppWindows.Popups(Pid))
        {
            try
            {
                if (w.TryGetCurrentPattern(WindowPattern.Pattern, out var wp) && Ui.Find(w, Ui.Type(ControlType.Menu), TreeScope.Subtree) is null)
                    ((WindowPattern)wp).Close();
                else
                {
                    Input.TryActivate(App.HwndOf(w));
                    if (AppWindows.ForegroundPid() == Pid) Input.Press(Pid, VK.Escape);
                }
            }
            catch { }
        }
        if (App.TryMain() is { } main && Ui.Find(main, Ui.Button("Mark all read")) is { } markRead)
            try { Ui.Invoke(markRead); } catch { }
    }

    private void CrawlPage(string page)
    {
        var stats = NewCrawler().Crawl($"page:{page}", () => PageRoot(page, TimeSpan.FromSeconds(15)), () =>
        {
            if (App.TryMain() is null) App.Command("activate");
            if (CurrentPage() != page) GoTo(page);
        }, afterEach: () => Cleanup());
        Crawler.WriteResults();
        Expect(Crawler.Problems.All(p => p.Surface != $"page:{page}"),
            "Problems: " + string.Join(" | ", Crawler.Problems.Where(p => p.Surface == $"page:{page}").Select(p => $"{p.Control}: {p.Issue}")));
        Expect(stats.Invoked > 0, "nothing was invoked");
    }

    [Fact] public void T01_Today() => Run(() => CrawlPage("today"));
    [Fact] public void T02_News() => Run(() => CrawlPage("news"));
    [Fact] public void T03_Social() => Run(() => CrawlPage("social"));
    [Fact] public void T04_Markets() => Run(() => CrawlPage("markets"));
    [Fact] public void T05_Upcoming() => Run(() => CrawlPage("upcoming"));
    [Fact] public void T06_System() => Run(() => CrawlPage("system"));
    [Fact] public void T07_Launchpad() => Run(() => CrawlPage("launchpad"));
    [Fact] public void T08_Ask() => Run(() => CrawlPage("ask"));
    [Fact] public void T09_Agents() => Run(() => CrawlPage("agents"));

    [Fact]
    public void T10_SettingsEverySection() => Run(() =>
    {
        var sections = new[] { "General", "Location & weather", "News", "Social", "Markets", "Agenda", "Predictions", "AI & models", "Apps & scenes", "Notifications", "Privacy & data", "About" };
        foreach (var section in sections)
        {
            Check("Crawler", $"settings section '{section}'", () =>
            {
                void Restore()
                {
                    if (App.TryMain() is null) App.Command("activate");
                    var page = GoTo("settings");
                    var list = Ui.WaitFind(page, Ui.Id("Sections"), "sections");
                    var item = Ui.FindAll(list, Ui.Type(ControlType.ListItem)).First(i => Ui.Texts(i).Contains(section));
                    if (!Ui.IsSelected(item)) { Ui.Select(item); Thread.Sleep(300); }
                }
                // Only crawl the section's own panel: skip the section list itself and the Edit JSON button (covered elsewhere).
                NewCrawler().Crawl($"settings:{section}", () => PageRoot("settings"), Restore,
                    extraSkip: n => n.Ancestors().Any(a => a.AutomationId == "Sections") || n.AutomationId == "Sections",
                    afterEach: () => Cleanup());
                Crawler.WriteResults();
                var problems = Crawler.Problems.Where(p => p.Surface == $"settings:{section}").ToList();
                Expect(problems.Count == 0, string.Join(" | ", problems.Select(p => $"{p.Control}: {p.Issue}")));
            });
        }
    });

    [Fact]
    public void T11_TitleBarAndNavFooter() => Run(() =>
    {
        NewCrawler().Crawl("shell", () => Main, () =>
        {
            if (App.TryMain() is null) App.Command("activate");
            if (CurrentPage() != "today") GoTo("today");
        },
        extraSkip: n => n.AutomationId.StartsWith("nav-", StringComparison.Ordinal) || n.Ancestors().Any(a => a.AutomationId.StartsWith("page-", StringComparison.Ordinal)),
        afterEach: () => Cleanup());
        Crawler.WriteResults();
        Expect(Crawler.Problems.All(p => p.Surface != "shell"), string.Join(" | ", Crawler.Problems.Where(p => p.Surface == "shell").Select(p => $"{p.Control}: {p.Issue}")));
    });

    [Fact]
    public void T12_QuickPanel() => Run(() =>
    {
        AutomationElement Flyout()
        {
            if (App.TryFlyout() is { } f) return f;
            App.Command("flyout");
            return Wait.For(App.TryFlyout, "quick panel");
        }
        NewCrawler().Crawl("quick panel", Flyout, () => Flyout(), afterEach: () => Cleanup(keepFlyout: true));
        Cleanup();
        Crawler.WriteResults();
        Expect(Crawler.Problems.All(p => p.Surface != "quick panel"), string.Join(" | ", Crawler.Problems.Where(p => p.Surface == "quick panel").Select(p => $"{p.Control}: {p.Issue}")));
    });

    [Fact]
    public void T13_Palette() => Run(() =>
    {
        AutomationElement Palette()
        {
            if (App.TryPalette() is { } p) return p;
            App.Command("palette");
            return Wait.For(App.TryPalette, "palette");
        }
        NewCrawler().Crawl("palette", Palette, () => Palette(), afterEach: () => Cleanup(keepPalette: true));
        Cleanup();
        Crawler.WriteResults();
        Expect(Crawler.Problems.All(p => p.Surface != "palette"), string.Join(" | ", Crawler.Problems.Where(p => p.Surface == "palette").Select(p => $"{p.Control}: {p.Issue}")));
    });

    [Fact]
    public void T14_TrayMenu() => Run(() =>
    {
        App.Command("tray-menu");
        var menu = Wait.For(() => AppWindows.Menu(Pid, "tray-menu"), "tray menu");
        var snap = TreeSnapshot.Capture(menu);
        var crawler = NewCrawler();
        crawler.Audit("tray menu", snap);
        lock (Crawler.Stats)
            Crawler.Stats.Add(new Crawler.SurfaceStats
            {
                Surface = "tray menu (audited; items exercised in A15)", InteractiveFound = snap.Descendants().Count(n => n.IsInteractive),
                Actionable = snap.Descendants().Count(n => n.IsActionable),
            });
        App.EnsureForeground(Main);
        Input.Press(Pid, VK.Escape);
        Cleanup();
        Crawler.WriteResults();
    });

    [Fact]
    public void T99_AccessibilityNamesReport() => Run(() =>
    {
        Crawler.WriteResults();
        var unnamed = Crawler.UnnamedControls
            .GroupBy(u => $"{u.Type} showing '{(u.ShownText.Length > 0 ? u.ShownText : u.HelpText)}'" + (u.AutomationId.Length > 0 ? $" #{u.AutomationId}" : ""))
            .Select(g => $"{g.Key} ×{g.Count()} [{string.Join(", ", g.Select(x => x.Surface).Distinct().Take(4))}]").ToList();
        var dumps = Crawler.DumpNames.Select(d => d.Name.Split(' ')[0]).Distinct().ToList();
        Step($"unnamed interactive controls: {Crawler.UnnamedControls.Count} ({unnamed.Count} distinct)");
        foreach (var u in unnamed) Step("   " + u);
        Step($"list items announced as object dumps: {Crawler.DumpNames.Count} ({string.Join(", ", dumps)})");
        Results.RecordFinding(TestName, "a11y", "Unnamed interactive controls", string.Join("\n", unnamed));
        Check("Accessibility", "every interactive control has an accessible name", () =>
            Expect(Crawler.UnnamedControls.Count == 0, $"{Crawler.UnnamedControls.Count} interactive controls have no accessible name, e.g. {string.Join("; ", unnamed.Take(6))}"));
        Check("Accessibility", "list items are not announced as object dumps", () =>
            Expect(Crawler.DumpNames.Count == 0, $"{Crawler.DumpNames.Count} list/data items are announced as type names or record dumps ({string.Join(", ", dumps.Take(10))})"));
    });
}
