using AquaHub.Core.Util;

namespace AquaHub.Services;

/// <summary>
/// Dry-run mode for end-to-end UI tests (<c>--e2e</c> or <c>AQUAHUB_E2E=1</c>). Every side effect on the
/// rest of the PC — launching/closing apps, opening links, media keys, volume, speech, autostart, secrets,
/// clipboard writes, notifications, model unloading — is recorded in <c>e2e-journal.log</c> in the profile
/// folder instead of being performed, so automation can click everything safely and assert on the journal.
/// </summary>
public static class Sandbox
{
    private static readonly object Gate = new();
    private static string? _journal;

    public static bool Enabled { get; private set; }

    public const string ClipboardFixture =
        "Dublin City Council has approved a plan to pedestrianise College Green, creating a new civic plaza in the heart of the city. " +
        "The scheme, which has been debated for over a decade, will reroute buses and taxis and add new cycle lanes. " +
        "Supporters say it will make the area safer and more attractive, while some businesses worry about access during construction.";

    public static void Enable(string profileRoot)
    {
        Enabled = true;
        _journal = Path.Combine(profileRoot, "e2e-journal.log");
        File.WriteAllText(_journal, "");
        Record("e2e", "dry-run mode enabled");
    }

    /// <summary>Returns true (and journals the action) when the caller must NOT perform the real side effect.</summary>
    public static bool Intercept(string action, string detail = "")
    {
        if (!Enabled) return false;
        Record(action, detail);
        return true;
    }

    public static void Record(string action, string detail)
    {
        if (_journal is null) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff}\t{action}\t{detail.Replace('\t', ' ').Replace('\n', ' ')}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(_journal, line); } catch { }
        }
        Log.Info("e2e", $"{action} {detail}");
    }
}
