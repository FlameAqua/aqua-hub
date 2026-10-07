using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Net;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Local;

/// <summary>A GGUF file in a Hugging Face repository: its path there, size and SHA-256 (the file's LFS id).</summary>
public sealed partial record HfFile(string Path, long Size, string Sha256)
{
    public string Name => System.IO.Path.GetFileName(Path);
    /// <summary>A vision projector ("mmproj-…gguf"), downloaded beside a model that can look at pictures.</summary>
    public bool IsProjector => Name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase);
    public string? Quant => Gguf.QuantFromName(Name);
    /// <summary>One part of a model split over several files ("…-00001-of-00003.gguf"), which Aqua doesn't download.</summary>
    public bool Split => SplitRx().IsMatch(Name);

    [GeneratedRegex(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitRx();
}

/// <summary>A repository a search found: how popular it is and what kind of model it holds.</summary>
public sealed record HfRepo(string Id, long Downloads, long Likes, DateTimeOffset? Updated, string? Pipeline);

/// <summary>A repository's GGUF files at one commit, and what Hugging Face's summary says about the model.</summary>
public sealed record HfRepoFiles(string Repo, string Revision, bool Gated, string? License, IReadOnlyList<HfFile> Models,
    IReadOnlyList<HfFile> Projectors, int ContextLength, string? Architecture, long Parameters);

/// <summary>
/// A model Aqua suggests for one role on cards of a size, pinned to a commit and to its files' sizes and SHA-256s (so the
/// download is exactly the file that was checked when this list was written).
/// </summary>
public sealed record ModelRecommendation(string Role, string Name, string Why, string Repo, string Revision, HfFile File, HfFile? Projector, long MinVramBytes, long MaxVramBytes = long.MaxValue)
{
    public string Id => "hf:" + Repo + "/" + File.Name;
    public long Bytes => File.Size + (Projector?.Size ?? 0);
}

/// <summary>How far a download has got.</summary>
public sealed record ModelDownloadProgress(string Step, long Done, long Total);

/// <summary>
/// Finds models on Hugging Face (public repositories only — Aqua has no account to sign in with) and downloads GGUF files
/// into the model library: only from huggingface.co, over HTTPS, at a fixed commit, each file checked against the size and
/// SHA-256 Hugging Face lists for it (multi-GB files resume where they stopped). One download at a time.
/// </summary>
public sealed partial class ModelDownloads
{
    private const string Hub = "https://huggingface.co/";
    private readonly ModelLibrary _library;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, GgufInfo> _headers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The most of a file a header read takes: a big vocabulary makes a header ten or twenty MB.</summary>
    internal const long HeaderLimit = 64L << 20;
    private CancellationTokenSource? _cts;

    /// <summary>Free bytes on the models folder's drive (tests replace it).</summary>
    internal Func<string, long> FreeSpace = folder =>
    {
        try { return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
    };
    /// <summary>How long a download may go without any data before it counts as stalled.</summary>
    internal TimeSpan StallAfter = TimeSpan.FromSeconds(90);

    public ModelDownloads(ModelLibrary library, HttpClient http)
    {
        _library = library;
        _http = http;
    }

    public bool Busy { get; private set; }
    /// <summary>The running download's latest progress, or null.</summary>
    public ModelDownloadProgress? Current { get; private set; }
    /// <summary>The running download (thread pool).</summary>
    public event Action<ModelDownloadProgress>? Progress;
    /// <summary>A download started or ended either way (thread pool).</summary>
    public event Action? Changed;

    /// <summary>
    /// The models Aqua suggests for the card they suit. Main (also the editor by default): Qwen3.5 9B Q5_K_M for 12 GB
    /// cards; Qwen3.8 27B with its MTP heads (IQ3_XXS on 16 GB, IQ3_S on 24 GB) for larger cards; creative: Gemma 4 12B;
    /// search by meaning: EmbeddingGemma 300M.
    /// </summary>
    public static readonly IReadOnlyList<ModelRecommendation> Recommendations = new[]
    {
        Qwen38("IQ3_S", 12_120_016_960, "58fd826723939933dc86f45b7fe04545cbc2de1c70f6fe2cdd3858c87a98c12f", 22L << 30, long.MaxValue, "The best Qwen3.8 27B that fits a 24 GB card."),
        Qwen38("IQ3_XXS", 10_442_827_840, "63f29a2189a6b4cc31f81e093d3856ad293a5583114439f41ae1ed7af4093262", 15L << 30, 22L << 30, "Qwen3.8 27B for a 16 GB card: 1 point behind the full model on hard science questions."),
        Qwen35Q5("main", "A 9B Q5 model that fits a 12 GB card with room for its vision projector and context."),
        new ModelRecommendation("main", "Qwen3.5 9B · Q4_K_M", "A strong, fast all-rounder for cards of 6–10 GB.", "unsloth/Qwen3.5-9B-GGUF",
            "3885219b6810b007914f3a7950a8d1b469d598a5",
            new HfFile("Qwen3.5-9B-Q4_K_M.gguf", 5_680_522_464, "03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8"),
            new HfFile("mmproj-F16.gguf", 918_166_080, "f70dc3509053962b0d0d3ee8a7eacebf5d60aa560cad78254ae8698516ae029f"), 6L << 30, 11L << 30),
        new ModelRecommendation("creative", "Gemma 4 12B · QAT Q4_0", "Google's own quantization: a different voice for writing and roleplay, and it reads pictures.",
            "google/gemma-4-12B-it-qat-q4_0-gguf", "29d097773436b69ff9feafd636ab4cf873786537",
            new HfFile("gemma-4-12b-it-qat-q4_0.gguf", 6_975_879_296, "93567e57a8fe10b23569b9d9ec38cd005deedf71e29477c421a4b83f418a538b"),
            new HfFile("mmproj-gemma-4-12b-it-qat-q4_0.gguf", 175_115_616, "cb018338a7538a9814d994bfe54644c71eb7ed54e31eae2f721e45fd3c260da7"), 10L << 30),
        new ModelRecommendation("search", "EmbeddingGemma 300M · Q8_0", "Turns text into meaning for search (Phase 8); small enough for the processor.",
            "ggml-org/embeddinggemma-300m-qat-q8_0-GGUF", "66f974f8cd48cc3b9c41c516b95508e75b4bee64",
            new HfFile("embeddinggemma-300m-qat-Q8_0.gguf", 328_577_056, "6fa0c02a9c302be6f977521d399b4de3a46310a4f2621ee0063747881b673f67"), null, 0),
    };

    private static ModelRecommendation Qwen38(string quant, long size, string sha, long min, long max, string why) =>
        new("main", $"Qwen3.8 27B · {quant} · MTP", why, "ISTA-DASLab/Qwen3.8-27B-GSQ-RCO-GGUF", "d562806dbafae37109975e970aae91b43e73b440",
            new HfFile($"Qwen3.8-27B-GSQ-RCO-{quant}-mtp.gguf", size, sha),
            new HfFile("mmproj-Qwen3.8-27B-BF16.gguf", 931_146_528, "13cb7bebccbd04afc8f4090cb949ecf8937cdf7377c5799b1a0c594e7c0d3e16"), min, max);

    private static ModelRecommendation Qwen35Q5(string role, string why) =>
        new(role, "Qwen3.5 9B · Q5_K_M", why, "unsloth/Qwen3.5-9B-GGUF", "725b4640b3d92a9d36037c79ac04a4378d9e08bd",
            new HfFile("Qwen3.5-9B-Q5_K_M.gguf", 6_577_841_312, "e90fa097659415cfdd6240ed1e620f4062473cb7a9ddc9d9b75abeef8094c2e0"),
            new HfFile("mmproj-F16.gguf", 918_166_080, "f70dc3509053962b0d0d3ee8a7eacebf5d60aa560cad78254ae8698516ae029f"), 11L << 30, 15L << 30);

    /// <summary>The suggestions for this PC's card: one main model for its size, and the creative and search ones that suit it.</summary>
    public static IReadOnlyList<ModelRecommendation> RecommendedFor(GpuFacts gpu) =>
        Recommendations.Where(r => r.Role != "main" ? gpu.TotalBytes >= r.MinVramBytes || r.MinVramBytes == 0
                                                    : gpu.TotalBytes >= r.MinVramBytes && gpu.TotalBytes < r.MaxVramBytes).ToList();

    /// <summary>Public GGUF repositories matching <paramref name="query"/>, most downloaded first.</summary>
    public async Task<IReadOnlyList<HfRepo>> SearchAsync(string query, CancellationToken ct = default)
    {
        var q = Regex.Replace(query.Trim(), @"\s+", " ");
        if (q.Length is 0 or > 100) return Array.Empty<HfRepo>();
        var url = $"{Hub}api/models?search={Uri.EscapeDataString(q)}&filter=gguf&sort=downloads&direction=-1&limit=30";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<HfRepo>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var id = m.Str("id") ?? m.Str("modelId");
            if (id is null || !RepoRx().IsMatch(id) || m.Bool("private") == true) continue;
            if (m.TryProp("gated", out var gated) && gated.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) continue;
            list.Add(new HfRepo(id, m.Lng("downloads") ?? 0, m.Lng("likes") ?? 0, TimeText.ParseLenient(m.Str("lastModified")), m.Str("pipeline_tag")));
        }
        return list;
    }

    /// <summary>A repository's GGUF files at its latest commit (split models left out), with the model's summary.</summary>
    public async Task<HfRepoFiles> FilesAsync(string repo, CancellationToken ct = default)
    {
        if (!RepoRx().IsMatch(repo)) throw new DownloadException("That isn't a Hugging Face repository name.");
        using var info = await GetJsonAsync($"{Hub}api/models/{repo}?expand[]=sha&expand[]=gated&expand[]=gguf&expand[]=cardData&expand[]=tags", ct).ConfigureAwait(false);
        var root = info.RootElement;
        var revision = root.Str("sha") ?? throw new DownloadException("Hugging Face didn't say which version of the repository is current.");
        if (!ShaRx().IsMatch(revision)) throw new DownloadException("Hugging Face gave an unexpected version id.");
        var gated = root.TryProp("gated", out var g) && g.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);
        var license = root.Arr("tags").Select(t => t.ValueKind == JsonValueKind.String ? t.GetString() : null).FirstOrDefault(t => t?.StartsWith("license:", StringComparison.Ordinal) == true)?[8..];
        var summary = root.TryProp("gguf", out var gg) ? gg : default;
        using var tree = await GetJsonAsync($"{Hub}api/models/{repo}/tree/{revision}?recursive=1", ct).ConfigureAwait(false);
        var files = new List<HfFile>();
        foreach (var f in tree.RootElement.EnumerateArray())
        {
            if (f.Str("type") != "file" || f.Str("path") is not { } path || !path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !SafePath(path)) continue;
            var sha = f.TryProp("lfs", out var lfs) ? lfs.Str("oid") : null;
            var size = f.Lng("size") ?? 0;
            if (sha is null || !ShaRx256().IsMatch(sha) || size <= 0) continue;
            files.Add(new HfFile(path, size, sha));
        }
        return new HfRepoFiles(repo, revision, gated, license,
            files.Where(f => !f.IsProjector && !f.Split && !f.Name.Contains("imatrix", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Size).ToList(),
            files.Where(f => f.IsProjector).OrderBy(f => f.Size).ToList(),
            summary.ValueKind == JsonValueKind.Object ? (int)(summary.Lng("context_length") ?? 0) : 0,
            summary.ValueKind == JsonValueKind.Object ? summary.Str("architecture") : null,
            summary.ValueKind == JsonValueKind.Object ? summary.Lng("total") ?? 0 : 0);
    }

    /// <summary>Downloads a suggested model (its file and projector, exactly as pinned).</summary>
    public Task<LocalModel> DownloadAsync(ModelRecommendation r, CancellationToken ct = default) => DownloadAsync(r.Repo, r.Revision, r.File, r.Projector, ct);

    /// <summary>
    /// Downloads <paramref name="file"/> (and <paramref name="projector"/>, for pictures) from <paramref name="repo"/> at
    /// <paramref name="revision"/> into the models folder, checks each, adds the model to the library and returns it.
    /// </summary>
    public async Task<LocalModel> DownloadAsync(string repo, string revision, HfFile file, HfFile? projector, CancellationToken ct = default)
    {
        if (!RepoRx().IsMatch(repo) || !ShaRx().IsMatch(revision) || !SafePath(file.Path) || (projector is not null && !SafePath(projector.Path)))
            throw new DownloadException("That isn't a model Aqua can download.");
        if (file.Split) throw new DownloadException("This model is split into several files, which Aqua doesn't download yet — pick a single-file one.");
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new DownloadException("A model is already downloading.");
        using var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Busy = true;
        Changed?.Invoke();
        try
        {
            var folder = Path.Combine(_library.Folder, repo.Replace("/", "__"));
            var total = file.Size + (projector?.Size ?? 0);
            var needed = total - PartBytes(folder, file, projector) + (1L << 30);
            if (FreeSpace(_library.Folder) < needed)
                throw new DownloadException($"There isn't enough free space on {Path.GetPathRoot(_library.Folder)?.TrimEnd('\\')} — the model needs about {HardwareFit.Gigabytes(needed)}.");
            Directory.CreateDirectory(folder);
            Log.Info("models", $"Downloading {file.Name} from {repo}");
            var modelPath = Path.Combine(folder, file.Name);
            long done = 0;
            if (!await AlreadyThereAsync(modelPath, file, cts.Token).ConfigureAwait(false))
                await VerifiedDownload.DownloadAsync(_http, new DownloadSpec($"{Hub}{repo}/resolve/{revision}/{Escape(file.Path)}", file.Size, file.Sha256), modelPath,
                    n => Report("Downloading " + file.Name, n, total), StallAfter, resume: true, cts.Token).ConfigureAwait(false);
            done = file.Size;
            (string, string, long, string)? kept = null;
            if (projector is not null)
            {
                var projectorPath = Path.Combine(folder, projector.Name);
                if (!await AlreadyThereAsync(projectorPath, projector, cts.Token).ConfigureAwait(false))
                    await VerifiedDownload.DownloadAsync(_http, new DownloadSpec($"{Hub}{repo}/resolve/{revision}/{Escape(projector.Path)}", projector.Size, projector.Sha256), projectorPath,
                        n => Report("Downloading the vision projector", done + n, total), StallAfter, resume: true, cts.Token).ConfigureAwait(false);
                kept = (projector.Name, projectorPath, projector.Size, projector.Sha256);
            }
            Report("Checking the model", total, total);
            var header = Gguf.Read(modelPath);
            if (header.Type is "mmproj" or "adapter") throw new DownloadException("That file is a " + (header.Type == "mmproj" ? "vision projector" : "adapter") + ", not a model.");
            _library.AddDownload(repo, revision, file.Name, modelPath, file.Size, file.Sha256, kept, DateTimeOffset.Now);
            Log.Info("models", "Downloaded " + file.Name);
            return _library.Find("hf:" + repo + "/" + file.Name) ?? throw new DownloadException("The model was downloaded but couldn't be added to the library.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new DownloadException("Cancelled — the download carries on where it stopped next time.");
        }
        catch (GgufFormatException ex)
        {
            throw new DownloadException("The file downloaded fine but isn't a model Aqua can read: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("models", "Download failed", ex);
            throw new DownloadException("Couldn't save the model: " + ex.Message);
        }
        finally
        {
            _cts = null;
            Busy = false;
            Current = null;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Reads a file's GGUF header from Hugging Face without downloading the model — an HTTP range of its first bytes, at
    /// most 64 MB, stopped as soon as the header has been read — so its layers, context cache and abilities are known
    /// before a download (and its fit is exact, not a guess from the file size). Kept in memory per file.
    /// </summary>
    public async Task<GgufInfo> HeaderAsync(string repo, string revision, HfFile file, CancellationToken ct = default)
    {
        if (!RepoRx().IsMatch(repo) || !ShaRx().IsMatch(revision) || !SafePath(file.Path) || file.Size <= 0)
            throw new DownloadException("That isn't a model Aqua can look at.");
        if (_headers.TryGetValue(file.Sha256, out var known)) return known;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Hub}{repo}/resolve/{revision}/{Escape(file.Path)}");
        request.Headers.Range = new RangeHeaderValue(0, Math.Min(file.Size, HeaderLimit) - 1);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 401 or 403) throw new DownloadException("That file needs a Hugging Face account — Aqua uses only public models.");
            if (!response.IsSuccessStatusCode) throw new DownloadException($"Hugging Face said {(int)response.StatusCode} {response.ReasonPhrase} — try again later.");
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new DownloadException("The file was offered over an insecure connection, so Aqua didn't read it.");
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var capped = new CappedStream(body, HeaderLimit);
            await using var buffered = new BufferedStream(capped, 1 << 20);
            var info = await Task.Run(() => Gguf.Read(buffered), cts.Token).ConfigureAwait(false);
            if (info.Type is "mmproj" or "adapter") throw new DownloadException("That file is a " + (info.Type == "mmproj" ? "vision projector" : "adapter") + ", not a model.");
            _headers[file.Sha256] = info;
            return info;
        }
        catch (GgufFormatException ex)
        {
            throw new DownloadException("Its header couldn't be read: " + ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new DownloadException("Couldn't read the model's header from Hugging Face — check the connection and try again.");
        }
    }

    /// <summary>A read-only stream that ends with an error after <c>limit</c> bytes (a header that never ends can't run on).</summary>
    private sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= limit) throw new GgufFormatException("its header is larger than Aqua reads before downloading");
            var n = inner.Read(buffer, offset, (int)Math.Min(count, limit - _read));
            _read += n;
            return n;
        }
    }

    /// <summary>Stops the download that is running; what it got so far is kept to carry on from.</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Deletes unfinished downloads (".part" files) in the models folder; false while a download runs.</summary>
    public bool DiscardUnfinished()
    {
        if (Busy) return false;
        try
        {
            if (Directory.Exists(_library.Folder))
                foreach (var part in Directory.GetFiles(_library.Folder, "*.part", SearchOption.AllDirectories)) File.Delete(part);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("models", "Couldn't delete unfinished downloads", ex);
            return false;
        }
    }

    private void Report(string step, long done, long total)
    {
        var p = new ModelDownloadProgress(step, Math.Min(done, total), total);
        Current = p;
        Progress?.Invoke(p);
    }

    /// <summary>A file already in place is reusable only when its bytes still match the pinned size and fingerprint.</summary>
    private static async Task<bool> AlreadyThereAsync(string path, HfFile file, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static long PartBytes(string folder, HfFile file, HfFile? projector)
    {
        long bytes = 0;
        foreach (var f in new[] { file, projector })
            if (f is not null && Path.Combine(folder, f.Name + ".part") is var part && File.Exists(part)) bytes += new FileInfo(part).Length;
        return bytes;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 401 or 403) throw new DownloadException("That repository needs a Hugging Face account — Aqua uses only public models.");
            if ((int)response.StatusCode == 404) throw new DownloadException("Hugging Face doesn't have that repository.");
            if (!response.IsSuccessStatusCode) throw new DownloadException($"Hugging Face said {(int)response.StatusCode} {response.ReasonPhrase} — try again later.");
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (text.Length > 8 * 1024 * 1024) throw new DownloadException("Hugging Face's answer was far bigger than expected.");
            return JsonDocument.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DownloadException("Couldn't reach Hugging Face — check the internet connection and try again.");
        }
        catch (JsonException)
        {
            throw new DownloadException("Hugging Face's answer couldn't be read.");
        }
    }

    /// <summary>A file path inside a repository that's safe to use: forward slashes, no "..", no drive or odd characters.</summary>
    internal static bool SafePath(string path) =>
        path.Length is > 0 and <= 300 && !path.StartsWith('/') && !path.Contains("..", StringComparison.Ordinal) && !path.Contains('\\')
        && path.Split('/').All(part => part.Length > 0 && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    private static string Escape(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}/[A-Za-z0-9][A-Za-z0-9._-]{0,95}$")]
    private static partial Regex RepoRx();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex ShaRx();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex ShaRx256();
}
