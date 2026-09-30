namespace AquaHub.E2E.Tests;

/// <summary>Agents: model-output inspector, Run all now, Pause all / Resume all, every per-agent Run button.</summary>
public sealed class A10_AgentsTests : E2ETestBase
{
    public A10_AgentsTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private static readonly Dictionary<string, string> AgentIds = new()
    {
        ["Weather"] = "weather", ["News Scout"] = "news-scout", ["Social Scout"] = "social-scout", ["Market Watch"] = "market-watch",
        ["Prediction Scout"] = "prediction-scout", ["Agenda Scout"] = "events-scout", ["Story Curator"] = "curator",
        ["Social Pulse"] = "pulse", ["Market Analyst"] = "market-analyst", ["Foresight"] = "foresight", ["News Editor"] = "news-editor",
        ["Chief of Staff"] = "briefing", ["Model Warden"] = "model-warden", ["Sentinel"] = "sentinel", ["Housekeeper"] = "keeper",
    };

    private AutomationElement Agents() => GoTo("agents");
    private AutomationElement Outputs() => Ui.WaitFind(GoTo("agents"), Ui.Id("Outputs"), "model output inspector list");
    private AutomationElement? FirstOutput() => Ui.Find(Outputs(), Ui.Type(ControlType.Group));

    /// <summary>Texts of the agent card that hosts a Run button (state, message, last/next run).</summary>
    private List<string> CardTexts(string runButton)
    {
        var button = Ui.Find(PageRoot("agents"), Ui.Button(runButton));
        if (button is null) return new();
        var card = button;
        for (var i = 0; i < 3 && card is not null && Ui.TypeOf(card) != ControlType.DataItem; i++) card = Ui.Parent(card);
        return card is null ? new() : Ui.Texts(card);
    }

    [Fact]
    public void T01_ModelOutputInspector() => Run(() =>
    {
        Check("Agents inspector", "a raw model output is listed (after an interactive command)", () =>
        {
            if (FirstOutput() is null)
            {
                // A phrase outside the command fast path is interpreted by the local model (interactive JSON completion),
                // which the inspector lists.
                App.Command("palette");
                var p = Wait.For(App.TryPalette, "palette");
                Ui.SetValue(Ui.WaitFind(p, Ui.Id("Input"), "input"), "could you make the sound a little quieter");
                var doIt = Wait.For(() => Ui.FindAll(Ui.WaitFind(App.TryPalette() ?? p, Ui.Id("Results"), "results"), Ui.Type(ControlType.ListItem), TreeScope.Children)
                    .FirstOrDefault(li => (Ui.Texts(li).FirstOrDefault() ?? "").StartsWith("Do it:", StringComparison.Ordinal)), "'Do it: …' item");
                Ui.Select(doIt);
                App.EnsureForeground(p, allowActivateCommand: false);
                Input.Press(Pid, VK.Enter);
                Wait.For(() => App.Log.Lines().Any(l => l.Contains("[ai] command via")), "command interpreted by the model", E2EConfig.AiTimeout, 500);
            }
            Wait.For(() => FirstOutput() is not null, "an expander in the model output inspector", TimeSpan.FromSeconds(30));
        });
        Check("Agents inspector", "expander expands to show the raw output and collapses", () =>
        {
            var exp = Wait.For(FirstOutput, "expander");
            Ui.Expand(exp);
            Wait.For(() => FirstOutput() is { } e && Ui.ExpandStateOf(e) == ExpandCollapseState.Expanded && Ui.Texts(e).Count >= 2, "expanded with raw output text");
            Ui.Collapse(FirstOutput()!);
            Wait.For(() => FirstOutput() is { } e && Ui.ExpandStateOf(e) == ExpandCollapseState.Collapsed, "collapsed");
        });
        Check("Agents inspector", "an expanded output stays open (survives the periodic refresh)", () =>
        {
            Ui.Expand(Wait.For(FirstOutput, "expander"));
            Thread.Sleep(17000);
            var again = FirstOutput();
            Expect(again is not null && Ui.ExpandStateOf(again) == ExpandCollapseState.Expanded,
                "The inspector list is rebuilt every 15 s, collapsing whatever the user expanded");
        });
    });

    [Fact]
    public void T02_RunAllNow() => Run(() =>
    {
        Agents();
        Check("Agents", "Run all now → the agents run", () =>
        {
            var mark = App.Log.Mark();
            Ui.Invoke(Ui.WaitButtonWithText(PageRoot("agents"), "Run all now"));
            Wait.For(() => App.Log.Since(mark).Where(l => l.Contains("[agents] ")).Select(l => l.Split("[agents] ")[1].Split(':')[0]).Distinct().Count() >= 8,
                "at least 8 different agents ran", TimeSpan.FromSeconds(90));
        });
    });

    [Fact]
    public void T03_PauseAllAndResumeAll() => Run(() =>
    {
        Agents();
        Check("Agents", "Pause all → label 'Resume all' and nothing runs", () =>
        {
            Ui.Invoke(Ui.WaitButtonWithText(PageRoot("agents"), "Pause all"));
            Ui.WaitButtonWithText(PageRoot("agents"), "Resume all");
            Thread.Sleep(1500);
            var mark = App.Log.Mark();
            Ui.Invoke(Button(PageRoot("agents"), "Run Weather now"));
            Thread.Sleep(4000);
            Expect(!App.Log.Since(mark).Any(l => l.Contains("[agents] weather:")), "an agent ran while all agents were paused");
        });
        Check("Agents", "Resume all → label 'Pause all' and queued work runs", () =>
        {
            var mark = App.Log.Mark();
            Ui.Invoke(Ui.WaitButtonWithText(PageRoot("agents"), "Resume all"));
            Ui.WaitButtonWithText(PageRoot("agents"), "Pause all");
            App.Log.WaitForLine(mark, l => l.Contains("[agents] weather:"), "weather runs after resume", TimeSpan.FromSeconds(20));
        });
    });

    [Fact]
    public void T04_EveryAgentRunButton() => Run(() =>
    {
        var buttons = Ui.FindAllWhere(Agents(), ControlType.Button, n => n.StartsWith("Run ", StringComparison.Ordinal) && n.EndsWith(" now", StringComparison.Ordinal)).Select(Ui.NameOf).ToList();
        Check("Agents", "15 agents with Run buttons", () => Expect(buttons.Count == 15, $"{buttons.Count} run buttons"));
        foreach (var b in buttons)
        {
            var name = b["Run ".Length..^" now".Length];
            var id = AgentIds.GetValueOrDefault(name, name.ToLowerInvariant());
            Check("Agents run", $"'{b}' → agent queued/working/ran", () =>
            {
                var mark = App.Log.Mark();
                Ui.Invoke(Ui.WaitFind(PageRoot("agents"), Ui.Button(b), b));
                // Collectors finish in seconds; AI agents queue behind the single model slot — seeing them queued or working is enough.
                Wait.For(() => App.Log.Since(mark).Any(l => l.Contains($"[agents] {id}:")) ||
                               CardTexts(b).Any(t => t is "Queued" or "Working" or "Working…"),
                    $"{id} queued, working or finished", TimeSpan.FromSeconds(45), 250);
            });
        }
    });
}
