using System.Diagnostics;

namespace AquaHub.E2E.Tests;

[Trait("Execution", "Offline")]
[Trait("Component", "Recipes")]
public sealed class RecipeRenderTests
{
    [Fact]
    public async Task T01_OffScreenAuthorReviewRunAndCancel()
    {
        var root = Directory.CreateDirectory(Path.Combine(E2EConfig.RunDir, "Recipes " + Guid.NewGuid().ToString("N"))).FullName;
        var captures = Path.Combine(root, "captures");
        var start = new ProcessStartInfo(E2EConfig.ExePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--e2e", "--data-dir", Path.Combine(root, "profile"), "--snapshot", captures, "--pages", "recipes-ui" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var error = Path.Combine(captures, "error.txt");
            Assert.False(File.Exists(error), File.Exists(error) ? File.ReadAllText(error) : "");
            Assert.Equal(0, process.ExitCode);
            foreach (var name in new[] { "editor-dark", "editor-light", "review-dark", "review-light", "runs-dark", "runs-light", "health-light", "built-in-light" })
                Assert.True(new FileInfo(Path.Combine(captures, name + ".png")).Length > 1024);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
