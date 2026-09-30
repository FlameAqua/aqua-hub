using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Reflection;
using System.Runtime.InteropServices;
using AquaHub.Core.Health;
using AquaHub.Core.Util;
using AquaHub.Services;
using Microsoft.Win32;

namespace AquaHub.Platform;

/// <summary>
/// The PC health check's eyes: reads what Windows already knows — drive space and drive health, Windows Update's history
/// and pending restart, blue screens and app crashes in the event logs, the antivirus Windows Security reports, devices
/// with errors, battery wear, startup apps, temporary files and the Recycle Bin. Everything is read-only and needs no
/// administrator rights; a check that can't run is listed as not checked rather than failing the rest.
/// </summary>
public static class HealthInspector
{
    public static async Task<HealthFacts> InspectAsync(Action<string>? progress, CancellationToken ct)
    {
        var skipped = new List<string>();
        var snap = Hub.System.Latest ?? await Task.Run(() => { Hub.System.Sample(); return Hub.System.Latest; }, ct);

        async Task<T?> Check<T>(string what, string step, Func<T?> read, int seconds = 15) where T : class
        {
            progress?.Invoke(step);
            try { return await Task.Run(read, ct).WaitAsync(TimeSpan.FromSeconds(seconds), ct); }
            catch (TimeoutException) { lock (skipped) skipped.Add(what + ": Windows took too long to answer"); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or InvalidCastException
                                          or TargetInvocationException or EventLogException or IOException or System.Security.SecurityException)
            {
                var inner = ex is TargetInvocationException { InnerException: { } e } ? e : ex;
                Log.Debug("health", $"{what} not checked: {inner.Message}");
                lock (skipped) skipped.Add(what + ": " + (inner is UnauthorizedAccessException ? "needs administrator rights" : "Windows didn't say"));
            }
            return null;
        }

        var drives = Drives();
        // The slow ones run side by side.
        var disks = Check("Drive health", "Asking the drives how they are…", PhysicalDisks);
        var updates = Check("Windows Update", "Reading Windows Update's history…", UpdateHistory, 25);
        var bsod = Check("Blue screens", "Looking for crashes in Windows' logs…", () => (object)Count("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, 30));
        var shutdowns = Check("Unexpected shutdowns", "Looking for crashes in Windows' logs…", () => (object)Count("System", "Microsoft-Windows-Kernel-Power", 41, 30));
        var crashes = Check("App crashes", "Looking for crashes in Windows' logs…", AppCrashes);
        var antivirus = Check("Antivirus", "Checking Windows Security…", Antivirus);
        var devices = Check("Devices", "Checking devices…", DeviceProblems);
        var battery = snap?.Battery is not null ? Check("Battery wear", "Checking the battery…", Battery) : Task.FromResult<BatteryFact?>(null);
        var startup = Check("Startup apps", "Counting startup apps…", StartupApps);
        var temp = Check("Temporary files", "Measuring temporary files…", () => (object)FolderGb(Path.GetTempPath(), TimeSpan.FromSeconds(6)), 12);
        await Task.WhenAll(disks, updates, bsod, shutdowns, crashes, antivirus, devices, battery, startup, temp);

        var update = await updates;
        var top = snap?.TopProcesses.OrderByDescending(p => p.MemoryMb).FirstOrDefault();
        return new HealthFacts
        {
            Checked = DateTimeOffset.Now,
            Machine = snap is null ? Environment.MachineName : $"{snap.MachineName} · {snap.OsName}",
            Drives = drives,
            PhysicalDisks = await disks,
            Battery = await battery,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            RestartPending = RestartPending(),
            LastUpdate = update?.Last,
            LastUpdateTitle = update?.Title,
            FailedUpdates = update?.Failed,
            BlueScreens = await bsod as int?,
            UnexpectedShutdowns = await shutdowns as int?,
            AppCrashes = await crashes,
            Antivirus = await antivirus,
            DeviceProblems = await devices,
            StartupApps = await startup,
            TempGb = await temp as double?,
            RecycleBinGb = RecycleBinGb(),
            RamPercent = snap?.RamPercent ?? 0,
            RamTotalGb = snap?.RamTotalGb ?? 0,
            TopMemoryApp = top?.Name,
            TopMemoryMb = top?.MemoryMb ?? 0,
            Skipped = skipped.Distinct().ToList(),
        };
    }

    private static List<DriveFact> Drives()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var list = new List<DriveFact>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DriveFact(d.Name.TrimEnd('\\'), SafeLabel(d), d.TotalSize / 1073741824.0, d.AvailableFreeSpace / 1073741824.0,
                    d.Name.Equals(system, StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static List<PhysicalDiskFact> PhysicalDisks() =>
        Wmi.Query(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, HealthStatus FROM MSFT_PhysicalDisk", "FriendlyName", "MediaType", "HealthStatus")
            .Select(r => new PhysicalDiskFact(r["FriendlyName"]?.ToString() ?? "Drive",
                Convert.ToInt32(r["MediaType"] ?? 0) switch { 3 => "hard drive", 4 => "SSD", 5 => "SCM drive", _ => "drive" },
                Convert.ToInt32(r["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" }))
            .Where(d => d.Health != "Unknown").ToList();

    private static List<AntivirusFact> Antivirus() =>
        Wmi.Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct", "displayName", "productState")
            .Select(r =>
            {
                var (on, current) = PcHealth.AntivirusState(Convert.ToInt32(r["productState"] ?? 0));
                return new AntivirusFact(r["displayName"]?.ToString() ?? "Antivirus", on, current);
            }).ToList();

    private static List<string> DeviceProblems() =>
        Wmi.Query(@"root\cimv2", "SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0", "Name", "ConfigManagerErrorCode")
            .Select(r => PcHealth.DeviceProblem(r["Name"]?.ToString() ?? "A device", Convert.ToInt32(r["ConfigManagerErrorCode"] ?? 0)))
            .OfType<string>().Distinct().ToList();

    private static BatteryFact? Battery()
    {
        var design = Wmi.Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity").Sum(r => Convert.ToDouble(r["DesignedCapacity"] ?? 0));
        var full = Wmi.Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity").Sum(r => Convert.ToDouble(r["FullChargedCapacity"] ?? 0));
        int? cycles = null;
        try { cycles = Wmi.Query(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount", "CycleCount").Select(r => Convert.ToInt32(r["CycleCount"] ?? 0)).FirstOrDefault(); }
        catch (Exception ex) when (ex is COMException or TargetInvocationException) { }
        return design > 0 && full > 0 ? new BatteryFact(design / 1000, full / 1000, cycles is > 0 ? cycles : null) : null;
    }

    private sealed record Updates(DateTimeOffset? Last, string? Title, List<string> Failed);

    /// <summary>Windows Update's own history (read-only; nothing is searched for or downloaded).</summary>
    private static Updates UpdateHistory()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new COMException("Windows Update isn't available");
        var session = Activator.CreateInstance(type)!;
        try
        {
            var searcher = Wmi.Call(session, "CreateUpdateSearcher", BindingFlags.InvokeMethod)!;
            var total = Convert.ToInt32(Wmi.Call(searcher, "GetTotalHistoryCount", BindingFlags.InvokeMethod));
            var entries = new List<(DateTimeOffset, string, bool)>();
            // Newest first; Defender's several-a-day definition downloads fill the recent entries, so look well back.
            if (total > 0 && Wmi.Call(searcher, "QueryHistory", BindingFlags.InvokeMethod, 0, Math.Min(total, 400)) is IEnumerable history)
            {
                foreach (var entry in history)
                {
                    if (Convert.ToInt32(Wmi.Call(entry, "Operation", BindingFlags.GetProperty)) != 1) continue;   // installations only
                    var result = Convert.ToInt32(Wmi.Call(entry, "ResultCode", BindingFlags.GetProperty));
                    if (result is not (2 or 3 or 4 or 5)) continue;
                    var date = DateTime.SpecifyKind((DateTime)Wmi.Call(entry, "Date", BindingFlags.GetProperty)!, DateTimeKind.Utc);
                    entries.Add((new DateTimeOffset(date), Wmi.Call(entry, "Title", BindingFlags.GetProperty)?.ToString() ?? "", result is 2 or 3));
                }
            }
            var (last, title, failed) = PcHealth.UpdateHistory(entries, DateTimeOffset.Now);
            return new Updates(last, title, failed);
        }
        finally { Marshal.FinalReleaseComObject(session); }
    }

    private static bool? RestartPending()
    {
        try
        {
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return wu is not null || cbs is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    private static int Count(string log, string provider, int id, int days)
    {
        var query = new EventLogQuery(log, PathType.LogName,
            $"*[System[Provider[@Name='{provider}'] and (EventID={id}) and TimeCreated[timediff(@SystemTime) <= {(long)TimeSpan.FromDays(days).TotalMilliseconds}]]]");
        using var reader = new EventLogReader(query);
        var n = 0;
        while (n < 500 && reader.ReadEvent() is { } e)
        {
            e.Dispose();
            n++;
        }
        return n;
    }

    private static List<CrashFact> AppCrashes()
    {
        var query = new EventLogQuery("Application", PathType.LogName,
            "*[System[Provider[@Name='Application Error' or @Name='Application Hang'] and (EventID=1000 or EventID=1002) and " +
            $"TimeCreated[timediff(@SystemTime) <= {(long)TimeSpan.FromDays(14).TotalMilliseconds}]]]");
        using var reader = new EventLogReader(query);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        while (n++ < 1000 && reader.ReadEvent() is { } e)
        {
            using (e)
            {
                // The first value is the program that crashed or hung ("chrome.exe").
                if (e.Properties.Count == 0 || e.Properties[0].Value is not string { Length: > 0 } app) continue;
                var name = Path.GetFileNameWithoutExtension(app.Trim());
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }
        return counts.Select(kv => new CrashFact(kv.Key, kv.Value)).OrderByDescending(c => c.Count).ToList();
    }

    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Apps that start with Windows and are switched on (Settings › Apps › Startup): the Run keys and Startup folders.</summary>
    private static List<string> StartupApps()
    {
        var names = new List<string>();
        static bool Off(RegistryKey? approved, string name) => approved?.GetValue(name) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
        void FromKey(RegistryKey root, string path, string approvedPath)
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;
            using var approved = root.OpenSubKey(Approved + approvedPath);
            names.AddRange(key.GetValueNames().Where(n => n.Length > 0 && !Off(approved, n)));
        }
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        FromKey(Registry.CurrentUser, run, "Run");
        FromKey(Registry.LocalMachine, run, "Run");
        FromKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32");
        foreach (var (folder, root) in new[] { (Environment.SpecialFolder.Startup, Registry.CurrentUser), (Environment.SpecialFolder.CommonStartup, Registry.LocalMachine) })
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) continue;
            using var approved = root.OpenSubKey(Approved + "StartupFolder");
            names.AddRange(Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) && !Off(approved, Path.GetFileName(f)))
                .Select(Path.GetFileNameWithoutExtension).OfType<string>());
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A folder's size, giving up after <paramref name="budget"/> (the answer is then "at least").</summary>
    internal static double FolderGb(string path, TimeSpan budget)
    {
        if (!Directory.Exists(path)) return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var watch = Stopwatch.StartNew();
        long bytes = 0;
        var files = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
        {
            try { bytes += file.Length; } catch (IOException) { }
            if (++files % 2000 == 0 && watch.Elapsed > budget) break;
        }
        return bytes / 1073741824.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref RecycleBinInfo info);

    private static double? RecycleBinGb()
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        try { return SHQueryRecycleBinW(null, ref info) == 0 ? info.Bytes / 1073741824.0 : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
}

/// <summary>
/// Read-only WMI queries without System.Management: Windows' own scripting object, called late-bound. Queries are
/// synchronous (flags 0) — the "forward-only" flags make every property read fail with DISP_E_EXCEPTION.
/// </summary>
internal static class Wmi
{
    public static object? Call(object target, string name, BindingFlags kind, params object[] args) =>
        target.GetType().InvokeMember(name, kind, null, target, args);

    public static List<Dictionary<string, object?>> Query(string scope, string wql, params string[] properties)
    {
        var rows = new List<Dictionary<string, object?>>();
        var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator") ?? throw new COMException("WMI isn't available");
        var locator = Activator.CreateInstance(type)!;
        try
        {
            var services = Call(locator, "ConnectServer", BindingFlags.InvokeMethod, ".", scope)!;
            if (Call(services, "ExecQuery", BindingFlags.InvokeMethod, wql, "WQL", 0) is not IEnumerable results) return rows;
            foreach (var item in results)
            {
                var values = Call(item, "Properties_", BindingFlags.GetProperty)!;
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in properties) row[p] = Call(Call(values, "Item", BindingFlags.InvokeMethod, p)!, "Value", BindingFlags.GetProperty);
                rows.Add(row);
            }
            return rows;
        }
        finally { Marshal.FinalReleaseComObject(locator); }
    }
}
