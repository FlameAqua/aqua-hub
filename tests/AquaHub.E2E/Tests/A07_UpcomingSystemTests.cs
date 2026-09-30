namespace AquaHub.E2E.Tests;

/// <summary>Upcoming (calendar shortcuts, prediction cards) and This PC (processes, Pause/Resume AI, Free VRAM).</summary>
public sealed class A07_UpcomingSystemTests : E2ETestBase
{
    public A07_UpcomingSystemTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private void ExpectAgendaSettings()
    {
        ExpectPage("settings");
        var settings = PageRoot("settings");
        Ui.WaitFind(settings, Ui.Id("NewCalUrl"), "calendar URL box (Agenda section)");
        var selected = Ui.FindAll(Ui.WaitFind(settings, Ui.Id("Sections"), "sections"), Ui.Type(ControlType.ListItem)).FirstOrDefault(Ui.IsSelected);
        Expect(selected is not null && Ui.Texts(selected).Contains("Agenda"), "Agenda section should be selected");
    }

    [Fact]
    public void T01_AddCalendarButtonsOpenAgendaSettings() => Run(() =>
    {
        Check("Upcoming", "'Add calendar' → Settings › Agenda", () =>
        {
            Ui.Invoke(Ui.WaitButtonWithText(GoTo("upcoming"), "Add calendar"));
            ExpectAgendaSettings();
        });
        Check("Upcoming", "'Add a calendar' (empty agenda card) → Settings › Agenda", () =>
        {
            var button = Ui.Find(GoTo("upcoming"), Ui.Button("Add a calendar"));
            if (button is null)
            {
                Step("   (card hidden: calendars already configured)");
                return;
            }
            Ui.Invoke(button);
            ExpectAgendaSettings();
        });
    });

    [Fact]
    public void T02_PredictionCardsOpenTheMarket() => Run(() =>
    {
        var page = GoTo("upcoming");
        var cards = Ui.FindAll(page, Ui.Type(ControlType.Button))
            .Where(b => b.Current.HelpText == "Open on the market's website").Select(Ui.NameOf).Distinct().ToList();
        Check("Upcoming", "prediction cards present", () => Expect(cards.Count > 0, "no prediction cards"));
        foreach (var name in cards)
            Check("Upcoming predictions", $"card '{(name.Length > 40 ? name[..40] + "…" : name)}' → journal open-url", () =>
                ExpectJournal("open-url", () => Ui.Invoke(Ui.WaitFind(PageRoot("upcoming"), Ui.Button(name), name)), d => d.StartsWith("https://", StringComparison.Ordinal)));
    });

    [Fact]
    public void T03_SystemProcessesAndAiControls() => Run(() =>
    {
        var page = GoTo("system");
        Check("This PC", "top processes list populated", () =>
            Wait.For(() => Ui.FindAll(PageRoot("system"), Ui.Type(ControlType.DataItem)).Count(d => Ui.Texts(d).Any(t => t.EndsWith('%'))) >= 3,
                "at least 3 process rows (name · CPU% · memory)", TimeSpan.FromSeconds(20)));
        Check("This PC", "machine, memory and network figures populated", () =>
        {
            var texts = Ui.Texts(PageRoot("system"));
            Expect(texts.Any(t => t.Contains("Windows", StringComparison.Ordinal)), "machine / OS line missing");
            Expect(texts.Any(t => t.EndsWith("GB in use", StringComparison.Ordinal)), "memory in-use figure missing");
            Expect(texts.Any(t => t.EndsWith("/s", StringComparison.Ordinal)), "network rate missing");
        });
        Check("This PC", "Pause AI → journal unload-model, label becomes Resume AI", () =>
        {
            ExpectJournal("unload-model", () => Ui.Invoke(Button(PageRoot("system"), "Pause AI")));
            Button(PageRoot("system"), "Resume AI");
            Wait.For(() => Ui.NameOf(Ui.WaitFind(Main, Ui.Id("AiState"), "AI state")).Contains("Paused by you"), "nav AI status says 'Paused by you'");
        });
        Check("This PC", "Resume AI → label back to Pause AI", () =>
        {
            Ui.Invoke(Button(PageRoot("system"), "Resume AI"));
            Button(PageRoot("system"), "Pause AI");
        });
        Check("This PC", "Free VRAM → journal unload-model (disabled when no model server answers)", () =>
        {
            var free = Button(PageRoot("system"), "Unload models from the GPU now");
            if (Ui.IsEnabled(free)) ExpectJournal("unload-model", () => Ui.Invoke(free));
        });
        Check("This PC", "Doctor's toolkit → journal maintenance-tool (nothing opens in the sandbox)", () =>
            ExpectJournal("maintenance-tool", () => Ui.Invoke(Button(PageRoot("system"), "Reliability Monitor")), d => d.StartsWith("Reliability Monitor", StringComparison.Ordinal)));
        Check("This PC", "Storage › Open C: → journal open-folder", () =>
            ExpectJournal("open-folder", () => Ui.Invoke(Button(PageRoot("system"), "Open C:")), d => d.StartsWith("C:", StringComparison.OrdinalIgnoreCase)));
        _ = page;
    });
}
