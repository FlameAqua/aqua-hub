using System.Text;
using System.Text.RegularExpressions;

namespace AquaHub.Core.Ai.Local;

/// <summary>What Aqua needs to know about a GGUF model file, read from its header — never its weights.</summary>
public sealed record GgufInfo
{
    public int Version { get; init; }
    /// <summary>general.architecture ("qwen35", "gemma4", "llama", "clip"…).</summary>
    public string Architecture { get; init; } = "";
    /// <summary>general.type: "model", "mmproj" (a vision projector) or "adapter".</summary>
    public string Type { get; init; } = "model";
    public string? Name { get; init; }
    public string? BaseName { get; init; }
    /// <summary>general.size_label, e.g. "27B".</summary>
    public string? SizeLabel { get; init; }
    /// <summary>The quantization named by general.file_type ("IQ2_S", "Q4_K_M"…), when it's one Aqua knows.</summary>
    public string? FileType { get; init; }
    /// <summary>Weights in the file, counted from the tensors' shapes.</summary>
    public long Parameters { get; init; }
    /// <summary>The longest context the model was trained for ({arch}.context_length; 0 = not stated).</summary>
    public int ContextLength { get; init; }
    public int Layers { get; init; }
    public int EmbeddingLength { get; init; }
    /// <summary>
    /// Layers that keep a cache of keys and values for every token (attention layers; a hybrid model's linear layers keep a
    /// fixed state instead, and an MTP layer only counts when MTP is used).
    /// </summary>
    public int KvLayers { get; init; }
    /// <summary>Key and value elements the cache holds per token of the whole context (layers that see all of it).</summary>
    public long KvElementsPerToken { get; init; }
    /// <summary>
    /// Key and value elements per token of a sliding-window layer, which keeps only the last <see cref="SlidingWindow"/>
    /// tokens (Gemma's local layers): their cache doesn't grow with the context.
    /// </summary>
    public long KvWindowElementsPerToken { get; init; }
    /// <summary>Tokens a sliding-window layer looks back over ({arch}.attention.sliding_window; 0 = none).</summary>
    public int SlidingWindow { get; init; }
    /// <summary>Multi-token prediction heads (llama.cpp's "nextn" tensors): speculative decoding without a second model.</summary>
    public bool HasMtp { get; init; }
    /// <summary>A vision encoder (a projector file, or a model with one built in).</summary>
    public bool HasVision { get; init; }
    /// <summary>A model that turns text into vectors (search by meaning), not one that writes.</summary>
    public bool IsEmbedding { get; init; }
    /// <summary>tokenizer.chat_template (up to 256 KB), or null.</summary>
    public string? ChatTemplate { get; init; }
    /// <summary>
    /// The header's other values: numbers, flags, strings up to 4 KB and numeric arrays up to 1,024 items (long[]); the
    /// tokenizer's long lists aren't kept. For a model's details view and for rules this record doesn't cover.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    public bool HasChatTemplate => !string.IsNullOrEmpty(ChatTemplate);
    /// <summary>The template has a thinking switch or thinking tags.</summary>
    public bool TemplateThinks => ChatTemplate is { } t && (t.Contains("enable_thinking", StringComparison.Ordinal) || t.Contains("<think>", StringComparison.Ordinal)
                                                            || t.Contains("reasoning_content", StringComparison.Ordinal));
    /// <summary>The template formats tool calls.</summary>
    public bool TemplateTools => ChatTemplate is { } t && t.Contains("tools", StringComparison.Ordinal);
    /// <summary>The template reads a reasoning effort level, and the levels it names.</summary>
    public IReadOnlyList<string> EffortLevels => ChatTemplate is { } t && t.Contains("reasoning_effort", StringComparison.Ordinal)
        ? new[] { "minimal", "low", "medium", "high", "xhigh", "max" }.Where(l => t.Contains("'" + l + "'", StringComparison.Ordinal) || t.Contains("\"" + l + "\"", StringComparison.Ordinal)).ToList()
        : Array.Empty<string>();
}

/// <summary>A file that isn't a GGUF model Aqua can read.</summary>
public sealed class GgufFormatException(string message) : Exception(message);

