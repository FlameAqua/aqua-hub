using System.IO.Compression;
using System.Text;

namespace AquaHub.E2E.Infrastructure;

/// <summary>
/// Captures the app's own windows (PrintWindow, so nothing else on the user's desktop is recorded) to PNG.
/// </summary>
public static class Screenshots
{
    /// <summary>Captures every visible top-level window of the process; returns the files written.</summary>
    public static List<string> CaptureProcess(int pid, string label)
    {
        var files = new List<string>();
        var i = 0;
        foreach (var hwnd in Win32.WindowsOf(pid))
        {
            try
            {
                if (!Win32.GetWindowRect(hwnd, out var r) || r.Width < 8 || r.Height < 8) continue;
                var title = Win32.TitleOf(hwnd);
                var safe = Sanitize($"{label}-{i++}-{(title.Length > 0 ? title : Win32.ClassOf(hwnd))}");
                var file = Path.Combine(E2EConfig.ScreenshotDir, safe + ".png");
                if (CaptureWindow(hwnd, file)) files.Add(file);
            }
            catch { /* best effort */ }
        }
        return files;
    }

    /// <summary>Average luminance (0–255) of a window's content, sampled from a PrintWindow capture.</summary>
    public static double MeanLuminance(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var r)) return -1;
        int w = r.Width, h = r.Height;
        var screen = Win32.GetDC(IntPtr.Zero);
        var mem = Win32.CreateCompatibleDC(screen);
        var bmp = Win32.CreateCompatibleBitmap(screen, w, h);
        var old = Win32.SelectObject(mem, bmp);
        try
        {
            if (!Win32.PrintWindow(hwnd, mem, 2)) return -1;
            Win32.SelectObject(mem, old);
            var bmi = new Win32.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            if (Win32.GetDIBits(mem, bmp, 0, (uint)h, px, ref bmi, 0) == 0) return -1;
            double sum = 0;
            long n = 0;
            for (var i = 0; i < px.Length; i += 4 * 7)
            {
                sum += 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                n++;
            }
            return n == 0 ? -1 : sum / n;
        }
        finally
        {
            Win32.SelectObject(mem, old);
            Win32.DeleteObject(bmp);
            Win32.DeleteDC(mem);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static bool CaptureWindow(IntPtr hwnd, string file)
    {
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        int w = r.Width, h = r.Height;
        if (w <= 0 || h <= 0 || w > 10000 || h > 10000) return false;
        var screen = Win32.GetDC(IntPtr.Zero);
        var mem = Win32.CreateCompatibleDC(screen);
        var bmp = Win32.CreateCompatibleBitmap(screen, w, h);
        var old = Win32.SelectObject(mem, bmp);
        try
        {
            // PW_RENDERFULLCONTENT (2) captures DirectComposition content (Mica/Acrylic WPF windows).
            if (!Win32.PrintWindow(hwnd, mem, 2)) return false;
            Win32.SelectObject(mem, old);
            var bmi = new Win32.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[w * h * 4];
            if (Win32.GetDIBits(mem, bmp, 0, (uint)h, pixels, ref bmi, 0) == 0) return false;
            File.WriteAllBytes(file, EncodePng(pixels, w, h));
            return true;
        }
        finally
        {
            Win32.SelectObject(mem, old);
            Win32.DeleteObject(bmp);
            Win32.DeleteDC(mem);
            Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        var result = sb.ToString();
        return result.Length > 120 ? result[..120] : result;
    }

    // ───────────── Minimal PNG encoder (BGRA top-down → RGBA scanlines) ─────────────
    private static byte[] EncodePng(byte[] bgra, int w, int h)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0); // filter: none
            var row = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var p = row + x * 4;
                raw.WriteByte(bgra[p + 2]);
                raw.WriteByte(bgra[p + 1]);
                raw.WriteByte(bgra[p]);
                raw.WriteByte(255);
            }
        }
        byte[] compressed;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(zs);
            compressed = z.ToArray();
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w);
        WriteBe(ihdr, 4, h);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        var c = new byte[4];
        WriteBe(c, 0, (int)crc);
        s.Write(c);
    }

    private static void WriteBe(byte[] b, int offset, int v)
    {
        b[offset] = (byte)(v >> 24);
        b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8);
        b[offset + 3] = (byte)v;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
