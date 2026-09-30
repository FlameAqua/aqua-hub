using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Health;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>
/// Queries the Windows Search index (the service behind Start and Explorer search) through its OLE DB provider: file
/// names in the allowed folders, and document text when asked. Read-only; empty when the service is off.
/// </summary>
internal static class WindowsSearch
{
    public static List<IndexedFile> Query(IReadOnlyList<string> terms, IReadOnlyList<string> scopes, bool content, int max)
    {
        var list = new List<IndexedFile>();
        var clean = terms.Select(t => new string(t.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray())).Where(t => t.Length >= 2).Distinct().Take(6).ToList();
        if (clean.Count == 0 || scopes.Count == 0) return list;
        var scope = string.Join(" OR ", scopes.Select(sc => "SCOPE='file:" + sc.Replace('\\', '/').Replace("'", "''") + "'"));
        var name = string.Join(" OR ", clean.Select(t => "System.FileName LIKE '%" + t + "%'"));
        var text = content ? " OR CONTAINS(*, '" + string.Join(" OR ", clean.Select(t => "\"" + t + "*\"")) + "')" : "";
        var sql = $"SELECT TOP {max} System.ItemPathDisplay, System.DateModified, System.Size, System.FileName FROM SystemIndex " +
                  $"WHERE ({scope}) AND System.ItemType <> 'Directory' AND (({name}){text}) ORDER BY System.DateModified DESC";
        var type = Type.GetTypeFromProgID("ADODB.Connection");
        if (type is null) return list;
        dynamic? connection = null;
        dynamic? records = null;
        try
        {
            connection = Activator.CreateInstance(type)!;
            connection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            records = connection.Execute(sql);
            while (!(bool)records.EOF)
            {
                string path = records.Fields.Item("System.ItemPathDisplay").Value?.ToString() ?? "";
                if (path.Length > 0)
                {
                    object? modified = records.Fields.Item("System.DateModified").Value;
                    object? size = records.Fields.Item("System.Size").Value;
                    string fileName = records.Fields.Item("System.FileName").Value?.ToString() ?? "";
                    var byName = clean.Any(t => fileName.Contains(t, StringComparison.OrdinalIgnoreCase));
                    list.Add(new IndexedFile(path, modified is DateTime d ? d.ToLocalTime() : DateTime.MinValue, size is null or DBNull ? 0 : Convert.ToInt64(size, CultureInfo.InvariantCulture), !byName));
                }
                records.MoveNext();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException or ArgumentException)
        {
            Log.Debug("ask", "Windows Search isn't available: " + ex.Message);
        }
        finally
        {
            try { records?.Close(); } catch { }
            try { connection?.Close(); } catch { }
            if (records is not null) Marshal.FinalReleaseComObject(records);
            if (connection is not null) Marshal.FinalReleaseComObject(connection);
        }
        return list;
    }
}

/// <summary>Windows side of Ask: OCR, PDF reading, image preparation, known folders and the "Use my PC" tools.</summary>
public sealed class AskPlatform : IAskPlatform
{
    public IEnumerable<AskTool> ComputerTools()
    {
        var tools = new List<AskTool>
        {
            new ScreenshotTool(), new OpenItemTool(), new LaunchAppTool(), new MediaControlTool(), new SystemStatusTool(), new HealthCheckTool(), new ClipboardTool(),
            new RunSceneTool(), new DoNotDisturbTool(),
        };
        // Operating app windows (Settings › Ask Aqua › "Let Ask operate apps").
        if (Hub.S.Ask.ControlApps)
            tools.AddRange(new AskTool[] { new ListWindowsTool(), new ReadWindowTool(), new FocusWindowTool(), new ClickTool(), new TypeTextTool(), new PressKeysTool(), new CloseWindowTool() });
        return tools;
    }

    public Task<IReadOnlyList<IndexedFile>> SearchIndexAsync(IReadOnlyList<string> terms, IReadOnlyList<string> scopes, bool content, CancellationToken ct) =>
        Task.Run<IReadOnlyList<IndexedFile>>(() => WindowsSearch.Query(terms, scopes, content, 80), ct);

