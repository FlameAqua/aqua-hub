using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Local;

/// <summary>
/// A model in Ollama's store, as llama.cpp can run it: the GGUF blob itself (and a vision projector's), its names and the
/// sampling defaults its publisher set. One blob pulled under two names is one model with both names.
/// </summary>
public sealed record OllamaModel(
    IReadOnlyList<string> Names,
    string ModelPath,
    long Size,
    string Sha256,
    string? ProjectorPath,
    IReadOnlyDictionary<string, double> Sampling,
    DateTimeOffset Modified)
{
    public string Name => Names[0];
}

/// <summary>
/// Reads Ollama's store (manifests and blobs) without changing anything in it. Blob paths are built only from digests of
/// the form "sha256:" + 64 hex digits, so a manifest can't point Aqua outside the store's blobs folder, and a blob must have
/// the size its manifest gives. Models that need something llama.cpp doesn't take from a file (a LoRA adapter) are left out.
/// </summary>
public static partial class OllamaStore
{
    private const string ModelType = "application/vnd.ollama.image.model";
    private const string ProjectorType = "application/vnd.ollama.image.projector";
    private const string ParamsType = "application/vnd.ollama.image.params";
    private const string AdapterType = "application/vnd.ollama.image.adapter";

    /// <summary>OLLAMA_MODELS when set, else %USERPROFILE%\.ollama\models.</summary>
    public static string DefaultFolder() =>
        Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } env
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ollama", "models");

    public static IReadOnlyList<OllamaModel> List(string? folder = null)
    {
        folder = Path.GetFullPath(folder ?? DefaultFolder());
        var manifests = Path.Combine(folder, "manifests");
        var blobs = Path.Combine(folder, "blobs");
        var found = new Dictionary<string, OllamaModel>(StringComparer.Ordinal);
        if (!Directory.Exists(manifests)) return Array.Empty<OllamaModel>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(manifests, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, AttributesToSkip = FileAttributes.ReparsePoint }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<OllamaModel>(); }

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 64 * 1024) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                string? model = null, projector = null, parameters = null;
                long size = 0;
                var adapter = false;
                foreach (var layer in doc.RootElement.Arr("layers"))
                {
                    var type = layer.Str("mediaType");
                    var digest = layer.Str("digest");
                    if (digest is null || !DigestRx().IsMatch(digest)) continue;
                    switch (type)
                    {
                        case ModelType: model = digest; size = layer.Lng("size") ?? 0; break;
                        case ProjectorType: projector = digest; break;
                        case ParamsType: parameters = digest; break;
                        case AdapterType: adapter = true; break;
                    }
                }
                if (model is null || adapter) continue;
                var modelPath = Blob(blobs, model);
                if (!File.Exists(modelPath) || new FileInfo(modelPath).Length != size || !Gguf.LooksLikeGguf(modelPath)) continue;
                var projectorPath = projector is null ? null : Blob(blobs, projector);
                if (projectorPath is not null && !File.Exists(projectorPath)) projectorPath = null;
                var name = NameOf(Path.GetRelativePath(manifests, file));
                if (name is null) continue;
                var sha = model[7..];
                if (found.TryGetValue(sha, out var known))
                {
                    found[sha] = known with { Names = known.Names.Append(name).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList() };
                    continue;
                }
                found[sha] = new OllamaModel(new[] { name }, modelPath, size, sha, projectorPath,
                    parameters is null ? new Dictionary<string, double>() : Sampling(Blob(blobs, parameters)), info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Debug("models", $"Skipped an Ollama manifest ({Path.GetFileName(file)}): {ex.Message}");
            }
        }
        return found.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// "registry.ollama.ai\library\qwen3.5\9b" → "qwen3.5:9b"; "registry.ollama.ai\me\model\latest" → "me/model:latest";
    /// "hf.co\owner\repo\file.gguf" → "hf.co/owner/repo:file.gguf".
    /// </summary>
    internal static string? NameOf(string relative)
    {
        var parts = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts.Any(p => p.Length > 200 || p.Any(char.IsControl))) return null;
        var tag = parts[^1];
        var model = string.Join('/', parts[..^1]);
        if (model.StartsWith("registry.ollama.ai/library/", StringComparison.OrdinalIgnoreCase)) model = model["registry.ollama.ai/library/".Length..];
        else if (model.StartsWith("registry.ollama.ai/", StringComparison.OrdinalIgnoreCase)) model = model["registry.ollama.ai/".Length..];
        return model + ":" + tag;
    }

    private static string Blob(string blobs, string digest) => Path.Combine(blobs, "sha256-" + digest[7..]);

    /// <summary>The sampling settings a params blob sets (temperature, top_p…); stop words and the rest are left out.</summary>
    private static Dictionary<string, double> Sampling(string path)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return values;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var key in new[] { "temperature", "top_k", "top_p", "min_p", "repeat_penalty", "presence_penalty", "frequency_penalty" })
                if (doc.RootElement.Dbl(key) is double v && double.IsFinite(v)) values[key] = v;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return values;
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestRx();
}
