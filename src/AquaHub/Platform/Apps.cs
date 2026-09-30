using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.Platform;

public sealed record CatalogApp(string Name, string Target, string Kind);

/// <summary>Discovers installed Start-menu apps (desktop and Store) and renders their icons.</summary>
public sealed class AppCatalog
{
    private List<CatalogApp>? _cache;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Display name for an AppUserModelID / catalog target, if the catalog has been loaded.</summary>
    public string? NameFor(string target) =>
        _cache?.FirstOrDefault(a => a.Target.Equals(target, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>Enumerates shell:AppsFolder on an STA thread (Shell COM objects are apartment-threaded).</summary>
    public Task<List<CatalogApp>> GetAppsAsync()
    {
        if (_cache is not null) return Task.FromResult(_cache);
        var tcs = new TaskCompletionSource<List<CatalogApp>>();
        var thread = new Thread(() =>
        {
            var list = new List<CatalogApp>();
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is not null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    foreach (var item in folder.Items())
                    {
                        string name = item.Name;
                        string path = item.Path;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                        if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                        var kind = path.Contains('!') ? "uwp" : path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? "url" : "shortcut";
                        list.Add(new CatalogApp(name, path, kind));
                    }
                    Marshal.FinalReleaseComObject(folder);
                    Marshal.FinalReleaseComObject(shell);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("apps", "Start menu enumeration failed", ex);
            }
            tcs.TrySetResult(list.GroupBy(a => a.Name).Select(g => g.First()).OrderBy(a => a.Name).ToList());
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task.ContinueWith(t => _cache = t.Result, TaskScheduler.Default);
    }

    /// <summary>Icon for an app entry (cached). Must be called on the UI thread.</summary>
    public ImageSource? IconFor(AppEntry app, int size = 48)
    {
        var key = app.Kind + ":" + app.Target + ":" + size;
        if (_icons.TryGetValue(key, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            var parsing = app.Kind switch
            {
                "uwp" => @"shell:AppsFolder\" + app.Target,
                "shortcut" when !Path.IsPathRooted(app.Target) => @"shell:AppsFolder\" + app.Target,
                _ => Environment.ExpandEnvironmentVariables(app.Target),
            };
            icon = ShellIcon(parsing, size);
        }
        catch (Exception ex)
        {
            Log.Debug("apps", $"Icon for {app.Name} failed: {ex.Message}");
        }
        _icons[key] = icon;
        return icon;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    private static ImageSource? ShellIcon(string parsingName, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
        try
        {
            // SIIGBF_BIGGERSIZEOK (0x1) | SIIGBF_ICONONLY (0x4)
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            try { return FromHBitmap(hbm); }
            finally { Native.DeleteObject(hbm); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (Native.GetObject(hbm, Marshal.SizeOf<Native.BITMAP>(), out var bmp) == 0) return null;
        var w = bmp.bmWidth;
        var h = Math.Abs(bmp.bmHeight);
        var bmi = new Native.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[w * h * 4];
        var hdc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, hdc);
        }
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        source.Freeze();
        return source;
    }
}

/// <summary>Launches/focuses/closes configured apps and opens links — only ever from user configuration.</summary>
public sealed class AppLauncher
{
    public bool Launch(AppEntry app)
    {
        if (Sandbox.Intercept("launch", app.Id)) return true;
        try
        {
            switch (app.Kind)
            {
                case "uwp":
                    Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + app.Target) { UseShellExecute = true });
                    return true;
                case "shortcut" when !Path.IsPathRooted(app.Target):
                    Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + app.Target) { UseShellExecute = true });
                    return true;
                case "url":
                    return OpenUrl(app.Target, allowAppProtocols: true);
                default:
                    var path = Environment.ExpandEnvironmentVariables(app.Target);
                    if (IsRunning(app) && Focus(app)) return true;
                    Process.Start(new ProcessStartInfo(path, app.Args ?? "")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                    });
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("launcher", $"Could not start {app.Name}", ex);
            return false;
        }
    }

    public static string ProcessNameOf(AppEntry app)
    {
        if (!string.IsNullOrWhiteSpace(app.ProcessName)) return app.ProcessName;
        if (app.Kind == "exe") return Path.GetFileNameWithoutExtension(app.Target);
        var name = app.Name.Split(' ')[0];
        return name;
    }

    public bool IsRunning(AppEntry app)
    {
        var name = ProcessNameOf(app);
        if (string.IsNullOrEmpty(name)) return false;
        var procs = Process.GetProcessesByName(name);
        var running = procs.Length > 0;
        foreach (var p in procs) p.Dispose();
        return running;
    }

    public bool Focus(AppEntry app)
    {
        if (Sandbox.Intercept("focus", app.Id)) return true;
        foreach (var p in Process.GetProcessesByName(ProcessNameOf(app)))
        {
            using (p)
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                WindowEffects.ForceForeground(p.MainWindowHandle);
                return true;
            }
        }
        return false;
    }

    /// <summary>Asks the app to close gracefully (like clicking its X). Never kills.</summary>
    public int Close(AppEntry app)
    {
        if (Sandbox.Intercept("close", app.Id)) return 1;
        var closed = 0;
        foreach (var p in Process.GetProcessesByName(ProcessNameOf(app)))
        {
            using (p)
            {
                try { if (p.CloseMainWindow()) closed++; } catch { }
            }
        }
        return closed;
    }

    /// <summary>Opens web links in the default browser. Only http(s) — blocks file:, javascript:, ms-*: and other handlers.</summary>
    public static bool OpenUrl(string? url, bool allowAppProtocols = false)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var ok = uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp ||
                 (allowAppProtocols && uri.Scheme is "spotify" or "steam" or "discord" or "vscode" or "ms-settings");
        if (!ok || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (Sandbox.Intercept("open-url", uri.AbsoluteUri)) return true;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("launcher", "Could not open link", ex);
            return false;
        }
    }

    public bool MusicSearch(string query, IEnumerable<AppEntry> apps)
    {
        if (Sandbox.Intercept("music-search", query)) return true;
        var player = apps.FirstOrDefault(a => a.IsMusicPlayer);
        var q = Uri.EscapeDataString(query.Trim());
        if (player is not null && player.Name.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo("spotify:search:" + q) { UseShellExecute = true });
                return true;
            }
            catch { /* fall through to web */ }
        }
        return OpenUrl("https://music.youtube.com/search?q=" + q);
    }

    /// <summary>Well-known apps that are pre-configured on first run when found in the Start menu.</summary>
    public static readonly (string Match, string Id, string[] Keywords, bool Music)[] Suggested =
    {
        ("Spotify", "spotify", new[] { "music", "songs" }, true),
        ("Visual Studio Code", "vscode", new[] { "code", "editor" }, false),
        ("Discord", "discord", new[] { "chat" }, false),
        ("Steam", "steam", new[] { "games", "gaming" }, false),
        ("Microsoft Edge", "edge", new[] { "browser", "web" }, false),
        ("Google Chrome", "chrome", new[] { "browser", "web" }, false),
        ("Firefox", "firefox", new[] { "browser", "web" }, false),
        ("Outlook", "outlook", new[] { "mail", "email" }, false),
        ("Microsoft Teams", "teams", new[] { "meetings" }, false),
        ("Slack", "slack", new[] { "chat" }, false),
        ("Obsidian", "obsidian", new[] { "notes" }, false),
        ("Notion", "notion", new[] { "notes" }, false),
        ("Terminal", "terminal", new[] { "shell", "console" }, false),
        ("File Explorer", "explorer", new[] { "files" }, false),
        ("Calculator", "calculator", new[] { "calc" }, false),
    };
}
