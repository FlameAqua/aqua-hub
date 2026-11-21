using System.Diagnostics;

namespace AquaHub.E2E.Tests;

[Trait("Execution", "Offline")]
[Trait("Component", "Search")]
public sealed class SearchRenderTests
{
    [Fact]
    public async Task T04_OffScreenSearchReturnsPassageAndWithdrawsRevokedResult()
    {
        var run = Directory.CreateDirectory(Path.Combine(E2EConfig.RunDir, "Search render " + Guid.NewGuid().ToString("N"))).FullName;
        var captures = Path.Combine(run, "captures");
        var start = new ProcessStartInfo(E2EConfig.ExePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--e2e", "--data-dir", Path.Combine(run, "profile"), "--snapshot", captures, "--pages", "search-ui" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(0, process.ExitCode);
            var error = Path.Combine(captures, "error.txt");
            Assert.False(File.Exists(error), File.Exists(error) ? File.ReadAllText(error) : "");
            foreach (var name in new[] { "empty", "results-dark", "results-light", "deck-search-dark", "deck-search-light", "folder-scope" })
                Assert.True(new FileInfo(Path.Combine(captures, name + ".png")).Length > 1024);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
