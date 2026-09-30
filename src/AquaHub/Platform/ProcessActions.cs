using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.Platform;

/// <summary>
/// What a process on the This PC page is and what you can do with it: see what it is (description, maker, file), find
/// its file, look it up, or end it. Ending asks first, is refused for what Windows can't run without, and closes
/// windowed apps the polite way (as if you clicked ×) so they can offer to save your work.
/// </summary>
public static class ProcessActions
{
    public sealed record Details(string Name, string? Path, string? Description, string? Company, int Count, bool HasWindow, bool PartOfWindows, string? Protected)
    {
        /// <summary>"Google Chrome" rather than "chrome" when the file says so.</summary>
        public string Title => Description is { Length: > 0 } d && d.Length <= 60 ? d : Name;
        /// <summary>Windows' own apps (Notepad, Paint) are only ever asked to close, never forced.</summary>
        public bool CanForce => Protected is null && !PartOfWindows;
    }

    // Ending these logs you off, crashes Windows or breaks it until a restart — or they are Aqua's own.
    private static readonly Dictionary<string, string> Kept = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = "Windows itself", ["System Idle"] = "not a real process — it's the processor's spare time", ["Idle"] = "not a real process — it's the processor's spare time",
        ["Registry"] = "part of Windows", ["smss"] = "part of Windows", ["csrss"] = "part of Windows — ending it crashes Windows",
        ["wininit"] = "part of Windows — ending it crashes Windows", ["winlogon"] = "part of Windows — ending it signs you out", ["services"] = "part of Windows — ending it crashes Windows",
        ["lsass"] = "part of Windows — ending it restarts the PC", ["LsaIso"] = "part of Windows", ["Secure System"] = "part of Windows", ["Memory Compression"] = "part of Windows",
        ["svchost"] = "hosts Windows services — stop a service in Services instead", ["dwm"] = "draws everything on screen", ["fontdrvhost"] = "part of Windows",
        ["sihost"] = "part of Windows", ["ctfmon"] = "handles typing and input", ["explorer"] = "the taskbar and desktop — Task Manager can restart it",
        ["MsMpEng"] = "Microsoft Defender", ["NisSrv"] = "Microsoft Defender", ["SecurityHealthService"] = "Windows Security", ["audiodg"] = "Windows audio",
        ["ollama"] = "Aqua's local AI — use “Turn off Ollama” instead", ["ollama app"] = "Aqua's local AI — use “Turn off Ollama” instead",
        ["llama-server"] = "it runs Aqua's local AI model — use “Free VRAM” or “Turn off Ollama” instead",
        ["ollama_llama_server"] = "it runs Aqua's local AI model — use “Free VRAM” or “Turn off Ollama” instead",
    };

    /// <summary>Why this process can't be ended from Aqua (by its name alone), or null.</summary>
    public static string? WhyKept(string name) =>
        name.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase) ? "this is Aqua — quit it from the tray instead"
        : Kept.TryGetValue(name, out var why) ? why : null;

    /// <summary>
    /// Why this app can't be ended from Aqua, or null: the named ones above; anything in Ollama's folder (its model runners,
    /// whatever they're called); Windows' own background processes; and background processes Aqua can't identify (no
    /// readable program file — usually Windows' or another account's). Windows apps with a window can be asked to close.
    /// </summary>
    public static string? WhyRefused(string name, string? path, bool hasWindow)
    {
        if (WhyKept(name) is { } kept) return kept;
        if (path is not null && OllamaManager.Executable is { } ollama && Path.GetDirectoryName(ollama) is { Length: > 0 } dir
            && path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return "it's part of Aqua's local AI (Ollama) — use “Free VRAM” or “Turn off Ollama” instead";
        if (!hasWindow && IsWindows(path)) return "it's part of Windows, running in the background — ending it can break Windows until a restart";
        if (!hasWindow && path is null) return "Aqua can't see which program this is (it may be part of Windows or run as administrator) — Task Manager can end it";
        return null;
    }

    private static bool IsWindows(string? path) =>
        path?.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Your own processes with this name: other sessions (other accounts, Windows services) are never touched.</summary>
    private static List<Process> Mine(string name)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var all = Process.GetProcessesByName(name);
        var mine = new List<Process>();
        foreach (var p in all)
        {
            bool same;
            try { same = p.SessionId == session && p.Id != Environment.ProcessId; }
            catch (InvalidOperationException) { same = false; }
            if (same) mine.Add(p);
            else p.Dispose();
        }
        return mine;
    }

    public static Details Describe(string name)
    {
        var processes = Mine(name);
        try
        {
            string? path = null;
            var windowed = false;
            foreach (var p in processes)
            {
                path ??= ImagePath(p.Id);
                windowed |= HasWindow(p);
            }
            FileVersionInfo? info = null;
            try { if (path is not null && File.Exists(path)) info = FileVersionInfo.GetVersionInfo(path); }
            catch (FileNotFoundException) { }
            var partOfWindows = IsWindows(path) || (path is null && Kept.ContainsKey(name));
            return new Details(name, path, Clean(info?.FileDescription), Clean(info?.CompanyName), processes.Count, windowed, partOfWindows,
                WhyRefused(name, path, windowed));
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The process's program file (works for most processes without administrator rights), or null.</summary>
    private static string? ImagePath(int pid)
    {
        const uint queryLimited = 0x1000;
        var handle = OpenProcess(queryLimited, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>Shows the program file selected in File Explorer (never runs it).</summary>
    public static void Reveal(string path)
    {
        if (!File.Exists(path) || Sandbox.Intercept("reveal", path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// Ends every process of the app. Windowed apps are asked to close (they can offer to save) unless
    /// <paramref name="force"/>; background ones end at once. Returns how many are still running a moment later, and
    /// why Windows refused if it did.
    /// </summary>
    public static async Task<(int Left, string? Error)> EndAsync(string name, bool force)
    {
        // Checked again here, against the processes as they are now — not what the page showed a minute ago.
        var now = Describe(name);
        if (now.Protected is { } why) return (0, $"Aqua won't end {name}: {why}.");
        if (force && !now.CanForce) return (0, $"Aqua only asks {now.Title} to close, because it's part of Windows. Task Manager can force it.");
        if (Sandbox.Intercept(force ? "force-end-task" : "end-task", name)) return (0, null);
        var processes = Mine(name);
        string? error = null;
        try
        {
            var windowed = processes.Where(HasWindow).ToList();
            // A windowed app's helpers (browser tabs and the like) close with it; ending them first would crash its pages.
            var targets = !force && windowed.Count > 0 ? windowed : processes;
            foreach (var p in targets)
            {
                try
                {
                    if (!force && windowed.Count > 0) p.CloseMainWindow();
                    else p.Kill();
                }
                catch (Win32Exception) { error = $"Windows didn't let Aqua end {name} — it runs with administrator rights. Task Manager can."; }
                catch (InvalidOperationException) { }   // already gone
            }
            Log.Info("system", $"{(force ? "Forced" : "Asked")} {name} to end ({targets.Count} of {processes.Count} processes)");
            for (var i = 0; i < (force ? 4 : 12); i++)
            {
                await Task.Delay(250);
                if (processes.All(Exited)) break;
            }
            return (processes.Count(p => !Exited(p)), error);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static bool HasWindow(Process p)
    {
        try { return p.MainWindowHandle != IntPtr.Zero; } catch (InvalidOperationException) { return false; }
    }

    private static bool Exited(Process p)
    {
        try { return p.HasExited; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return true; }
    }
}
