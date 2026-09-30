using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>
/// The app windows on this PC, for Ask's "operate apps" tools: listing them, reading their controls through UI
/// Automation, and acting on a control the user approved (invoke, toggle, select, set a value, type, press keys).
/// Never Aqua's own windows (so Ask can't approve its own requests), never apps that run typed commands, password
/// managers, sign-in or security prompts (<see cref="DesktopRules"/>), and never password fields. Keystrokes only ever
/// go to the approved window: it must really be in front (checked before every chunk), or nothing more is sent.
/// </summary>
internal static class DesktopWindows
{
    public sealed record Window(IntPtr Handle, string Title, string Process, bool Minimized);

    public static bool IsBlocked(Window w) => DesktopRules.IsBlockedProcess(w.Process);

    public static List<Window> List()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<Window>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if (((long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = Title(h);
            if (title.Length == 0 || title == "Program Manager") return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == own) return true;
            string process;
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); process = p.ProcessName; }
            catch { process = ""; }
            list.Add(new Window(h, title, process, Native.IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>A window by (part of) its title or its app's process name, the frontmost first.</summary>
    public static Window? Find(string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return null;
        var all = List();
        return all.FirstOrDefault(w => w.Title.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Process.Equals(q, StringComparison.OrdinalIgnoreCase) || w.Process.Equals(q.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Process.Contains(q.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
    }

    private static string Title(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    public static void Focus(Window w)
    {
        if (w.Minimized) Native.ShowWindow(w.Handle, Native.SW_RESTORE);
        WindowEffects.ForceForeground(w.Handle);
    }

    /// <summary>
    /// True when the window in front is <paramref name="w"/> or one of its own dialogs (same root owner), belongs to
    /// the same app, and that app isn't off limits (nor Aqua itself).
    /// </summary>
    public static bool IsInFront(Window w)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg != w.Handle && GetAncestor(fg, 3 /* GA_ROOTOWNER */) != w.Handle) return false;
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return !DesktopRules.IsBlockedProcess(p.ProcessName);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Brings the window to the front and waits briefly until it really is; false when Windows kept something else there.</summary>
    public static bool BringToFront(Window w)
    {
        if (IsInFront(w)) return true;
        Hub.Ui.Invoke(() => Focus(w));
        for (var i = 0; i < 12; i++)
        {
            if (IsInFront(w)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    public static void Close(Window w) => PostMessage(w.Handle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

    // ───────────────────────────── Reading controls ─────────────────────────────

    private static readonly ControlType[] Useful =
    {
        ControlType.Button, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.Edit, ControlType.Document,
        ControlType.Hyperlink, ControlType.ListItem, ControlType.MenuItem, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
        ControlType.SplitButton, ControlType.Slider, ControlType.Spinner, ControlType.Text, ControlType.MenuBar,
    };

    /// <summary>The window's controls (up to <paramref name="max"/>, within 4 seconds), numbered, as text for the model.</summary>
    public static (string Text, List<AutomationElement> Elements) Read(Window w, int max = 120)
    {
        var elements = new List<AutomationElement>();
        var sb = new StringBuilder();
        var root = AutomationElement.FromHandle(w.Handle);
        var walker = TreeWalker.ControlViewWalker;
        var sw = Stopwatch.StartNew();
        var texts = 0;
        void Walk(AutomationElement parent, int depth)
        {
            if (depth > 25) return;
            AutomationElement? child;
            try { child = walker.GetFirstChild(parent); } catch { return; }
            while (child is not null && elements.Count < max && sw.ElapsedMilliseconds < 4000)
            {
                try
                {
                    var c = child.Current;
                    if (!c.IsOffscreen && Useful.Contains(c.ControlType))
                    {
                        var name = (c.Name ?? "").ReplaceLineEndings(" ").Trim();
                        var isText = c.ControlType == ControlType.Text;
                        if (!(isText && (name.Length < 2 || ++texts > 40)) && !(name.Length == 0 && c.ControlType != ControlType.Edit && c.ControlType != ControlType.Document))
                        {
                            elements.Add(child);
                            sb.Append('[').Append(elements.Count).Append("] ").Append(c.ControlType.ProgrammaticName.Replace("ControlType.", ""))
                              .Append(name.Length > 0 ? " “" + HtmlText.Truncate(name, 80) + "”" : "");
                            if (!c.IsEnabled) sb.Append(" (disabled)");
                            if (c.IsPassword) sb.Append(" (password field — Aqua won't touch it)");
                            else if (c.ControlType == ControlType.Edit && child.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                                sb.Append(" = “").Append(HtmlText.Truncate(((ValuePattern)vp).Current.Value ?? "", 80)).Append('”');
                            if (child.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) sb.Append(((TogglePattern)tp).Current.ToggleState == ToggleState.On ? " (on)" : " (off)");
                            sb.Append('\n');
                        }
                    }
                    Walk(child, depth + 1);
                }
                catch (ElementNotAvailableException) { }
                catch (COMException) { }
                try { child = walker.GetNextSibling(child); } catch { break; }
            }
        }
        Walk(root, 0);
        if (elements.Count >= max || sw.ElapsedMilliseconds >= 4000) sb.Append("(more controls not listed)\n");
        return (sb.ToString(), elements);
    }

    /// <summary>A control by its number from <see cref="Read"/> ("7" or "#7") or by its name (exact, then partial).</summary>
    public static AutomationElement? Pick(IReadOnlyList<AutomationElement> elements, string target, Func<AutomationElement, bool>? prefer = null)
    {
        var t = target.Trim().TrimStart('#', '[').TrimEnd(']');
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= elements.Count) return elements[n - 1];
        string Name(AutomationElement e) { try { return e.Current.Name ?? ""; } catch { return ""; } }
        var candidates = elements.Where(e => prefer?.Invoke(e) ?? true).ToList();
        return candidates.FirstOrDefault(e => Name(e).Equals(target.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(e => Name(e).Contains(target.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Invokes, toggles, selects or expands a control. Returns what happened, or null when it can't be clicked.</summary>
    public static string? Activate(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return "clicked"; }
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) { ((TogglePattern)tp).Toggle(); return "switched " + (((TogglePattern)tp).Current.ToggleState == ToggleState.On ? "on" : "off"); }
        if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) { ((SelectionItemPattern)sp).Select(); return "selected"; }
        if (e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
        {
            var ec = (ExpandCollapsePattern)ep;
            if (ec.Current.ExpandCollapseState == ExpandCollapseState.Expanded) { ec.Collapse(); return "collapsed"; }
            ec.Expand();
            return "opened";
        }
        return null;
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    /// <summary>Presses the chords while <paramref name="w"/> is in front; returns how many were pressed (fewer when it lost the front).</summary>
    public static int Press(Window w, IEnumerable<(List<ushort> Modifiers, ushort Key)> chords)
    {
        var pressed = 0;
        foreach (var (mods, key) in chords)
        {
            if (!IsInFront(w)) break;
            var inputs = new List<INPUT>();
            foreach (var m in mods) inputs.Add(KeyInput(m, up: false));
            inputs.Add(KeyInput(key, up: false));
            inputs.Add(KeyInput(key, up: true));
            foreach (var m in Enumerable.Reverse(mods)) inputs.Add(KeyInput(m, up: true));
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            pressed++;
            Thread.Sleep(60);
        }
        return pressed;
    }

    /// <summary>Types text as Unicode keystrokes while <paramref name="w"/> is in front; returns how many characters were sent.</summary>
    public static int Type(Window w, string text)
    {
        var typed = 0;
        foreach (var chunk in text.ReplaceLineEndings("\r").Chunk(32))
        {
            if (!IsInFront(w)) break;
            var inputs = new List<INPUT>();
            foreach (var ch in chunk)
            {
                if (ch == '\r') { inputs.Add(KeyInput(0x0D, false)); inputs.Add(KeyInput(0x0D, true)); continue; }
                inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 /* UNICODE */ } } });
                inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 | 0x0002 /* KEYUP */ } } });
            }
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            typed += chunk.Length;
            Thread.Sleep(20);
        }
        return typed;
    }

    private static INPUT KeyInput(ushort vk, bool up) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 0x0002u : 0u } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
}

// ───────────────────────────── Tools ─────────────────────────────

/// <summary>Base for the tools that work with app windows: finds the window and refuses blocked apps.</summary>
internal abstract class WindowTool : AskTool
{
    protected static (DesktopWindows.Window? Window, string Error) Resolve(JsonElement args)
    {
        var name = Arg(args, "window");
        if (name.Length == 0) return (null, "say which window (its title or app name, from list_windows)");
        var w = DesktopWindows.Find(name);
        if (w is null) return (null, $"no open window matches “{name}” — use list_windows to see what's open");
        if (DesktopRules.NotAllowed(w.Process, Hub.S.Ask.OperateApps, Hub.S.Ask.AllowedApps) is { } why) return (null, $"Aqua doesn't operate {w.Process}: {why}");
        return (w, "");
    }

    /// <summary>The window as the approval card names it: its title and app ("“Inbox – Outlook” (OUTLOOK)"), so you see exactly which app acts.</summary>
    protected static string Label(JsonElement args)
    {
        var name = Arg(args, "window");
        if (name.Length == 0) return "the window";
        return DesktopWindows.Find(name) is { } w ? $"“{HtmlText.Truncate(w.Title, 50)}” ({w.Process})" : "“" + HtmlText.Truncate(name, 40) + "”";
    }

    protected static List<AutomationElement> Elements(AskRun run, DesktopWindows.Window w)
    {
        if (run.Bag.TryGetValue("uia:" + w.Handle, out var cached) && cached is List<AutomationElement> list) return list;
        var (_, elements) = DesktopWindows.Read(w);
        run.Bag["uia:" + w.Handle] = elements;
        return elements;
    }
}

internal sealed class ListWindowsTool : AskTool
{
    public override string Name => "list_windows";
    public override string Description => "List the app windows open on the user's PC (title and app), frontmost first.";
    public override JsonObject Parameters => Schema();
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "layers";
    public override string Describe(JsonElement args) => "Listed your open windows";

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var windows = DesktopWindows.List();
        if (windows.Count == 0) return Task.FromResult(new ToolResult("No app windows are open.", "none open"));
        var sb = new StringBuilder("Open windows (frontmost first):\n");
        foreach (var w in windows.Take(30))
            sb.Append("- “").Append(HtmlText.Truncate(w.Title, 90)).Append("” · ").Append(w.Process).Append(w.Minimized ? " (minimised)" : "")
              .Append(DesktopRules.NotAllowed(w.Process, Hub.S.Ask.OperateApps, Hub.S.Ask.AllowedApps) is not null ? " (off limits)" : "").Append('\n');
        return Task.FromResult(new ToolResult(sb.ToString(), Plural.Of(windows.Count, "window")));
    }
}

internal sealed class ReadWindowTool : WindowTool
{
    public override string Name => "read_window";
    public override string Description => "Read an app window's controls (buttons, fields, links, menu items, text), numbered so you can click or type into them. Read before acting.";
    public override JsonObject Parameters => Schema(("window", "string", "The window's title or app name, e.g. Notepad or Spotify", true));
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "eye";
    public override string Describe(JsonElement args) => "Read " + Label(args);

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var (w, error) = Resolve(args);
        if (w is null) return ToolResult.Fail(error);
        var (text, elements) = await Task.Run(() => DesktopWindows.Read(w), ct).ConfigureAwait(false);
        run.Bag["uia:" + w.Handle] = elements;
        run.SawPrivate = true;
        return new ToolResult($"Window “{w.Title}” ({w.Process}) — controls:\n" + (text.Length > 0 ? text : "(no readable controls)"), Plural.Of(elements.Count, "control"));
    }
}

internal sealed class FocusWindowTool : WindowTool
{
    public override string Name => "focus_window";
    public override string Description => "Bring an app window to the front (restoring it if minimised).";
    public override JsonObject Parameters => Schema(("window", "string", "The window's title or app name", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override string Icon => "external";
    public override string Describe(JsonElement args) => "Switch to " + Label(args);

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var (w, error) = Resolve(args);
        if (w is null) return ToolResult.Fail(error);
        if (Sandbox.Intercept("ui-focus", w.Title)) return new ToolResult($"“{w.Title}” is in front now.", "done");
        return await Task.Run(() => DesktopWindows.BringToFront(w), ct).ConfigureAwait(false)
            ? new ToolResult($"“{w.Title}” is in front now.", "done")
            : ToolResult.Fail($"Windows kept another window in front of “{w.Title}” — the user can click it to bring it forward");
    }
}

internal sealed class ClickTool : WindowTool
{
    public override string Name => "click";
    public override string Description => "Click a button, link, tab, menu item, checkbox or list item in an app window — by its number from read_window or its name.";
    public override JsonObject Parameters => Schema(
        ("window", "string", "The window's title or app name", true),
        ("target", "string", "The control's number from read_window (e.g. 7) or its name (e.g. Save)", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override bool ConfirmAfterUntrusted => true;
    public override string Icon => "bolt";
    public override string Describe(JsonElement args) => $"Click “{HtmlText.Truncate(Arg(args, "target"), 60)}” in {Label(args)}";

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var (w, error) = Resolve(args);
        if (w is null) return ToolResult.Fail(error);
        return await Task.Run(() =>
        {
            var elements = Elements(run, w);
            var e = DesktopWindows.Pick(elements, Arg(args, "target"), el =>
            {
                try { var t = el.Current.ControlType; return t != ControlType.Text && t != ControlType.Edit && t != ControlType.Document; } catch { return false; }
            });
            if (e is null) return ToolResult.Fail($"no control “{Arg(args, "target")}” in that window — read_window lists them");
            string name;
            try { name = e.Current.Name; if (!e.Current.IsEnabled) return ToolResult.Fail($"“{name}” is disabled"); if (e.Current.IsPassword) return ToolResult.Fail("Aqua never touches password fields"); }
            catch (ElementNotAvailableException) { return ToolResult.Fail("that control is gone — read the window again"); }
            if (Sandbox.Intercept("ui-click", w.Title + " › " + name)) return new ToolResult($"Clicked “{name}”.", "clicked");
            try
            {
                var what = DesktopWindows.Activate(e);
                run.Bag.Remove("uia:" + w.Handle); // the window may have changed
                return what is null ? ToolResult.Fail($"“{name}” can't be clicked by Aqua (it has no button action)") : new ToolResult($"“{name}”: {what}.", what);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException) { return ToolResult.Fail(ex.Message); }
        }, ct).ConfigureAwait(false);
    }
}

internal sealed class TypeTextTool : WindowTool
{
    public override string Name => "type_text";
    public override string Description =>
        "Type text into a field or document in an app window (the field's number or name from read_window, or the focused one). submit=true presses Enter after. Never password fields.";
    public override JsonObject Parameters => Schema(
        ("window", "string", "The window's title or app name", true),
        ("text", "string", "What to type", true),
        ("target", "string", "Optional: the field's number from read_window or its name", false),
        ("submit", "boolean", "Optional: press Enter afterwards", false));
    public override ToolAccess Access => ToolAccess.Act;
    public override bool ConfirmAfterUntrusted => true;
    // Pressing Enter sends or runs what was typed: that needs its own OK every time.
    public override bool OneAtATime(JsonElement args) => Flag(args, "submit");
    public override string Icon => "edit";
    // The whole text (up to what the card can show): the user approves exactly what will be typed.
    public override string Describe(JsonElement args) => $"Type “{HtmlText.Truncate(Arg(args, "text"), 600)}” into {Label(args)}" + (Flag(args, "submit") ? " and press Enter" : "");

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var (w, error) = Resolve(args);
        if (w is null) return ToolResult.Fail(error);
        var text = Arg(args, "text");
        if (text.Length == 0) return ToolResult.Fail("nothing to type");
        if (text.Length > 4000) return ToolResult.Fail("that's too long to type (4,000 characters at most)");
        return await Task.Run(() =>
        {
            var elements = Elements(run, w);
            bool IsField(AutomationElement el) { try { var t = el.Current.ControlType; return t == ControlType.Edit || t == ControlType.Document || t == ControlType.ComboBox; } catch { return false; } }
            var target = Arg(args, "target");
            var e = target.Length > 0 ? DesktopWindows.Pick(elements, target, IsField) : null;
            e ??= elements.FirstOrDefault(el => { try { return IsField(el) && el.Current.HasKeyboardFocus; } catch { return false; } })
                  ?? elements.FirstOrDefault(IsField);
            if (e is null) return ToolResult.Fail("no text field in that window — read_window lists its controls");
            try { if (e.Current.IsPassword) return ToolResult.Fail("Aqua never types into password fields"); }
            catch (ElementNotAvailableException) { return ToolResult.Fail("that field is gone — read the window again"); }
            if (Sandbox.Intercept("ui-type", w.Title + " › " + HtmlText.Truncate(text, 60))) return new ToolResult("Typed it.", "typed");
            try
            {
                const string lost = "another window came to the front";
                var single = e.Current.ControlType == ControlType.Edit && e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !((ValuePattern)vp).Current.IsReadOnly;
                if (single && e.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
                    ((ValuePattern)p).SetValue(text); // straight into that field: no keystrokes, no focus needed
                else
                {
                    if (!DesktopWindows.BringToFront(w)) return ToolResult.Fail($"Windows kept another window in front of “{w.Title}”, so nothing was typed");
                    e.SetFocus();
                    Thread.Sleep(80);
                    var typed = DesktopWindows.Type(w, text);
                    if (typed < text.ReplaceLineEndings("\r").Length)
                        return ToolResult.Fail($"stopped after {Plural.Of(typed, "character")} because {lost}");
                }
                if (Flag(args, "submit"))
                {
                    if (!DesktopWindows.BringToFront(w)) return ToolResult.Fail($"typed it, but didn't press Enter because {lost}");
                    try { e.SetFocus(); } catch (ElementNotAvailableException) { }
                    if (DesktopWindows.Press(w, new[] { (new List<ushort>(), (ushort)0x0D) }) == 0) return ToolResult.Fail($"typed it, but didn't press Enter because {lost}");
                }
                run.Bag.Remove("uia:" + w.Handle);
                return new ToolResult($"Typed {Plural.Of(text.Length, "character")} into “{w.Title}”" + (Flag(args, "submit") ? " and pressed Enter." : "."), "typed");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException) { return ToolResult.Fail(ex.Message); }
        }, ct).ConfigureAwait(false);
    }
}

internal sealed class PressKeysTool : WindowTool
{
    public override string Name => "press_keys";
    public override string Description => "Press keyboard shortcuts in an app window, e.g. \"ctrl+s\", \"ctrl+shift+t\", \"enter\", \"tab, tab, space\". Not the Windows key or Alt+F4.";
    public override JsonObject Parameters => Schema(
        ("window", "string", "The window's title or app name", true),
        ("keys", "string", "Keys like ctrl+s or enter; several separated by commas", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override bool ConfirmAfterUntrusted => true;
    // Shortcuts can send, delete or run things: each press needs its own OK.
    public override bool OneAtATime(JsonElement args) => true;
    public override string Icon => "bolt";
    public override string Describe(JsonElement args) => $"Press {HtmlText.Truncate(Arg(args, "keys"), 80)} in {Label(args)}";

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var (w, error) = Resolve(args);
        if (w is null) return ToolResult.Fail(error);
        var chords = KeyChords.Parse(Arg(args, "keys"));
        if (chords is null) return ToolResult.Fail("only letters, digits, F1–F12, Enter, Tab, Esc, arrows and similar keys with Ctrl/Shift/Alt are allowed (no Windows key or Alt+F4)");
        if (Sandbox.Intercept("ui-keys", w.Title + " › " + Arg(args, "keys"))) return new ToolResult("Pressed.", "pressed");
        var pressed = await Task.Run(() => DesktopWindows.BringToFront(w) ? DesktopWindows.Press(w, chords) : -1, ct).ConfigureAwait(false);
        run.Bag.Remove("uia:" + w.Handle);
        if (pressed < 0) return ToolResult.Fail($"Windows kept another window in front of “{w.Title}”, so no keys were pressed");
        if (pressed < chords.Count) return ToolResult.Fail($"pressed {pressed} of {chords.Count} because another window came to the front");
        return new ToolResult($"Pressed {Arg(args, "keys")} in “{w.Title}”.", "pressed");
    }
}

internal sealed class CloseWindowTool : WindowTool
{
    public override string Name => "close_window";
    public override string Description => "Close an app window the way its X button does (the app can still ask to save).";
    public override JsonObject Parameters => Schema(("window", "string", "The window's title or app name", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override bool ConfirmAfterUntrusted => true;
    public override string Icon => "close";
    public override string Describe(JsonElement args) => "Close " + Label(args);

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var (w, error) = Resolve(args);
        if (w is null) return Task.FromResult(ToolResult.Fail(error));
        if (!Sandbox.Intercept("ui-close", w.Title)) DesktopWindows.Close(w);
        return Task.FromResult(new ToolResult($"Asked “{w.Title}” to close.", "closed"));
    }
}
