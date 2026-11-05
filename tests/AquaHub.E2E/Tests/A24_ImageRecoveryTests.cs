using System.Text.Json.Nodes;

namespace AquaHub.E2E.Tests;

/// <summary>Real WPF/service integration with fake Comfy responses. Does not establish actual GPU release.</summary>
[Trait("Execution", "Offline")]
[Trait("Component", "Images")]
public sealed class A24_ImageRecoveryTests(AppFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    protected override SessionOptions Options => new() { Offline = true };
    private AutomationElement Field(string id) => Ui.WaitFind(PageRoot("ask"), Ui.Id(id), id);
    private string Status => Ui.NameOf(Field("image-status"));
    private void OpenImages()
    {
        GoTo("ask");
        Ui.SetToggle(Field("ask-tab-images"), true);
    }
    private void StartWith(ComfyFixture server, bool recovery = false)
    {
        Fixture.Restart(new SessionOptions
        {
            Offline = true,
            EditSettings = root => root["imageGeneration"] = new JsonObject { ["endpoint"] = server.Endpoint },
            SeedProfile = profile =>
            {
                if (!recovery) return;
                var folder = Path.Combine(profile, "generated-images");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "gpu-recovery.txt"), server.Endpoint);
            },
        });
        OpenImages();
    }

    [Fact]
    public void T01_ModelPairingMismatchThenCorrectedConnection() => Run(() =>
    {
        using var server = new ComfyFixture();
        StartWith(server);
        Ui.Invoke(Field("image-check"));
        Wait.For(() => Status.Contains("Connected · models ready"), "fixture inventory populated");
        Ui.Expand(Field("image-setup"));
        Ui.SelectComboItem(Field("image-encoder"), "qwen_3_8b.safetensors", Pid);
        Ui.Invoke(Field("image-check"));
        Wait.For(() => Status.Contains("4B", StringComparison.OrdinalIgnoreCase) && !Status.StartsWith("Checking"), "pairing mismatch visible");
        Assert.DoesNotContain("models ready", Status);
        Ui.SelectComboItem(Field("image-encoder"), "qwen_3_4b.safetensors", Pid);
        Ui.Invoke(Field("image-check"));
        Wait.For(() => Status.Contains("Connected · models ready"), "corrected pairing ready");
        Assert.DoesNotContain(server.Requests, r => r.Contains("/prompt") || r.Contains("/interrupt"));
    });

    [Fact]
    public void T02_RestartRecoveryRetainsReservationUntilFixtureConfirmsIdleAndFree() => Run(() =>
    {
        using var server = new ComfyFixture { BusyQueue = true };
        StartWith(server, recovery: true);
        var marker = Path.Combine(App.ProfileDir, "generated-images", "gpu-recovery.txt");
        Assert.True(File.Exists(marker));
        Assert.False(Field("image-generate").Current.IsEnabled);
        Ui.Invoke(Field("image-recover"));
        Wait.For(() => server.Requests.Count(r => r == "GET /queue") >= 2, "busy queue observed on successive recovery polls");
        Assert.True(File.Exists(marker));
        Assert.DoesNotContain("POST /free", server.Requests);
        server.BusyQueue = false;
        try { Wait.For(() => Status.Contains("GPU released."), "fixture recovery completed", TimeSpan.FromSeconds(15)); }
        finally { Step("Recovery state: " + Status + "; fixture requests: " + string.Join(", ", server.Requests)); }
        Assert.False(File.Exists(marker));
        Assert.True(Field("image-generate").Current.IsEnabled);
        Assert.Single(server.Requests, r => r == "POST /free");
        Assert.True(server.Requests.Count(r => r == "GET /system_stats") >= 2);
        Assert.DoesNotContain(server.Requests, r => r.Contains("/prompt") || r.Contains("/interrupt"));
    });

    [Fact]
    [Trait("Component", "Lifecycle")]
    public void T03_QuitCancelsAnObservedInFlightConnectionCheck() => Run(() =>
    {
        using var server = new ComfyFixture { HoldPath = "object_info" };
        StartWith(server);
        Ui.Invoke(Field("image-check"));
        Assert.True(server.RequestHeld.Task.Wait(TimeSpan.FromSeconds(10)), "fixture did not observe the in-flight request");
        var app = App;
        var mark = app.Log.Mark();
        AllowsExit = true;
        app.Command("quit");
        Assert.True(app.Process.WaitForExit(15000), "quit failed to join the cancelled request");
        Assert.True(server.ClientDisconnected.Task.Wait(TimeSpan.FromSeconds(5)), "cancelled HTTP connection was not closed");
        Assert.Equal(0, app.Process.ExitCode);
        Assert.Empty(app.Log.ErrorsSince(mark));
    });
}
