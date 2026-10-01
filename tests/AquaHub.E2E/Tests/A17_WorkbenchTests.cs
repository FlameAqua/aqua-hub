namespace AquaHub.E2E.Tests;

/// <summary>
/// The Workbench: writing a skill by hand (drafting needs the model), switching it off and on, trying it in Ask,
/// deleting it; adding, editing and forgetting memories; the abilities list.
/// </summary>
public sealed class A17_WorkbenchTests : E2ETestBase
{
    public A17_WorkbenchTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private AutomationElement Bench() => GoTo("workbench");

    [Fact]
    public void T01_SkillsCanBeWrittenTriedAndDeleted() => Run(() =>
    {
        Bench();
        Check("Workbench", "Write it myself → editor; Save → the skill is listed", () =>
        {
            Ui.Invoke(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-scratch"), "Write it myself"));
            Ui.SetValue(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-skill-name"), "skill name"), "E2E focus time");
            Ui.SetValue(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-skill-when"), "use it when"), "When I say e2e focus time");
            Ui.SetValue(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-skill-triggers"), "triggers"), "e2e focus time");
            Ui.SetValue(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-skill-instructions"), "instructions"), "Reply with one line: focus mode is on.");
            Ui.Invoke(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-save"), "Save skill"));
            Wait.For(() => Ui.Texts(PageRoot("workbench")).Contains("E2E focus time"), "the skill in the list");
        });
        Check("Workbench", "the skill's switch turns it off and on", () =>
        {
            var toggle = Ui.WaitFind(PageRoot("workbench"), Ui.Name("Use E2E focus time"), "skill switch");
            Ui.SetToggle(toggle, false);
            Wait.For(() => Ui.ToggleStateOf(toggle) == ToggleState.Off, "switched off");
            Ui.SetToggle(toggle, true);
            Wait.For(() => Ui.ToggleStateOf(toggle) == ToggleState.On, "switched on");
        });
        Check("Workbench", "Try it → Ask opens a new chat with the skill's example request", () =>
        {
            var tryIt = Ui.FindAll(PageRoot("workbench"), Ui.Button("Try it in a new chat")).First();
            Ui.Invoke(tryIt);
            ExpectPage("ask");
            Wait.For(() => Ui.AllTexts(PageRoot("ask")).Any(t => t == "e2e focus time"), "the example request in Ask");
            var stop = Ui.Find(PageRoot("ask"), Ui.Id("StopButton"));
            if (stop is not null) Ui.Invoke(stop);
        });
        Check("Workbench", "Delete → confirm → the skill is gone", () =>
        {
            Bench();
            Ui.Invoke(Ui.FindAll(PageRoot("workbench"), Ui.Button("Delete this skill")).First());
            // A message box is owned by the main window, so UI Automation lists it there.
            var dialog = Wait.For(() => AppWindows.WithOwned(Pid).FirstOrDefault(w => Ui.NameOf(w) == "Workbench" && Ui.Find(w, Ui.Button("OK")) is not null), "the confirmation");
            Ui.Invoke(Ui.WaitFind(dialog, Ui.Button("OK"), "OK"));
            Wait.For(() => !Ui.Texts(PageRoot("workbench")).Contains("E2E focus time"), "the skill removed");
        });
    });

    [Fact]
    public void T02_MemoryAndAbilities() => Run(() =>
    {
        Bench();
        Check("Workbench", "Memory: add → listed; forget → gone", () =>
        {
            Ui.Select(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-tab-memory"), "Memory tab"));
            Ui.SetValue(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-memory-input"), "memory box"), "E2E: my spare key is in the blue drawer");
            Ui.Invoke(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-remember"), "Remember"));
            Wait.For(() => Ui.Texts(PageRoot("workbench")).Contains("E2E: my spare key is in the blue drawer"), "the memory listed");
            Ui.Invoke(Ui.FindAll(PageRoot("workbench"), Ui.Button("Forget this")).First());
            Wait.For(() => !Ui.Texts(PageRoot("workbench")).Contains("E2E: my spare key is in the blue drawer"), "the memory forgotten");
        });
        Check("Workbench", "What Aqua can do: groups and tools listed", () =>
        {
            Ui.Select(Ui.WaitFind(PageRoot("workbench"), Ui.Id("wb-tab-abilities"), "abilities tab"));
            Wait.For(() => Ui.Texts(PageRoot("workbench")).Contains("Your feeds") && Ui.Texts(PageRoot("workbench")).Contains("The web"), "the tool groups");
        });
    });
}
