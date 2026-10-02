namespace AquaHub.Core.Settings;

/// <summary>Aqua's keyboard shortcuts: the ones that work from any app (you pick them) and the fixed ones in its window.</summary>
public static class Shortcuts
{
    /// <param name="Keys">Shown as key caps, split at "+".</param>
    public sealed record Entry(string Keys, string Does, string Where);

    /// <summary>Everything Aqua's window answers to. Keep in step with MainWindow.OnKey and the pages' own key handlers.</summary>
    public static readonly IReadOnlyList<Entry> InApp = new Entry[]
    {
        new("Ctrl+K", "Command palette", "Anywhere in Aqua"),
        new("Ctrl+1…9", "Today, News, Social, Markets, Upcoming, This PC, Launchpad, Ask, Agents", "Anywhere in Aqua"),
        new("Ctrl+,", "Settings", "Anywhere in Aqua"),
        new("F5", "Refresh the page", "Anywhere in Aqua"),
        new("Esc", "Close a popup or panel", "Anywhere in Aqua"),
        new("Ctrl+F", "Search settings", "Settings"),
        new("Enter", "Send", "Ask"),
        new("Shift+Enter", "New line", "Ask"),
        new("↑", "Edit your last question", "Ask, in an empty box"),
        new("↑ ↓", "Move through the results", "Command palette"),
        new("Enter", "Run the highlighted result", "Command palette"),
        new("Tab", "Ask about what you typed", "Command palette"),
    };

    private static readonly Dictionary<string, string> Reserved = new(StringComparer.Ordinal)
    {
        ["Ctrl+K"] = "the command palette",
        ["Ctrl+F"] = "the settings search",
        ["Ctrl+1"] = "Today", ["Ctrl+2"] = "News", ["Ctrl+3"] = "Social", ["Ctrl+4"] = "Markets", ["Ctrl+5"] = "Upcoming",
        ["Ctrl+6"] = "This PC", ["Ctrl+7"] = "Launchpad", ["Ctrl+8"] = "Ask", ["Ctrl+9"] = "Agents",
    };

    /// <summary>
    /// Why <paramref name="gesture"/> shouldn't become a shortcut for any app, or null when nothing in Aqua stands in
    /// its way. <paramref name="others"/> are Aqua's other shortcuts for any app, by what they open. Whether another app
    /// already holds the keys is for Windows to say.
    /// </summary>
    public static string? Clash(string gesture, IReadOnlyDictionary<string, string> others)
    {
        if (!TryNormalize(gesture, out var keys, out var modifiers)) return $"{gesture} can't be used as a shortcut.";
        foreach (var (name, other) in others)
            if (TryNormalize(other, out var taken, out _) && taken == keys) return $"{keys} already opens {name}.";
        if (Reserved.TryGetValue(keys, out var inApp)) return $"{keys} opens {inApp} inside Aqua.";
        // Ctrl or Alt with one key is how apps do their own commands (copy, menus, Alt+F4).
        if (modifiers is "Ctrl" or "Alt") return $"Most apps use {keys} themselves. Try adding Shift.";
        return null;
    }

    /// <summary>"alt+ctrl+h" → "Ctrl+Alt+H": modifiers in a fixed order, then one key (A–Z, 0–9, F1–F24, Space or Enter).</summary>
    public static bool TryNormalize(string gesture, out string keys, out string modifiers)
    {
        keys = modifiers = "";
        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;
        foreach (var raw in (gesture ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                case "space":
                    if (key is not null) return false;
                    key = "Space";
                    break;
                case "enter":
                    if (key is not null) return false;
                    key = "Enter";
                    break;
                case var k when k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]):
                    if (key is not null) return false;
                    key = k.ToUpperInvariant();
                    break;
                case var f when f.Length > 1 && f[0] == 'f' && int.TryParse(f[1..], out var n) && n is >= 1 and <= 24:
                    if (key is not null) return false;
                    key = "F" + n;
                    break;
                default:
                    return false;
            }
        }
        if (key is null || !(ctrl || alt || win)) return false;
        var mods = new List<string>(4);
        if (ctrl) mods.Add("Ctrl");
        if (alt) mods.Add("Alt");
        if (shift) mods.Add("Shift");
        if (win) mods.Add("Win");
        modifiers = string.Join("+", mods);
        keys = modifiers + "+" + key;
        return true;
    }
}