    public Task<(string Text, int Pages, int Total)> ReadPdfAsync(byte[] pdf, int maxPages, CancellationToken ct) => OcrService.ReadPdfAsync(pdf, maxPages, ct);
    public Task<string> ReadImageTextAsync(byte[] image, CancellationToken ct) => OcrService.ReadImageAsync(image, ct);
    public byte[] PrepareImage(byte[] image, int maxEdge = 1600) => Images.Prepare(image, maxEdge);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The real Downloads folder (it can be moved); other tokens use the .NET defaults.</summary>
    public string? KnownFolder(string token)
    {
        if (token != "DOWNLOADS") return null;
        var id = DownloadsId;
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>Reads what the user attached: text from documents, OCR for PDFs, images prepared for the model (with OCR too).</summary>
    public async Task<AskAttachment> AttachFileAsync(string path, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        var s = Hub.S.Ask;
        if (Documents.IsImage(path))
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return await AttachImageAsync(bytes, name, path, ct);
        }
        if (Documents.IsPdf(path))
        {
            var (text, pages, total) = await OcrService.ReadPdfAsync(await File.ReadAllBytesAsync(path, ct), s.MaxPdfPages, ct);
            return new AskAttachment
            {
                Name = name, Kind = AttachmentKind.Document, Path = path, Text = text,
                Note = pages < total ? $"first {pages} of {total} pages" : Plural.Of(total, "page"),
            };
        }
        var content = await Task.Run(() => Documents.ReadText(path), ct);
        return new AskAttachment { Name = name, Kind = AttachmentKind.Document, Path = path, Text = content, Note = Plural.Of(content.Length / 5, "word") };
    }

    public async Task<AskAttachment> AttachImageAsync(byte[] bytes, string name, string? path = null, CancellationToken ct = default)
    {
        var prepared = Images.Prepare(bytes);
        var text = await OcrService.ReadImageAsync(prepared, ct);
        return new AskAttachment { Name = name, Kind = AttachmentKind.Image, Path = path, Image = prepared, Text = text };
    }
}

// ───────────────────────────── Tools that act on or look at this PC ─────────────────────────────

internal sealed class ScreenshotTool : AskTool
{
    public override string Name => "take_screenshot";
    public override string Description =>
        "Take a screenshot to see what the user sees (Aqua's own window is hidden while capturing). By default the screen they're working on " +
        "(where their front window is); or pick a screen (1 = leftmost, \"main\", \"all\"), or one app window by title or app name — even if it's behind others.";
    public override JsonObject Parameters => Schema(
        ("screen", "string", "Optional: a screen number (1 = leftmost), or \"left\", \"right\", \"main\", \"second\", \"all\". Default: the screen the user is working on", false),
        ("window", "string", "Optional: capture only this app window, by its title or app name (e.g. Chrome, Spotify, Word)", false));
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "image";
    public override bool NeedsApproval(AskSettings s) => s.ConfirmScreenshots;
    public override string Describe(JsonElement args) =>
        Arg(args, "window") is { Length: > 0 } w ? $"Take a screenshot of “{HtmlText.Truncate(w, 40)}”"
        : Arg(args, "screen").ToLowerInvariant() is { Length: > 0 } s ? s == "all" ? "Take a screenshot of all screens" : "Take a screenshot of screen " + s
        : "Take a screenshot";

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var monitors = ScreenCapture.Monitors();
        byte[]? png;
        string what;
        if (Arg(args, "window") is { Length: > 0 } name)
        {
            var w = DesktopWindows.Find(name);
            if (w is null) return ToolResult.Fail($"no open window matches “{name}” — list_windows shows what's open");
            if (DesktopRules.IsPrivateWindow(w.Process)) return ToolResult.Fail($"Aqua doesn't look at {w.Process} (password managers and sign-in prompts are the user's alone)");
            if (w.Minimized) return ToolResult.Fail($"“{w.Title}” is minimised — it has to be on screen to be captured (focus_window can bring it back)");
            png = await Task.Run(() => ScreenCapture.CaptureWindow(w.Handle, w.Title), ct);
            what = $"the window “{w.Title}” ({w.Process})";
        }
        else
        {
            var choice = Arg(args, "screen").Trim().ToLowerInvariant();
            if (choice is "all" or "every" or "both")
            {
                png = await Hub.Ui.InvokeAsync(ScreenCapture.CaptureAllAsync).Task.Unwrap();
                what = monitors.Count == 1 ? "the screen" : $"all {monitors.Count} screens side by side";
            }
            else
            {
                var primary = monitors.FirstOrDefault(m => m.Primary)?.Number ?? 1;
                var number = choice.Length > 0 ? AskAgent.ScreenNumber(choice, monitors.Count, primary) : null;
                var picked = number is { } k ? monitors.FirstOrDefault(m => m.Number == k) : null;
                if (choice.Length > 0 && picked is null)
                    return ToolResult.Fail($"there's no screen “{choice}” — this PC has {Plural.Of(monitors.Count, "screen")} (1 = leftmost; " +
                                           "“left”, “right”, “main”, “second” and “all” work too)");
                // By default: where the user is working — the screen of their front window (not Aqua's).
                var front = DesktopWindows.List().FirstOrDefault(x => !x.Minimized);
                var monitor = picked ?? (front is not null ? ScreenCapture.MonitorOf(front.Handle, monitors) : null) ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
                if (monitor is null) return ToolResult.Fail("no screen found");
                png = await Hub.Ui.InvokeAsync(() => ScreenCapture.CaptureMonitorAsync(monitor)).Task.Unwrap();
                var inFront = DesktopWindows.List().FirstOrDefault(x => !x.Minimized && ScreenCapture.MonitorOf(x.Handle, monitors)?.Number == monitor.Number);
                what = monitors.Count == 1 ? "the screen" : monitor.Label + (inFront is not null ? $", where “{HtmlText.Truncate(inFront.Title, 60)}” is in front" : "");
            }
        }
        if (png is null) return ToolResult.Fail(Arg(args, "window") is { Length: > 0 }
            ? "Windows couldn't draw that window for a capture — try the whole screen it's on instead"
            : "the screen couldn't be captured");
        run.SawPrivate = true;
        var prepared = Images.Prepare(png);
        var ocr = await OcrService.ReadImageAsync(prepared, ct);
        // Its own kind: a moment's view of the screen is never kept with the chat.
        var n = run.Book.Add("Screenshot", "Your screen", null, "screen", ocr);
        var sb = new StringBuilder();
        sb.Append('[').Append(n).Append("] Screenshot of ").Append(what).Append(run.Vision ? ", attached for you to look at." : " (you can't see images; its text is below).");
        if (monitors.Count > 1)
            sb.Append("\nThis PC has ").Append(monitors.Count).Append(" screens: ").Append(string.Join("; ", monitors.Select(m => m.Label)))
              .Append(". To see another, call take_screenshot with screen = a number or \"all\", or window = an app.");
        // Addresses in view, top of the screen first (a browser's address bar): with Web on, the page itself can be read when
        // the question needs it.
        var links = AskPlanner.ScreenLinks(ocr);
        if (links.Count > 0)
            sb.Append("\nWeb addresses visible on screen, top first (the first is most likely the address bar): ").Append(string.Join(", ", links.Take(3)))
              .Append(run.Options.UsesWeb ? " — if the question is about that page, read_webpage can open it for the full text." : ".");
        sb.Append(ocr.Length > 0 ? "\nText on screen (OCR):\n" + HtmlText.Truncate(ocr, run.Vision ? 4000 : 6000) : "\n(no text found on screen)");
        return new ToolResult(sb.ToString(), "captured", Image: run.Vision ? prepared : null);
    }
}

