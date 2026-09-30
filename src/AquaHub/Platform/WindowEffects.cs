using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AquaHub.Platform;

public enum Backdrop { None, Mica, Acrylic, Tabbed }

/// <summary>Windows 11 DWM integration: Mica / Acrylic backdrops, immersive dark mode, rounded corners.</summary>
public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// <summary>System backdrops need Windows 11 22H2 (build 22621) or later.</summary>
    public static bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621;
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>When true (snapshot mode, unsupported OS) windows paint an opaque background instead of a backdrop.</summary>
    public static bool ForceOpaque { get; set; }

    public static bool Apply(Window window, Backdrop backdrop, bool dark, bool roundCorners = true)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var darkValue = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));
        if (IsWindows11)
        {
            var corner = roundCorners ? 2 : 1; // DWMWCP_ROUND : DWMWCP_DONOTROUND
            Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        if (ForceOpaque || backdrop == Backdrop.None || !BackdropSupported) return false;

        var type = backdrop switch { Backdrop.Mica => 2, Backdrop.Acrylic => 3, Backdrop.Tabbed => 4, _ => 1 };
        var hr = Native.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        if (hr != 0) return false;
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } source)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        return true;
    }

    public static void SetDark(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var v = dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }

    /// <summary>Hide from Alt+Tab and the taskbar (flyouts, palette).</summary>
    public static void MakeToolWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW;
        ex &= ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Reliably brings a window to the foreground (works around focus-stealing prevention).</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        var fg = Native.GetForegroundWindow();
        var fgThread = Native.GetWindowThreadProcessId(fg, out _);
        var thisThread = Native.GetCurrentThreadId();
        if (fgThread != thisThread)
        {
            Native.AttachThreadInput(thisThread, fgThread, true);
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
            Native.AttachThreadInput(thisThread, fgThread, false);
        }
        else
        {
            Native.SetForegroundWindow(hwnd);
        }
    }

    /// <summary>Work area (physical pixels) and DPI scale of the monitor nearest to a point.</summary>
    public static (Native.RECT Work, double Scale) MonitorAt(Native.POINT pt)
    {
        var mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref info);
        var scale = 1.0;
        if (Native.GetDpiForMonitor(mon, 0, out var dpiX, out _) == 0) scale = dpiX / 96.0;
        return (info.rcWork, scale);
    }

    /// <summary>Taskbar edge: 0 left, 1 top, 2 right, 3 bottom.</summary>
    public static uint TaskbarEdge()
    {
        var data = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>() };
        Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref data);
        return data.uEdge;
    }

    public static void Place(Window window, int x, int y, int width, int height, bool topmost)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Native.SetWindowPos(hwnd, topmost ? Native.HWND_TOPMOST : IntPtr.Zero, x, y, width, height,
            Native.SWP_NOACTIVATE | (topmost ? 0 : Native.SWP_NOZORDER));
    }
}
