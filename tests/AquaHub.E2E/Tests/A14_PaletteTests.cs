namespace AquaHub.E2E.Tests;

/// <summary>Command palette (opened with --palette and Ctrl+K): filtering, arrow keys + Enter, Tab to ask inline.</summary>
public sealed class A14_PaletteTests : E2ETestBase
{
    public A14_PaletteTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private AutomationElement Open(bool viaCtrlK = false)
    {
        if (App.TryPalette() is { } p) return p;
        if (viaCtrlK) KeysToMain(VK.Control, VK.K);
        else App.Command("palette");
        var palette = Wait.For(App.TryPalette, "palette window");
        Ui.WaitFind(palette, Ui.Id("Input"), "palette input");
        return palette;
    }

    private List<string> ResultTitles(AutomationElement palette) =>
        Ui.FindAll(Ui.WaitFind(palette, Ui.Id("Results"), "results"), Ui.Type(ControlType.ListItem), TreeScope.Children)
          .Select(li => Ui.Texts(li).FirstOrDefault() ?? "").ToList();

    private AutomationElement Type(string text, bool viaCtrlK = false)
    {
        var p = Open(viaCtrlK);
        Ui.SetValue(Ui.WaitFind(p, Ui.Id("Input"), "input"), text);
        Thread.Sleep(300);
        return p;
    }

    private void Keys(AutomationElement palette, params VK[] keys)
    {
        App.EnsureForeground(palette, allowActivateCommand: false);
        foreach (var k in keys) Input.Press(Pid, k);
    }

    [Fact]
    public void T01_OpenAndClose() => Run(() =>
    {
        Check("Palette", "--palette opens it; Esc closes", () =>
        {
            var p = Open();
            Expect(ResultTitles(p).Count > 0, "no default results");
            Keys(p, VK.Escape);
            Wait.For(() => App.TryPalette() is null, "closed");
        });
        Check("Palette", "Ctrl+K in the main window opens it; Esc closes", () =>
        {
            GoTo("today");
            var p = Open(viaCtrlK: true);
            Keys(p, VK.Escape);
            Wait.For(() => App.TryPalette() is null, "closed");
        });
        Check("Palette", "--palette again toggles it closed without leaking hidden windows", () =>
        {
            Open();
            App.Command("palette");
            Wait.For(() => App.TryPalette() is null, "hidden");
            var hidden = AppWindows.CountWin32Windows(Pid, AppWindows.PaletteTitle, includeHidden: true);
            Expect(hidden == 0, $"{hidden} palette window(s) remain alive but hidden after toggling it off (never closed)");
        });
    });

    [Fact]
    public void T02_TypingFilters() => Run(() =>
    {
        Check("Palette", "typing 'mark' ranks Markets", () =>
        {
            var p = Type("mark");
            Wait.For(() => ResultTitles(p).Contains("Markets"), "Markets in results");
        });
        Check("Palette", "nonsense only offers Ask / Do it", () =>
        {
            var p = Type("zzqxv");
            Wait.For(() => ResultTitles(p).All(t => t.StartsWith("Ask Aqua:", StringComparison.Ordinal) || t.StartsWith("Do it:", StringComparison.Ordinal)), "only Ask/Do it items");
        });
        Check("Palette", "list items have readable accessible names", () =>
        {
            var p = Open();
            var names = Ui.FindAll(Ui.WaitFind(p, Ui.Id("Results"), "results"), Ui.Type(ControlType.ListItem), TreeScope.Children).Select(Ui.NameOf).ToList();
            Expect(names.All(n => !n.StartsWith("AquaHub.", StringComparison.Ordinal)), $"results are announced as '{names.FirstOrDefault()}'");
        });
        if (App.TryPalette() is { } open) Keys(open, VK.Escape);
    });