/// <summary>
/// Reads a GGUF file's header: its metadata and its tensors' names and shapes (versions 2 and 3). A model file can come
/// from anywhere, so every length is checked against what the file can hold and against fixed caps before anything is
/// read or allocated; arrays Aqua doesn't need (a tokenizer's 250,000 tokens) are skipped, not kept.
/// </summary>
public static partial class Gguf
{
    private const uint Magic = 0x46554747; // "GGUF", little-endian
    private const int MaxKeptString = 256 * 1024;
    private const long MaxString = 64L * 1024 * 1024;
    private const long MaxCount = 100_000_000;
    private const int MaxTensors = 1_000_000;

    public static GgufInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Read(stream);
    }

    /// <summary>True when the file starts like a GGUF file (cheap: reads 4 bytes).</summary>
    public static bool LooksLikeGguf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
            Span<byte> head = stackalloc byte[4];
            return stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && BitConverter.ToUInt32(head) == Magic;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public static GgufInfo Read(Stream stream)
    {
        var r = new Reader(stream);
        if (r.U32() != Magic) throw new GgufFormatException("not a GGUF file");
        var version = (int)r.U32();
        if (version is not (2 or 3)) throw new GgufFormatException($"GGUF version {version} isn't one Aqua reads");
        var tensorCount = r.U64();
        var kvCount = r.U64();
        if (tensorCount > MaxTensors || kvCount > MaxTensors) throw new GgufFormatException("the header's counts are implausible");

        var meta = new Dictionary<string, object>(StringComparer.Ordinal);
        for (ulong i = 0; i < kvCount; i++)
        {
            var key = r.String(4096) ?? throw new GgufFormatException("a metadata key is too long");
            var valueType = r.U32();
            var value = ReadValue(r, valueType, keep: key == "tokenizer.chat_template" ? MaxKeptString : 4096);
            if (value is not null) meta[key] = value;
        }

        var arch = Str(meta, "general.architecture") ?? "";
        long parameters = 0;
        var kvBlocks = new Dictionary<int, long>();
        var mtpBlocks = new HashSet<int>();
        var hasMtp = false;
        var hasVisionTensors = false;
        Span<long> shape = stackalloc long[8];
        for (ulong i = 0; i < tensorCount; i++)
        {
            var name = r.String(4096) ?? throw new GgufFormatException("a tensor name is too long");
            var dims = (int)r.U32();
            if (dims is < 1 or > 8) throw new GgufFormatException("a tensor has an implausible shape");
            double elements = 1;
            for (var d = 0; d < dims; d++)
            {
                var n = r.U64();
                if (n > (ulong)long.MaxValue) throw new GgufFormatException("a tensor is implausibly large");
                shape[d] = (long)n;
                elements *= n;
            }
            r.U32(); // the tensor's type
            r.U64(); // its offset in the data
            parameters += elements > long.MaxValue / 2 ? 0 : (long)elements;

            if (name.Contains(".nextn.", StringComparison.Ordinal) || name.StartsWith("mtp.", StringComparison.Ordinal))
            {
                hasMtp = true;
                if (BlockOf(name) is int b) mtpBlocks.Add(b);
                continue;
            }
            if (name.StartsWith("v.", StringComparison.Ordinal) || name.StartsWith("mm.", StringComparison.Ordinal)) hasVisionTensors = true;
            // Attention keys and values: the output size of attn_k and attn_v is what the cache keeps per token.
            if (dims >= 2 && BlockOf(name) is int block && KvTensor().Match(name) is { Success: true })
                kvBlocks[block] = kvBlocks.GetValueOrDefault(block) + shape[1];
        }
        foreach (var b in mtpBlocks) kvBlocks.Remove(b);

        var layers = Int(meta, arch + ".block_count");
        var embedding = Int(meta, arch + ".embedding_length");
        var kvElements = kvBlocks.Values.Sum();
        var kvLayers = kvBlocks.Count;
        if (kvLayers == 0 && layers > 0)
        {
            // Fused or unusual attention: estimate from the header (every layer, grouped-query heads).
            var heads = Int(meta, arch + ".attention.head_count");
            var kvHeads = Int(meta, arch + ".attention.head_count_kv") is > 0 and var h ? h : heads;
            var keyLength = Int(meta, arch + ".attention.key_length") is > 0 and var k ? k : heads > 0 ? embedding / heads : 0;
            var valueLength = Int(meta, arch + ".attention.value_length") is > 0 and var v ? v : keyLength;
            kvLayers = layers;
            kvElements = (long)layers * kvHeads * (keyLength + valueLength);
        }
        if (Int(meta, arch + ".nextn_predict_layers") > 0) hasMtp = true;

        // Sliding-window layers keep only the window's tokens: Gemma gives a per-layer pattern (1 = sliding), older files a
        // period (every Nth layer sees everything). Without either, every layer is taken to see the whole context.
        var window = Int(meta, arch + ".attention.sliding_window");
        long windowElements = 0;
        if (window > 0 && kvBlocks.Count > 0 && meta.TryGetValue(arch + ".attention.sliding_window_pattern", out var pattern))
        {
            Func<int, bool>? sliding = pattern switch
            {
                long[] perLayer => b => b < perLayer.Length && perLayer[b] != 0,
                _ when ToLong(pattern) is long period and > 1 => b => (b + 1) % period != 0,
                _ => null,
            };
            if (sliding is not null)
            {
                windowElements = kvBlocks.Where(kv => sliding(kv.Key)).Sum(kv => kv.Value);
                kvElements -= windowElements;
            }
        }

        var type = (Str(meta, "general.type") ?? (arch == "clip" ? "mmproj" : "model")).ToLowerInvariant();
        return new GgufInfo
        {
            Version = version,
            Architecture = arch,
            Type = type,
            Name = Str(meta, "general.name"),
            BaseName = Str(meta, "general.basename"),
            SizeLabel = Str(meta, "general.size_label"),
            FileType = meta.TryGetValue("general.file_type", out var ft) && ToLong(ft) is long f ? FileTypeName(f) : null,
            Parameters = parameters,
            ContextLength = Int(meta, arch + ".context_length"),
            Layers = layers,
            EmbeddingLength = embedding,
            KvLayers = kvLayers,
            KvElementsPerToken = kvElements,
            KvWindowElementsPerToken = windowElements,
            SlidingWindow = window,
            HasMtp = hasMtp,
            HasVision = type == "mmproj" ? Bool(meta, "clip.has_vision_encoder") ?? true : hasVisionTensors,
            IsEmbedding = arch.Contains("bert", StringComparison.Ordinal) || arch.Contains("embedding", StringComparison.Ordinal)
                          || meta.ContainsKey(arch + ".pooling_type"),
            ChatTemplate = Str(meta, "tokenizer.chat_template"),
            Metadata = meta.Where(kv => kv.Key != "tokenizer.chat_template").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>The quantization a file name gives ("Qwen3.8-27B-IQ2_S-mtp.gguf" → "IQ2_S"), which is often more exact than the header's.</summary>
    public static string? QuantFromName(string fileName) => QuantRx().Match(fileName) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    // llama.cpp's LLAMA_FTYPE_MOSTLY_* values (llama.h); removed ones are left out.
    private static string? FileTypeName(long value) => value switch
    {
        0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 7 => "Q8_0", 8 => "Q5_0", 9 => "Q5_1", 10 => "Q2_K", 11 => "Q3_K_S",
        12 => "Q3_K_M", 13 => "Q3_K_L", 14 => "Q4_K_S", 15 => "Q4_K_M", 16 => "Q5_K_S", 17 => "Q5_K_M", 18 => "Q6_K",
        19 => "IQ2_XXS", 20 => "IQ2_XS", 21 => "Q2_K_S", 22 => "IQ3_XS", 23 => "IQ3_XXS", 24 => "IQ1_S", 25 => "IQ4_NL",
        26 => "IQ3_S", 27 => "IQ3_M", 28 => "IQ2_S", 29 => "IQ2_M", 30 => "IQ4_XS", 31 => "IQ1_M", 32 => "BF16",
        36 => "TQ1_0", 37 => "TQ2_0", 38 => "MXFP4",
        _ => null,
    };

    private static object? ReadValue(Reader r, uint type, int keep)
    {
        switch (type)
        {
            case 0: return (long)r.U8();
            case 1: return (long)(sbyte)r.U8();
            case 2: return (long)r.U16();
            case 3: return (long)(short)r.U16();
            case 4: return (long)r.U32();
            case 5: return (long)(int)r.U32();
            case 6: return (double)r.F32();
            case 7: return r.U8() != 0;
            case 8: return r.String(keep);
            case 10: return (long)r.U64();
            case 11: return (long)r.U64();
            case 12: return r.F64();
            case 9:
            {
                var itemType = r.U32();
                var count = r.U64();
                if (count > MaxCount) throw new GgufFormatException("an array is implausibly long");
                // Small numeric arrays (per-layer head counts) are kept; everything else is skipped.
                var size = ScalarSize(itemType);
                if (size > 0 && count <= 1024)
                {
                    var values = new long[count];
                    for (ulong i = 0; i < count; i++) values[i] = ReadValue(r, itemType, 0) is { } v ? ToLong(v) ?? 0 : 0;
                    return values;
                }
                if (size > 0) { r.Skip((long)count * size); return null; }
                if (itemType == 8) { for (ulong i = 0; i < count; i++) r.String(0); return null; }
                if (itemType == 9) throw new GgufFormatException("nested arrays aren't supported");
                throw new GgufFormatException($"unknown array type {itemType}");
            }
            default: throw new GgufFormatException($"unknown metadata type {type}");
        }
    }

    private static int ScalarSize(uint type) => type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => 0 };

    private static int? BlockOf(string name)
    {
        if (!name.StartsWith("blk.", StringComparison.Ordinal)) return null;
        var end = name.IndexOf('.', 4);
        return end > 4 && int.TryParse(name.AsSpan(4, end - 4), out var n) ? n : null;
    }

    private static string? Str(Dictionary<string, object> meta, string key) => meta.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;
    private static bool? Bool(Dictionary<string, object> meta, string key) => meta.TryGetValue(key, out var v) && v is bool b ? b : null;

    private static int Int(Dictionary<string, object> meta, string key) => meta.TryGetValue(key, out var v) switch
    {
        false => 0,
        _ => v is long[] arr ? (int)Math.Clamp(arr.DefaultIfEmpty(0).Max(), 0, int.MaxValue) : ToLong(v) is long l ? (int)Math.Clamp(l, 0, int.MaxValue) : 0,
    };

    private static long? ToLong(object v) => v switch { long l => l, double d when d is >= long.MinValue and <= long.MaxValue => (long)d, bool b => b ? 1 : 0, _ => null };

    [GeneratedRegex(@"\.attn_[kv]\.weight$")]
    private static partial Regex KvTensor();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])(?:IQ[1-4]_(?:XXS|XS|NL|S|M)|Q[2-8]_K(?:_[SML])?|Q[4-8]_[01]|TQ[12]_0|MXFP4|BF16|F16|F32)(?![A-Za-z0-9])")]
    private static partial Regex QuantRx();

    /// <summary>Little-endian reads with every length checked against the stream and the caps.</summary>
    private sealed class Reader(Stream s)
    {
        private readonly byte[] _buf = new byte[8];

        private void Fill(int n)
        {
            if (s.ReadAtLeast(_buf.AsSpan(0, n), n, throwOnEndOfStream: false) != n) throw new GgufFormatException("the header ends early");
        }

        public byte U8() { Fill(1); return _buf[0]; }
        public ushort U16() { Fill(2); return BitConverter.ToUInt16(_buf, 0); }
        public uint U32() { Fill(4); return BitConverter.ToUInt32(_buf, 0); }
        public ulong U64() { Fill(8); return BitConverter.ToUInt64(_buf, 0); }
        public float F32() { Fill(4); return BitConverter.ToSingle(_buf, 0); }
        public double F64() { Fill(8); return BitConverter.ToDouble(_buf, 0); }

        /// <summary>A string; kept when it's at most <paramref name="keep"/> bytes, else skipped (null).</summary>
        public string? String(int keep)
        {
            var length = U64();
            if (length > MaxString) throw new GgufFormatException("a string is implausibly long");
            if (length > (ulong)keep) { Skip((long)length); return null; }
            if (length == 0) return "";
            var bytes = new byte[length];
            if (s.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) != bytes.Length) throw new GgufFormatException("the header ends early");
            return Encoding.UTF8.GetString(bytes);
        }

        public void Skip(long n)
        {
            if (n < 0) throw new GgufFormatException("a negative length");
            if (s.CanSeek)
            {
                if (s.Position + n > s.Length) throw new GgufFormatException("the header ends early");
                s.Seek(n, SeekOrigin.Current);
                return;
            }
            Span<byte> scratch = stackalloc byte[4096];
            while (n > 0)
            {
                var got = s.Read(scratch[..(int)Math.Min(n, scratch.Length)]);
                if (got <= 0) throw new GgufFormatException("the header ends early");
                n -= got;
            }
        }
    }
}
