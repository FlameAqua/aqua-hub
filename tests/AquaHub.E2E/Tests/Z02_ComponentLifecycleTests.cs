namespace AquaHub.E2E.Tests;

[Trait("Execution", "Offline")]
[Trait("Component", "Lifecycle")]
public sealed class Z02_ComponentLifecycleTests(AppFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    protected override SessionOptions Options => new() { Offline = true };
    [Fact]
    [Trait("Category", "Smoke")]
    public void T01_CloseActivateAndQuitUseOnlyOwnedSession() => Run(() =>
    {
        GoTo("models");
        var app = App;
        ((WindowPattern)Main.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Wait.For(() => app.TryMain() is null, "window hidden to tray");
        Assert.True(app.IsAlive);
        app.Command("activate");
        PageRoot("models");
        AllowsExit = true;
        var mark = app.Log.Mark();
        app.Command("quit");
        Assert.True(app.Process.WaitForExit(15000), "offline app did not quit within 15 seconds");
        Assert.Equal(0, app.Process.ExitCode);
        Assert.Empty(app.Log.ErrorsSince(mark));
    });
}
