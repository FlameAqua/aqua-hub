namespace AquaHub.E2E.Tests;

public sealed class A21_UiPolishTests : E2ETestBase
{
    public A21_UiPolishTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    [Fact]
    public void T01_NotificationPopupClosesOnOwnerOutsideAndEscape() => Run(() =>
    {
        GoTo("today");
        App.EnsureForeground(Main);
        var bell = Ui.WaitFind(Main, Ui.Id("BellButton"), "notification button");
        bool Open() => AppWindows.FindInPopups(Pid, Ui.Id("alerts-popup")) is not null;
        Input.ClickElement(Pid, bell);
        Wait.For(Open, "notifications open");
        Shot("caption-and-alerts");
        Input.ClickElement(Pid, bell);
        Wait.For(() => !Open(), "owner closes notifications without reopening");
        Input.ClickElement(Pid, bell);
        Wait.For(Open, "notifications reopen");
        Input.Press(Pid, VK.Escape);
        Wait.For(() => !Open(), "Escape closes notifications");
        Input.ClickElement(Pid, bell);
        Wait.For(Open, "notifications open for outside click");
        Input.ClickElement(Pid, Ui.WaitFind(Main, Ui.Id("nav-models"), "Models navigation"));
        Wait.For(() => !Open(), "outside click closes notifications");
        PageRoot("models");
    });

    [Fact]
    public void T02_ThemeSwitchPersistsAndLeavesPageUsable() => Run(() =>
    {
        GoTo("settings");
        var sections = Ui.WaitFind(PageRoot("settings"), Ui.Id("Sections"), "settings sections");
        Ui.Select(Ui.FindAll(sections, Ui.Type(ControlType.ListItem)).First(i => Ui.Texts(i).Contains("General")));
        var theme = Ui.WaitFind(PageRoot("settings"), Ui.Id("settings-theme"), "theme picker");
        Ui.SelectComboItem(theme, "Light", Pid);
        App.Settings.WaitForString("general.theme", "light");
        Shot("light-caption");
        Ui.SelectComboItem(theme, "Dark", Pid);
        App.Settings.WaitForString("general.theme", "dark");
        GoTo("news");
        Ui.WaitFind(PageRoot("news"), Ui.Name("Filter stories"), "news search after theme changes");
    });

    [Fact]
    public void T03_FeedRefreshShowsPausedOutcomeAndRecovers() => Run(() =>
    {
        GoTo("agents");
        if (Ui.ButtonWithText(PageRoot("agents"), "Pause all") is { } pause) Ui.Invoke(pause);
        Ui.WaitButtonWithText(PageRoot("agents"), "Resume all");
        foreach (var (page, id) in new[] { ("news", "news-scout"), ("social", "social-scout") })
        {
            GoTo(page);
            var button = Ui.WaitFind(PageRoot(page), Ui.Id(id + "-refresh"), "refresh " + page);
            Wait.For(() => Ui.IsEnabled(button), "existing collector completes");
            Ui.Invoke(button);
            Wait.For(() => Ui.Texts(PageRoot(page)).Any(t => t is "Agents paused · resume in Agents" or "Sources disabled"), "visible paused or disabled outcome");
            Expect(Ui.IsEnabled(button), "refresh remains usable after paused outcome");
            Shot(page + "-refresh");
        }
        GoTo("agents");
        Ui.Invoke(Ui.WaitButtonWithText(PageRoot("agents"), "Resume all"));
    });
}
