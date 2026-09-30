using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.Platform;

/// <summary>
/// Windows' own default apps (Settings › Apps › Default apps) for when nothing is chosen in the Launchpad: the music app
/// that opens .mp3 files (Media Player unless you changed it), the browser that opens links, the mail app, and the
/// video, photo, PDF and text apps. Looked up from Windows' file associations each time, so a change is picked up.
/// </summary>
public static class DefaultApps
{
    public sealed record DefaultApp(string Role, string Name, string? Executable, string? AppId);

    /// <summary>Roles, what Windows associates them with, and how people ask for them.</summary>
    public static readonly (string Role, string Assoc, string[] Aliases)[] Roles =
    {
        ("music", ".mp3", new[] { "music", "music player", "music app", "my music", "media player" }),
        ("browser", "http", new[] { "browser", "web browser", "my browser", "internet" }),
        ("email", "mailto", new[] { "email", "e-mail", "mail", "mail app", "email app", "my email", "my mail" }),
        ("video", ".mp4", new[] { "video player", "videos app", "movie player" }),
        ("photos", ".jpg", new[] { "photos", "photos app", "photo viewer", "image viewer", "picture viewer" }),
        ("pdf", ".pdf", new[] { "pdf reader", "pdf viewer", "pdf app" }),
        ("text", ".txt", new[] { "text editor", "notes app" }),
    };

    private const uint AssocfInitIgnoreUnknown = 0x400;
    private const uint AssocstrExecutable = 2, AssocstrFriendlyAppName = 4, AssocstrAppId = 21;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint AssocQueryString(uint flags, uint str, string assoc, string? extra, StringBuilder? output, ref uint length);

    private static string? Query(uint what, string assoc)
    {
        try
        {
            uint length = 0;
            AssocQueryString(AssocfInitIgnoreUnknown, what, assoc, null, null, ref length);
            if (length <= 1) return null;
            var sb = new StringBuilder((int)length);
            return AssocQueryString(AssocfInitIgnoreUnknown, what, assoc, null, sb, ref length) == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>The role a request names ("my browser", "music player"), or null.</summary>
    public static string? RoleOf(string request)
    {
        var r = request.Trim().ToLowerInvariant().Replace("the ", "").Replace("default ", "");
        return Roles.FirstOrDefault(x => x.Aliases.Contains(r) || x.Role == r).Role;
    }

    /// <summary>Windows' default app for a role, or null when Windows doesn't name one.</summary>
    public static DefaultApp? For(string role)
    {
        var entry = Roles.FirstOrDefault(r => r.Role == role);
        if (entry.Role is null) return null;
        var name = Query(AssocstrFriendlyAppName, entry.Assoc);
        var exe = Query(AssocstrExecutable, entry.Assoc);
        var appId = Query(AssocstrAppId, entry.Assoc);
        if (name is null && exe is null && appId is null) return null;
        // No default chosen: Windows answers with its "Pick an application" (OpenWith) dialog.
        if (exe?.EndsWith("OpenWith.exe", StringComparison.OrdinalIgnoreCase) == true && appId is null) return null;
        // Store apps (Media Player, Photos) are started by their app ID; the file under WindowsApps can't be run directly.
        var packaged = appId is { Length: > 0 } && appId.Contains('!');
        return new DefaultApp(role, name ?? Path.GetFileNameWithoutExtension(exe ?? appId ?? role), packaged ? null : exe, packaged ? appId : null);
    }

    public static bool IsRunning(DefaultApp app)
    {
        var process = app.Executable is { } exe ? Path.GetFileNameWithoutExtension(exe)
            : app.AppId?.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase) == true ? "Microsoft.Media.Player" : null;
        if (process is null) return false;
        try { return Process.GetProcessesByName(process).Length > 0; } catch { return false; }
    }

    public static bool Launch(DefaultApp app)
    {
        if (Sandbox.Intercept("launch-default", app.Role + ": " + app.Name)) return true;
        try
        {
            if (app.AppId is { } id) Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + id) { UseShellExecute = true });
            else if (app.Executable is { } exe && File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            else return false;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("launcher", $"Couldn't start the default {app.Role} app", ex);
            return false;
        }
    }
}
