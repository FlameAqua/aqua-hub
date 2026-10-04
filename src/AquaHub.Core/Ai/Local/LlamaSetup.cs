using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using AquaHub.Core.Net;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Local;

/// <summary>An installed llama.cpp: its server program and which build it is.</summary>
public sealed record LlamaInstall(string Server, string Build, string Backend);

/// <summary>How far an install has got: what it's doing and the bytes done out of all it has to download.</summary>
public sealed record LlamaProgress(string Step, long Done, long Total);

/// <summary>Why llama.cpp couldn't be set up, in plain words.</summary>
public sealed class LlamaSetupException(string message) : Exception(message);

/// <summary>
/// Installs and removes llama.cpp in Aqua's own folder (…\AquaHub\llama), only when asked. Each archive streams to a
/// ".part" file and must match its pinned size and SHA-256 (<see cref="LlamaCatalog"/>), or it is deleted. From the archives
/// only llama-server.exe and the libraries beside it (llama.cpp's own and NVIDIA's CUDA runtime) are kept — no other
/// programs. The install record can only name a llama-server.exe inside this folder.
/// </summary>
public sealed class LlamaSetup
{
    private const string ServerName = "llama-server.exe";
    private const long MaxUnpacked = 4L * 1024 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;

    /// <summary>Free bytes where llama.cpp goes (tests replace it).</summary>
    internal Func<long> FreeSpace;
    /// <summary>How long a download may go without any data before it counts as stalled.</summary>
    internal TimeSpan StallAfter = TimeSpan.FromSeconds(60);

    public LlamaSetup(string folder, HttpClient http, Architecture? arch = null)
    {
        Folder = Path.GetFullPath(folder);
        _http = http;
        Architecture = arch ?? RuntimeInformation.OSArchitecture;
        FreeSpace = () =>
        {
            try { return new DriveInfo(Path.GetPathRoot(Folder)!).AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return long.MaxValue; }
        };
    }

    public string Folder { get; }
    public Architecture Architecture { get; }
    public bool Busy { get; private set; }
    /// <summary>The install that is running, as it goes (thread pool).</summary>
    public event Action<LlamaProgress>? Progress;
    /// <summary>Installed, removed, or an install ended either way (thread pool).</summary>
    public event Action? Changed;
    /// <summary>The running install's latest progress, or null when none is running.</summary>
    public LlamaProgress? Current { get; private set; }

    private string ManifestPath => Path.Combine(Folder, "llama.json");

    private sealed record Manifest(string Build, string Backend, string Server);

