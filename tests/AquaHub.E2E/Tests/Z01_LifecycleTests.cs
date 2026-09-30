namespace AquaHub.E2E.Tests;

/// <summary>Window lifecycle: close keeps the app running, activation brings it back, --quit and tray Quit exit cleanly.</summary>
public sealed class Z01_LifecycleTests : E2ETestBase
{
    public Z01_LifecycleTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private static void CloseWindow(AutomationElement window) =>
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();

    [Fact]
    public void T01_CloseMainWindowKeepsRunningAndActivateReopens() => Run(() =>
    {
        GoTo("markets");
        Check("Lifecycle", "closing the main window keeps the process (tray) running", () =>
        {
            CloseWindow(Main);
            Wait.For(() => App.TryMain() is null, "main window gone");
            Thread.Sleep(1500);
            Expect(App.IsAlive, "process exited after closing the window");
        });
        Check("Lifecycle", "plain launch (activate) shows the main window again", () =>
        {
            App.Command("activate");
            Wait.For(() => App.TryMain() is not null, "main window back", TimeSpan.FromSeconds(15));
            PageRoot("today", TimeSpan.FromSeconds(15));
        });
        Check("Lifecycle", "--flyout and --palette work while the main window is closed", () =>
        {
            CloseWindow(Main);
            Wait.For(() => App.TryMain() is null, "main window gone");
            App.Command("flyout");
            Wait.For(App.TryFlyout, "quick panel");
            App.Command("flyout");
            Wait.For(() => App.TryFlyout() is null, "quick panel hidden");
            App.Command("palette");
            var p = Wait.For(App.TryPalette, "palette");
            App.EnsureForeground(p, allowActivateCommand: false);
            Input.Press(Pid, VK.Escape);
            Wait.For(() => App.TryPalette() is null, "palette closed");
            App.Command("activate");
            Wait.For(() => App.TryMain() is not null, "main window back");
        });
    });

    [Fact]
    public void T02_CloseToTraySettingIsHonoured() => Run(() =>
    {
        Check("Lifecycle", "with 'Closing the window keeps Aqua Hub running' OFF, closing the window quits", () =>
        {
            var settings = GoTo("settings");
            var sw = Ui.WaitFind(settings, Ui.And(Ui.Type(ControlType.CheckBox), Ui.Name("Closing the window keeps Aqua Hub running")), "close-to-tray switch");
            Ui.SetToggle(sw, false);
            App.Settings.WaitForBool("general.closeToTray", false);
            GoTo("today");
            AllowsExit = true;
            CloseWindow(Main);
            var exited = Wait.Until(() => !App.IsAlive, TimeSpan.FromSeconds(10));
            if (!exited)
            {
                AllowsExit = false;
                App.Command("activate");
                Wait.For(() => App.TryMain() is not null, "main window back");
                var s = Ui.WaitFind(GoTo("settings"), Ui.And(Ui.Type(ControlType.CheckBox), Ui.Name("Closing the window keeps Aqua Hub running")), "switch");
                Ui.SetToggle(s, true);
                App.Settings.WaitForBool("general.closeToTray", true);
                throw new Xunit.Sdk.XunitException("The setting is saved but ignored: the app keeps running in the tray after the window is closed");
            }
        });
    });

    [Fact]
    public void T03_QuitCommandExitsCleanly()
    {
        // Fresh instance: the "still running" hint is shown at most once per profile, so start from a clean copy.
        Fixture.Restart();
        Run(() =>
    {
        AllowsExit = true;
        var journalMark = App.Journal.Mark();
        Check("Lifecycle", "--quit exits with code 0 and no errors", () =>
        {
            var mark = App.Log.Mark();
            App.Command("quit");
            Expect(App.Process.WaitForExit(15000), "process still running 15 s after --quit");
            Expect(App.Process.ExitCode == 0, $"exit code {App.Process.ExitCode}");
            var errors = App.Log.ErrorsSince(mark);
            Expect(errors.Count == 0, "errors logged during shutdown: " + string.Join(" | ", errors));
        });
        Check("Lifecycle", "quitting does not announce 'Aqua Hub is still running'", () =>
        {
            var hint = App.Journal.Find(journalMark, "toast", d => d.Contains("still running", StringComparison.OrdinalIgnoreCase));
            Expect(hint is null, "Quitting shows the 'Aqua Hub is still running' tray notification (the window-closed hint fires during shutdown)");
        });
    });
    }

    [Fact]
    public void T04_TrayQuitExitsCleanly() => Run(() =>
    {
        AllowsExit = true;
        Check("Lifecycle", "tray menu 'Quit Aqua Hub' exits the process", () =>
        {
            var mark = App.Log.Mark();
            App.Command("tray-menu");
            var menu = Wait.For(() => AppWindows.Menu(Pid, "tray-menu"), "tray menu");
            Ui.Invoke(MenuItem(menu, "Quit Aqua Hub"));
            Expect(App.Process.WaitForExit(15000), "process still running 15 s after Quit");
            Expect(App.Process.ExitCode == 0, $"exit code {App.Process.ExitCode}");
            Expect(App.Log.CrashesSince(mark).Count == 0, "crash during quit");
        });
    });
}
