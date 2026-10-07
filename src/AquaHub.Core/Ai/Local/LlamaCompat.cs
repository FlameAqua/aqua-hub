using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Local;

/// <summary>Which program runs a model.</summary>
public enum LlamaServerKind
{
    /// <summary>Aqua's pinned llama.cpp build.</summary>
    Aqua,
    /// <summary>The llama-server Settings names (Ai.Llama.ServerPath); used for every model.</summary>
    Custom,
    /// <summary>The llama-server that comes with Ollama, for Ollama's own models that llama.cpp itself can't read.</summary>
    Ollama,
}

/// <summary>The program a model runs on, how to name it to people, and why it was chosen when that isn't obvious.</summary>
public sealed record LlamaServerChoice(string Path, LlamaServerKind Kind, string Label, string? Note = null);

/// <summary>What kind of failure stopped a model's server from starting.</summary>
public enum LlamaFailureKind
{
    None,
    /// <summary>The file says something this server can't read: the same file on the same server always fails.</summary>
    Format,
    /// <summary>Not enough memory on the card or in the PC: it may work once something else lets go of it.</summary>
    Memory,
    /// <summary>Anything else (a crash, a timeout, a taken port): worth another try later.</summary>
    Other,
}

/// <summary>
/// What's known about running a model file on a server program: rules for files llama.cpp is known not to read, and how
/// llama.cpp's own output says a load failed.
/// </summary>
public static partial class LlamaCompat
{
    /// <summary>
    /// Why upstream llama.cpp (Aqua's build) can't read this file, or null when nothing known stops it. Ollama converts
    /// some models itself and writes metadata its own engine reads but llama.cpp doesn't: Qwen3.5's M-RoPE sections as
    /// three numbers where llama.cpp requires exactly four (its loader checks the length and refuses the file).
    /// </summary>
    public static string? UpstreamProblem(GgufInfo info)
    {
        var key = info.Architecture + ".rope.dimension_sections";
        if (info.Metadata.TryGetValue(key, out var value) && value is long[] sections && sections.Length != 4)
            return $"llama.cpp can't read this copy of the model: its {key} has {sections.Length} entries where llama.cpp needs 4 "
                   + "(Ollama converted it for its own engine). Ollama's model file is left untouched.";
        return null;
    }

    /// <summary>What a line of llama.cpp's output says about a failed load.</summary>
    public static LlamaFailureKind Classify(string line)
    {
        var l = line.ToLowerInvariant();
        if (l.Contains("out of memory") || l.Contains("failed to allocate") || l.Contains("cudamalloc failed") || l.Contains("unable to allocate")
            || l.Contains("outofdevicememory") || l.Contains("not enough memory"))
            return LlamaFailureKind.Memory;
        if (l.Contains("wrong array length") || l.Contains("unknown model architecture") || l.Contains("missing tensor")
            || l.Contains("wrong number of tensors") || l.Contains("has wrong shape") || l.Contains("invalid magic")
            || l.Contains("failed to read magic") || l.Contains("key not found in model") || l.Contains("unknown ggml type")
            || l.Contains("invalid ggml type") || l.Contains("error loading model hyperparameters")
            || l.Contains("error loading model architecture") || l.Contains("error loading model vocabulary")
            || l.Contains("unsupported model"))
            return LlamaFailureKind.Format;
        return LlamaFailureKind.None;
    }

    /// <summary>A buffer llama.cpp placed on a graphics device ("CUDA0 model buffer size = 5888.88 MiB"): its kind and size.</summary>
    public static (string Kind, double MiB)? GpuBuffer(string line)
    {
        if (BufferRx().Match(line) is not { Success: true } m) return null;
        if (!double.TryParse(m.Groups[3].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var mib) || mib < 0 || mib > 1 << 22) return null;
        return (m.Groups[2].Value, mib);
    }

    [GeneratedRegex(@"\b(?:CUDA|Vulkan|ROCm|HIP|SYCL)(\d+)\s+(model|KV|compute|RS)\s+buffer size\s*=\s*([\d.]+)\s*MiB", RegexOptions.IgnoreCase)]
    private static partial Regex BufferRx();

    /// <summary>The vision projector is loading onto a graphics device ("CLIP using CUDA0 backend"); its size follows.</summary>
    public static bool ProjectorOnGpu(string line) => ClipGpuRx().IsMatch(line);

