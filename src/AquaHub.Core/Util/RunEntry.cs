namespace AquaHub.Core.Util;

/// <summary>
/// Start with Windows is a command line in the user's Run key. These are the rules for when Aqua rewrites its own entry;
/// the app does the registry work.
/// </summary>
public static class RunEntry
{
    /// <summary>The program a command line starts: <c>"C:\path\AquaHub.exe" --background</c> gives <c>C:\path\AquaHub.exe</c>.</summary>
    public static string ProgramOf(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : c.Trim('"');
        }
        var space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    /// <summary>
    /// With Start with Windows on, whether this copy should rewrite an entry that starts another program: yes if this
    /// copy is installed (it takes over from a build run from source before installing), or if that program is gone.
    /// A source build never takes the entry from an installed copy.
    /// </summary>
    public static bool ShouldTakeOver(string current, string exe, bool installed, Func<string, bool> exists)
    {
        var target = ProgramOf(current);
        if (string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) return false;
        return installed || !exists(target);
    }
}
