using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using AquaHub.Core.Util;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AquaHub.Platform;

/// <summary>
/// Text recognition with Windows' built-in OCR (Windows.Media.Ocr) and PDF rendering (Windows.Data.Pdf) — both run on
/// this PC, no service involved. PDFs are rendered page by page and read with OCR, which also covers scanned documents.
/// </summary>
public static class OcrService
{
    private static OcrEngine? _engine;
    private static bool _checked;

    private static OcrEngine? Engine()
    {
        if (_checked) return _engine;
        _checked = true;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        }
        catch (Exception ex) { Log.Warn("ocr", "Windows OCR is unavailable", ex); }
        if (_engine is null) Log.Warn("ocr", "No OCR language installed (Settings › Time & language › Language › add an OCR-capable language)");
        return _engine;
    }

    public static bool Available => Engine() is not null;

    public static async Task<string> ReadImageAsync(byte[] image, CancellationToken ct = default)
    {
        var engine = Engine();
        if (engine is null) return "";
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(image.AsBuffer()).AsTask(ct);
            stream.Seek(0);
            return await RecognizeAsync(engine, stream, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("ocr", "Couldn't read text in the image", ex);
            return "";
        }
    }

    private static async Task<string> RecognizeAsync(OcrEngine engine, IRandomAccessStream stream, CancellationToken ct)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        var (w, h) = (decoder.PixelWidth, decoder.PixelHeight);
        var max = OcrEngine.MaxImageDimension;
        var transform = new BitmapTransform();
        if (w > max || h > max)
        {
            var scale = Math.Min(max / (double)w, max / (double)h);
            transform.ScaledWidth = (uint)(w * scale);
            transform.ScaledHeight = (uint)(h * scale);
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    /// <summary>Renders and reads up to <paramref name="maxPages"/> pages. Returns the text, pages read and total pages.</summary>
    public static async Task<(string Text, int Pages, int Total)> ReadPdfAsync(byte[] pdf, int maxPages, CancellationToken ct = default)
    {
        var engine = Engine();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pdf.AsBuffer()).AsTask(ct);
        stream.Seek(0);
        PdfDocument doc;
        try { doc = await PdfDocument.LoadFromStreamAsync(stream).AsTask(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("That PDF couldn't be opened (it may be password-protected or damaged)", ex);
        }
        var total = (int)doc.PageCount;
        if (engine is null) return ("", 0, total);
        var pages = Math.Min(total, Math.Max(1, maxPages));
        var sb = new StringBuilder();
        for (uint i = 0; i < pages; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            using var rendered = new InMemoryRandomAccessStream();
            // ~150 dpi for a letter/A4 page: sharp enough for OCR, small enough to be quick.
            await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = 1700 }).AsTask(ct);
            rendered.Seek(0);
            var text = await RecognizeAsync(engine, rendered, ct);
            if (text.Trim().Length == 0) continue;
            if (pages > 1) sb.Append("## Page ").Append(i + 1).Append('\n');
            sb.Append(text).Append("\n\n");
        }
        return (sb.ToString(), pages, total);
    }
}