    /// <summary>The vision projector's weights ("load_hparams: model size: 875.61 MiB"), or null.</summary>
    public static double? ProjectorMiB(string line) =>
        ClipSizeRx().Match(line) is { Success: true } m && double.TryParse(m.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var mib)
        && mib is >= 0 and < 1 << 20 ? mib : null;

    [GeneratedRegex(@"CLIP using (?:CUDA|Vulkan|ROCm|HIP|SYCL)\d*\s+backend", RegexOptions.IgnoreCase)]
    private static partial Regex ClipGpuRx();

    [GeneratedRegex(@"load_hparams:\s+model size:\s+([\d.]+)\s*MiB", RegexOptions.IgnoreCase)]
    private static partial Regex ClipSizeRx();
}

/// <summary>One model file on one server program on this PC: whether it loaded, how, and how fast it ran.</summary>
public sealed class CompatEntry
{
    public string ModelId { get; set; } = "";
    public string ModelName { get; set; } = "";
    /// <summary>The file's SHA-256 when known (Ollama's blobs and Aqua's downloads), else its path, size and time.</summary>
    public string ModelKey { get; set; } = "";
    /// <summary>The server program's kind, size and time: a new build is a new server.</summary>
    public string ServerKey { get; set; } = "";
    /// <summary>For people: "llama.cpp b11387 · cuda13", "Ollama's engine (version 0.4.1-dev)".</summary>
    public string Server { get; set; } = "";
    /// <summary>"ok", "format", "memory" or "failed".</summary>
    public string Outcome { get; set; } = "";
    public string? Error { get; set; }
    public DateTimeOffset When { get; set; }
    public int Context { get; set; }
    public string Cache { get; set; } = "";
    public int MtpDraft { get; set; }
    public int? GpuLayers { get; set; }
    public int? TotalLayers { get; set; }
    public double LoadSeconds { get; set; }
    /// <summary>What llama.cpp reported placing on the graphics card, in MiB.</summary>
    public double ModelMiB { get; set; }
    public double CacheMiB { get; set; }
    public double ComputeMiB { get; set; }
    public string? Gpu { get; set; }
    public string? Driver { get; set; }
    /// <summary>Answers measured, and their average prompt-reading and writing speeds (tokens a second).</summary>
    public int Answers { get; set; }
    public double PromptPerSecond { get; set; }
    public double OutputPerSecond { get; set; }

    public bool Ok => Outcome == "ok";
    public LlamaFailureKind Failure => Outcome switch { "format" => LlamaFailureKind.Format, "memory" => LlamaFailureKind.Memory, "ok" => LlamaFailureKind.None, _ => LlamaFailureKind.Other };
}

/// <summary>
/// The compatibility record for this PC (llama\compat.json): each model file on each server program it was started with,
/// whether it loaded, with what context, cache and MTP, what llama.cpp put on the graphics card, and the measured speeds.
/// The runtime reads it to avoid starting a file a server is known not to read, and it's the source of the compatibility
/// matrix. Without a path it lives in memory only.
/// </summary>
public sealed class CompatBook
{
    private const int MaxEntries = 200;
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, CompatEntry> _entries = new(StringComparer.Ordinal);

