using System.Runtime.InteropServices;
using AquaHub.Core.Net;

namespace AquaHub.Core.Ai.Local;

/// <summary>One llama.cpp build for one kind of hardware: its archive and, for CUDA, NVIDIA's runtime libraries.</summary>
public sealed record LlamaBuild(string Backend, string Label, DownloadSpec Archive, DownloadSpec? Runtime = null)
{
    public long Size => Archive.Size + (Runtime?.Size ?? 0);
}

/// <summary>
/// The llama.cpp Aqua installs, pinned: one build from the project's GitHub releases (ggml-org/llama.cpp), with the sizes and
/// SHA-256s GitHub publishes for its files. CUDA builds run on NVIDIA cards, Vulkan on any recent card (AMD, Intel, NVIDIA),
/// and the CPU build anywhere. A new pin is a code change, reviewed like any other.
/// </summary>
public static class LlamaCatalog
{
    /// <summary>The pinned build (4 Oct 2026).</summary>
    public const string Build = "b11387";
    private const string Release = "https://github.com/ggml-org/llama.cpp/releases/download/" + Build + "/";

    private static DownloadSpec File(string name, long size, string sha256) => new(Release + name, size, sha256);

    public static readonly LlamaBuild Cuda12 = new("cuda12", "CUDA 12.4 (NVIDIA)",
        File("llama-" + Build + "-bin-win-cuda-12.4-x64.zip", 263_807_979, "e3537199c169ff24644d95d6c344956513867f4b4464f48129e656acb496836b"),
        File("cudart-llama-bin-win-cuda-12.4-x64.zip", 391_443_627, "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6"));

    public static readonly LlamaBuild Cuda13 = new("cuda13", "CUDA 13.4 (NVIDIA, recent drivers)",
        File("llama-" + Build + "-bin-win-cuda-13.4-x64.zip", 153_003_904, "36d6baf1f8b7ab8b6d9b14d93bd889e1ebaef526efeba9f9f060899398bec5bd"),
        File("cudart-llama-bin-win-cuda-13.4-x64.zip", 423_535_356, "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668"));

    public static readonly LlamaBuild Vulkan = new("vulkan", "Vulkan (any recent graphics card)",
        File("llama-" + Build + "-bin-win-vulkan-x64.zip", 33_285_918, "bed482e577f6b86e2b6a2d15d67cc2dbdd34498ad445168044292be4b7919540"));

    public static readonly LlamaBuild Cpu = new("cpu", "Processor only",
        File("llama-" + Build + "-bin-win-cpu-x64.zip", 19_365_702, "52bfd6002e6a3e51feba42ac455c5b4ad991cf1c28d8902d5a3a22eacceda512"));

    public static readonly LlamaBuild CpuArm = new("cpu", "Processor only (Arm)",
        File("llama-" + Build + "-bin-win-cpu-arm64.zip", 12_218_568, "15cf9bf6c8dfba4d47025f366d7074a0faffe574e9fccee0a8414500af73b64d"));

    /// <summary>CUDA 13 builds need a driver that runs CUDA 13.4 programs.</summary>
    public static readonly Version Cuda13Driver = new(13, 4);
    /// <summary>CUDA 12 builds need a driver that runs CUDA 12.4 programs (NVIDIA's 551 drivers and later).</summary>
    public static readonly Version Cuda12Driver = new(12, 4);

    /// <summary>
    /// The build for this PC: <paramref name="preference"/> when it's one this processor has, else — for "auto" — CUDA 13 on
    /// an NVIDIA card whose driver runs it, CUDA 12 on other NVIDIA cards with a recent enough driver, Vulkan on any other
    /// card, and the processor-only build without one.
    /// </summary>
    public static LlamaBuild Pick(GpuFacts gpu, Architecture arch, string preference = "auto")
    {
        if (arch == Architecture.Arm64) return CpuArm;
        if (arch != Architecture.X64) throw new PlatformNotSupportedException("llama.cpp isn't built for this processor (" + arch + ").");
        switch (preference)
        {
            case "cuda12": return Cuda12;
            case "cuda13": return Cuda13;
            case "vulkan": return Vulkan;
            case "cpu": return Cpu;
        }
        if (!gpu.Present) return Cpu;
        if (gpu.Nvidia && gpu.CudaDriver is { } cuda)
        {
            if (cuda >= Cuda13Driver) return Cuda13;
            if (cuda >= Cuda12Driver) return Cuda12;
        }
        return Vulkan;
    }

    public static IEnumerable<LlamaBuild> All(Architecture arch) =>
        arch == Architecture.Arm64 ? new[] { CpuArm } : new[] { Cuda12, Cuda13, Vulkan, Cpu };

    /// <summary>"cuda12" → the build; null for an unknown name.</summary>
    public static LlamaBuild? Named(string backend, Architecture arch) =>
        All(arch).FirstOrDefault(b => b.Backend.Equals(backend, StringComparison.OrdinalIgnoreCase));
}
