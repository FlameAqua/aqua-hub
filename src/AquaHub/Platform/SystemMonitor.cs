using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using AquaHub.Core.Models;
using AquaHub.Core.Util;
using Microsoft.Win32;

namespace AquaHub.Platform;

/// <summary>
/// Low-overhead PC telemetry: CPU (GetSystemTimes), memory, disks, network throughput, GPU and VRAM
/// (PDH GPU engine / adapter-memory counters, mapped to adapters via DXGI) and top processes via a single
/// NtQuerySystemInformation snapshot (no per-process handles). Sampling rate adapts to UI visibility.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private ulong _lastIdle, _lastKernel, _lastUser;
    private long _lastNetRx, _lastNetTx;
    private DateTime _lastNetTime;
    private readonly GpuMonitor _gpu = new();
    private readonly ProcessSampler _processes = new();
    private readonly string _cpuName;
    private readonly string _osName;
    private Timer? _timer;
    private DateTime _lastDisks = DateTime.MinValue;
    private List<DiskInfo> _disks = new();

    public event Action<SystemSnapshot>? Sampled;
    public SystemSnapshot? Latest { get; private set; }
    public readonly Queue<(double Cpu, double Ram, double Gpu, double NetDown, double NetUp)> History = new();

    /// <summary>Include per-process sampling (only while a view shows it).</summary>
    public bool DetailedProcesses { get; set; }
    /// <summary>How many of the busiest (and of the largest) processes each sample keeps (This PC › Top processes › Show more).</summary>
    public int ProcessCount { get; set; } = 8;

    public SystemMonitor()
    {
        _cpuName = ReadCpuName();
        _osName = ReadOsName();
        Native.GetSystemTimes(out var idle, out var kernel, out var user);
        _lastIdle = idle.Value; _lastKernel = kernel.Value; _lastUser = user.Value;
        (_lastNetRx, _lastNetTx) = NetTotals();
        _lastNetTime = DateTime.UtcNow;
    }

    public void Start(TimeSpan interval)
    {
        _timer ??= new Timer(_ => Sample(), null, TimeSpan.FromMilliseconds(300), interval);
        _timer.Change(TimeSpan.FromMilliseconds(300), interval);
    }

    public void SetInterval(TimeSpan interval) => _timer?.Change(interval, interval);

    private int _sampling;

    public void Sample()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var snapshot = Collect();
            Latest = snapshot;
            lock (History)
            {
                History.Enqueue((snapshot.CpuPercent, snapshot.RamPercent, snapshot.Gpu?.Utilization ?? 0, snapshot.NetDownBps, snapshot.NetUpBps));
                while (History.Count > 90) History.Dequeue();
            }
            Sampled?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Warn("system", "Sampling failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private SystemSnapshot Collect()
    {
        // CPU
        Native.GetSystemTimes(out var idle, out var kernel, out var user);
        var idleD = idle.Value - _lastIdle;
        var total = (kernel.Value - _lastKernel) + (user.Value - _lastUser);
        _lastIdle = idle.Value; _lastKernel = kernel.Value; _lastUser = user.Value;
        var cpu = total == 0 ? 0 : Math.Clamp((1.0 - idleD / (double)total) * 100.0, 0, 100);

        // Memory
        var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref mem);
        var totalGb = mem.ullTotalPhys / 1073741824.0;
        var usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;

        // Disks (every 30 s)
        if (DateTime.UtcNow - _lastDisks > TimeSpan.FromSeconds(30))
        {
            _disks = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => new DiskInfo(d.Name.TrimEnd('\\'), SafeLabel(d), d.TotalSize / 1073741824.0, d.AvailableFreeSpace / 1073741824.0))
                .ToList();
            _lastDisks = DateTime.UtcNow;
        }

        // Network
        var (rx, tx) = NetTotals();
        var now = DateTime.UtcNow;
        var secs = Math.Max(0.2, (now - _lastNetTime).TotalSeconds);
        var down = Math.Max(0, (rx - _lastNetRx) / secs);
        var up = Math.Max(0, (tx - _lastNetTx) / secs);
        _lastNetRx = rx; _lastNetTx = tx; _lastNetTime = now;

        // Battery
        BatteryInfo? battery = null;
        if (Native.GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryLifePercent <= 100)
            battery = new BatteryInfo(ps.BatteryLifePercent, ps.ACLineStatus == 1, ps.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(ps.BatteryLifeTime) : null);

        return new SystemSnapshot
        {
            CpuPercent = cpu,
            CpuName = _cpuName,
            LogicalCores = Environment.ProcessorCount,
            RamUsedGb = usedGb,
            RamTotalGb = totalGb,
            Disks = _disks,
            NetDownBps = down,
            NetUpBps = up,
            Gpu = _gpu.Sample(),
            Battery = battery,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            OsName = _osName,
            MachineName = Environment.MachineName,
            TopProcesses = DetailedProcesses ? _processes.Sample(ProcessCount) : new(),
            Time = DateTimeOffset.Now,
        };
    }

    private static string SafeLabel(DriveInfo d)
    {
        try { return d.VolumeLabel; } catch { return ""; }
    }

    private static (long Rx, long Tx) NetTotals()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch { }
        return (rx, tx);
    }

    private static string ReadCpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
    }

    private static string ReadOsName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = Environment.OSVersion.Version.Build;
        var display = key?.GetValue("DisplayVersion") as string;
        var edition = (key?.GetValue("EditionID") as string) ?? "";
        var name = build >= 22000 ? "Windows 11" : "Windows 10";
        if (edition.Contains("Professional", StringComparison.OrdinalIgnoreCase)) name += " Pro";
        else if (edition.Contains("Core", StringComparison.OrdinalIgnoreCase)) name += " Home";
        return $"{name} {display} (build {build})".Trim();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gpu.Dispose();
    }
}

