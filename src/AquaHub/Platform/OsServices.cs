using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Services;
using Microsoft.Win32;

namespace AquaHub.Platform;

/// <summary>One running instance per user; later launches signal the first one and exit.</summary>
public sealed class SingleInstance : IDisposable
{
    public static readonly string[] Pages = { "today", "news", "social", "markets", "upcoming", "system", "launchpad", "ask", "agents", "settings" };

    /// <summary>What a second launch can ask the running instance to do (`--page markets` becomes "page-markets").</summary>
    public static readonly string[] Commands = new[] { "activate", "flyout", "palette", "tray-menu", "read-brief", "quit" }
        .Concat(Pages.Select(p => "page-" + p)).ToArray();
    private readonly Mutex _mutex;
    private readonly Dictionary<string, EventWaitHandle> _events = new();
    private readonly List<RegisteredWaitHandle> _waits = new();
    private readonly bool _owned;

    /// <param name="scope">Profile folder: one instance per profile (tests can run beside the real app).</param>
    public SingleInstance(string name, string scope)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var id = Hash.Short(sid, name, scope.ToLowerInvariant());
        _mutex = new Mutex(true, $"Local\\{name}.{id}", out _owned);
        foreach (var cmd in Commands)
            _events[cmd] = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{name}.{id}.{cmd}");
    }

    public bool IsFirst => _owned;

    /// <summary>Asks the running instance to perform a command (activate, flyout, palette, tray-menu, read-brief, quit, page-…).</summary>
    public void Signal(string command) => _events.GetValueOrDefault(command, _events["activate"]).Set();

    public void Listen(Action<string> onCommand)
    {
        foreach (var (cmd, handle) in _events)
            _waits.Add(ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => onCommand(cmd), null, Timeout.Infinite, false));
    }

    public void Dispose()
    {
        foreach (var w in _waits) w.Unregister(null);
        if (_owned) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
        foreach (var e in _events.Values) e.Dispose();
    }
}

/// <summary>Per-user autostart via the HKCU Run key (no admin rights, easy to audit).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Name = "AquaHub";

    /// <summary>Arguments that select this profile (empty for the default one), for anything that relaunches Aqua Hub.</summary>
    public static string ProfileArgs { get; private set; } = "";

    /// <summary>Each profile gets its own Run entry so a test/secondary profile never overwrites the main one.</summary>
    public static void UseProfile(string profileRoot, bool isDefault)
    {
        Name = isDefault ? "AquaHub" : $"AquaHub ({Hash.Short(profileRoot.ToLowerInvariant())[..6]})";
        ProfileArgs = isDefault ? "" : $" --data-dir \"{profileRoot}\"";
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) is string;
    }

    /// <summary>
    /// At start-up: brings the Run entry in line with the setting. An installed copy also takes over an entry that
    /// starts another copy (e.g. a build run from source before installing), and any copy repairs an entry whose
    /// program no longer exists. A source build never takes the entry from an installed copy.
    /// </summary>
    public static void Sync(bool enabled, bool installed)
    {
        string? current;
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) current = key?.GetValue(Name) as string;
        if (!enabled)
        {
            if (current is not null) Set(false);
            return;
        }
        if (current is null) Set(true);
        else if (Environment.ProcessPath is { } exe && RunEntry.ShouldTakeOver(current, exe, installed, File.Exists))
        {
            Log.Info("autostart", "Start with Windows now starts this copy of Aqua Hub");
            Set(true);
        }
    }

    /// <summary>Uninstalling: removes every Aqua Hub Run entry (any profile) that starts this program.</summary>
    public static void RemoveEntriesFor(string? exe)
    {
        if (exe is null) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if ((name == "AquaHub" || name.StartsWith("AquaHub (", StringComparison.Ordinal))
                    && key.GetValue(name) is string command
                    && string.Equals(RunEntry.ProgramOf(command), exe, StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue(name, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("autostart", "Could not remove the Start with Windows entries", ex);
        }
    }

    public static void Set(bool enabled)
    {
        if (Sandbox.Intercept("autostart", enabled.ToString())) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
                key.SetValue(Name, $"\"{exe}\" --background{ProfileArgs}");
            }
            else if (key.GetValue(Name) is not null)
            {
                key.DeleteValue(Name, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("autostart", "Could not update autostart", ex);
        }
    }
}

/// <summary>Secrets in Windows Credential Manager (DPAPI-protected, per user). Never written to settings.json.</summary>
public sealed class CredentialVault : ISecretStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private static string Target(string key) => "AquaHub/" + key;

    public string? Get(string key)
    {
        if (!Native.CredReadW(Target(key), CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Native.CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            Native.CredFree(ptr);
        }
    }

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Native.CredDeleteW(Target(key), CRED_TYPE_GENERIC, 0);
            return;
        }
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Native.CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(key),
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
                Comment = "Aqua Hub",
            };
            if (!Native.CredWriteW(ref cred, 0)) Log.Warn("vault", $"CredWrite failed ({Marshal.GetLastWin32Error()})");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }
}

