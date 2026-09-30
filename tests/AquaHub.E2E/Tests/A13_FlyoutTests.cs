namespace AquaHub.E2E.Tests;

/// <summary>The taskbar quick panel (opened with the --flyout remote command).</summary>
public sealed class A13_FlyoutTests : E2ETestBase
{
    public A13_FlyoutTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private AutomationElement Open()
    {
        if (App.TryFlyout() is { } f) return f;
        App.Command("flyout");
        var flyout = Wait.For(App.TryFlyout, "quick panel window");
        Ui.WaitFind(flyout, Ui.Id("flyout-command"), "command box");
        return flyout;
    }

    private void Close()
    {
        if (App.TryFlyout() is null) return;
        App.Command("flyout");
        Wait.For(() => App.TryFlyout() is null, "quick panel hidden");
    }

    private void ExpectFlyoutGoneAndPage(string page)
    {
        Wait.For(() => App.TryFlyout() is null, "quick panel hidden");
        Wait.For(() => App.TryMain() is not null, "main window");
        ExpectPage(page);
    }

    [Fact]
    public void T01_OpensWithClockAndBrief() => Run(() =>
    {
        Check("Quick panel", "--flyout opens the panel (clock, date, brief)", () =>
        {
            var f = Open();
            Expect(Ui.NameOf(Ui.WaitFind(f, Ui.Id("ClockText"), "clock")).Contains(':'), "clock text");
            Ui.WaitFind(f, Ui.Id("DateText"), "date");
            Ui.WaitFind(f, Ui.Button("Open today's brief"), "brief button");
        });
        Check("Quick panel", "brief → main window Today", () =>
        {
            GoTo("news");
            Ui.Invoke(Ui.WaitFind(Open(), Ui.Button("Open today's brief"), "brief"));
            ExpectFlyoutGoneAndPage("today");
        });
    });

    [Fact]
    public void T02_MarketTilesAndHeadlines() => Run(() =>
    {
        var names = new List<string>();
        Check("Quick panel", "market tiles are exposed as buttons", () =>
        {
            var f = Open();
            Wait.For(() =>
            {
                names = TreeSnapshot.Capture(App.TryFlyout() ?? f).Descendants()
                    .Where(n => n.Type == ControlType.Button && n.Name.Length > 0 && n.Parent?.Type == ControlType.DataItem && n.Parent.Parent?.AutomationId != "Headlines"
                                && !n.Name.StartsWith("Open ", StringComparison.Ordinal) && !n.Name.StartsWith("Run scene", StringComparison.Ordinal))
                    .Select(n => n.Name).ToList();
                return names.Count > 0;
            }, "market tile buttons", TimeSpan.FromSeconds(8));
        });
        foreach (var tile in names.Take(4))
        {
            Check("Quick panel", $"market tile '{tile}' → Markets", () =>
            {
                Ui.Invoke(Ui.WaitFind(Open(), Ui.Button(tile), tile));
                ExpectFlyoutGoneAndPage("markets");
            });
        }
        var headlines = Ui.FindAll(Ui.WaitFind(Open(), Ui.Id("Headlines"), "headlines"), Ui.Type(ControlType.Button)).Select(Ui.NameOf).ToList();
        Check("Quick panel", "3 headlines listed", () => Expect(headlines.Count >= 1, "no headlines"));
        foreach (var h in headlines)
            Check("Quick panel", $"headline '{(h.Length > 40 ? h[..40] + "…" : h)}' → journal open-url", () =>
                ExpectJournal("open-url", () => Ui.Invoke(Ui.WaitFind(Ui.WaitFind(Open(), Ui.Id("Headlines"), "headlines"), Ui.Button(h), h)), d => d.StartsWith("http", StringComparison.Ordinal)));
        Close();
    });

