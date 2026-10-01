using System.Runtime.InteropServices;
using System.Windows;

namespace AquaHub.E2E.Infrastructure;

public enum VK : ushort
{
    Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Control = 0x11, Alt = 0x12, Escape = 0x1B, Space = 0x20,
    PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Delete = 0x2E,
    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 0x41, K = 0x4B,
    Apps = 0x5D, F5 = 0x74, F9 = 0x78, F10 = 0x79, OemComma = 0xBC,
}

/// <summary>
/// Real (synthetic) keyboard and mouse input, used only where UI Automation patterns cannot reach
/// (keyboard shortcuts, Enter in text boxes, context menus, MouseUp-only pickers).
/// Every call first verifies that the foreground window belongs to the app under test, so keystrokes can
/// never land in the user's own windows.
/// </summary>
public static class Input
{
    private static readonly VK[] Extended = { VK.Left, VK.Up, VK.Right, VK.Down, VK.Delete, VK.Home, VK.End, VK.PageUp, VK.PageDown, VK.Apps };

    /// <summary>Brings a window of the app to the foreground (AttachThreadInput dance, same as the app itself does).</summary>
    public static bool TryActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        var pid = Win32.PidOf(hwnd);
        if (AppWindows.ForegroundPid() == pid) return true;
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        var fg = Win32.GetForegroundWindow();
        var fgThread = Win32.GetWindowThreadProcessId(fg, out _);
        var me = Win32.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && Win32.AttachThreadInput(me, fgThread, true);
        try
        {
            Win32.BringWindowToTop(hwnd);
            Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(me, fgThread, false);
        }
        return Wait.Until(() => AppWindows.ForegroundPid() == pid, TimeSpan.FromSeconds(2), 50);
    }

    /// <summary>Throws unless the foreground window belongs to <paramref name="pid"/>.</summary>
    public static void Guard(int pid)
    {
        var fg = AppWindows.ForegroundPid();
        if (fg != pid)
            throw new InputRefusedException($"Refusing to send input: the foreground window belongs to process {fg}, not the app under test ({pid}).");
    }

    public static void Chord(int pid, params VK[] keys)
    {
        Guard(pid);
        var inputs = new List<Win32.INPUT>();
        foreach (var k in keys) inputs.Add(Key(k, up: false));
        foreach (var k in Enumerable.Reverse(keys)) inputs.Add(Key(k, up: true));
        Send(inputs);
        Thread.Sleep(60);
    }

    public static void Press(int pid, VK key, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Guard(pid);
            Send(new List<Win32.INPUT> { Key(key, false), Key(key, true) });
            Thread.Sleep(60);
        }
    }

    /// <summary>Types text as Unicode keystrokes.</summary>
    public static void Type(int pid, string text)
    {
        Guard(pid);
        var inputs = new List<Win32.INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }
        Send(inputs);
        Thread.Sleep(80);
    }

    /// <summary>Clicks a screen point, but only if the window under it belongs to the app under test.</summary>
    public static void Click(int pid, Point screenPoint, bool right = false)
    {
        Guard(pid);
        var pt = new Win32.POINT { X = (int)Math.Round(screenPoint.X), Y = (int)Math.Round(screenPoint.Y) };
        var under = Win32.WindowFromPoint(pt);
        if (Win32.PidOf(under) != pid)
            throw new InputRefusedException($"Refusing to click at {pt.X},{pt.Y}: the window there belongs to process {Win32.PidOf(under)}.");
        Win32.GetCursorPos(out var old);
        Win32.SetCursorPos(pt.X, pt.Y);
        Thread.Sleep(40);
        var down = right ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_LEFTDOWN;
        var upFlag = right ? Win32.MOUSEEVENTF_RIGHTUP : Win32.MOUSEEVENTF_LEFTUP;
        Send(new List<Win32.INPUT> { Mouse(down) });
        Thread.Sleep(50);
        Send(new List<Win32.INPUT> { Mouse(upFlag) });
        Thread.Sleep(120);
        // Park the pointer where it was, so we leave the user's cursor alone as much as possible.
        if (!right) Win32.SetCursorPos(old.X, old.Y);
    }

    /// <summary>Clicks the centre (or clickable point) of an element after scrolling it into view.</summary>
    public static void ClickElement(int pid, AutomationElement element, bool right = false)
    {
        Ui.BringIntoView(element);
        Point p;
        if (!element.TryGetClickablePoint(out p))
        {
            var r = element.Current.BoundingRectangle;
            if (r.IsEmpty) throw new InputRefusedException($"{Ui.Describe(element)} has no on-screen area to click");
            p = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        }
        Click(pid, p, right);
    }

    private static void Send(List<Win32.INPUT> inputs)
    {
        var arr = inputs.ToArray();
        var sent = Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
        if (sent != arr.Length)
            throw new InvalidOperationException($"SendInput injected {sent}/{arr.Length} events (error {Marshal.GetLastWin32Error()}).");
    }

    private static Win32.INPUT Key(VK vk, bool up)
    {
        var flags = up ? Win32.KEYEVENTF_KEYUP : 0u;
        if (Extended.Contains(vk)) flags |= Win32.KEYEVENTF_EXTENDEDKEY;
        return new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            U = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)Win32.MapVirtualKey((uint)vk, 0), dwFlags = flags } },
        };
    }

    private static Win32.INPUT Unicode(char ch, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion { ki = new Win32.KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = Win32.KEYEVENTF_UNICODE | (up ? Win32.KEYEVENTF_KEYUP : 0u) } },
    };

    private static Win32.INPUT Mouse(uint flags) => new()
    {
        type = Win32.INPUT_MOUSE,
        U = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dwFlags = flags } },
    };
}

/// <summary>
/// Input wasn't sent because the target isn't ready for it (another window is in front, or the element has no area
/// yet). A focus race, so waits and retries treat it as transient.
/// </summary>
public sealed class InputRefusedException(string message) : InvalidOperationException(message);