/// <summary>Cheap OS signals used for eco mode, game mode and notification suppression.</summary>
public static class OsSignals
{
    /// <summary>True while a full-screen app, D3D game or presentation is running.</summary>
    public static bool IsFullscreenBusy()
    {
        if (Native.SHQueryUserNotificationState(out var state) != 0) return false;
        // 2 = QUNS_BUSY, 3 = QUNS_RUNNING_D3D_FULL_SCREEN, 4 = QUNS_PRESENTATION_MODE
        return state is 2 or 3 or 4;
    }

    public static TimeSpan IdleTime()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var now = (uint)Environment.TickCount;
        return TimeSpan.FromMilliseconds(unchecked(now - info.dwTime));
    }

    public static bool IsOnBattery() =>
        Native.GetSystemPowerStatus(out var s) && s.ACLineStatus == 0 && s.BatteryFlag != 128;

    public static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
    }

    /// <summary>The taskbar/Start theme, which can differ from the apps theme.</summary>
    public static bool IsTaskbarDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is not int v || v == 0;
    }

    public static bool TransparencyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("EnableTransparency") is not int v || v != 0;
    }

    /// <summary>Releases unused memory back to the OS after the UI has been closed.</summary>
    public static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Native.SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
    }
}

/// <summary>Honest self-measurement: the same "private working set" figure Task Manager shows, plus commit and GC heap.</summary>
public readonly record struct MemoryStats(double PrivateWorkingSetMb, double CommitMb, double WorkingSetMb, double GcHeapMb)
{
    public override string ToString() =>
        $"{PrivateWorkingSetMb:0} MB private working set, {CommitMb:0} MB committed, {WorkingSetMb:0} MB incl. shared DLLs, GC heap {GcHeapMb:0.0} MB";

    public static MemoryStats Current()
    {
        const double mb = 1048576.0;
        var c = new Counters { cb = (uint)Marshal.SizeOf<Counters>() };
        var gcHeap = GC.GetGCMemoryInfo().HeapSizeBytes / mb;
        // PROCESS_MEMORY_COUNTERS_EX2 (Windows 10 21H2+) has the private working set; older builds only fill EX.
        if (K32GetProcessMemoryInfo(new IntPtr(-1), ref c, c.cb))
            return new(c.PrivateWorkingSetSize / mb, c.PrivateUsage / mb, c.WorkingSetSize / mb, gcHeap);
        c.cb = (uint)(Marshal.SizeOf<Counters>() - 2 * sizeof(ulong));
        return K32GetProcessMemoryInfo(new IntPtr(-1), ref c, c.cb)
            ? new(Math.Min(c.WorkingSetSize, c.PrivateUsage) / mb, c.PrivateUsage / mb, c.WorkingSetSize / mb, gcHeap)
            : new(Environment.WorkingSet / mb, Environment.WorkingSet / mb, Environment.WorkingSet / mb, gcHeap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll")] private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint cb);
}
