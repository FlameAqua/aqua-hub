using System.Text.Json;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Local;

public enum ModelSource { Aqua, Ollama }

/// <summary>A model Aqua's runtime can run: its file, where it came from and what its header says it can do.</summary>
public sealed record LocalModel
{
    /// <summary>"hf:owner/repo/file.gguf" for a download, "ollama:name:tag" for a model in Ollama's store.</summary>
    public required string Id { get; init; }
    /// <summary>For people: "qwen3.5:9b", "Qwen3.8 27B · IQ2_S".</summary>
    public required string Name { get; init; }
    public required ModelSource Source { get; init; }
    public required string Path { get; init; }
    public required GgufInfo Info { get; init; }
    public long Size { get; init; }
    public string? ProjectorPath { get; init; }
    public long ProjectorSize { get; init; }
    public string? Sha256 { get; init; }
    /// <summary>Hugging Face repository a download came from.</summary>
    public string? Repo { get; init; }
    /// <summary>Other names it goes by (Ollama: the same blob pulled under two names).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    /// <summary>Sampling defaults its publisher set (Ollama's params): temperature, top_p…</summary>
    public IReadOnlyDictionary<string, double> Sampling { get; init; } = new Dictionary<string, double>();

    public string? Quant => Gguf.QuantFromName(System.IO.Path.GetFileName(Path)) ?? Info.FileType;
    /// <summary>
    /// It can look at pictures in Aqua's runtime: llama.cpp needs a separate projector file (Ollama's merged files, with
    /// vision built into the model file, read as text-only).
    /// </summary>
    public bool Vision => ProjectorPath is not null;
    public bool Thinking => Info.TemplateThinks;
    public bool Tools => Info.TemplateTools;
    public bool Embedding => Info.IsEmbedding;
    public bool Mtp => Info.HasMtp;
    /// <summary>What loading it reads from disk (the model and its projector).</summary>
    public long Bytes => Size + ProjectorSize;
}

/// <summary>
/// The models Aqua's runtime can run: those Aqua downloaded (recorded in the models folder's library.json, each checked
/// against its SHA-256 when it arrived) and, unless switched off, those in Ollama's store, read where they are. A model's
/// header is read once per file version and kept in memory. Files a record names must be inside the models folder, so a
/// tampered library.json can't point the runtime elsewhere; only Aqua's own downloads can be removed from here.
/// </summary>
public sealed class ModelLibrary
{
    public const string LibraryFile = "library.json";
    private readonly Func<LlamaSettings> _settings;
    private readonly string _defaultFolder;
    private readonly Func<string> _ollamaFolder;
    private readonly object _gate = new();
    private readonly Dictionary<(string Path, long Size, DateTime Modified), GgufInfo> _headers = new();
    private IReadOnlyList<LocalModel> _models = Array.Empty<LocalModel>();
    private IReadOnlyList<string> _problems = Array.Empty<string>();

    public ModelLibrary(Func<LlamaSettings> settings, string defaultFolder, Func<string>? ollamaFolder = null)
    {
        _settings = settings;
        _defaultFolder = Path.GetFullPath(defaultFolder);
        _ollamaFolder = ollamaFolder ?? (() => OllamaStore.DefaultFolder());
    }

    /// <summary>Where downloads go: Settings' folder, or the profile's "models" folder.</summary>
    public string Folder => _settings().ModelsFolder is { Length: > 0 } custom ? Path.GetFullPath(custom) : _defaultFolder;

    /// <summary>The models found by the last <see cref="Refresh"/>.</summary>
    public IReadOnlyList<LocalModel> Models { get { lock (_gate) return _models; } }
    /// <summary>Files the last refresh couldn't use, in plain words ("qwen3.5:9b: not a model file Aqua can read").</summary>
    public IReadOnlyList<string> Problems { get { lock (_gate) return _problems; } }
    /// <summary>The list changed (a refresh, a download, a removal); raised on the thread that changed it.</summary>
    public event Action? Changed;

