using System.Collections;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, byte> ActiveReads = new();

    public static async Task<HealthFacts> InspectAsync(Action<string>? progress, CancellationToken ct)
    {
        var readings = new ConcurrentQueue<HealthReading>();

        async Task<T?> Check<T>(string what, string step, Func<T?> read, int seconds = 15,
            Func<T, HealthReadingState>? quality = null) where T : class
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(step);
            if (!ActiveReads.TryAdd(what, 0))
            {
                readings.Enqueue(new(what, HealthReadingState.TimedOut));
                return null;
            }
            // COM calls may outlive the wait. Keep ownership until the call actually returns, even after a timeout.
            var worker = Task.Run(() =>
            {
                try
                {
                    var value = read();
                    return (value, value is null ? HealthReadingState.Unavailable : quality?.Invoke(value) ?? HealthReadingState.Observed);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, Classify(ex)); }
                finally { ActiveReads.TryRemove(what, out _); }
            });
            try
            {
                var (value, state) = await worker.WaitAsync(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                readings.Enqueue(new(what, state));
                return value;
            }
            catch (TimeoutException)
            {
                readings.Enqueue(new(what, HealthReadingState.TimedOut));
                return null;
            }
        }

        var snap = await Check("Memory", "Reading the latest memory sample…", () =>
        {
            if (Hub.System.Latest is { } recent && DateTimeOffset.Now - recent.Time <= TimeSpan.FromSeconds(10)) return recent;
            Hub.System.Sample();
            return Hub.System.Latest is { } current && DateTimeOffset.Now - current.Time <= TimeSpan.FromSeconds(10) ? current : null;
        }, seconds: 5, quality: snapshot => snapshot.RamTotalGb > 0 ? HealthReadingState.Observed : HealthReadingState.Unavailable);
        var drives = Check("Drive space", "Reading available disk space…", Drives, quality: rows =>
            rows.Count > 0 && rows.Any(d => d.System) ? HealthReadingState.Observed : HealthReadingState.Partial);
        var disks = Check("Drive health", "Asking the drives how they are…", PhysicalDisks, quality: rows =>
            rows.Count == 0 ? HealthReadingState.Unsupported
            : rows.Any(d => d.Health == "Unknown") ? HealthReadingState.Partial : HealthReadingState.Observed);
        var updates = Check("Windows Update", "Reading Windows Update’s history…", UpdateHistory, 25, history =>
            history.Last is null ? HealthReadingState.Partial : HealthReadingState.Observed);
        var restart = Check("Pending restart", "Checking for a pending restart…", () => (object)RestartPending());
        var bsod = Check("Blue screens", "Looking for crashes in Windows' logs…", () => (object)Count("System", "Microsoft-Windows-WER-SystemErrorReporting", 1001, 30));
        var shutdowns = Check("Unexpected shutdowns", "Looking for crashes in Windows' logs…", () => (object)Count("System", "Microsoft-Windows-Kernel-Power", 41, 30));
        var crashes = Check("App crashes", "Looking for crashes in Windows' logs…", AppCrashes);
        var diskRetries = Check("Disk I/O retries", "Reading recent disk events…", () => (object)Count("System", "disk", 153, 7, 100));
        var antivirus = Check("Antivirus", "Checking Windows Security…", Antivirus, quality: rows =>
            rows.Count == 0 ? HealthReadingState.Unavailable : HealthReadingState.Observed);
        var firewall = Check("Windows Firewall", "Reading active firewall profiles…", Firewall, quality: rows =>
            rows.Count == 0 ? HealthReadingState.Unavailable
            : rows.Any(profile => profile.Enabled is null) ? HealthReadingState.Partial : HealthReadingState.Observed);
        var devices = Check("Devices", "Checking devices…", DeviceProblems);
        var battery = snap?.Battery is not null ? Check("Battery wear", "Checking the battery…", Battery) : Task.FromResult<BatteryFact?>(null);
        if (snap is not null && snap.Battery is null) readings.Enqueue(new("Battery wear", HealthReadingState.Unsupported));
        var startup = Check("Startup apps", "Counting startup apps…", StartupApps);
        var temp = Check("Temporary files", "Measuring temporary files…", () => (object)FolderGb(Path.GetTempPath(), TimeSpan.FromSeconds(6), ct), 12);
        var recycle = Check("Recycle Bin", "Checking the Recycle Bin…", () => RecycleBinGb() as object);
        await Task.WhenAll(drives, disks, updates, restart, bsod, shutdowns, crashes, diskRetries, antivirus, firewall, devices, battery, startup, temp, recycle).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var update = await updates;
        var av = await antivirus;
        var top = snap?.TopProcesses.OrderByDescending(p => p.MemoryMb).FirstOrDefault();
        return new HealthFacts
        {
            Checked = DateTimeOffset.Now,
            Machine = snap is null ? Environment.MachineName : $"{snap.MachineName} · {snap.OsName}",
            Drives = await drives ?? new(),
            PhysicalDisks = await disks,
            Battery = await battery,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            RestartPending = await restart as bool?,
            LastUpdate = update?.Last,
            LastUpdateTitle = update?.Title,
            FailedUpdates = update?.Failed,
            BlueScreens = await bsod as int?,
            UnexpectedShutdowns = await shutdowns as int?,
            AppCrashes = await crashes,
            Antivirus = av is { Count: > 0 } ? av : null,
            FirewallProfiles = await firewall,
            StorageRetries = await diskRetries as int?,
            DeviceProblems = await devices,
            StartupApps = await startup,
            TempGb = await temp as double?,
            RecycleBinGb = await recycle as double?,
            RamPercent = snap?.RamPercent ?? 0,
            RamTotalGb = snap?.RamTotalGb ?? 0,
            TopMemoryApp = top?.Name,
            TopMemoryMb = top?.MemoryMb ?? 0,
            MemorySampled = snap?.Time,
            Readings = readings.OrderBy(reading => reading.Name, StringComparer.Ordinal).ToList(),
        };
    }

    private static HealthReadingState Classify(Exception ex)
    {
        while (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;
        return ex switch
        {
            TimeoutException => HealthReadingState.TimedOut,
            UnauthorizedAccessException or System.Security.SecurityException => HealthReadingState.Denied,
            { HResult: unchecked((int)0x80070005) or unchecked((int)0x80041003) } => HealthReadingState.Denied,
            { HResult: unchecked((int)0x80040154) or unchecked((int)0x8004100E) or unchecked((int)0x80041010) } => HealthReadingState.Unsupported,
            PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException => HealthReadingState.Unsupported,
            _ => HealthReadingState.Unavailable,
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
            .ToList();

    private static List<FirewallProfileFact> Firewall()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new PlatformNotSupportedException();
        var policy = Activator.CreateInstance(type)!;
        try
        {
            var active = Convert.ToInt32(Wmi.Call(policy, "CurrentProfileTypes", BindingFlags.GetProperty));
            var result = new List<FirewallProfileFact>();
            foreach (var (id, name) in new[] { (1, "Domain"), (2, "Private"), (4, "Public") })
            {
                if ((active & id) == 0) continue;
                try { result.Add(new(name, Wmi.Call(policy, "FirewallEnabled", BindingFlags.GetProperty, id) as bool?)); }
                catch (Exception ex) when (ex is COMException or TargetInvocationException or UnauthorizedAccessException)
                {
                    if (result.Count == 0 && (active & 7) == id) throw;
                    result.Add(new(name, null));
                }
            }
            return result;
        }
        finally { Marshal.FinalReleaseComObject(policy); }
    }

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

    private static bool RestartPending()
    {
        using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        if (wu is not null) return true;
        using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        return cbs is not null;
    }

    private static int Count(string log, string provider, int id, int days, int limit = 500)
    {
        var query = new EventLogQuery(log, PathType.LogName,
            $"*[System[Provider[@Name='{provider}'] and (EventID={id}) and TimeCreated[timediff(@SystemTime) <= {(long)TimeSpan.FromDays(days).TotalMilliseconds}]]]");
        using var reader = new EventLogReader(query);
        var n = 0;
        while (n < limit && reader.ReadEvent(TimeSpan.FromSeconds(1)) is { } e)
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
        while (n++ < 1000 && reader.ReadEvent(TimeSpan.FromSeconds(1)) is { } e)
        {
            using (e)
            {
                // The first value is the program that crashed or hung ("chrome.exe").
                var properties = e.Properties;
                if (properties.Count == 0 || properties[0].Value is not string { Length: > 0 } app) continue;
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

    /// <summary>A bounded estimate of a folder's size. A scan beyond the time or file limit produces no estimate.</summary>
    internal static double FolderGb(string path, TimeSpan budget, CancellationToken ct = default)
    {
        if (!Directory.Exists(path)) return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var watch = Stopwatch.StartNew();
        long bytes = 0;
        var files = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
        {
            try { bytes += file.Length; } catch (IOException) { }
            if (++files % 256 != 0) continue;
            ct.ThrowIfCancellationRequested();
            if (watch.Elapsed > budget || files >= 100_000) throw new TimeoutException();
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
        var result = SHQueryRecycleBinW(null, ref info);
        Marshal.ThrowExceptionForHR(result);
        return result == 0 ? info.Bytes / 1073741824.0 : null;
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
                if (rows.Count >= 256) throw new InvalidOperationException("The WMI reading exceeded its row limit.");
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
