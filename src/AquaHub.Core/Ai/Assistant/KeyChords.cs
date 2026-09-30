namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// The keyboard shortcuts Ask may press in an app window ("ctrl+s", "tab, tab, space"): letters, digits, F1–F12 and a
/// few named keys, with Ctrl, Shift or Alt. Never the Windows key, Alt+F4 (closing goes through close_window, which
/// asks the app politely), Alt+F11 (Office's macro editor), or chords that switch to another window or open Start or
/// Task Manager. Pure and unit-tested; the platform turns the result into key presses.
/// </summary>
public static class KeyChords
{
    public const ushort Shift = 0x10, Control = 0x11, Alt = 0x12;

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20, ["backspace"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
    };

    /// <summary>Virtual-key chords for "ctrl+s, enter" (at most six), or null when anything isn't allowed.</summary>
    public static List<(List<ushort> Modifiers, ushort Key)>? Parse(string text)
    {
        var chords = new List<(List<ushort>, ushort)>();
        foreach (var raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (chords.Count == 6) return null;
            var parts = raw.ToLowerInvariant().Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var mods = new List<ushort>();
            foreach (var m in parts[..^1])
            {
                var vk = m switch { "ctrl" or "control" => Control, "shift" => Shift, "alt" => Alt, _ => (ushort)0 };
                if (vk == 0 || mods.Contains(vk)) return null; // win, cmd and anything else
                mods.Add(vk);
            }
            var key = parts[^1];
            ushort code;
            if (Named.TryGetValue(key, out var named)) code = named;
            else if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) code = char.ToUpperInvariant(key[0]);
            else if (key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 12) code = (ushort)(0x70 + f - 1);
            else return null;
            if (mods.Contains(Alt) && code == 0x73) return null; // Alt+F4
            if (mods.Contains(Alt) && code == 0x7A) return null; // Alt+F11: Office's macro (VBA) editor
            if (mods.Contains(Alt) && code is 0x09 or 0x1B) return null; // Alt+Tab, Alt+Esc: switch to another window
            if (mods.Contains(Control) && code == 0x1B) return null; // Ctrl+Esc (Start), Ctrl+Shift+Esc (Task Manager)
            chords.Add((mods, code));
        }
        return chords.Count > 0 ? chords : null;
    }
}
