namespace AquaHub.E2E.Infrastructure;

/// <summary>
/// Top-level UI Automation windows of one process. Note: WPF <c>Popup</c> content (bell list, search results,
/// combo drop-downs) is exposed inside the owner window's subtree, while context menus without an owner in the
/// tree (the tray menu) and dialogs are separate top-level windows.
/// </summary>
public static class AppWindows
{
    public const string MainTitle = "Aqua Hub";
    public const string FlyoutTitle = "Aqua Hub quick panel";
    public const string PaletteTitle = "Aqua command palette";

    public static List<AutomationElement> TopLevel(int pid) =>
        Ui.FindAll(AutomationElement.RootElement, Ui.Pid(pid), TreeScope.Children);

    public static AutomationElement? Window(int pid, string title) =>
        TopLevel(pid).FirstOrDefault(w => Ui.NameOf(w) == title);

    /// <summary>Searches every top-level window of the process (and therefore every popup inside them).</summary>
    public static AutomationElement? FindInAll(int pid, Condition condition)
    {
        foreach (var w in TopLevel(pid))
        {
            var hit = Ui.Find(w, condition, TreeScope.Subtree);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Alias kept for readability at call sites that look for drop-down/popup content.</summary>
    public static AutomationElement? FindInPopups(int pid, Condition condition) => FindInAll(pid, condition);

    /// <summary>Top-level transient windows (tray/context menus, dialogs) — not the main window, quick panel or palette.</summary>
    public static List<AutomationElement> Popups(int pid) =>
        TopLevel(pid).Where(w => Ui.NameOf(w) is not (MainTitle or FlyoutTitle or PaletteTitle)).ToList();

    /// <summary>A top-level menu window (tray menu or an element's context menu).</summary>
    public static AutomationElement? Menu(int pid, string? automationId = null)
    {
        foreach (var w in Popups(pid))
        {
            var menu = Ui.Find(w, automationId is null ? Ui.Type(ControlType.Menu) : Ui.And(Ui.Type(ControlType.Menu), Ui.Id(automationId)), TreeScope.Subtree);
            if (menu is not null) return menu;
        }
        return null;
    }

    public static int ForegroundPid() => Win32.PidOf(Win32.GetForegroundWindow());

    /// <summary>
    /// Top-level windows of the process plus the windows they own (message boxes, file dialogs), which UI Automation
    /// lists under their owner rather than under the desktop.
    /// </summary>
    public static List<AutomationElement> WithOwned(int pid)
    {
        var all = new List<AutomationElement>();
        foreach (var w in TopLevel(pid))
        {
            all.Add(w);
            all.AddRange(Ui.FindAll(w, Ui.Type(ControlType.Window), TreeScope.Children));
        }
        return all;
    }

    /// <summary>
    /// A window of the process by title wherever UI Automation puts it: an owned window (a modal file dialog) is a
    /// child of its owner rather than of the desktop, and a just-opened one may only be visible to Win32 yet.
    /// </summary>
    public static AutomationElement? AnyWindow(int pid, string title)
    {
        foreach (var w in TopLevel(pid))
        {
            if (Ui.NameOf(w) == title) return w;
            if (Ui.Find(w, Ui.And(Ui.Type(ControlType.Window), Ui.Name(title)), TreeScope.Children) is { } owned) return owned;
        }
        var hwnd = IntPtr.Zero;
        Win32.EnumWindows((h, _) =>
        {
            if (Win32.PidOf(h) != pid || Win32.TitleOf(h) != title || !Win32.IsWindowVisible(h)) return true;
            hwnd = h;
            return false;
        }, IntPtr.Zero);
        try { return hwnd == IntPtr.Zero ? null : AutomationElement.FromHandle(hwnd); }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return null; }
    }

    /// <summary>Asks every window of the process with this title to close (e.g. a dialog a failed check left open).</summary>
    public static void CloseWin32Windows(int pid, string title) =>
        Win32.EnumWindows((h, _) =>
        {
            if (Win32.PidOf(h) == pid && Win32.TitleOf(h) == title) Win32.PostMessage(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);

    /// <summary>Counts every window (visible or hidden) of the process with the given title.</summary>
    public static int CountWin32Windows(int pid, string title, bool includeHidden)
    {
        var n = 0;
        Win32.EnumWindows((h, _) =>
        {
            if (Win32.PidOf(h) == pid && Win32.TitleOf(h) == title && (includeHidden || Win32.IsWindowVisible(h))) n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }
}