    public CompatBook(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            foreach (var e in JsonSerializer.Deserialize<List<CompatEntry>>(File.ReadAllText(path), JsonUtil.Options) ?? [])
                if (e.ModelKey.Length > 0 && e.ServerKey.Length > 0) _entries[Key(e.ModelKey, e.ServerKey)] = e;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("llama", "The compatibility record couldn't be read; starting a new one", ex);
        }
    }

    public IReadOnlyList<CompatEntry> All
    {
        get { lock (_gate) return _entries.Values.OrderByDescending(e => e.When).ToList(); }
    }

    public CompatEntry? Get(string modelKey, string serverKey)
    {
        lock (_gate) return _entries.GetValueOrDefault(Key(modelKey, serverKey));
    }

    /// <summary>Every record for a model (by library id), newest first.</summary>
    public IReadOnlyList<CompatEntry> For(string modelId)
    {
        lock (_gate) return _entries.Values.Where(e => e.ModelId == modelId).OrderByDescending(e => e.When).ToList();
    }

    /// <summary>Adds or replaces the record for this file on this server (speeds carry over when it loaded before).</summary>
    public void Put(CompatEntry entry)
    {
        lock (_gate)
        {
            var key = Key(entry.ModelKey, entry.ServerKey);
            if (_entries.TryGetValue(key, out var old) && entry.Ok && old.Ok)
            {
                entry.Answers = old.Answers;
                entry.PromptPerSecond = old.PromptPerSecond;
                entry.OutputPerSecond = old.OutputPerSecond;
            }
            _entries[key] = entry;
            while (_entries.Count > MaxEntries)
                _entries.Remove(_entries.MinBy(kv => kv.Value.When).Key);
        }
        Save();
    }

    /// <summary>One answer's speeds, averaged into the record it ran on.</summary>
    public void AddSpeed(string modelKey, string serverKey, double promptPerSecond, double outputPerSecond)
    {
        bool save;
        lock (_gate)
        {
            if (!_entries.TryGetValue(Key(modelKey, serverKey), out var e) || !e.Ok) return;
            var n = e.Answers;
            if (promptPerSecond > 0) e.PromptPerSecond = n == 0 || e.PromptPerSecond <= 0 ? promptPerSecond : (e.PromptPerSecond * n + promptPerSecond) / (n + 1);
            if (outputPerSecond > 0) e.OutputPerSecond = n == 0 || e.OutputPerSecond <= 0 ? outputPerSecond : (e.OutputPerSecond * n + outputPerSecond) / (n + 1);
            e.Answers = n + 1;
            // Written with the first answer and every tenth after it (and when Aqua quits), not on every answer.
            save = e.Answers % 10 == 1;
        }
        if (save) Save();
    }

    /// <summary>Writes what's in memory (Aqua is quitting).</summary>
    public void Flush() => Save();

    /// <summary>Forgets a model's failures (a deliberate fresh try); what loaded fine is kept.</summary>
    public void ForgetFailures(string modelId)
    {
        lock (_gate)
            foreach (var key in _entries.Where(kv => kv.Value.ModelId == modelId && !kv.Value.Ok).Select(kv => kv.Key).ToList())
                _entries.Remove(key);
        Save();
    }

    /// <summary>The record as a Markdown table: the compatibility matrix for this PC.</summary>
    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Model | Server | Result | Context · cache · MTP | Layers on card | On the card (model + cache + work) | Load | Prompt tok/s | Write tok/s | Card · driver | Checked |");
        sb.AppendLine("|---|---|---|---|---|---|---:|---:|---:|---|---|");
        foreach (var e in All)
        {
            var result = e.Ok ? "Loads" : (e.Failure == LlamaFailureKind.Format ? "Can't read the file" : e.Failure == LlamaFailureKind.Memory ? "Out of memory" : "Failed")
                                          + (e.Error is { Length: > 0 } err ? ": " + err.Replace('|', '/') : "");
            var setup = e.Ok ? $"{e.Context / 1024}K · {e.Cache} · {(e.MtpDraft > 0 ? "MTP " + e.MtpDraft : "off")}" : "";
            var layers = e.GpuLayers is int g && e.TotalLayers is int t ? $"{g}/{t}" : e.Ok ? "all" : "";
            var vram = e.ModelMiB > 0 ? $"{e.ModelMiB + e.CacheMiB + e.ComputeMiB:0} MiB ({e.ModelMiB:0} + {e.CacheMiB:0} + {e.ComputeMiB:0})" : "";
            sb.AppendLine(string.Join(" | ", "| " + e.ModelName, e.Server, result, setup, layers, vram,
                e.Ok ? $"{e.LoadSeconds:0.0} s" : "", e.PromptPerSecond > 0 ? $"{e.PromptPerSecond:0}" : "", e.OutputPerSecond > 0 ? $"{e.OutputPerSecond:0.0}" : "",
                $"{e.Gpu} · {e.Driver}", e.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " |"));
        }
        return sb.ToString();
    }

    private void Save()
    {
        if (_path is null) return;
        List<CompatEntry> all;
        lock (_gate) all = _entries.Values.OrderBy(e => e.When).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, JsonUtil.Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("llama", "compat record: " + ex.Message);
        }
    }

    private static string Key(string modelKey, string serverKey) => modelKey + "|" + serverKey;
}