    /// <summary>Reads library.json and Ollama's store again (file IO and headers: call it off the UI thread).</summary>
    public IReadOnlyList<LocalModel> Refresh()
    {
        var models = new List<LocalModel>();
        var problems = new List<string>();
        foreach (var entry in ReadEntries())
        {
            var path = Inside(entry.Path);
            if (path is null || !File.Exists(path) || new FileInfo(path).Length != entry.Size)
            {
                problems.Add($"{entry.File}: the file is missing or incomplete");
                continue;
            }
            var info = Header(path, problems, entry.File);
            if (info is null) continue;
            var projector = entry.Projector is { } p && Inside(p.Path) is { } pp && File.Exists(pp) ? pp : null;
            models.Add(new LocalModel
            {
                Id = "hf:" + entry.Repo + "/" + entry.File,
                Name = NameFor(info, entry.File),
                Source = ModelSource.Aqua,
                Path = path,
                Info = info,
                Size = entry.Size,
                ProjectorPath = projector,
                ProjectorSize = projector is null ? 0 : entry.Projector!.Size,
                Sha256 = entry.Sha256,
                Repo = entry.Repo,
            });
        }
        if (_settings().UseOllamaModels)
        {
            foreach (var o in OllamaStore.List(_ollamaFolder()))
            {
                var info = Header(o.ModelPath, problems, o.Name);
                if (info is null) continue;
                models.Add(new LocalModel
                {
                    Id = "ollama:" + o.Name,
                    Name = o.Name,
                    Source = ModelSource.Ollama,
                    Path = o.ModelPath,
                    Info = info,
                    Size = o.Size,
                    ProjectorPath = o.ProjectorPath,
                    ProjectorSize = o.ProjectorPath is { } pp ? SafeLength(pp) : 0,
                    Sha256 = o.Sha256,
                    Aliases = o.Names.Skip(1).ToList(),
                    Sampling = o.Sampling,
                });
            }
        }
        // A download of a file Ollama also has (the same SHA-256) is shown once, as Aqua's.
        var ordered = models.GroupBy(m => m.Sha256 ?? m.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var preferred = g.OrderBy(m => m.Source).First();
                var aliases = g.SelectMany(Identities).Concat(g.SelectMany(m => m.Aliases))
                    .Where(id => !id.Equals(preferred.Id, StringComparison.OrdinalIgnoreCase)
                                 && !id.Equals(preferred.Name, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return preferred with { Aliases = aliases };
            })
            .OrderBy(m => m.Embedding).ThenByDescending(m => m.Info.Parameters).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (_gate)
        {
            _models = ordered;
            _problems = problems;
        }
        Changed?.Invoke();
        return ordered;
    }

    /// <summary>
    /// The model a setting or request names: its id, an Ollama name ("qwen3.5:9b"), or one of its other names; null when
    /// nothing matches.
    /// </summary>
    public LocalModel? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim();
        var models = Models;
        return models.FirstOrDefault(m => m.Id.Equals(n, StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault(m => Identities(m).Any(id => id.Equals(n, StringComparison.OrdinalIgnoreCase)
                   || ("ollama:" + id).Equals(n, StringComparison.OrdinalIgnoreCase)))
               ?? models.FirstOrDefault(m =>
               {
                   var unprefixed = n.StartsWith("ollama:", StringComparison.OrdinalIgnoreCase) ? n[7..] : n;
                   if (unprefixed.Contains(':')) return false;
                   var latest = unprefixed + ":latest";
                   return Identities(m).Any(id => id.Equals(latest, StringComparison.OrdinalIgnoreCase)
                       || id.Equals("ollama:" + latest, StringComparison.OrdinalIgnoreCase));
               })
               ?? models.FirstOrDefault(m => Path.GetFileName(m.Path).Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Identities(LocalModel model)
    {
        yield return model.Id;
        yield return model.Name;
        foreach (var alias in model.Aliases) yield return alias;
    }

    /// <summary>
    /// Records a finished download (its files already checked and in the models folder) and adds it to the list.
    /// </summary>
    public void AddDownload(string repo, string revision, string file, string path, long size, string sha256,
        (string File, string Path, long Size, string Sha256)? projector, DateTimeOffset now)
    {
        lock (_gate)
        {
            var entries = ReadEntries().Where(e => !(e.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) && e.File.Equals(file, StringComparison.OrdinalIgnoreCase))).ToList();
            entries.Add(new Entry(repo, revision, file, Relative(path), size, sha256.ToLowerInvariant(),
                projector is { } p ? new ProjectorEntry(p.File, Relative(p.Path), p.Size, p.Sha256.ToLowerInvariant()) : null, now));
            WriteEntries(entries);
        }
        Refresh();
    }

    /// <summary>
    /// Deletes one of Aqua's own downloads (its file, and its projector unless another model uses it). Models in Ollama's
    /// store are Ollama's to remove: false for those, or when nothing matches.
    /// </summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            var entries = ReadEntries();
            var gone = entries.FirstOrDefault(e => ("hf:" + e.Repo + "/" + e.File).Equals(id, StringComparison.OrdinalIgnoreCase));
            if (gone is null) return false;
            var keep = entries.Where(e => e != gone).ToList();
            WriteEntries(keep);
            TryDelete(Inside(gone.Path));
            if (gone.Projector is { } p && !keep.Any(e => e.Projector?.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase) == true))
                TryDelete(Inside(p.Path));
            Log.Info("models", "Removed " + gone.File);
        }
        Refresh();
        return true;
    }

