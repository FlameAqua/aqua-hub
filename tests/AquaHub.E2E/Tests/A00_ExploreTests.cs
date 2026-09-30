namespace AquaHub.E2E.Tests;

/// <summary>Only runs when AQUAHUB_E2E_EXPLORE=1: dumps the UI Automation tree of every surface (diagnostics).</summary>
public sealed class ExploreFactAttribute : FactAttribute
{
    public ExploreFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AQUAHUB_E2E_EXPLORE") != "1") Skip = "Diagnostics only (set AQUAHUB_E2E_EXPLORE=1)";
    }
}

public sealed class A00_ExploreTests : E2ETestBase
{
    public A00_ExploreTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private void DumpTo(string name, AutomationElement root)
    {
        var dir = Path.Combine(E2EConfig.ResultsDir, "trees");
        Directory.CreateDirectory(dir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snap = TreeSnapshot.Capture(root);
        var text = TreeSnapshot.Dump(snap);
        File.WriteAllText(Path.Combine(dir, name + ".txt"), text);
        Step($"dumped {name}: {snap.Descendants().Count()} nodes in {sw.ElapsedMilliseconds} ms");
    }

    [ExploreFact]
    public void DumpAllSurfaces() => Run(() =>
    {
        DumpTo("00-main-initial", Main);
        foreach (var page in AllPages)
        {
            GoTo(page);
            Thread.Sleep(1500);
            DumpTo("page-" + page, Main);
        }
        GoTo("today");
        foreach (var w in App.Windows()) Step("window: " + Ui.Describe(w));

        App.Command("flyout");
        var flyout = Wait.For(App.TryFlyout, "flyout");
        Thread.Sleep(800);
        DumpTo("flyout", flyout);
        App.Command("flyout");
        Wait.Until(() => App.TryFlyout() is null, TimeSpan.FromSeconds(4));

        App.Command("palette");
        var palette = Wait.For(App.TryPalette, "palette");
        Thread.Sleep(800);
        DumpTo("palette", palette);
        App.Command("palette");
        Wait.Until(() => App.TryPalette() is null, TimeSpan.FromSeconds(4));

        App.Command("tray-menu");
        Thread.Sleep(1200);
        var i = 0;
        foreach (var w in AppWindows.Popups(Pid)) DumpTo($"popup-{i++}", w);
        DismissTransients();
    });

    private void DescribeWindows(string label)
    {
        foreach (var h in Win32.WindowsOf(Pid))
        {
            Win32.GetWindowRect(h, out var r);
            Step($"{label}: hwnd {h} class={Win32.ClassOf(h)} title='{Win32.TitleOf(h)}' rect={r.Left},{r.Top} {r.Width}x{r.Height}");
        }
        foreach (var w in App.Windows()) Step($"{label}: UIA top-level {Ui.Describe(w)} class={w.Current.ClassName}");
        Step($"{label}: foreground pid {AppWindows.ForegroundPid()} (app {Pid})");
    }

    [ExploreFact]
    public void ProbePopups() => Run(() =>
    {
        GoTo("today");
        DescribeWindows("before");
        Ui.Invoke(Ui.WaitFind(Main, Ui.Id("BellButton"), "bell"));
        Thread.Sleep(1000);
        DescribeWindows("bell (inactive)");
        var mark = AppWindows.FindInAll(Pid, Ui.Name("Mark all read"));
        Step("Mark all read found: " + (mark is null ? "no" : Ui.Describe(mark)));
        App.EnsureForeground(Main);
        Ui.Invoke(Ui.WaitFind(Main, Ui.Id("BellButton"), "bell"));
        Thread.Sleep(1000);
        DescribeWindows("bell (active)");
        mark = AppWindows.FindInAll(Pid, Ui.Name("Mark all read"));
        Step("Mark all read found: " + (mark is null ? "no" : Ui.Describe(mark)));
        foreach (var w in AppWindows.Popups(Pid)) DumpTo("probe-bell", w);
        DismissTransients();
    });

    [ExploreFact]
    public void DumpSettingsSectionsAndPopups() => Run(() =>
    {
        var settings = GoTo("settings");
        var sections = Ui.WaitFind(settings, Ui.Id("Sections"), "sections list");
        var items = Ui.FindAll(sections, Ui.Type(ControlType.ListItem));
        var n = 0;
        foreach (var item in items)
        {
            Ui.Select(item);
            Thread.Sleep(700);
            DumpTo($"settings-{n++:00}", PageRoot("settings"));
        }

        GoTo("today");
        Ui.Invoke(Ui.WaitFind(Main, Ui.Id("BellButton"), "bell"));
        Thread.Sleep(900);
        var k = 0;
        foreach (var w in AppWindows.Popups(Pid)) DumpTo($"bell-popup-{k++}", w);
        DismissTransients();

        var markets = GoTo("markets");
        Ui.SetValue(Ui.WaitFind(markets, Ui.Id("AddBox"), "add box"), "tesla");
        Thread.Sleep(3000);
        k = 0;
        foreach (var w in AppWindows.Popups(Pid)) DumpTo($"markets-search-popup-{k++}", w);
        DismissTransients();

        var launchpad = GoTo("launchpad");
        Ui.Invoke(Ui.WaitButtonWithText(launchpad, "Add app"));
        Thread.Sleep(3000);
        DumpTo("launchpad-picker", PageRoot("launchpad"));
    });
}