    /// <summary>The llama.cpp that is installed and complete, or null.</summary>
    public LlamaInstall? Installed()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return null;
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
            if (m is null || Inside(m.Server) is not { } server) return null;
            if (!Path.GetFileName(server).Equals(ServerName, StringComparison.OrdinalIgnoreCase) || !File.Exists(server)) return null;
            return new LlamaInstall(server, m.Build, m.Backend);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            Log.Debug("llama", "Couldn't read what's installed: " + ex.Message);
            return null;
        }
    }

    /// <summary>A path the manifest names, when it is inside llama.cpp's folder.</summary>
    private string? Inside(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(Folder, relative));
        return full.StartsWith(Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Downloads, checks and unpacks <paramref name="build"/>, then makes it the one in use; the build it replaces goes.</summary>
    public async Task<LlamaInstall> InstallAsync(LlamaBuild build, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new LlamaSetupException("llama.cpp is already being installed.");
        using var cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Busy = true;
        try
        {
            var total = build.Size;
            // The archives, what comes out of them (CUDA's libraries unpack to about twice their size), and a margin.
            var needed = total * 3 + (256L << 20);
            if (FreeSpace() < needed)
                throw new LlamaSetupException($"There isn't enough free space on {Path.GetPathRoot(Folder)?.TrimEnd('\\')} — llama.cpp needs about {HardwareFit.Gigabytes(needed)}.");
            Directory.CreateDirectory(Folder);
            Log.Info("llama", $"Installing llama.cpp {LlamaCatalog.Build} ({build.Backend})");

            var target = Path.Combine(Folder, $"llama.cpp-{LlamaCatalog.Build}-{build.Backend}");
            var staging = target + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            long done = 0;
            long unpacked = 0;
            var archives = new List<(DownloadSpec Spec, bool Server)> { (build.Archive, true) };
            if (build.Runtime is { } runtime) archives.Add((runtime, false));
            foreach (var (spec, hasServer) in archives)
            {
                var zip = Path.Combine(Folder, Path.GetFileName(new Uri(spec.Url).LocalPath));
                var step = hasServer ? "Downloading llama.cpp" : "Downloading NVIDIA's CUDA libraries";
                try
                {
                    await VerifiedDownload.DownloadAsync(_http, spec, zip, n => Report(step, done + n, total), StallAfter, resume: false, cts.Token).ConfigureAwait(false);
                    done += spec.Size;
                    Report("Unpacking", done, total);
                    unpacked += Unpack(zip, staging, hasServer, unpacked, cts.Token);
                }
                finally { TryDelete(zip); }
            }
            if (!File.Exists(Path.Combine(staging, ServerName))) throw new LlamaSetupException("The llama.cpp download doesn't contain " + ServerName + ".");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);

            var manifest = new Manifest(LlamaCatalog.Build, build.Backend, Path.GetRelativePath(Folder, Path.Combine(target, ServerName)));
            var tmp = ManifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(manifest));
            File.Move(tmp, ManifestPath, overwrite: true);
            TidyUp(target);
            Log.Info("llama", $"Installed llama.cpp {LlamaCatalog.Build} ({build.Backend})");
            return Installed() ?? throw new LlamaSetupException("llama.cpp was downloaded but its files aren't where they should be — try again.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new LlamaSetupException("Cancelled.");
        }
        catch (DownloadException ex)
        {
            throw new LlamaSetupException(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Warn("llama", "Install failed", ex);
            throw new LlamaSetupException("Couldn't save llama.cpp's files: " + ex.Message);
        }
        finally
        {
            _cts = null;
            Busy = false;
            Current = null;
            foreach (var leftover in SafeEntries("*.new", directories: true)) TryDeleteDirectory(leftover);
            foreach (var part in SafeEntries("*.part", directories: false)) TryDelete(part);
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Keeps llama-server.exe and the libraries in its folder (from llama.cpp's archive), or every library (from the CUDA
    /// runtime's); nothing else, no subfolders. Returns the bytes unpacked.
    /// </summary>
    private static long Unpack(string zip, string staging, bool hasServer, long before, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zip);
        string? folder = null;
        if (hasServer)
        {
            var server = archive.Entries.Where(e => e.Name.Equals(ServerName, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(e => e.FullName.Length).FirstOrDefault()
                         ?? throw new LlamaSetupException("The llama.cpp download doesn't contain " + ServerName + ".");
            folder = server.FullName[..^server.Name.Length];
        }
        long unpacked = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0 || entry.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            var keep = entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                       || (hasServer && entry.Name.Equals(ServerName, StringComparison.OrdinalIgnoreCase));
            if (!keep) continue;
            if (folder is not null && !(entry.FullName.Length == folder.Length + entry.Name.Length && entry.FullName.StartsWith(folder, StringComparison.Ordinal))) continue;
            unpacked += entry.Length;
            if (before + unpacked > MaxUnpacked) throw new LlamaSetupException("The llama.cpp download unpacks to far more than expected.");
            ct.ThrowIfCancellationRequested();
            entry.ExtractToFile(Path.Combine(staging, entry.Name), overwrite: true);
        }
        return unpacked;
    }

    /// <summary>Stops the install that is running; what it downloaded so far is deleted.</summary>
    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Deletes llama.cpp: every build and the install record (stop the models it runs first).</summary>
    public void Remove()
    {
        if (Busy) throw new LlamaSetupException("Cancel the install first.");
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("llama", "Couldn't remove llama.cpp", ex);
            throw new LlamaSetupException("llama.cpp is still in use — stop its models and try again.");
        }
        Log.Info("llama", "Removed");
        Changed?.Invoke();
    }

    private void Report(string step, long done, long total)
    {
        var p = new LlamaProgress(step, Math.Min(done, total), total);
        Current = p;
        Progress?.Invoke(p);
    }

    /// <summary>After an install: builds the install record doesn't name go.</summary>
    private void TidyUp(string keep)
    {
        foreach (var dir in SafeEntries("llama.cpp-*", directories: true))
            if (!dir.Equals(keep, StringComparison.OrdinalIgnoreCase)) TryDeleteDirectory(dir);
    }

    private IEnumerable<string> SafeEntries(string pattern, bool directories)
    {
        try
        {
            if (!Directory.Exists(Folder)) return Array.Empty<string>();
            return directories ? Directory.GetDirectories(Folder, pattern) : Directory.GetFiles(Folder, pattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
