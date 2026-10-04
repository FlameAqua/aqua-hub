using System.Runtime.InteropServices;
using System.Text;
using AquaHub.Core.Ai.Local;
using AquaHub.Core.Util;

namespace AquaHub.Platform;

/// <summary>
/// The graphics card a model would run on, for Aqua's runtime: the adapter with the most dedicated memory (DXGI, from the
/// system monitor), and on an NVIDIA card its driver's own library (nvml.dll, installed with the driver) for the CUDA
/// version the driver runs, the card's compute capability and how much of its memory is free. Read-only. Without NVIDIA's
/// library, free memory is the card's size less what Windows reports in use.
/// </summary>
public static class GpuReader
{
    private static readonly object Gate = new();
    private static bool _nvmlTried;
    private static bool _nvml;

    public static GpuFacts Read(SystemMonitor monitor)
    {
        if (monitor.GpuAdapter is not { } adapter || adapter.DedicatedBytes < 512L << 20) return GpuFacts.None;
        var vendor = adapter.Vendor switch { 0x10DE => "nvidia", 0x1002 => "amd", 0x8086 => "intel", _ => "other" };
        var used = (long)((monitor.Latest?.Gpu?.VramUsedGb ?? 0) * (1L << 30));
        var facts = new GpuFacts(adapter.Name, vendor, adapter.DedicatedBytes, Math.Max(0, adapter.DedicatedBytes - used));
        return vendor == "nvidia" && Nvidia(adapter.Name) is { } nvidia ? nvidia with { Name = adapter.Name } : facts;
    }

    /// <summary>The NVIDIA card NVML knows by <paramref name="name"/> (else its largest), or null when NVML isn't there.</summary>
    private static GpuFacts? Nvidia(string name)
    {
        lock (Gate)
        {
            try
            {
                if (!_nvmlTried)
                {
                    _nvmlTried = true;
                    _nvml = nvmlInit_v2() == 0;
                }
                if (!_nvml) return null;
                Version? cuda = nvmlSystemGetCudaDriverVersion_v2(out var v) == 0 && v > 0 ? new Version(v / 1000, v % 1000 / 10) : null;
                var driver = new StringBuilder(96);
                var driverVersion = nvmlSystemGetDriverVersion(driver, (uint)driver.Capacity) == 0 ? driver.ToString() : null;
                if (nvmlDeviceGetCount_v2(out var count) != 0 || count == 0) return null;
                GpuFacts? best = null;
                for (uint i = 0; i < count && i < 16; i++)
                {
                    if (nvmlDeviceGetHandleByIndex_v2(i, out var device) != 0) continue;
                    var label = new StringBuilder(128);
                    if (nvmlDeviceGetName(device, label, (uint)label.Capacity) != 0) continue;
                    if (nvmlDeviceGetMemoryInfo(device, out var memory) != 0) continue;
                    var compute = nvmlDeviceGetCudaComputeCapability(device, out var major, out var minor) == 0 ? major + minor / 10.0 : 0;
                    var facts = new GpuFacts(label.ToString(), "nvidia", (long)memory.Total, (long)memory.Free, cuda, compute, driverVersion);
                    if (name.Contains(label.ToString(), StringComparison.OrdinalIgnoreCase) || label.ToString().Contains(name, StringComparison.OrdinalIgnoreCase)) return facts;
                    if (best is null || facts.TotalBytes > best.TotalBytes) best = facts;
                }
                return best;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                Log.Debug("gpu", "NVIDIA's management library isn't available: " + ex.Message);
                _nvml = false;
                return null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlSystemGetCudaDriverVersion_v2(out int version);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCudaComputeCapability(IntPtr device, out int major, out int minor);
}
