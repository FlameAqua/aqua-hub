using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AquaHub.Core.Util;

namespace AquaHub.Core.Speech;

/// <summary>A file Aqua downloads to set up Whisper: exactly this size and SHA-256, or it's thrown away.</summary>
public sealed record WhisperDownload(string Url, long Size, string Sha256);

/// <summary>One model file: the English-only models are more accurate for English than multilingual ones of the same size.</summary>
public sealed record WhisperModelFile(string FileName, bool EnglishOnly, WhisperDownload Download);

/// <summary>A model size Aqua offers, as an English-only file and a multilingual one (the same file when there's no English-only model).</summary>
public sealed record WhisperModelChoice(string Id, string Name, string Note, WhisperModelFile English, WhisperModelFile Multilingual)
{
    public WhisperModelFile For(string language) => WhisperCatalog.IsEnglish(language) ? English : Multilingual;

    /// <summary>e.g. "Small — recommended, 181 MB".</summary>
    public string Label(string language) => $"{Name} — {Note}, {WhisperCatalog.Megabytes(For(language).Download.Size)}";
}

/// <summary>
/// What Aqua installs for Whisper, pinned: whisper.cpp's own Windows build (its command-line program and libraries, from
/// the project's GitHub release) and one of the whisper.cpp project's quantized models (from its Hugging Face repository,
/// at one fixed commit). The sizes and SHA-256s are the ones GitHub and Hugging Face publish for those files.
/// </summary>
public static class WhisperCatalog
{
    /// <summary>whisper.cpp v1.9.4 — GitHub build b5130.</summary>
    public const string Version = "1.9.4";
    private const string Release = "https://github.com/ggml-org/whisper.cpp/releases/download/b5130/";
    private const string Models = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    public const string DefaultChoice = "small";

    /// <summary>The CPU build for this PC's processor, or null when there isn't one.</summary>
    public static WhisperDownload? Runtime(Architecture arch) => arch switch
    {
        Architecture.X64 => new(Release + "whisper-bin-x64.zip", 8_573_270, "f9ec6c52a2e949b62ab51fa21d0d497958f9e41c3010c157c4e42932d5316f3c"),
        Architecture.Arm64 => new(Release + "whisper-bin-win-cpu-arm64.zip", 4_361_895, "799543b926ab5b6c2d60cab269a2092e0ae8d27820e9e15429e59de3699546fc"),
        _ => null,
    };

    private static WhisperModelFile Model(string file, bool english, long size, string sha256) => new(file, english, new(Models + file, size, sha256));

