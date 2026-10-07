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
        new("F5", "Refresh everything", "Anywhere in Aqua"),
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

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tilde"] = "Tilde", ["oem3"] = "Tilde", ["~"] = "Tilde", ["`"] = "Tilde",
        ["equal"] = "Equal", ["oemplus"] = "Equal", ["="] = "Equal",
        ["semicolon"] = "Semicolon", ["oem1"] = "Semicolon", [";"] = "Semicolon", [":"] = "Semicolon",
        ["comma"] = "Comma", ["oemcomma"] = "Comma", [","] = "Comma",
        ["minus"] = "Minus", ["oemminus"] = "Minus", ["-"] = "Minus", ["_"] = "Minus",
        ["period"] = "Period", ["oemperiod"] = "Period", ["."] = "Period",
        ["slash"] = "Slash", ["oemquestion"] = "Slash", ["/"] = "Slash", ["?"] = "Slash",
        ["openbracket"] = "OpenBracket", ["oem4"] = "OpenBracket", ["["] = "OpenBracket", ["{"] = "OpenBracket",
        ["backslash"] = "Backslash", ["oem5"] = "Backslash", ["\\"] = "Backslash", ["|"] = "Backslash",
        ["closebracket"] = "CloseBracket", ["oem6"] = "CloseBracket", ["]"] = "CloseBracket", ["}"] = "CloseBracket",
        ["quote"] = "Quote", ["oem7"] = "Quote", ["'"] = "Quote", ["\""] = "Quote",
        ["intlbackslash"] = "IntlBackslash", ["oem102"] = "IntlBackslash",
        ["esc"] = "Escape", ["escape"] = "Escape",
        ["tab"] = "Tab", ["home"] = "Home", ["end"] = "End",
        ["pageup"] = "PageUp", ["prior"] = "PageUp", ["pgup"] = "PageUp",
        ["pagedown"] = "PageDown", ["next"] = "PageDown", ["pgdn"] = "PageDown",
        ["left"] = "Left", ["arrowleft"] = "Left", ["up"] = "Up", ["arrowup"] = "Up",
        ["right"] = "Right", ["arrowright"] = "Right", ["down"] = "Down", ["arrowdown"] = "Down",
        ["insert"] = "Insert", ["ins"] = "Insert", ["delete"] = "Delete", ["del"] = "Delete",
        ["backspace"] = "Backspace", ["back"] = "Backspace",
        ["numpad0"] = "NumPad0", ["numpad1"] = "NumPad1", ["numpad2"] = "NumPad2", ["numpad3"] = "NumPad3", ["numpad4"] = "NumPad4",
        ["numpad5"] = "NumPad5", ["numpad6"] = "NumPad6", ["numpad7"] = "NumPad7", ["numpad8"] = "NumPad8", ["numpad9"] = "NumPad9",
        ["numpadadd"] = "NumPadAdd", ["add"] = "NumPadAdd",
        ["numpadsubtract"] = "NumPadSubtract", ["subtract"] = "NumPadSubtract",
        ["numpadmultiply"] = "NumPadMultiply", ["multiply"] = "NumPadMultiply",
        ["numpaddivide"] = "NumPadDivide", ["divide"] = "NumPadDivide",
        ["numpaddecimal"] = "NumPadDecimal", ["decimal"] = "NumPadDecimal",
        ["numpadseparator"] = "NumPadSeparator", ["separator"] = "NumPadSeparator",
        ["numlock"] = "NumLock", ["printscreen"] = "PrintScreen", ["scrolllock"] = "ScrollLock", ["pause"] = "Pause",
    };

    private static readonly IReadOnlyDictionary<string, int> VirtualKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Escape"] = 0x1B, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Tilde"] = 0xC0, ["Equal"] = 0xBB, ["Semicolon"] = 0xBA, ["Comma"] = 0xBC, ["Minus"] = 0xBD,
        ["Period"] = 0xBE, ["Slash"] = 0xBF, ["OpenBracket"] = 0xDB, ["Backslash"] = 0xDC,
        ["CloseBracket"] = 0xDD, ["Quote"] = 0xDE, ["IntlBackslash"] = 0xE2,
        ["NumPad0"] = 0x60, ["NumPad1"] = 0x61, ["NumPad2"] = 0x62, ["NumPad3"] = 0x63, ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65, ["NumPad6"] = 0x66, ["NumPad7"] = 0x67, ["NumPad8"] = 0x68, ["NumPad9"] = 0x69,
        ["NumPadMultiply"] = 0x6A, ["NumPadAdd"] = 0x6B, ["NumPadSeparator"] = 0x6C, ["NumPadSubtract"] = 0x6D,
        ["NumPadDecimal"] = 0x6E, ["NumPadDivide"] = 0x6F,
        ["NumLock"] = 0x90, ["ScrollLock"] = 0x91, ["PrintScreen"] = 0x2C, ["Pause"] = 0x13,
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
            if (KeyAliases.TryGetValue(raw, out var alias))
            {
                if (key is not null) return false;
                key = alias;
                continue;
            }
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

    /// <summary>Maps a normalized primary key to the Windows virtual key used by RegisterHotKey.</summary>
    public static bool TryGetVirtualKey(string key, out int virtualKey)
    {
        if (VirtualKeys.TryGetValue(key, out virtualKey)) return true;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            virtualKey = char.ToUpperInvariant(key[0]);
            return true;
        }
        if (key.Length > 1 && (key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 24)
        {
            virtualKey = 0x70 + n - 1;
            return true;
        }
        virtualKey = 0;
        return false;
    }
}