/// <summary>GPU utilisation / dedicated VRAM for the primary discrete adapter (vendor-neutral, no drivers SDKs).</summary>
public sealed unsafe class GpuMonitor : IDisposable
{
    private IntPtr _query;
    private IntPtr _engineCounter;
    private IntPtr _memoryCounter;
    private readonly List<(string Name, long Luid, double VramGb)> _adapters = new();
    private bool _primed;

    public GpuMonitor()
    {
        try { EnumerateAdapters(); } catch (Exception ex) { Log.Warn("gpu", "DXGI enumeration failed", ex); }
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
            {
                PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _engineCounter);
                PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _memoryCounter);
                PdhCollectQueryData(_query);
            }
        }
        catch (Exception ex) { Log.Warn("gpu", "PDH unavailable", ex); }
    }

    public string? PrimaryName => _adapters.Count > 0 ? _adapters[0].Name : null;

    public GpuInfo? Sample()
    {
        if (_adapters.Count == 0) return null;
        var primary = _adapters[0];
        if (_query == IntPtr.Zero) return new GpuInfo(primary.Name, 0, 0, primary.VramGb);
        if (PdhCollectQueryData(_query) != 0) return new GpuInfo(primary.Name, 0, 0, primary.VramGb);
        if (!_primed) { _primed = true; }
        var luidTag = LuidTag(primary.Luid);

        // Utilisation: sum per engine type (across processes), then take the busiest engine type — like Task Manager.
        var perEngine = new Dictionary<string, double>();
        foreach (var (name, value) in ReadArray(_engineCounter))
        {
            if (!name.Contains(luidTag, StringComparison.OrdinalIgnoreCase)) continue;
            var idx = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            var type = idx >= 0 ? name[(idx + 8)..] : "other";
            perEngine[type] = perEngine.GetValueOrDefault(type) + value;
        }
        var util = perEngine.Count == 0 ? 0 : Math.Clamp(perEngine.Values.Max(), 0, 100);

        double usedBytes = 0;
        foreach (var (name, value) in ReadArray(_memoryCounter))
            if (name.Contains(luidTag, StringComparison.OrdinalIgnoreCase)) usedBytes += value;

        return new GpuInfo(primary.Name, util, usedBytes / 1073741824.0, primary.VramGb);
    }

    private static string LuidTag(long luid)
    {
        var high = (int)(luid >> 32);
        var low = (uint)(luid & 0xFFFFFFFF);
        return $"luid_0x{high:X8}_0x{low:X8}";
    }

    private List<(string Name, double Value)> ReadArray(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return list;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer);
            if (status != 0) return list;
            var itemSize = IntPtr.Size == 8 ? 24 : 16;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * itemSize;
                var namePtr = Marshal.ReadIntPtr(item);
                var cstatus = Marshal.ReadInt32(item + IntPtr.Size);
                if (cstatus != 0) continue;
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + (IntPtr.Size == 8 ? 16 : 8)));
                list.Add((Marshal.PtrToStringUni(namePtr) ?? "", value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    private void EnumerateAdapters()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) != 0 || factory == IntPtr.Zero) return;
        try
        {
            var vtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[12];
            var desc = stackalloc byte[320];
            for (uint i = 0; i < 8; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) != 0 || adapter == IntPtr.Zero) break;
                try
                {
                    var avtbl = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)avtbl[10];
                    if (getDesc1(adapter, desc) != 0) continue;
                    var name = new string((char*)desc).Trim();
                    var dedicated = (ulong)*(nuint*)(desc + 272);
                    var luidLow = *(uint*)(desc + 296);
                    var luidHigh = *(int*)(desc + 300);
                    var flags = *(uint*)(desc + 304);
                    if ((flags & 2) != 0) continue; // software adapter
                    _adapters.Add((name, ((long)luidHigh << 32) | luidLow, dedicated / 1073741824.0));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
        _adapters.Sort((a, b) => b.VramGb.CompareTo(a.VramGb));
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>All-process CPU/memory sampling from one NtQuerySystemInformation call (validated at runtime).</summary>
public sealed class ProcessSampler
{
    private readonly Dictionary<int, long> _lastTimes = new();
    private DateTime _lastSample = DateTime.MinValue;
    private bool? _layoutOk;

