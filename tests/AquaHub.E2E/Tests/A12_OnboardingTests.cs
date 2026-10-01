using System.Text;

namespace AquaHub.E2E.Tests;

/// <summary>First run (empty profile): the five onboarding steps, their inputs, Back/Next and Finish.</summary>
public sealed class A12_OnboardingTests : E2ETestBase
{
    public A12_OnboardingTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    protected override SessionOptions Options => new() { EmptyProfile = true };

    private AutomationElement Next => Ui.WaitFind(Main, Ui.Id("NextButton"), "Next button");
    private AutomationElement? Back => Ui.Find(Main, Ui.Id("BackButton"));

    private void GoNext(string expectHeading)
    {
        Ui.Invoke(Next);
        Ui.WaitFind(Main, Ui.Text(expectHeading), $"step heading '{expectHeading}'");
    }

    [Fact]
    public void T01_FullOnboardingFlow() => Run(() =>
    {
        Check("Onboarding", "first run shows the welcome step", () =>
        {
            Ui.WaitFind(Main, Ui.Text("Welcome to Aqua Hub"), "welcome heading", TimeSpan.FromSeconds(20));
            Expect(Ui.NameOf(Next) == "Get started", $"primary button is '{Ui.NameOf(Next)}'");
            Expect(Back is null || Ui.IsOffscreen(Back!), "Back button should be hidden on step 1");
        });
        Check("Onboarding", "Esc does not dismiss onboarding", () =>
        {
            PressInMain(VK.Escape);
            Thread.Sleep(600);
            Ui.WaitFind(Main, Ui.Text("Welcome to Aqua Hub"), "still on welcome");
        });
        Check("Onboarding", "Get started → step 2 (location)", () => GoNext("Where are you?"));
        Check("Onboarding", "a new profile has no place until one is chosen", () =>
        {
            var chosen = Ui.NameOf(Ui.WaitFind(Main, Ui.Id("CityChosen"), "chosen city"));
            Expect(chosen.StartsWith("No place yet", StringComparison.Ordinal), $"a new profile already has a place: '{chosen}'");
        });
        Check("Onboarding", "'Use my location' (simulated in dry runs) → a town is chosen", () =>
        {
            ExpectJournal("locate", () => Ui.Invoke(Ui.WaitFind(Main, Ui.Id("use-my-location"), "Use my location button")));
            Wait.For(() => Ui.NameOf(Ui.WaitFind(Main, Ui.Id("CityChosen"), "chosen city")).StartsWith("Cork", StringComparison.Ordinal), "CityChosen = Cork…");
        });

        AutomationElement? Result(string city) =>
            Ui.FindAll(Ui.Find(Main, Ui.Id("CityResults")) ?? Main, Ui.Type(ControlType.ListItem)).FirstOrDefault(li => Ui.NameOf(li).StartsWith(city, StringComparison.Ordinal));
        Check("Onboarding", "city search 'Galway' + pick → chosen city shown", () =>
        {
            App.EnsureForeground(Main);
            Ui.SetValue(Ui.WaitFind(Main, Ui.Id("City"), "city box"), "Galway");
            var item = Wait.For(() => Result("Galway"), "Galway result", TimeSpan.FromSeconds(20));
            App.EnsureForeground(Main);
            Input.ClickElement(Pid, item);
            Wait.For(() => Ui.NameOf(Ui.WaitFind(Main, Ui.Id("CityChosen"), "chosen city")).StartsWith("Galway", StringComparison.Ordinal), "CityChosen = Galway…");
        });
        Check("Onboarding", "units chips (°F · mph)", () =>
        {
            var imperial = Ui.WaitFind(Main, Ui.Id("Imperial"), "imperial chip");
            Ui.Select(imperial);
            Wait.For(() => Ui.IsSelected(imperial) && !Ui.IsSelected(Ui.WaitFind(Main, Ui.Id("Metric"), "metric")), "imperial selected");
        });
        Check("Onboarding", "Back → step 1, Next → step 2 keeps the city", () =>
        {
            Ui.Invoke(Ui.WaitFind(Main, Ui.Id("BackButton"), "Back"));
            Ui.WaitFind(Main, Ui.Text("Welcome to Aqua Hub"), "welcome again");
            GoNext("Where are you?");
            Expect(Ui.NameOf(Ui.WaitFind(Main, Ui.Id("CityChosen"), "chosen")).StartsWith("Galway", StringComparison.Ordinal), "city lost after Back/Next");
        });
        Check("Onboarding", "Next → step 3 (interests)", () => GoNext("What do you care about?"));
        Check("Onboarding", "interest chips toggle ('sport' on, 'AI' off)", () =>
        {
            var sport = Ui.WaitFind(Main, Ui.And(Ui.Type(ControlType.Button), Ui.Name("sport")), "sport chip");
            if (Ui.ToggleStateOf(sport) != ToggleState.On) Ui.Toggle(sport);
            var ai = Ui.WaitFind(Main, Ui.And(Ui.Type(ControlType.Button), Ui.Name("AI")), "AI chip");
            if (Ui.ToggleStateOf(ai) == ToggleState.On) Ui.Toggle(ai);
            Expect(Ui.ToggleStateOf(Ui.WaitFind(Main, Ui.And(Ui.Type(ControlType.Button), Ui.Name("sport")), "sport")) == ToggleState.On, "sport not on");
        });
        Check("Onboarding", "the chosen place brings its communities; the chip editor adds 'connacht'", () =>
        {
            Ui.WaitFind(Main, Ui.Button("Remove Galway"), "the place's own subreddit (r/Galway)");
            var box = Ui.WaitFind(Ui.WaitFind(Main, Ui.Id("Subs"), "subs editor"), Ui.Type(ControlType.Edit), "subs input");
            Ui.SetValue(box, "r/connacht");
            FocusAndPress(Main, box, VK.Enter);
            Ui.WaitFind(Main, Ui.Button("Remove connacht"), "connacht chip");
        });
        Check("Onboarding", "watch chip editor adds 'TSLA'", () =>
        {
            var box = Ui.WaitFind(Ui.WaitFind(Main, Ui.Id("Watch"), "watch editor"), Ui.Type(ControlType.Edit), "watch input");
            Ui.SetValue(box, "tsla");
            FocusAndPress(Main, box, VK.Enter);
            Ui.WaitFind(Main, Ui.Button("Remove TSLA"), "TSLA chip");
        });
        Check("Onboarding", "Next → step 4 shows the local AI status", () =>
        {
            GoNext("Your private AI");
            Wait.For(() => Ui.NameOf(Ui.WaitFind(Main, Ui.Id("AiTitle"), "AI title")) != "Checking…", "AI check finished", TimeSpan.FromSeconds(20));
            var title = Ui.NameOf(Ui.WaitFind(Main, Ui.Id("AiTitle"), "AI title"));
            Step("   AI: " + title);
            Expect(title.StartsWith("Ready — using", StringComparison.Ordinal) || title.StartsWith("No local model", StringComparison.Ordinal), title);
        });
        Check("Onboarding", "'Pause AI … gaming' switch off", () =>
        {
            var sw = Ui.WaitFind(Main, Ui.Id("PauseGaming"), "pause gaming switch");
            Ui.SetToggle(sw, false);
        });
        Check("Onboarding", "Next → step 5 with Finish", () =>
        {
            GoNext("You're all set");
            Expect(Ui.NameOf(Next) == "Finish", $"button is '{Ui.NameOf(Next)}'");
            Ui.SetToggle(Ui.WaitFind(Main, Ui.Id("StartWithWindows"), "start with windows"), true);
        });
        Check("Onboarding", "Finish → overlay closes, settings saved, autostart journaled", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Next);
            Wait.For(() => Ui.Find(Main, Ui.Id("NextButton")) is null, "onboarding overlay closed");
            App.Settings.WaitForBool("onboardingComplete", true);
            App.Journal.WaitFor(mark, "autostart", d => d == "True");
            var s = App.Settings;
            Expect(s.GetString("location.city") == "Galway", "city " + s.GetString("location.city"));
            Expect(s.GetString("location.units") == "imperial", "units " + s.GetString("location.units"));
            Expect(s.GetStrings("news.interests").Contains("sport") && !s.GetStrings("news.interests").Contains("AI"), "interests " + string.Join(",", s.GetStrings("news.interests")));
            var subs = s.GetStrings("social.subreddits");
            Expect(subs.Contains("connacht") && subs.Contains("Galway") && subs.Contains("ireland"), "subreddits " + string.Join(",", subs));
            Expect(s.GetString("location.country") == "IE" && s.GetString("location.timezone") == "Europe/Dublin", "the place's country and time zone");
            Expect(s.Symbols("markets.watchlist").Contains("TSLA"), "watchlist " + string.Join(",", s.Symbols("markets.watchlist")));
            Expect(s.GetBool("ai.pauseWhenFullscreen") == false, "pauseWhenFullscreen");
            Expect(s.GetBool("general.launchAtStartup") == true, "launchAtStartup");
            PageRoot("today");
        });
        Check("Onboarding", "first-run apps and scenes survive Finish", () =>
        {
            Thread.Sleep(1500);
            var apps = App.Settings.Count("apps");
            var scenes = App.Settings.Count("scenes");
            Step($"   apps={apps} scenes={scenes}");
            Expect(apps > 0 && scenes >= 4, $"Finishing onboarding wiped the first-run defaults: {apps} apps, {scenes} scenes in settings.json");
        });
        Check("Onboarding", "settings.json is written as proper UTF-8 (Tánaiste, Dáil)", () =>
        {
            var text = File.ReadAllText(App.Settings.FilePath, Encoding.UTF8);
            Expect(text.Contains("Tánaiste") && text.Contains("Dáil") && !text.Contains("Ã"), "non-ASCII defaults are mangled in settings.json");
        });
    });
}
