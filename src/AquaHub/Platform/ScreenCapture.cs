using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.Platform;

/// <summary>
/// Screenshots for Ask: a screen (any of them, numbered left to right), all screens at once, one app window (even when
/// it's behind others), or a region you snip yourself with Windows' Snipping Tool (Win+Shift+S overlay), picked up from
/// the clipboard. Aqua's own windows are hidden for the moment of a screen capture. Nothing is saved to disk; the image
/// goes only to the local model or to on-device OCR.
/// </summary>
public static class ScreenCapture
{
    /// <summary>A monitor, numbered from the left (1 = leftmost).</summary>
    public sealed record Monitor(int Number, Native.RECT Bounds, bool Primary)
    {
        public string Label => $"screen {Number}{(Primary ? " (main)" : "")}, {Bounds.Width}×{Bounds.Height}";
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Native.RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Native.RECT value, int size);

    /// <summary>The monitors as Windows arranges them, numbered left to right (then top to bottom).</summary>
    public static List<Monitor> Monitors()
    {
        var found = new List<(Native.RECT Rect, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(h, ref info)) found.Add((info.rcMonitor, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return found.OrderBy(m => m.Rect.Left).ThenBy(m => m.Rect.Top).Select((m, i) => new Monitor(i + 1, m.Rect, m.Primary)).ToList();
    }

    /// <summary>The monitor a window is on (most of it).</summary>
    public static Monitor? MonitorOf(IntPtr hwnd, IReadOnlyList<Monitor> monitors)
    {
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref info)) return null;
        return monitors.FirstOrDefault(m => m.Bounds.Left == info.rcMonitor.Left && m.Bounds.Top == info.rcMonitor.Top);
    }

    /// <summary>Captures one monitor (Aqua hidden for the moment) as PNG.</summary>
    public static async Task<byte[]?> CaptureMonitorAsync(Monitor monitor)
    {
        if (Sandbox.Intercept("screenshot", "screen " + monitor.Number)) return Fixture();
        return await HiddenAsync(() => Capture(monitor.Bounds));
    }

    /// <summary>Captures every monitor as one image (Aqua hidden for the moment) as PNG.</summary>
    public static async Task<byte[]?> CaptureAllAsync()
    {
        if (Sandbox.Intercept("screenshot", "all screens")) return Fixture();
        var all = Monitors();
        if (all.Count == 0) return null;
        var union = new Native.RECT { Left = all.Min(m => m.Bounds.Left), Top = all.Min(m => m.Bounds.Top), Right = all.Max(m => m.Bounds.Right), Bottom = all.Max(m => m.Bounds.Bottom) };
        return await HiddenAsync(() => Capture(union));
    }

