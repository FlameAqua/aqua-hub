namespace AquaHub.E2E.Tests;

[Trait("Execution", "Offline")]
[Trait("Component", "Search")]
public sealed class FileSearchComponentTests(AppFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    protected override SessionOptions Options => new() { Offline = true };
    private AutomationElement Field(string id) => Ui.WaitFind(Main, Ui.Id(id), id);

    [Fact]
    public void T01_EmptyIndexAndFilters() => Run(() =>
    {
        var page = GoTo("search");
        Ui.SetValue(Field("search-query"), "semester notes");
        Wait.For(() => Ui.Texts(page).Contains("Add a folder to start searching."), "empty search state");
        Ui.Invoke(Field("search-filters"));
        Assert.NotNull(Ui.WaitFind(page, Ui.Name("File extension"), "extension filter"));
    });

    [Fact]
    public void T03_ApproveSearchRevealAndRevoke() => Run(() =>
    {
        var folder = Directory.CreateDirectory(Path.Combine(E2EConfig.RunDir, "Local notes")).FullName;
        File.WriteAllText(Path.Combine(folder, "Workshop.txt"), "The ceramics kiln cools overnight.");
        var page = GoTo("search");
        Ui.Invoke(Field("search-add"));
        var dialog = Wait.For(() => AppWindows.WithOwned(Pid).FirstOrDefault(w => Ui.NameOf(w) == "Choose a folder for Search"), "owned folder picker");
        var entry = Ui.WaitFind(dialog, Ui.And(Ui.Type(ControlType.Edit), Ui.Id("1148")), "folder path");
        Ui.SetValue(entry, folder);
        Ui.Invoke(Ui.WaitFind(dialog, Ui.Button("Select Folder"), "picker selection"));
        Ui.SetToggle(Ui.WaitFind(page, Ui.Name("Include document contents"), "content consent"), true);
        Ui.Invoke(Ui.WaitFind(page, Ui.Button("Approve folder"), "approve Search folder"));
        Ui.SetValue(Field("search-query"), "kiln");
        Wait.For(() => Ui.Texts(page).Contains("Workshop.txt"), "indexed passage result");
        Ui.Select(Ui.WaitFind(Field("search-results"), Ui.Type(ControlType.ListItem), "matching file"));
        var mark = App.Journal.Mark();
        Ui.Invoke(Ui.WaitFind(page, Ui.Button("Show in folder"), "reveal result"));
        Wait.For(() => App.Journal.Find(mark, "reveal-file") != null, "intercepted Explorer action");
        Ui.Invoke(Field("search-folders"));
        Ui.Invoke(Ui.WaitFind(page, Ui.Button("Remove folder"), "revoke approval"));
        Wait.For(() => Ui.Texts(page).Contains("Add a folder to start searching.") && !Ui.Texts(page).Contains("Workshop.txt"), "revoked result cleared");
    });

    [Fact]
    public void T02_PauseSurvivesNavigation() => Run(() =>
    {
        GoTo("search");
        Ui.Invoke(Field("search-pause"));
        Wait.For(() => Field("search-pause").Current.Name == "Resume indexing", "index paused");
        GoTo("today");
        GoTo("search");
        Assert.Equal("Resume indexing", Field("search-pause").Current.Name);
        Ui.Invoke(Field("search-pause"));
        Wait.For(() => Field("search-pause").Current.Name == "Pause indexing", "index resumed");
    });
}
