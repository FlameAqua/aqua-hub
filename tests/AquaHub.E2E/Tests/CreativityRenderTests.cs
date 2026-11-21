using System.Diagnostics;

namespace AquaHub.E2E.Tests;

[Trait("Execution", "Offline")]
[Trait("Component", "Creativity")]
public sealed class CreativityRenderTests
{
    [Fact]
    public async Task T01_OffScreenReadAloudAndPersonalityWorkflows()
    {
        var run = Directory.CreateDirectory(Path.Combine(E2EConfig.RunDir, "Creativity render " + Guid.NewGuid().ToString("N"))).FullName;
        var captures = Path.Combine(run, "captures");
        var start = new ProcessStartInfo(E2EConfig.ExePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--e2e", "--data-dir", Path.Combine(run, "profile"), "--snapshot", captures, "--pages", "creativity-ui" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var error = Path.Combine(captures, "error.txt");
            Assert.False(File.Exists(error), File.Exists(error) ? File.ReadAllText(error) : "");
            Assert.Equal(0, process.ExitCode);
            foreach (var section in new[] { "voice", "personalities", "roleplay" })
                foreach (var theme in new[] { "dark", "light" })
                    Assert.True(new FileInfo(Path.Combine(captures, section + "-" + theme + ".png")).Length > 1024);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
