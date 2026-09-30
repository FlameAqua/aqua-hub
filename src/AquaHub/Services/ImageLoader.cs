using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using AquaHub.Core.Net;
using AquaHub.Core.Util;

namespace AquaHub.Services;

/// <summary>
/// Attached async image loading for remote thumbnails: HTTPS only, size-capped, disk-cached,
/// decoded off the UI thread at display size (keeps memory small), and fully disabled by the
/// privacy setting. Usage: &lt;Image svc:Img.Url="{Binding ImageUrl}" svc:Img.DecodeWidth="320"/&gt;
/// </summary>
public static class Img
{
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly Dictionary<string, WeakReference<BitmapSource>> Memory = new();
    private static string? _cacheDir;
    private static HttpFetcher? _http;
    private static Func<bool> _enabled = () => true;

    public static void Init(string cacheDir, HttpFetcher http, Func<bool> enabled)
    {
        _cacheDir = cacheDir;
        _http = http;
        _enabled = enabled;
        Directory.CreateDirectory(cacheDir);
        _ = Task.Run(() => Prune(cacheDir));
    }

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(Img), new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth", typeof(int), typeof(Img), new PropertyMetadata(320));

    public static void SetUrl(DependencyObject d, string? v) => d.SetValue(UrlProperty, v);
    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetDecodeWidth(DependencyObject d, int v) => d.SetValue(DecodeWidthProperty, v);
    public static int GetDecodeWidth(DependencyObject d) => (int)d.GetValue(DecodeWidthProperty);

    private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var url = e.NewValue as string;
        image.Source = null;
        if (string.IsNullOrWhiteSpace(url) || !_enabled() || _http is null) return;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        var width = GetDecodeWidth(image);
        var key = url + "#" + width;
        lock (Memory)
        {
            if (Memory.TryGetValue(key, out var weak) && weak.TryGetTarget(out var hit))
            {
                image.Source = hit;
                return;
            }
        }
        var bmp = await Task.Run(() => LoadAsync(url, width));
        if (bmp is null || GetUrl(image) != url) return;
        lock (Memory) Memory[key] = new WeakReference<BitmapSource>(bmp);
        image.Opacity = 0;
        image.Source = bmp;
        image.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    private static async Task<BitmapSource?> LoadAsync(string url, int width)
    {
        await Gate.WaitAsync();
        try
        {
            byte[]? bytes = null;
            var file = _cacheDir is null ? null : Path.Combine(_cacheDir, Hash.Short(url) + ".img");
            if (file is not null && File.Exists(file)) bytes = await File.ReadAllBytesAsync(file);
            if (bytes is null)
            {
                var res = await _http!.GetAsync(url, maxBytes: 4 * 1024 * 1024);
                if (!res.Ok || res.ContentType is null || !res.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
                bytes = res.Bytes;
                if (file is not null) { try { await File.WriteAllBytesAsync(file, bytes); } catch { } }
            }
            return Decode(bytes, width);
        }
        catch (Exception ex)
        {
            Log.Debug("images", $"{HttpFetcher.Redact(url)}: {ex.Message}");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static BitmapSource? Decode(byte[] bytes, int width)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0) bi.DecodePixelWidth = width;
            bi.StreamSource = new MemoryStream(bytes);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private static void Prune(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var files = new DirectoryInfo(dir).GetFiles("*.img").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (f.LastWriteTimeUtc < cutoff || total > 200L * 1024 * 1024) f.Delete();
            }
        }
        catch { }
    }
}