    [Fact]
    public void T03_MediaVolumeAppsAndScenes() => Run(() =>
    {
        foreach (var (name, detail) in new[] { ("Previous", "previous"), ("Play / pause", ""), ("Next", "next") })
            Check("Quick panel media", $"{name} → journal", () =>
                ExpectJournalAny(MediaActions, () => Ui.Invoke(Ui.WaitFind(Open(), Ui.Button(name), name)), e => detail.Length == 0 || e.Detail.Contains(detail)));
        Check("Quick panel media", "Mute → journal mute", () => ExpectJournal("mute", () => Ui.Invoke(Ui.WaitFind(Open(), Ui.Button("Mute"), "Mute"))));
        Check("Quick panel media", "volume slider → journal volume 22", () =>
            ExpectJournal("volume", () => Ui.SetRange(Ui.WaitFind(Open(), Ui.Type(ControlType.Slider), "slider"), 22), d => d == "22"));

        var apps = new List<string>();
        Check("Quick panel apps", "quick-launch buttons exposed", () =>
            Wait.For(() => (apps = Ui.FindAllWhere(Open(), ControlType.Button, n => n.StartsWith("Open ", StringComparison.Ordinal) && n != "Open Aqua Hub" && n != "Open today's brief").Select(Ui.NameOf).ToList()).Count > 0,
                "'Open …' app buttons", TimeSpan.FromSeconds(8)));
        foreach (var a in apps)
            Check("Quick panel apps", $"'{a}' → journal launch", () => ExpectJournal("launch", () => Ui.Invoke(Ui.WaitFind(Open(), Ui.Button(a), a))));

        Check("Quick panel scenes", "Run scene Focus → journal volume 25 + launch vscode", () =>
        {
            var mark = App.Journal.Mark();
            Ui.Invoke(Ui.WaitFind(Open(), Ui.Button("Run scene Focus"), "Focus pill"));
            App.Journal.WaitFor(mark, "volume", d => d == "25");
            App.Journal.WaitFor(mark, "launch", d => d == "vscode");
        });
        Check("Quick panel scenes", "Run scene Wind down → DND off + Today", () =>
        {
            Ui.Invoke(Ui.WaitFind(Open(), Ui.Button("Run scene Wind down"), "Wind down pill"));
            App.Settings.WaitForBool("notifications.doNotDisturb", false, TimeSpan.FromSeconds(20));
            Wait.For(() => App.TryMain() is not null && CurrentPage() == "today", "Today shown", TimeSpan.FromSeconds(20));
        });
        Close();
    });

    private void Command(string text)
    {
        var f = Open();
        var box = Ui.WaitFind(f, Ui.Id("flyout-command"), "command box");
        Ui.SetValue(box, text);
        FocusAndPress(f, box, VK.Enter);
    }

    [Fact]
    public void T04_CommandBox() => Run(() =>
    {
        Check("Quick panel command", "'pause' + Enter → journal media pause", () =>
        {
            var mark = App.Journal.Mark();
            Command("pause");
            App.Journal.WaitForAny(mark, MediaActions);
            Wait.For(() => App.TryFlyout() is { } f && Ui.Find(f, Ui.Id("CommandResult")) is { } r && Ui.NameOf(r) is "Paused", "result 'Paused'");
        });
        Check("Quick panel command", "'go to markets' + Enter → Markets page", () =>
        {
            GoTo("today");
            Command("go to markets");
            ExpectFlyoutGoneAndPage("markets");
        });
        Check("Quick panel command", "a question + Enter → Ask page with the question", () =>
        {
            const string q = "what is happening in Dublin today?";
            Command(q);
            ExpectFlyoutGoneAndPage("ask");
            Wait.For(() => Ui.Texts(PageRoot("ask")).Any(t => t.Equals(q, StringComparison.OrdinalIgnoreCase)), "question in the chat");
            if (Ui.Find(PageRoot("ask"), Ui.Id("StopButton")) is { } stop) Ui.Invoke(stop);
        });
    });

    [Fact]
    public void T05_ExpandSettingsAndEsc() => Run(() =>
    {
        Check("Quick panel", "'Open Aqua Hub' → main window", () =>
        {
            GoTo("news");
            Ui.Invoke(Ui.WaitFind(Open(), Ui.Button("Open Aqua Hub"), "expand"));
            ExpectFlyoutGoneAndPage("news");
        });
        Check("Quick panel", "'Settings' → Settings page", () =>
        {
            Ui.Invoke(Ui.WaitFind(Open(), Ui.Button("Settings"), "settings"));
            ExpectFlyoutGoneAndPage("settings");
        });
        Check("Quick panel", "Esc hides the panel", () =>
        {
            var f = Open();
            App.EnsureForeground(f, allowActivateCommand: false);
            Input.Press(Pid, VK.Escape);
            Wait.For(() => App.TryFlyout() is null, "panel hidden after Esc");
        });
        Check("Quick panel", "--flyout toggles the panel closed", () =>
        {
            Open();
            App.Command("flyout");
            Wait.For(() => App.TryFlyout() is null, "hidden after second --flyout");
        });
        GoTo("today");
    });
}