    /// <summary>A path a record names, when it's inside the models folder (null otherwise).</summary>
    internal string? Inside(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var folder = Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private string Relative(string path)
    {
        var relative = Path.GetRelativePath(Folder, path);
        return Inside(relative) is not null ? relative : throw new ArgumentException("A model must be inside the models folder.", nameof(path));
    }

    /// <summary>"Qwen3.8 27B · IQ2_S" from the header (falling back to the file name), with "· MTP" when it has the heads.</summary>
    internal static string NameFor(GgufInfo info, string file)
    {
        var name = (info.BaseName ?? info.Name ?? Path.GetFileNameWithoutExtension(file)).Replace('-', ' ').Replace('_', ' ').Trim();
        if (info.SizeLabel is { Length: > 0 } size && !name.Contains(size, StringComparison.OrdinalIgnoreCase)) name += " " + size;
        var quant = Gguf.QuantFromName(file) ?? info.FileType;
        return name + (quant is null ? "" : " · " + quant) + (info.HasMtp ? " · MTP" : "");
    }

    private GgufInfo? Header(string path, List<string> problems, string label)
    {
        try
        {
            var file = new FileInfo(path);
            var key = (path, file.Length, file.LastWriteTimeUtc);
            lock (_headers)
                if (_headers.TryGetValue(key, out var known)) return known;
            var info = Gguf.Read(path);
            if (info.Type is "mmproj" or "adapter")
            {
                problems.Add($"{label}: a {(info.Type == "mmproj" ? "vision projector" : "adapter")}, not a model on its own");
                return null;
            }
            lock (_headers) _headers[key] = info;
            return info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GgufFormatException)
        {
            problems.Add($"{label}: not a model file Aqua can read ({ex.Message})");
            return null;
        }
    }

    private sealed record Entry(string Repo, string Revision, string File, string Path, long Size, string Sha256, ProjectorEntry? Projector, DateTimeOffset Added);
    private sealed record ProjectorEntry(string File, string Path, long Size, string Sha256);
    private sealed record Record(int Version, List<Entry> Models);

    private List<Entry> ReadEntries()
    {
        var path = Path.Combine(Folder, LibraryFile);
        try
        {
            if (!File.Exists(path)) return new();
            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), JsonUtil.Options);
            return (record?.Models ?? new()).Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Repo) && !string.IsNullOrWhiteSpace(e.File)
                                                         && !string.IsNullOrWhiteSpace(e.Path) && e.Size > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Warn("models", "Couldn't read the model library's record", ex);
            return new();
        }
    }

    private void WriteEntries(List<Entry> entries)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, LibraryFile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Record(1, entries), JsonUtil.Options));
        File.Move(tmp, path, overwrite: true);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("models", "Couldn't delete " + Path.GetFileName(path), ex); }
    }
}
