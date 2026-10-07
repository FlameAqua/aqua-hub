namespace AquaHub.Core.Ai.Local;

/// <summary>
/// The graphics card a model would run on, as the app reads it (DXGI, and NVIDIA's driver for its CUDA version):
/// <see cref="None"/> when there isn't one Aqua can use.
/// </summary>
public sealed record GpuFacts(string Name, string Vendor, long TotalBytes, long FreeBytes, Version? CudaDriver = null, double ComputeCapability = 0, string? Driver = null)
{
    public static readonly GpuFacts None = new("", "", 0, 0);
    public bool Present => TotalBytes > 0;
    public bool Nvidia => Vendor.Equals("nvidia", StringComparison.OrdinalIgnoreCase);
    /// <summary>"RTX 4070 SUPER, 12 GB" for people.</summary>
    public string Label => Present ? $"{Name}, {HardwareFit.Gigabytes(TotalBytes)}" : "no graphics card Aqua can use";
}

public enum FitVerdict
{
    /// <summary>All of it on the graphics card, with room to spare.</summary>
    Fits,
    /// <summary>On the card, but little room is left for anything else (games, video, other apps).</summary>
    Tight,
    /// <summary>Some layers would run on the processor: it works, more slowly.</summary>
    PartlyOnCpu,
    /// <summary>No graphics card Aqua can use: it would run on the processor alone (slowly, for a big model).</summary>
    CpuOnly,
    /// <summary>Too big to run well on this PC (or at all).</summary>
    TooBig,
}

/// <summary>An estimate of what a model needs at one context size, against what the card has.</summary>
public sealed record FitEstimate(FitVerdict Verdict, long WeightsBytes, long CacheBytes, long OverheadBytes, long AvailableBytes, int ContextTokens)
{
    public long TotalBytes => WeightsBytes + CacheBytes + OverheadBytes;
}

/// <summary>
/// What fits on this PC. The estimates are deliberately simple — the weights (the model file and its vision projector),
/// the context cache from the model's own layer shapes, and a fixed working allowance — and llama.cpp still checks at
/// load time (--fit), moving layers to the processor if needed. Measured on an RTX 4070 SUPER (4 Oct): Qwen3.8-27B IQ2_S
/// at 32K tokens needed 8,454 MiB of weights, 1,237 MiB of context and 240 MiB of working memory on the card, against
/// 1,088 + 420 MiB estimated here for the last two.
/// </summary>
public static class HardwareFit
{
    /// <summary>What Windows and the desktop keep for themselves on the card (about 1 GiB on a 12 GB card, measured).</summary>
    public const long DesktopReserve = 1L << 30;
    /// <summary>Room llama.cpp is told to leave free when it fits a model (--fit-target), for whatever else wants the card.</summary>
    public const int FitMarginMiB = 768;
    /// <summary>Working memory (compute buffers, a hybrid model's fixed state) beyond weights and cache.</summary>
    public const long WorkingAllowance = 420L << 20;

    /// <summary>Context sizes Aqua chooses between.</summary>
    public static readonly int[] ContextSteps = { 4096, 8192, 16384, 32768, 65536, 131072, 262144 };

    /// <summary>Bytes per cached element: f16 = 2, q8_0 = 34 per 32, q4_0 = 18 per 32.</summary>
    public static double BytesPerElement(string cacheType) => cacheType switch { "f16" => 2.0, "q4_0" => 18.0 / 32, _ => 34.0 / 32 };

    /// <summary>The context cache at <paramref name="context"/> tokens: full-context layers grow with it, sliding-window layers stop at their window.</summary>
    public static long CacheBytes(GgufInfo info, int context, string cacheType)
    {
        var window = info.SlidingWindow > 0 ? Math.Min(context, info.SlidingWindow + 512) : context;
        return (long)((info.KvElementsPerToken * (double)context + info.KvWindowElementsPerToken * (double)window) * BytesPerElement(cacheType));
    }

    /// <summary>The model at <paramref name="context"/> tokens on <paramref name="gpu"/>.</summary>
    public static FitEstimate Estimate(LocalModel model, int context, string cacheType, GpuFacts gpu, bool vision = true)
    {
        var weights = model.Size + (vision ? model.ProjectorSize : 0);
        var cache = CacheBytes(model.Info, context, cacheType);
        var total = weights + cache + WorkingAllowance;
        var available = Available(gpu);
        // Off the card, a model runs from system memory: past about twice what the card holds (or 16 GB with no card) it's
        // too slow to be worth offering.
        var verdict = !gpu.Present ? (weights <= 16L << 30 ? FitVerdict.CpuOnly : FitVerdict.TooBig)
            : total + ((long)FitMarginMiB << 20) <= available ? FitVerdict.Fits
            : total <= available ? FitVerdict.Tight
            : weights <= available * 2 ? FitVerdict.PartlyOnCpu
            : FitVerdict.TooBig;
        return new FitEstimate(verdict, weights, cache, WorkingAllowance, available, context);
    }

    /// <summary>What a model may use on the card: all of it bar the desktop's share (not what's free right now, which other apps change).</summary>
    public static long Available(GpuFacts gpu) => gpu.Present ? Math.Max(0, gpu.TotalBytes - DesktopReserve) : 0;

    /// <summary>
    /// The context to load a graphics-card model with: <paramref name="wanted"/> when set, else the largest step that fits
    /// with llama.cpp's margin — at most 64K, or 128K on a card of 24 GB or more, never more than the model was trained for,
    /// and never under 8K (llama.cpp then moves layers to the processor rather than shrink the context further).
    /// </summary>
    public static int Context(LocalModel model, string cacheType, GpuFacts gpu, int wanted = 0)
    {
        var trained = model.Info.ContextLength > 0 ? model.Info.ContextLength : 32768;
        if (wanted > 0) return Math.Min(wanted, trained);
        var cap = gpu.TotalBytes >= 24L << 30 ? 131072 : 65536;
        var best = Math.Min(8192, trained);
        foreach (var step in ContextSteps)
        {
            if (step > cap || step > trained) break;
            if (step < 8192) continue;
            if (Estimate(model, step, cacheType, gpu).Verdict == FitVerdict.Fits) best = step;
        }
        return best;
    }

    /// <summary>"9.3 GB" (decimal, as disks and download pages count).</summary>
    public static string Gigabytes(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{Math.Max(1, bytes / 1_000_000):0} MB";
}
