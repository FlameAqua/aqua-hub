namespace AquaHub.E2E.Tests;

/// <summary>
/// Updates you can't miss, with a pretend release (<c>--demo-update</c>: nothing is downloaded or installed): the
/// banner offers it, shows the download and Restart now, Settings carries a dot, and Not now puts the banner away.
/// </summary>
public sealed class A18_UpdateTests : E2ETestBase
{
    public A18_UpdateTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    protected override SessionOptions Options => new() { ExtraArgs = new[] { "--demo-update" } };

    private AutomationElement Action => Ui.WaitFind(Main, Ui.Id("update-bar-action"), "banner button");
    private string BarText() => Ui.Find(Main, Ui.Id("update-bar-text")) is { } t ? Ui.NameOf(t) : "";
    private string SettingsHelp() => Ui.WaitFind(Main, Ui.Id("nav-settings"), "Settings in the sidebar").Current.HelpText;

    [Fact]
    [Trait("Category", "Smoke")]
    public void T01_TheBannerTakesAnUpdateFromOfferToRestart() => Run(() =>
    {
        GoTo("today");
        Check("Updates", "a new version shows a banner and a dot on Settings", () =>
        {
            Wait.For(() => BarText().Contains("9.9.9 is available", StringComparison.Ordinal), "banner offering 9.9.9");
            Expect(Ui.NameOf(Action) == "Download", "the banner offers Download");
            Expect(SettingsHelp().Contains("9.9.9 is available", StringComparison.Ordinal), "Settings says an update is available");
        });
        Check("Updates", "Download shows its progress, then Restart now", () =>
        {
            Ui.Invoke(Action);
            Wait.For(() => BarText().StartsWith("Downloading Aqua Hub 9.9.9", StringComparison.Ordinal), "download under way");
            Expect(Ui.Find(Main, Ui.Id("update-bar-progress")) is not null, "a progress bar while it downloads");
            Wait.For(() => Ui.NameOf(Action) == "Restart now", "Restart now", TimeSpan.FromSeconds(20));
            Expect(BarText().Contains("9.9.9 is ready", StringComparison.Ordinal), "the banner says it's ready");
            Expect(SettingsHelp().Contains("ready to install", StringComparison.Ordinal), "Settings says it's ready to install");
        });
        Check("Updates", "Restart now hands over to the installer (a pretend release installs nothing)", () =>
        {
            ExpectJournal("update-restart", () => Ui.Invoke(Action), d => d == "9.9.9");
            Wait.For(() => BarText().Contains("the updater didn't start", StringComparison.Ordinal), "the banner says nothing was installed");
            Expect(App.IsAlive, "Aqua keeps running");
        });
        Check("Updates", "What's new opens Settings › About › Updates", () =>
        {
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("update-bar-notes"), "What's new"));
            var page = PageRoot("settings");
            Expect(Ui.NameOf(Ui.WaitFind(page, Ui.Id("update-action"), "the Updates card's button")) == "Restart now", "the card offers Restart now too");
        });
        Check("Updates", "Not now puts the banner away; the dot on Settings stays", () =>
        {
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("update-bar-dismiss"), "Not now"));
            Wait.For(() => BarText().Length == 0, "banner put away");
            Expect(SettingsHelp().Contains("ready to install", StringComparison.Ordinal), "Settings still says an update is ready");
        });
    });
}