    public List<ProcInfo> Sample(int top)
    {
        try
        {
            if (_layoutOk != false)
            {
                var result = SampleNative(top);
                if (result is not null) return result;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("system", "Native process sampling failed; using fallback", ex);
            _layoutOk = false;
        }
        return SampleFallback(top);
    }

    private List<ProcInfo>? SampleNative(int top)
    {
        var size = 1 << 20;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            while (true)
            {
                buffer = Marshal.AllocHGlobal(size);
                var status = Native.NtQuerySystemInformation(5, buffer, size, out var needed);
                if (status == 0) break;
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != unchecked((int)0xC0000004) || size > 64 << 20) return null;
                size = Math.Max(size * 2, needed + 65536);
            }

            var now = DateTime.UtcNow;
            var elapsed100ns = _lastSample == DateTime.MinValue ? 0 : (now - _lastSample).Ticks;
            _lastSample = now;
            var cores = Environment.ProcessorCount;
            var seen = new Dictionary<int, long>();
            var list = new List<ProcInfo>();
            var self = Environment.ProcessId;
            var offset = 0;
            while (true)
            {
                var entry = buffer + offset;
                var next = Marshal.ReadInt32(entry);
                var userTime = Marshal.ReadInt64(entry + 40);
                var kernelTime = Marshal.ReadInt64(entry + 48);
                var nameLen = (ushort)Marshal.ReadInt16(entry + 56);
                var namePtr = Marshal.ReadIntPtr(entry + 64);
                var pid = (int)Marshal.ReadIntPtr(entry + 80).ToInt64();
                var workingSet = Marshal.ReadIntPtr(entry + 144).ToInt64();
                // WorkingSetPrivateSize: what Task Manager's "Memory" column shows (shared DLL pages excluded).
                var privateSet = Marshal.ReadInt64(entry + 8);
                var name = pid == 0 ? "System Idle" : namePtr == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(namePtr, nameLen / 2);

                if (_layoutOk is null && pid == self)
                {
                    // Validate the structure layout against a known process (ourselves).
                    var managed = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
                    _layoutOk = name.StartsWith("AquaHub", StringComparison.OrdinalIgnoreCase) || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                        ? workingSet > managed / 4 && workingSet < managed * 4 && privateSet > 0 && privateSet <= workingSet
                        : false;
                    if (_layoutOk == false) return null;
                }

                if (pid != 0)
                {
                    var cpuTime = userTime + kernelTime;
                    seen[pid] = cpuTime;
                    double cpu = 0;
                    if (elapsed100ns > 0 && _lastTimes.TryGetValue(pid, out var prev))
                        cpu = Math.Clamp((cpuTime - prev) / (double)elapsed100ns / cores * 100.0, 0, 100);
                    list.Add(new ProcInfo(pid, Path.GetFileNameWithoutExtension(name), cpu, (privateSet > 0 ? privateSet : workingSet) / 1048576.0));
                }
                if (next == 0) break;
                offset += next;
            }
            _lastTimes.Clear();
            foreach (var kv in seen) _lastTimes[kv.Key] = kv.Value;

            // Merge multi-process apps (browsers etc.) by name for a readable list. Return the busiest by CPU and the
            // largest by memory, so the page can sort by either column.
            var merged = list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ProcInfo(g.First().Pid, g.Key, g.Sum(p => p.Cpu), g.Sum(p => p.MemoryMb)))
                .ToList();
            return merged.OrderByDescending(p => p.Cpu).Take(top)
                .Union(merged.OrderByDescending(p => p.MemoryMb).Take(top))
                .ToList();
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<ProcInfo> SampleFallback(int top) =>
        System.Diagnostics.Process.GetProcesses()
            .Select(p => { try { return new ProcInfo(p.Id, p.ProcessName, 0, p.WorkingSet64 / 1048576.0); } catch { return null; } finally { p.Dispose(); } })
            .Where(p => p is not null).Cast<ProcInfo>()
            .GroupBy(p => p.Name).Select(g => new ProcInfo(g.First().Pid, g.Key, 0, g.Sum(p => p.MemoryMb)))
            .OrderByDescending(p => p.MemoryMb).Take(top).ToList();
}