    [Fact]
    public void T03_ArrowKeysAndEnterRunItems() => Run(() =>
    {
        Check("Palette", "'News' + Enter → News page", () =>
        {
            GoTo("today");
            var p = Type("News");
            Wait.For(() => ResultTitles(p).FirstOrDefault() == "News", "News first");
            Keys(p, VK.Enter);
            Wait.For(() => App.TryPalette() is null, "palette closed");
            ExpectPage("news");
        });
        Check("Palette", "Down / Up change the selection; Enter runs the selected page", () =>
        {
            var p = Type("settings");
            var list = Ui.WaitFind(p, Ui.Id("Results"), "results");
            AutomationElement Selected() => Ui.FindAll(list, Ui.Type(ControlType.ListItem), TreeScope.Children).First(Ui.IsSelected);
            Wait.For(() => ResultTitles(p).FirstOrDefault() == "Settings", "Settings first");
            var first = Ui.Texts(Selected()).First();
            Keys(p, VK.Down);
            Wait.For(() => Ui.Texts(Selected()).First() != first, "selection moved down");
            Keys(p, VK.Up);
            Wait.For(() => Ui.Texts(Selected()).First() == first, "selection moved back up");
            Keys(p, VK.Enter);
            ExpectPage("settings");
        });
        Check("Palette", "'Calculator' + Enter → journal launch calculator", () =>
        {
            var p = Type("Calculator");
            Wait.For(() => ResultTitles(p).FirstOrDefault() == "Calculator", "Calculator first");
            ExpectJournal("launch", () => Keys(p, VK.Enter), d => d == "calculator");
        });
        Check("Palette", "'Focus' + Enter → scene steps journaled", () =>
        {
            var p = Type("Focus");
            Wait.For(() => ResultTitles(p).FirstOrDefault() == "Focus", "Focus scene first");
            var mark = App.Journal.Mark();
            var logMark = App.Log.Mark();
            Keys(p, VK.Enter);
            App.Journal.WaitFor(mark, "volume", d => d == "25");
            App.Journal.WaitFor(mark, "launch", d => d == "vscode");
            App.Settings.WaitForBool("notifications.doNotDisturb", true);
            // The (hidden) palette stays alive until the scene finishes; let it go before opening the next one.
            App.Log.WaitForLine(logMark, l => l.Contains("Ran scene Focus"), "scene finished", TimeSpan.FromSeconds(20));
        });
        Check("Palette", "'Do not disturb' toggle item → DND off again", () =>
        {
            var p = Type("turn notifications back on");
            Wait.For(() => ResultTitles(p).FirstOrDefault() == "Turn notifications back on", "'Turn notifications back on' first");
            Keys(p, VK.Enter);
            App.Settings.WaitForBool("notifications.doNotDisturb", false);
        });
        Check("Palette", "mouse click on a result runs it", () =>
        {
            GoTo("today");
            var p = Type("Agents");
            Wait.For(() => ResultTitles(p).FirstOrDefault() == "Agents", "Agents first");
            var item = Ui.FindAll(Ui.WaitFind(p, Ui.Id("Results"), "results"), Ui.Type(ControlType.ListItem), TreeScope.Children).First();
            App.EnsureForeground(p, allowActivateCommand: false);
            Input.ClickElement(Pid, item);
            ExpectPage("agents");
        });
    });

    [Fact]
    public void T04_TabAsksInlineAndContinueInAsk() => Run(() =>
    {
        const string q = "What's the weather like in Dublin today?";
        Check("Palette", "Tab → inline answer from the local model", () =>
        {
            var p = Type(q);
            Keys(p, VK.Tab);
            var answer = Ui.WaitFind(p, Ui.Id("AnswerText"), "answer text");
            Wait.For(() => Ui.NameOf(Ui.WaitFind(App.TryPalette() ?? p, Ui.Id("AnswerText"), "answer")).Length > 20 &&
                           !Ui.NameOf(Ui.WaitFind(App.TryPalette() ?? p, Ui.Id("AnswerLabel"), "label")).Contains("thinking"),
                "inline answer", E2EConfig.AiTimeout, 500);
            var text = Ui.NameOf(Ui.WaitFind(App.TryPalette()!, Ui.Id("AnswerText"), "answer"));
            Step("   inline answer: " + (text.Length > 140 ? text[..140] + "…" : text));
            Expect(!text.StartsWith("Sorry", StringComparison.Ordinal) && !text.StartsWith("The local model isn't available", StringComparison.Ordinal), text);
            _ = answer;
        });
        Check("Palette", "'Continue in Ask' → Ask page with the question", () =>
        {
            var p = App.TryPalette() ?? throw new InvalidOperationException("palette closed before 'Continue in Ask'");
            Ui.Invoke(Ui.WaitButtonWithText(p, "Continue in Ask"));
            Wait.For(() => App.TryPalette() is null, "palette closed");
            ExpectPage("ask");
            Wait.For(() => Ui.AllTexts(PageRoot("ask")).Contains(q), "question in the Ask chat");
            if (Ui.Find(PageRoot("ask"), Ui.Id("StopButton")) is { } stop) Ui.Invoke(stop);
        });
    });
}
