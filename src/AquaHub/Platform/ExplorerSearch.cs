using System.Runtime.InteropServices;
using AquaHub.Core.Util;
using AquaHub.Services;
using Microsoft.Win32;

namespace AquaHub.Platform;

/// <summary>Per-user static File Explorer verbs, owned by the installed default-profile copy.</summary>
internal static class ExplorerSearch
{
    private const string Owner = "AquaHubExecutable";
    private const string Label = "Search here with Aqua";
    internal static bool Available => !Sandbox.Enabled && Hub.Updates.IsInstalled && Autostart.ProfileArgs.Length == 0;
    private static readonly (string Key, string Argument)[] Verbs =
    [
        (@"Software\Classes\Directory\shell\AquaHub.Search", "%1"),
        (@"Software\Classes\Directory\Background\shell\AquaHub.Search", "%V"),
    ];

    // The dot keeps the closing quote clear of any trailing backslash supplied by Explorer.
    internal static string Command(string exe, string argument) => $"\"{exe}\" --search-folder \"{argument}\\.\"";

    internal static bool Set(bool enabled, string? exe)
    {
        if (exe is null) return false;
        bool changed = false;
        try
        {
            foreach (var (path, argument) in Verbs)
            {
                using var existing = Registry.CurrentUser.OpenSubKey(path);
                bool owned = string.Equals(existing?.GetValue(Owner) as string, exe, StringComparison.OrdinalIgnoreCase);
                if (existing != null && !owned)
                {
                    if (enabled) return false;
                    continue;
                }
                if (!enabled)
                {
                    if (owned)
                    {
                        existing!.Dispose();
                        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                        changed = true;
                    }
                    continue;
                }
                var command = Command(exe, argument);
                using var current = existing?.OpenSubKey("command");
                if (owned && Equals(current?.GetValue(null), command)) continue;
                using var verb = Registry.CurrentUser.CreateSubKey(path);
                verb.SetValue(Owner, exe);
                verb.SetValue(null, Label);
                verb.SetValue("Icon", $"\"{exe}\",0");
                if (argument == "%1") verb.SetValue("MultiSelectModel", "Single");
                using var launch = verb.CreateSubKey("command");
                launch.SetValue(null, command);
                changed = true;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("search", "Could not update the File Explorer menu", ex);
            return false;
        }
        finally
        {
            if (changed) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
