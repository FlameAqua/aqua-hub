using System.Globalization;

namespace AquaHub.E2E.Infrastructure;

/// <summary>Locations and timeouts for the suite. Everything can be overridden through environment variables.</summary>
public static class E2EConfig
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>Home of the suite's build, warm profile and results (scripts\e2e.ps1 sets it up):
    /// %LOCALAPPDATA%\AquaHub.E2E, apart from the real app's %LOCALAPPDATA%\AquaHub profile.</summary>
    public static string Root => Env("AQUAHUB_E2E_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaHub.E2E");

    /// <summary>Dry-run capable build of the app under test (never the developer's src\ output).</summary>
    public static string ExePath => Env("AQUAHUB_E2E_EXE") ?? Path.Combine(Root, "e2e-bin", "AquaHub.exe");

    /// <summary>Warm profile (cached news/markets, onboarding done). Copied, never used directly.</summary>
    public static string PristineProfile => Env("AQUAHUB_E2E_PRISTINE") ?? Path.Combine(Root, "e2e-profile-pristine");

    public static string RunsRoot => Env("AQUAHUB_E2E_RUNS") ?? Path.Combine(Root, "e2e-runs");

    public static string RunId { get; } = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static readonly Lazy<string> RunDirLazy = new(() =>
    {
        var dir = Path.Combine(RunsRoot, RunId);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "screenshots"));
        Directory.CreateDirectory(Path.Combine(dir, "profiles"));
        Directory.CreateDirectory(Path.Combine(dir, "results"));
        return dir;
    });

    /// <summary>Folder for this test run: profiles\, screenshots\, results\ and run.log.</summary>
    public static string RunDir => RunDirLazy.Value;

    public static string ScreenshotDir => Path.Combine(RunDir, "screenshots");
    public static string ProfilesDir => Path.Combine(RunDir, "profiles");
    public static string ResultsDir => Path.Combine(RunDir, "results");

    /// <summary>Budget for answers from the local model (Ask page, inline answers).</summary>
    public static TimeSpan AiTimeout =>
        TimeSpan.FromSeconds(int.TryParse(Env("AQUAHUB_E2E_AI_TIMEOUT"), out var s) ? s : 150);

    /// <summary>Budget for ordinary UI reactions.</summary>
    public static TimeSpan UiTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>settings.json is written ~0.6 s after a change; allow generous slack.</summary>
    public static TimeSpan PersistTimeout { get; } = TimeSpan.FromSeconds(6);
}