    /// <summary>
    /// Captures one window as PNG — drawn by the window itself (PrintWindow with full content), so it works even when
    /// other windows cover it; falls back to the screen area it occupies.
    /// </summary>
    public static byte[]? CaptureWindow(IntPtr hwnd, string label)
    {
        if (Sandbox.Intercept("screenshot", "window " + label)) return Fixture();
        if (!GetWindowRect(hwnd, out var outer) || outer.Width <= 0 || outer.Height <= 0) return null;
        // The visible frame (without the invisible resize border) inside the window's rectangle.
        var frame = DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var ext, Marshal.SizeOf<Native.RECT>()) == 0 ? ext : outer;
        var screen = Native.GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, outer.Width, outer.Height);
        var old = SelectObject(mem, bmp);
        try
        {
            // Only the window itself: copying the screen instead could pick up whatever covers it (a password manager).
            if (!PrintWindow(hwnd, mem, 2 /* PW_RENDERFULLCONTENT */))
            {
                Log.Info("ask", $"Windows couldn't draw “{label}” for a capture");
                return null;
            }
            SelectObject(mem, old);
            var full = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var crop = new Int32Rect(Math.Max(0, frame.Left - outer.Left), Math.Max(0, frame.Top - outer.Top),
                Math.Min(frame.Width, outer.Width), Math.Min(frame.Height, outer.Height));
            BitmapSource source = crop.Width > 0 && crop.Height > 0 && crop.X + crop.Width <= full.PixelWidth && crop.Y + crop.Height <= full.PixelHeight
                ? new CroppedBitmap(full, crop) : full;
            return Images.Png(source);
        }
        catch (Exception ex)
        {
            Log.Warn("ask", "Window capture failed", ex);
            return null;
        }
        finally
        {
            // A bitmap still selected into a DC can't be deleted: select the original back first, whichever way we got here.
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Hides Aqua's windows for the moment of a screen capture (opacity keeps their state and focus intact).</summary>
    private static async Task<byte[]?> HiddenAsync(Func<byte[]?> capture)
    {
        var windows = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w.Opacity > 0).ToList();
        var faded = windows.Select(w => (Window: w, w.Opacity)).ToList();
        foreach (var (w, _) in faded) w.Opacity = 0;
        try
        {
            await Task.Delay(220);
            return capture();
        }
        finally
        {
            foreach (var (w, opacity) in faded) w.Opacity = opacity;
        }
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    private const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    /// <summary>Captures the monitor under <paramref name="anchor"/> (default: the one Aqua's window is on) as PNG.</summary>
    public static async Task<byte[]?> CaptureScreenAsync(Window? anchor = null)
    {
        if (Sandbox.Intercept("screenshot", "screen")) return Fixture();
        var windows = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w.Opacity > 0).ToList();
        var monitor = MonitorFor(anchor ?? windows.FirstOrDefault(w => w is UI.Shell.MainWindow) ?? windows.FirstOrDefault());
        // Hide Aqua for the moment of capture (opacity keeps the windows' state and focus intact).
        var faded = windows.Select(w => (Window: w, w.Opacity)).ToList();
        foreach (var (w, _) in faded) w.Opacity = 0;
        try
        {
            await Task.Delay(220);
            return Capture(monitor);
        }
        finally
        {
            foreach (var (w, opacity) in faded) w.Opacity = opacity;
        }
    }

    private static Native.RECT MonitorFor(Window? w)
    {
        IntPtr mon;
        if (w is not null && new WindowInteropHelper(w).Handle is var h && h != IntPtr.Zero) mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
        else
        {
            Native.GetCursorPos(out var pt);
            mon = Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST);
        }
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref info);
        return info.rcMonitor;
    }

    private static byte[]? Capture(Native.RECT r)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        var old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT)) return null;
            SelectObject(mem, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            return Images.Png(source);
        }
        catch (Exception ex)
        {
            Log.Warn("ask", "Screen capture failed", ex);
            return null;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Opens Windows' snipping overlay and waits (up to two minutes) for the snip to land on the clipboard.
    /// Returns null if you cancel or nothing arrives.
    /// </summary>
    public static async Task<byte[]?> SnipAsync(CancellationToken ct = default)
    {
        if (Sandbox.Intercept("screenshot", "snip")) return Fixture();
        var before = GetClipboardSequenceNumber();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("ask", "Couldn't open the snipping overlay", ex); return null; }
        var until = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            await Task.Delay(300, CancellationToken.None);
            if (GetClipboardSequenceNumber() == before) continue;
            if (ClipboardImage() is { } png) return png;
            before = GetClipboardSequenceNumber();
        }
        return null;
    }

    /// <summary>The clipboard's image as PNG, if it holds one.</summary>
    public static byte[]? ClipboardImage()
    {
        try
        {
            if (!Clipboard.ContainsImage()) return null;
            return Clipboard.GetImage() is { } img ? Images.Png(img) : null;
        }
        catch (Exception ex) when (ex is COMException or ExternalException) { return null; }
    }

    /// <summary>A small fixed image for dry-run (e2e) sessions.</summary>
    private static byte[] Fixture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 360));
            dc.DrawText(new FormattedText("Aqua Hub e2e screenshot", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 28, Brushes.Black, 1.0), new Point(40, 150));
        }
        var bmp = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return Images.Png(bmp);
    }
}

/// <summary>Image helpers: PNG/JPEG encoding and scaling for the model.</summary>
public static class Images
{
    public static byte[] Png(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static byte[] Jpeg(BitmapSource source, int quality = 88)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource Decode(byte[] bytes, int? decodeWidth = null)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodeWidth is { } w) img.DecodePixelWidth = w;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>
    /// Scales an image so its long edge is at most <paramref name="maxEdge"/> px (vision models see a downscaled copy
    /// anyway) and re-encodes: PNG for screenshots and graphics, JPEG for large photos.
    /// </summary>
    public static byte[] Prepare(byte[] bytes, int maxEdge = 1600)
    {
        try
        {
            var probe = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var (w, h) = (probe.PixelWidth, probe.PixelHeight);
            BitmapSource src = Math.Max(w, h) <= maxEdge
                ? Decode(bytes)
                : w >= h ? Decode(bytes, maxEdge) : Decode(bytes, (int)Math.Round(w * (maxEdge / (double)h)));
            var png = Png(src);
            return png.Length > 1_500_000 ? Jpeg(src) : png;
        }
        catch (Exception ex)
        {
            Log.Warn("ask", "Couldn't read that image", ex);
            return bytes;
        }
    }

    public static BitmapSource? Thumbnail(byte[] bytes, int width = 120)
    {
        try { return Decode(bytes, width); }
        catch { return null; }
    }
}