    private static readonly WhisperModelFile Turbo = Model("ggml-large-v3-turbo-q5_0.bin", false, 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2");

    public static readonly IReadOnlyList<WhisperModelChoice> Choices = new[]
    {
        new WhisperModelChoice("base", "Base", "fastest",
            Model("ggml-base.en-q5_1.bin", true, 59_721_011, "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f"),
            Model("ggml-base-q5_1.bin", false, 59_707_625, "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898")),
        new WhisperModelChoice("small", "Small", "recommended",
            Model("ggml-small.en-q5_1.bin", true, 190_098_681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30"),
            Model("ggml-small-q5_1.bin", false, 190_085_487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb")),
        new WhisperModelChoice("turbo", "Large v3 Turbo", "most accurate, slower", Turbo, Turbo),
    };

    public static WhisperModelChoice Choice(string? id) => Choices.FirstOrDefault(c => c.Id == id) ?? Choices.First(c => c.Id == DefaultChoice);

    /// <summary>"en", "en-IE" and the like; empty counts as English.</summary>
    public static bool IsEnglish(string? language) =>
        string.IsNullOrWhiteSpace(language) || language.Trim().Equals("en", StringComparison.OrdinalIgnoreCase)
        || language.Trim().StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    public static string Megabytes(long bytes) => $"{Math.Max(1, Math.Round(bytes / 1048576.0)):0} MB";
}

/// <summary>An installed Whisper: its program, its model and which model that is.</summary>
public sealed record WhisperInstall(string Cli, string Model, string ChoiceId, bool EnglishOnly, string Version)
{
    /// <summary>e.g. "Whisper Small (English)".</summary>
    public string Name => "Whisper " + WhisperCatalog.Choice(ChoiceId).Name + (EnglishOnly ? " (English)" : "");
}

/// <summary>How far an install has got: what it's doing and the bytes done out of all it has to download.</summary>
public sealed record WhisperProgress(string Step, long Done, long Total);

/// <summary>Why Whisper couldn't be set up, in plain words.</summary>
public sealed class WhisperSetupException(string message) : Exception(message);

/// <summary>
/// Installs and removes Whisper in Aqua's own folder (…\AquaHub\whisper), only when you click Install. Each file is
/// streamed to a ".part" file, checked against its pinned size and SHA-256, and only then put in place — anything else
/// is deleted. From the whisper.cpp archive only the command-line program and its libraries are kept. The model is
/// used from where it was checked; nothing runs until both are in place.
/// </summary>
public sealed class WhisperSetup
{
    private const string CliName = "whisper-cli.exe";
    private readonly HttpClient _http;
    private readonly Architecture _arch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    /// <summary>Free bytes where Whisper goes (tests replace it).</summary>
    internal Func<long> FreeSpace;
    /// <summary>How long a download may go without any data before it counts as stalled.</summary>
    internal TimeSpan StallAfter = TimeSpan.FromSeconds(60);

    public WhisperSetup(string folder, HttpClient http, Architecture? arch = null)
    {
        Folder = Path.GetFullPath(folder);
        _http = http;
        _arch = arch ?? RuntimeInformation.OSArchitecture;
        FreeSpace = () =>
        {
            try { return new DriveInfo(Path.GetPathRoot(Folder)!).AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
        };
    }

    public string Folder { get; }
    public bool Busy { get; private set; }
    /// <summary>The install that is running, as it goes (thread pool).</summary>
    public event Action<WhisperProgress>? Progress;
    /// <summary>Installed, removed, or an install ended either way (thread pool).</summary>
    public event Action? Changed;
    /// <summary>The running install's latest progress, or null when none is running.</summary>
    public WhisperProgress? Current { get; private set; }

    private string ManifestPath => Path.Combine(Folder, "whisper.json");

    private sealed record Manifest(string Version, string Cli, string Model, string Choice, bool EnglishOnly, long ModelSize);

    /// <summary>The Whisper that is installed and complete, or null.</summary>
    public WhisperInstall? Installed()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
            if (m is null || Inside(m.Cli) is not { } cli || Inside(m.Model) is not { } model) return null;
            if (!Path.GetFileName(cli).Equals(CliName, StringComparison.OrdinalIgnoreCase) || !File.Exists(cli)) return null;
            if (!File.Exists(model) || new FileInfo(model).Length != m.ModelSize) return null;
            return new WhisperInstall(cli, model, WhisperCatalog.Choice(m.Choice).Id, m.EnglishOnly, m.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Log.Debug("whisper", "Couldn't read what's installed: " + ex.Message);
            return null;
        }
    }

    /// <summary>A path the manifest names, when it is inside Whisper's folder.</summary>
    private string? Inside(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(Folder, relative));
        return full.StartsWith(Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Downloads what's missing for this model (the program once; a model per choice) and makes it the one in use.</summary>
    public Task<WhisperInstall> InstallAsync(string choiceId, string language, CancellationToken ct = default)
    {
        var runtime = WhisperCatalog.Runtime(_arch)
                      ?? throw new WhisperSetupException("Whisper isn't available for this PC's processor (" + _arch + ").");
        var choice = WhisperCatalog.Choice(choiceId);
        return InstallAsync(runtime, choice.For(language), choice.Id, ct);
    }

    internal async Task<WhisperInstall> InstallAsync(WhisperDownload runtime, WhisperModelFile model, string choiceId, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new WhisperSetupException("Whisper is already being installed.");
        using var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Busy = true;
        try
        {
            var before = Installed();
            var needRuntime = before is null || before.Version != WhisperCatalog.Version;
            var needModel = before is null || !Path.GetFileName(before.Model).Equals(model.FileName, StringComparison.OrdinalIgnoreCase);
            var total = (needRuntime ? runtime.Size : 0) + (needModel ? model.Download.Size : 0);
            // Room for the archive and what comes out of it, the model, and a margin.
            var needed = (needRuntime ? runtime.Size * 4 : 0) + (needModel ? model.Download.Size : 0) + 64L * 1024 * 1024;
            if (FreeSpace() < needed)
                throw new WhisperSetupException($"There isn't enough free space on {Path.GetPathRoot(Folder)?.TrimEnd('\\')} — Whisper needs about {WhisperCatalog.Megabytes(needed)}.");
            Directory.CreateDirectory(Folder);
            Log.Info("whisper", $"Installing whisper.cpp {WhisperCatalog.Version} and {model.FileName}" + (needRuntime ? "" : " (program already installed)"));

            long done = 0;
            var cli = before?.Cli;
            if (needRuntime)
            {
                cli = await InstallRuntimeAsync(runtime, n => Report("Downloading whisper.cpp", done + n, total), cts.Token).ConfigureAwait(false);
                done += runtime.Size;
            }
            var modelPath = Path.Combine(Folder, "models", model.FileName);
            if (needModel)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
                var name = WhisperCatalog.Choice(choiceId).Name + (model.EnglishOnly ? " (English)" : "");
                await DownloadAsync(model.Download, modelPath, n => Report($"Downloading the {name} model", done + n, total), cts.Token).ConfigureAwait(false);
            }
            else modelPath = before!.Model;

            Report("Finishing", total, total);
            var manifest = new Manifest(WhisperCatalog.Version, Path.GetRelativePath(Folder, cli!), Path.GetRelativePath(Folder, modelPath),
                                        choiceId, model.EnglishOnly, model.Download.Size);
            var tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(manifest));
            File.Move(tmp, ManifestPath, overwrite: true);
            TidyUp(Path.GetDirectoryName(cli!)!, modelPath);
            Log.Info("whisper", "Installed " + model.FileName);
            return Installed() ?? throw new WhisperSetupException("Whisper was downloaded but its files aren't where they should be — try again.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new WhisperSetupException("Cancelled.");
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("whisper", "Download failed: " + ex.Message);
            throw new WhisperSetupException("Couldn't download Whisper — check the internet connection and try again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("whisper", "Install failed", ex);
            throw new WhisperSetupException("Couldn't save Whisper's files: " + ex.Message);
        }
        finally
        {
            _cts = null;
            Busy = false;
            Current = null;
            foreach (var part in SafeFiles("*.part")) TryDelete(part);
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Stops the install that is running; what it downloaded so far is deleted.</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Deletes Whisper: the program, every model and the install record.</summary>
    public void Remove()
    {
        if (Busy) throw new WhisperSetupException("Cancel the install first.");
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("whisper", "Couldn't remove Whisper", ex);
            throw new WhisperSetupException("Whisper is still in use — try again in a moment.");
        }
        Log.Info("whisper", "Removed");
        Changed?.Invoke();
    }

    private void Report(string step, long done, long total)
    {
        var p = new WhisperProgress(step, Math.Min(done, total), total);
        Current = p;
        Progress?.Invoke(p);
    }

    /// <summary>
    /// Downloads the archive, checks it, and keeps whisper-cli.exe with the libraries beside it (nothing else — no other
    /// programs, no subfolders). Returns the program's path.
    /// </summary>
    private async Task<string> InstallRuntimeAsync(WhisperDownload runtime, Action<long> progress, CancellationToken ct)
    {
        var zip = Path.Combine(Folder, "whisper.cpp.zip");
        var target = Path.Combine(Folder, $"whisper.cpp-{WhisperCatalog.Version}-{_arch.ToString().ToLowerInvariant()}");
        var staging = target + ".new";
        try
        {
            await DownloadAsync(runtime, zip, progress, ct).ConfigureAwait(false);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var cli = archive.Entries.Where(e => e.Name.Equals(CliName, StringComparison.OrdinalIgnoreCase))
                              .OrderBy(e => e.FullName.Length).FirstOrDefault()
                          ?? throw new WhisperSetupException("The whisper.cpp download doesn't contain " + CliName + ".");
                var folder = cli.FullName[..^cli.Name.Length];
                long unpacked = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Length == 0 || entry.FullName.Length != folder.Length + entry.Name.Length
                        || !entry.FullName.StartsWith(folder, StringComparison.Ordinal)) continue;
                    if (entry != cli && !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                    unpacked += entry.Length;
                    if (unpacked > 400L * 1024 * 1024) throw new WhisperSetupException("The whisper.cpp download unpacks to far more than expected.");
                    ct.ThrowIfCancellationRequested();
                    entry.ExtractToFile(Path.Combine(staging, entry.Name), overwrite: true);
                }
            }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            return Path.Combine(target, CliName);
        }
        finally
        {
            TryDelete(zip);
            if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Streams one file to "target.part", checking its size as it comes and its SHA-256 at the end.</summary>
    private async Task DownloadAsync(WhisperDownload file, string target, Action<long> progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new WhisperSetupException("Whisper downloads only over HTTPS.");
        var part = target + ".part";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallAfter);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            // HttpClient never follows a redirect from HTTPS to HTTP; this is the belt to that braces.
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new WhisperSetupException("The download was redirected away from HTTPS, so it was stopped.");
            if (!response.IsSuccessStatusCode)
                throw new WhisperSetupException($"The download server said {(int)response.StatusCode} {response.ReasonPhrase} — try again later.");
            if (response.Content.Headers.ContentLength is long announced && announced > file.Size && response.Content.Headers.ContentEncoding.Count == 0)
                throw new WhisperSetupException("The download is bigger than the file Aqua expects, so it wasn't saved.");

            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long got = 0;
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                while (true)
                {
                    stall.CancelAfter(StallAfter);
                    var n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    got += n;
                    if (got > file.Size) throw new WhisperSetupException("The download is bigger than the file Aqua expects, so it was deleted.");
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    progress(got);
                }
            }
            if (got != file.Size) throw new WhisperSetupException("The download ended early — try again.");
            if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("whisper", $"{Path.GetFileName(target)} didn't match its SHA-256");
                throw new WhisperSetupException("The download didn't match its published fingerprint (SHA-256), so it was deleted. Try again later.");
            }
            File.Move(part, target, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WhisperSetupException("The download stalled — check the internet connection and try again.");
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>After an install: the program folders and models the install record doesn't name go.</summary>
    private void TidyUp(string keepRuntime, string keepModel)
    {
        foreach (var dir in SafeDirectories("whisper.cpp-*"))
            if (!dir.Equals(keepRuntime, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var models = Path.Combine(Folder, "models");
        if (!Directory.Exists(models)) return;
        foreach (var file in Directory.GetFiles(models))
            if (!file.Equals(keepModel, StringComparison.OrdinalIgnoreCase)) TryDelete(file);
    }

    private IEnumerable<string> SafeFiles(string pattern)
    {
        try { return Directory.Exists(Folder) ? Directory.GetFiles(Folder, pattern, SearchOption.AllDirectories) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private IEnumerable<string> SafeDirectories(string pattern)
    {
        try { return Directory.Exists(Folder) ? Directory.GetDirectories(Folder, pattern) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