internal sealed class OpenItemTool : AskTool
{
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".hta", ".cpl",
        ".jar", ".lnk", ".url", ".reg", ".pif", ".appref-ms", ".application", ".msc", ".gadget", ".inf", ".sys", ".dll",
    };

    public override string Name => "open_item";
    public override string Description =>
        "Open something for the user: a web link (https), or a document or folder in the folders they allowed. Programs and scripts are never opened.";
    public override JsonObject Parameters => Schema(("target", "string", "An https:// link, or the full path of a file or folder", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override string Icon => "external";
    // A link in full (the approval card must show exactly what the browser would be sent to); a file by its name.
    public override string Describe(JsonElement args) => "Open " + Short(Arg(args, "target"));
    public override bool OpensLink(JsonElement args) => Arg(args, "target").StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private static string Short(string t) => t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? HtmlText.Truncate(t, 400) + " in your browser" : Path.GetFileName(t.TrimEnd('\\')) is { Length: > 0 } n ? n : t;

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var target = Arg(args, "target");
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AppLauncher.OpenUrl(target) ? new ToolResult("Opened " + target + " in the browser.", "opened") : ToolResult.Fail("that link can't be opened"));
        var files = new LocalFiles(() => LocalFiles.ExpandFolders(Hub.S.Ask.Folders, run.Platform is { } p ? p.KnownFolder : null));
        if (!files.CanRead(target, out var full, out var reason)) return Task.FromResult(ToolResult.Fail(reason));
        if (Runnable.Contains(Path.GetExtension(full))) return Task.FromResult(ToolResult.Fail("Aqua never opens programs, scripts or shortcuts"));
        if (!File.Exists(full) && !Directory.Exists(full)) return Task.FromResult(ToolResult.Fail("it doesn't exist"));
        if (Sandbox.Intercept("open-file", full)) return Task.FromResult(new ToolResult("Opened " + full, "opened"));
        try
        {
            if (Directory.Exists(full)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + full + "\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
            return Task.FromResult(new ToolResult("Opened " + full, "opened"));
        }
        catch (Exception ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
    }
}

internal sealed class LaunchAppTool : AskTool
{
    /// <summary>Windows Settings pages Ask may open by name (ms-settings: links only; nothing is changed).</summary>
    internal static readonly Dictionary<string, string> SettingsPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["settings"] = "ms-settings:", ["bluetooth"] = "ms-settings:bluetooth", ["display"] = "ms-settings:display", ["sound"] = "ms-settings:sound",
        ["wifi"] = "ms-settings:network-wifi", ["wi-fi"] = "ms-settings:network-wifi", ["network"] = "ms-settings:network", ["windows update"] = "ms-settings:windowsupdate",
        ["apps"] = "ms-settings:appsfeatures", ["default apps"] = "ms-settings:defaultapps", ["notifications"] = "ms-settings:notifications",
        ["power"] = "ms-settings:powersleep", ["battery"] = "ms-settings:batterysaver", ["storage"] = "ms-settings:storagesense", ["mouse"] = "ms-settings:mousetouchpad",
        ["keyboard"] = "ms-settings:typing", ["printers"] = "ms-settings:printers", ["privacy"] = "ms-settings:privacy", ["microphone"] = "ms-settings:privacy-microphone",
        ["camera"] = "ms-settings:privacy-webcam", ["speech"] = "ms-settings:privacy-speech", ["personalisation"] = "ms-settings:personalization",
        ["personalization"] = "ms-settings:personalization", ["background"] = "ms-settings:personalization-background", ["themes"] = "ms-settings:themes",
        ["night light"] = "ms-settings:nightlight", ["focus"] = "ms-settings:focus", ["vpn"] = "ms-settings:network-vpn", ["date and time"] = "ms-settings:dateandtime",
        ["language"] = "ms-settings:regionlanguage", ["accounts"] = "ms-settings:yourinfo", ["gaming"] = "ms-settings:gaming-gamebar", ["about"] = "ms-settings:about",
    };

    public override string Name => "launch_app";
    public override string Description =>
        "Start an app by name: the user's Launchpad apps, anything in the Start menu (e.g. Notepad, Spotify, Calculator, Word), their default app for a job " +
        "(\"browser\", \"email\", \"music player\", \"photos\", \"pdf reader\") or a Windows Settings page (e.g. \"bluetooth settings\").";
    public override JsonObject Parameters => Schema(("name", "string", "The app's name, e.g. Spotify, or a settings page like \"sound settings\"", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override string Icon => "launchpad";
    public override string Describe(JsonElement args) => "Open " + Arg(args, "name");

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var name = Arg(args, "name").Trim();
        if (name.Length == 0) return ToolResult.Fail("say which app");
        // A Windows Settings page ("bluetooth settings", "settings › sound").
        var settingsName = System.Text.RegularExpressions.Regex.Replace(name, @"\b(windows\s+)?settings?\b|›|>|page", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (name.Contains("setting", StringComparison.OrdinalIgnoreCase) && SettingsPages.TryGetValue(settingsName.Length == 0 ? "settings" : settingsName, out var page))
            return AppLauncher.OpenUrl(page, allowAppProtocols: true) ? new ToolResult($"Opened Windows Settings ({(settingsName.Length == 0 ? "home" : settingsName)}).", "opened") : ToolResult.Fail("Settings couldn't be opened");
        // "My browser", "the music app": Windows' default for that (unless the Launchpad names one for music).
        if (DefaultApps.RoleOf(name) is { } role && !(role == "music" && Hub.S.Apps.Any(a => a.IsMusicPlayer)))
        {
            if (DefaultApps.For(role) is not { } byDefault) return ToolResult.Fail($"Windows has no default {role} app set");
            if (DesktopRules.LaunchRefusal(byDefault.Name, byDefault.Executable ?? byDefault.AppId ?? "") is { } refused) return ToolResult.Fail(refused);
            return DefaultApps.Launch(byDefault) ? new ToolResult($"Started {byDefault.Name} (your default {role} app)", "started") : ToolResult.Fail(byDefault.Name + " couldn't be started");
        }
        // The Launchpad first, then the Start menu.
        var apps = Hub.S.Apps;
        var app = apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                  ?? apps.FirstOrDefault(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || a.Keywords.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (app is not null)
        {
            if (DesktopRules.LaunchRefusal(app.Name, app.Target + " " + app.Args) is { } refused) return ToolResult.Fail(refused);
            return Hub.Launcher.Launch(app) ? new ToolResult("Started " + app.Name, "started") : ToolResult.Fail(app.Name + " couldn't be started");
        }
        var catalog = await Hub.Catalog.GetAppsAsync().ConfigureAwait(false);
        var found = catalog.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? catalog.Where(a => a.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Name.Length).FirstOrDefault()
                    ?? catalog.Where(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).OrderBy(a => a.Name.Length).FirstOrDefault();
        if (found is null)
            return ToolResult.Fail($"no app called “{name}” in the Launchpad or the Start menu");
        if (DesktopRules.LaunchRefusal(found.Name, found.Target) is { } no) return ToolResult.Fail(no);
        var entry = new AppEntry { Id = "catalog:" + found.Name, Name = found.Name, Kind = found.Kind == "uwp" ? "uwp" : found.Kind == "url" ? "url" : "shortcut", Target = found.Target };
        return Hub.Launcher.Launch(entry) ? new ToolResult("Started " + found.Name, "started") : ToolResult.Fail(found.Name + " couldn't be started");
    }
}

internal sealed class RunSceneTool : AskTool
{
    public override string Name => "run_scene";
    public override string Description => "Run one of the user's scenes (their saved routines of apps, music, volume and do-not-disturb), by name.";
    public override JsonObject Parameters => Schema(("name", "string", "The scene's name", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override string Icon => "wand";
    public override string Describe(JsonElement args) => "Run the scene “" + Arg(args, "name") + "”";

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var name = Arg(args, "name");
        var scene = Hub.S.Scenes.FirstOrDefault(sc => sc.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? Hub.S.Scenes.FirstOrDefault(sc => sc.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (scene is null) return ToolResult.Fail($"no scene called {name} (the user has: {string.Join(", ", Hub.S.Scenes.Select(sc => sc.Name))})");
        await Hub.Ui.InvokeAsync(() => Hub.Actions.RunSceneAsync(scene)).Task.Unwrap().ConfigureAwait(false);
        return new ToolResult("Ran the scene " + scene.Name, "done");
    }
}

internal sealed class DoNotDisturbTool : AskTool
{
    public override string Name => "do_not_disturb";
    public override string Description => "Turn Aqua Hub's do-not-disturb on or off: it holds back Aqua's own notifications and alerts (not other apps').";
    public override JsonObject Parameters => Schema(("on", "boolean", "true to turn do-not-disturb on, false to turn it off", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override string Icon => "bell";
    public override string Describe(JsonElement args) => On(args) ? "Turn on do-not-disturb" : "Turn off do-not-disturb";

    private static bool On(JsonElement args) => Arg(args, "on").ToLowerInvariant() is not ("false" or "off" or "no" or "0");

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var result = await Hub.Ui.InvokeAsync(() => Hub.Actions.ExecuteAsync(new HubCommand(On(args) ? "dnd_on" : "dnd_off"))).Task.Unwrap();
        return new ToolResult(result, result);
    }
}

internal sealed class MediaControlTool : AskTool
{
    public override string Name => "media_control";
    public override string Description => "Control what's playing or the volume: play, pause, next, previous, volume_up, volume_down or mute.";
    public override JsonObject Parameters => Schema(("action", "string", "play, pause, next, previous, volume_up, volume_down or mute", true));
    public override ToolAccess Access => ToolAccess.Act;
    public override string Icon => "play";
    public override string Describe(JsonElement args) => Arg(args, "action") switch
    {
        "play" => "Play media", "pause" => "Pause media", "next" => "Next track", "previous" => "Previous track",
        "volume_up" => "Turn the volume up", "volume_down" => "Turn the volume down", "mute" => "Mute or unmute", var a => "Media: " + a,
    };

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var action = Arg(args, "action") switch
        {
            "play" => "media_play", "pause" => "media_pause", "next" => "media_next", "previous" => "media_previous",
            "volume_up" => "volume_up", "volume_down" => "volume_down", "mute" => "mute_toggle", _ => "",
        };
        if (action.Length == 0) return ToolResult.Fail("unknown media action");
        var result = await Hub.Ui.InvokeAsync(() => Hub.Actions.ExecuteAsync(new HubCommand(action))).Task.Unwrap();
        return new ToolResult(result.Length > 0 ? result : "Done", result.Length > 0 ? result : "done");
    }
}

internal sealed class SystemStatusTool : AskTool
{
    public override string Name => "system_status";
    public override string Description => "How the PC is doing right now: CPU, memory, GPU, disks, battery, network and the busiest processes.";
    public override JsonObject Parameters => Schema();
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "system";
    public override string Describe(JsonElement args) => "Checked this PC";

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var snap = Hub.State.System;
        if (snap is null) return Task.FromResult(ToolResult.Fail("no system readings yet"));
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"{snap.MachineName} · {snap.OsName} · up {(int)snap.Uptime.TotalHours} h\n");
        sb.Append(inv, $"CPU {snap.CpuPercent:0}% ({snap.CpuName}, {snap.LogicalCores} threads)\n");
        sb.Append(inv, $"Memory {snap.RamUsedGb:0.0} of {snap.RamTotalGb:0.0} GB ({snap.RamPercent:0}%)\n");
        if (snap.Gpu is { } g) sb.Append(inv, $"GPU {g.Name}: {g.Utilization:0}% busy, VRAM {g.VramUsedGb:0.0} of {g.VramTotalGb:0.0} GB\n");
        foreach (var d in snap.Disks) sb.Append(inv, $"Disk {d.Name} {d.Label}: {d.FreeGb:0} GB free of {d.TotalGb:0} GB\n");
        if (snap.Battery is { } b) sb.Append(inv, $"Battery {b.Percent}%{(b.Charging ? " (charging)" : "")}\n");
        sb.Append(inv, $"Network ↓ {snap.NetDownBps / 1e6 * 8:0.0} Mbit/s ↑ {snap.NetUpBps / 1e6 * 8:0.0} Mbit/s\n");
        sb.Append("Busiest processes: ").Append(string.Join(", ", snap.TopProcesses.Take(8).Select(p => string.Create(inv, $"{p.Name} ({p.Cpu:0}% CPU, {p.MemoryMb:0} MB)")))).Append('\n');
        run.SawPrivate = true;
        return Task.FromResult(new ToolResult(sb.ToString(), "done"));
    }
}

/// <summary>The PC health check (This PC › Health check), for "is my PC OK?", "why does it keep crashing?", "what maintenance does it need?".</summary>
internal sealed class HealthCheckTool : AskTool
{
    public override string Name => "check_pc_health";
    public override string Description =>
        "Run a health check of this PC (read-only, about 10 seconds): drive space and drive health, Windows Update and pending restarts, blue screens " +
        "and app crashes, antivirus, devices with problems, battery wear, startup apps and clutter. Use it when the user asks whether the PC is healthy, " +
        "why it is slow or crashing, or what maintenance it needs. Each finding says what to do.";
    public override JsonObject Parameters => Schema();
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "pulse";
    public override string Describe(JsonElement args) => "Ran a health check of this PC";

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        // The findings are enough for the model; the page's own summary would be a second model call.
        var report = await Hub.Health.RunAsync(summarise: false, ct, reuseFor: TimeSpan.FromMinutes(10));
        run.SawPrivate = true;
        return new ToolResult(PcHealth.ToText(report) + "\nThe user can see this report and open the tools it mentions on the This PC page (Health check).", report.Verdict);
    }
}

internal sealed class ClipboardTool : AskTool
{
    public override string Name => "read_clipboard";
    public override string Description => "Read the text the user copied (their clipboard).";
    public override JsonObject Parameters => Schema();
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "copy";
    public override bool NeedsApproval(AskSettings s) => s.ConfirmActions;
    public override string Describe(JsonElement args) => "Read your clipboard";

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var text = await Hub.Ui.InvokeAsync(ActionExecutor.ClipboardText);
        if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("the clipboard has no text");
        run.SawPrivate = true;
        var n = run.Book.Add("Clipboard", "Your clipboard", null, "clipboard");
        return new ToolResult($"[{n}] Clipboard text:\n" + HtmlText.Truncate(text, 8000), Plural.Of(ReadWords(text), "word"));
    }

    private static int ReadWords(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
