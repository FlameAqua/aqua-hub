using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AquaHub.Platform;

/// <summary>Draws the Aqua Hub mark with WPF and converts it to an HICON (tray icon with live badge).</summary>
public static class IconFactory
{
    private static readonly Color A = Color.FromRgb(0x2B, 0xE0, 0xD2);
    private static readonly Color B = Color.FromRgb(0x14, 0xA9, 0xE8);
    private static readonly Color C = Color.FromRgb(0x25, 0x5C, 0xF0);

    /// <param name="trend">+1 / -1 draws a small up (green) / down (red) marker — the market "ticker" on the tray icon.</param>
    public static DrawingVisual DrawLogo(double s, Color? badge = null, bool paused = false, int trend = 0)
    {
        var dv = new DrawingVisual();
        using var dc = dv.RenderOpen();
        var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        grad.GradientStops.Add(new GradientStop(paused ? Color.FromRgb(0x8A, 0x93, 0x9C) : A, 0));
        grad.GradientStops.Add(new GradientStop(paused ? Color.FromRgb(0x6F, 0x78, 0x82) : B, 0.55));
        grad.GradientStops.Add(new GradientStop(paused ? Color.FromRgb(0x55, 0x5D, 0x66) : C, 1));
        var inset = Math.Max(0.5, s * 0.03);
        var rect = new Rect(inset, inset, s - 2 * inset, s - 2 * inset);
        dc.DrawRoundedRectangle(grad, null, rect, s * 0.24, s * 0.24);
        var white = Brushes.White;
        var c = new Point(s / 2, s / 2);
        var ringR = s * 0.265;
        dc.DrawEllipse(null, new Pen(white, Math.Max(1.15, s * 0.075)), c, ringR, ringR);
        dc.DrawEllipse(white, null, c, s * 0.105, s * 0.105);
        var a = -Math.PI / 4;
        var sat = new Point(c.X + ringR * Math.Cos(a), c.Y + ringR * Math.Sin(a));
        dc.DrawEllipse(new SolidColorBrush(paused ? Color.FromRgb(0x72, 0x7B, 0x85) : Color.FromRgb(0x18, 0x9C, 0xEA)), null, sat, s * 0.105, s * 0.105);
        dc.DrawEllipse(white, null, sat, s * 0.068, s * 0.068);
        if (badge is { } col)
        {
            var r = s * 0.2;
            var p = new Point(s - r - inset * 0.2, s - r - inset * 0.2);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)), null, p, r + s * 0.05, r + s * 0.05);
            dc.DrawEllipse(new SolidColorBrush(col), null, p, r, r);
        }
        if (trend != 0)
        {
            var w = s * 0.46;
            var x0 = inset * 0.2;
            var y0 = s - w * 0.87 - inset * 0.2;
            var tri = trend > 0
                ? new[] { new Point(x0, y0 + w * 0.87), new Point(x0 + w, y0 + w * 0.87), new Point(x0 + w / 2, y0) }
                : new[] { new Point(x0, y0), new Point(x0 + w, y0), new Point(x0 + w / 2, y0 + w * 0.87) };
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(tri[0], true, true);
                g.LineTo(tri[1], true, true);
                g.LineTo(tri[2], true, true);
            }
            geo.Freeze();
            var fill = new SolidColorBrush(trend > 0 ? Color.FromRgb(0x3F, 0xD6, 0x8B) : Color.FromRgb(0xFF, 0x5C, 0x6E));
            dc.DrawGeometry(fill, new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)), Math.Max(1, s * 0.06)), geo);
        }
        return dv;
    }

    public static BitmapSource Render(DrawingVisual dv, int size)
    {
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Creates an HICON the caller must free with <see cref="Native.DestroyIcon"/>.</summary>
    public static IntPtr CreateHIcon(BitmapSource source)
    {
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        var stride = w * 4;
        var pixels = new byte[stride * h];
        source.CopyPixels(pixels, stride, 0);

        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var hdc = Native.GetDC(IntPtr.Zero);
        var color = Native.CreateDIBSection(hdc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, hdc);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var mask = Native.CreateBitmap(w, h, 1, 1, IntPtr.Zero);
        var info = new Native.ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = Native.CreateIconIndirect(ref info);
        Native.DeleteObject(color);
        Native.DeleteObject(mask);
        return icon;
    }

    /// <summary>Tray icon sized for the current system DPI.</summary>
    public static IntPtr CreateTrayIcon(Color? badge = null, bool paused = false, int trend = 0)
    {
        var dpi = Native.GetDpiForSystem();
        var size = Native.GetSystemMetricsForDpi(49 /*SM_CXSMICON*/, dpi);
        if (size <= 0) size = 16;
        return CreateHIcon(Render(DrawLogo(size, badge, paused, trend), size));
    }

    public static IntPtr CreateLargeIcon() => CreateHIcon(Render(DrawLogo(64), 64));
}
