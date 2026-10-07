using System.Globalization;
using System.Text.Json;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Local;

/// <summary>Where a model runs: the graphics card (one generation model at a time) or the processor (e.g. embedding models).</summary>
public enum LlamaLane { Gpu, Cpu }

/// <summary>How one model's server is started: its arguments and environment, and what they settle.</summary>
public sealed record LlamaLaunch(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    int Port,
    string Alias,
    int ContextTokens,
    LlamaLane Lane,
    int MtpDraft);

/// <summary>
/// The llama-server command line for one model. Every server listens only on 127.0.0.1, wants the key it's given (from its
/// environment, never its command line, which other programs can read), serves one conversation at a time with an 8-bit
/// context cache by default, never downloads anything (--offline), and has the web UI, the slots view, file-reading tools
/// and media paths off. A graphics-card model gets the context that fits (or the one asked for) and lets llama.cpp move
/// layers to the processor rather than fail; a processor model is kept off the card entirely.
/// </summary>
public static class LlamaArgs
{
    /// <summary>The environment variable llama-server reads its key from.</summary>
    public const string KeyVariable = "LLAMA_API_KEY";

    public static LlamaLaunch Build(LocalModel model, LlamaLane lane, LlamaSettings s, GpuFacts gpu, int port, string key,
        int? contextTokens = null, int mtpDraft = 0, int cpuCores = 0)
    {
        var alias = model.Id;
        var a = new List<string> { "-m", model.Path };
        if (model.ProjectorPath is { } projector && !model.Embedding) a.AddRange(new[] { "--mmproj", projector });
        a.AddRange(new[] { "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture), "-a", alias });

        int context;
        if (model.Embedding)
        {
            context = Math.Clamp(model.Info.ContextLength > 0 ? model.Info.ContextLength : 2048, 512, 8192);
            a.AddRange(new[] { "--embeddings", "-c", Num(context) });
        }
        else if (lane == LlamaLane.Gpu)
        {
            context = contextTokens is > 0 ? Math.Min(contextTokens.Value, Math.Max(model.Info.ContextLength, 2048)) : HardwareFit.Context(model, s.CacheType, gpu, s.ContextTokens);
            a.AddRange(new[] { "-c", Num(context), "-np", "1" });
        }
        else
        {
            context = contextTokens is > 0 ? contextTokens.Value : Math.Min(16384, model.Info.ContextLength > 0 ? model.Info.ContextLength : 16384);
            a.AddRange(new[] { "-c", Num(context), "-np", "1" });
        }

        if (lane == LlamaLane.Cpu)
        {
            var threads = s.CpuThreads > 0 ? s.CpuThreads : Math.Max(1, (cpuCores > 0 ? cpuCores : Environment.ProcessorCount) / 2);
            a.AddRange(new[] { "--device", "none", "-ngl", "0", "-t", Num(threads) });
        }
        else a.AddRange(new[] { "--fit", "on", "--fit-target", Num(HardwareFit.FitMarginMiB) });

        if (!model.Embedding)
        {
            // A quantized value cache needs flash attention.
            a.AddRange(new[] { "-fa", s.CacheType == "f16" ? "auto" : "on", "-ctk", s.CacheType, "-ctv", s.CacheType });
            a.AddRange(new[] { "--jinja", "--reasoning-format", "deepseek" });
            if (mtpDraft > 0 && model.Mtp) a.AddRange(new[] { "--spec-type", "draft-mtp", "--spec-draft-n-max", Num(mtpDraft) });
            // The publisher's sampling defaults (Ollama's params); the temperature is set by each request.
            foreach (var (name, flag) in new[] { ("top_k", "--top-k"), ("top_p", "--top-p"), ("min_p", "--min-p"), ("repeat_penalty", "--repeat-penalty"),
                                                ("presence_penalty", "--presence-penalty"), ("frequency_penalty", "--frequency-penalty") })
                if (model.Sampling.TryGetValue(name, out var v)) a.AddRange(new[] { flag, v.ToString("0.###", CultureInfo.InvariantCulture) });
        }
        if (s.IdleUnloadMinutes > 0) a.AddRange(new[] { "--sleep-idle-seconds", Num(s.IdleUnloadMinutes * 60) });
        a.AddRange(new[] { "--no-webui", "--offline", "--no-slots", "--cors-origins", "localhost", "-lv", "4" });

        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [KeyVariable] = key };
        return new LlamaLaunch(a, env, port, alias, context, lane, mtpDraft > 0 && model.Mtp ? mtpDraft : 0);
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// How fast each model writes with and without its MTP heads on this PC, from real answers. With MTP on "auto", a model
/// that has the heads tries a two-token draft first (the publishers' setting), then none, and keeps whichever wrote faster
/// over at least <see cref="Samples"/> answers of 64 tokens or more; it tries again after 30 days or on a new llama.cpp
/// build. Live (4 Oct, RTX 4070 SUPER): qwen3.5:9b wrote 75.7 tokens/s without and 60–69 with a three-token draft, about
/// the same with one — so MTP isn't simply switched on.
/// </summary>
public sealed class SpeedBook
{
    public const int Samples = 3;
    public const int Draft = 2;
    private readonly string? _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public sealed class Entry
    {
        public string Build { get; set; } = "";
        public DateTimeOffset Started { get; set; }
        public double WithMtp { get; set; }
        public int WithMtpCount { get; set; }
        public double Without { get; set; }
        public int WithoutCount { get; set; }
    }

    /// <param name="path">Where the measurements are kept (null: in memory only).</param>
    public SpeedBook(string? path)
    {
        _path = path;
        try
        {
            if (path is not null && File.Exists(path))
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), JsonUtil.Options) ?? new(StringComparer.Ordinal);
            _entries = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _entries = new(StringComparer.Ordinal); }
    }

    /// <summary>The draft length to start <paramref name="model"/> with (0 = no MTP), for the setting and what's been measured.</summary>
    public int Choose(LocalModel model, string setting, string build, DateTimeOffset now)
    {
        if (!model.Mtp || setting == "off" || model.Embedding) return 0;
        if (setting == "on") return Draft;
        lock (_gate)
        {
            if (!_entries.TryGetValue(model.Id, out var e) || e.Build != build || now - e.Started > TimeSpan.FromDays(30))
                return Draft;
            if (e.WithMtpCount < Samples) return Draft;
            if (e.WithoutCount < Samples) return 0;
            // MTP has to earn its keep: 5 % faster, or it's off.
            return e.WithMtp >= e.Without * 1.05 ? Draft : 0;
        }
    }

    /// <summary>One answer's writing speed, from the server's timings (answers under 64 tokens are too short to tell).</summary>
    public void Record(LocalModel model, int mtpDraft, string build, int tokens, double tokensPerSecond, DateTimeOffset now)
    {
        if (!model.Mtp || tokens < 64 || !double.IsFinite(tokensPerSecond) || tokensPerSecond <= 0) return;
        lock (_gate)
        {
            if (!_entries.TryGetValue(model.Id, out var e) || e.Build != build || now - e.Started > TimeSpan.FromDays(30))
                _entries[model.Id] = e = new Entry { Build = build, Started = now };
            if (mtpDraft > 0) { e.WithMtp = Average(e.WithMtp, e.WithMtpCount, tokensPerSecond); e.WithMtpCount++; }
            else { e.Without = Average(e.Without, e.WithoutCount, tokensPerSecond); e.WithoutCount++; }
            Save();
        }
    }

    /// <summary>What's been measured for a model: tokens/s with MTP and without (0 = not yet), for the model's details.</summary>
    public (double WithMtp, double Without) Measured(string modelId)
    {
        lock (_gate) return _entries.TryGetValue(modelId, out var e) ? (e.WithMtp, e.Without) : (0, 0);
    }

    private static double Average(double mean, int count, double value) => count >= 20 ? mean * 0.95 + value * 0.05 : (mean * count + value) / (count + 1);

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, JsonUtil.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug("llama", "Couldn't save the speed book: " + ex.Message); }
    }
}
