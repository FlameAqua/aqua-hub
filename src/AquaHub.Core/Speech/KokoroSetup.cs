using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using AquaHub.Core.Net;

namespace AquaHub.Core.Speech;

public sealed record KokoroVoice(string Id, string Name, string Language)
{
    public override string ToString() => Name;
}

public sealed record KokoroInstall(string Cli, string Model, string DataRoot);

/// <summary>A pinned CPU speech package in its own profile directory, published only after verification.</summary>
public sealed class KokoroSetup(string folder, HttpClient http)
{
    private const string RuntimeName = "audiocpp_cli.exe";
    private const string ModelName = "kokoro.gguf";
    private const string Release = "https://github.com/0xShug0/audio.cpp/releases/download/v0.9.5/";
    private static readonly DownloadSpec Runtime = new(Release + "audio-v0.9.5-bin-windows-x64-cpu.zip",
        33_795_232, "fa38ab6f02ebb1256728068549c0beac3bf2fdf1e348a301f0892d44ac14e26a");
    private static readonly DownloadSpec Phonemizer = new(
        "https://files.pythonhosted.org/packages/9d/ed/a3d872fbad4f3a3f3db0e8c31768ab14e77cd77306de16b8b20b1e1df7ea/espeakng_loader-0.2.4-py3-none-win_amd64.whl",
        9_437_292, "41f1e08ac9deda2efd1ea9de0b81dab9f5ae3c4b24284f76533d0a7b1dd7abd7");
    private static readonly DownloadSpec Model = new(
        "https://huggingface.co/audio-cpp/audio.cpp-gguf/resolve/6f45df32f8fb283e4cfce3d3e1cb7688986fcbe6/Kokoro-82M-GGUF/kokoro-82m-q8_0.gguf",
        189_550_976, "9a4ca6176460cdc8c7845b804c0bbe85a4791ee3ffff568a3fdadbb708c63f47");

    public static readonly IReadOnlyList<KokoroVoice> Voices = Array.AsReadOnly<KokoroVoice>(
    [
        new("bf_emma", "Emma · British English", "en-gb"),
        new("bm_george", "George · British English", "en-gb"),
        new("af_heart", "Heart · American English", "en-us"),
        new("am_michael", "Michael · American English", "en-us"),
    ]);

    // The pinned upstream build enables native CPU instructions on its build host.
    public static bool Supported => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64 && Avx512F.IsSupported;
    public static long DownloadSize => Runtime.Size + Phonemizer.Size + Model.Size;
    public static KokoroVoice Voice(string? id) => Voices.FirstOrDefault(v => v.Id == id) ?? Voices[0];

    private string Target => Path.Combine(folder, "audio.cpp-0.9.5-kokoro-q8");
    private string WorkFolder => Path.Combine(folder, "speech-work");

    public string CreateWorkFolder()
    {
        if (Directory.Exists(WorkFolder) && File.GetAttributes(WorkFolder).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The speech work folder is a link.");
        var work = Path.Combine(WorkFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    public void ClearAbandonedSpeech() => TryDeleteOwned(WorkFolder);

    public void ReleaseWorkFolder(string work)
    {
        if (string.Equals(Path.GetDirectoryName(work), WorkFolder, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(Path.GetFileName(work), "N", out _))
            TryDeleteOwned(work);
    }

    public KokoroInstall? Installed() => Supported ? Inspect(Target) : null;

    private static KokoroInstall? Inspect(string root)
    {
        try
        {
            var cli = Path.Combine(root, RuntimeName);
            var model = Path.Combine(root, ModelName);
            var dll = Path.Combine(root, "espeak-ng.dll");
            var data = Path.Combine(root, "espeak-ng-data", "phontab");
            if (!File.Exists(cli) || !File.Exists(model) || !File.Exists(dll) || !File.Exists(data)
                || new FileInfo(model).Length != Model.Size) return null;
            return new(cli, model, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task InstallAsync(IProgress<long> progress, bool repair, CancellationToken cancellation)
    {
        if (!Supported) throw new ReadAloudException("This Kokoro build needs 64-bit Windows and AVX-512 support.");
        if (!repair && Installed() is not null) return;
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            long done = 0;
            foreach (var (spec, name) in new[] { (Runtime, "runtime.zip"), (Phonemizer, "espeak.zip"), (Model, ModelName) })
            {
                await VerifiedDownload.DownloadAsync(http, spec, Path.Combine(staging, name), bytes => progress.Report(done + bytes),
                    TimeSpan.FromSeconds(45), resume: false, cancellation).ConfigureAwait(false);
                done += spec.Size;
            }

            Extract(staging, "runtime.zip", name => !name.Contains('/') &&
                (name is RuntimeName or "LICENSE" || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ? name : null, cancellation);
            Extract(staging, "espeak.zip", name =>
                name.StartsWith("espeakng_loader/espeak-ng-data/", StringComparison.Ordinal) || name == "espeakng_loader/espeak-ng.dll"
                    ? name["espeakng_loader/".Length..] : null, cancellation);
            if (Inspect(staging) is null) throw new ReadAloudException("The verified Kokoro package is incomplete.");
            cancellation.ThrowIfCancellationRequested();
            Publish(staging);
        }
        finally { TryDeleteOwned(staging); }
    }

    private void Publish(string staging)
    {
        if (!Directory.Exists(Target)) { Directory.Move(staging, Target); return; }
        if (File.GetAttributes(Target).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The Kokoro install folder is a link.");
        var previous = Path.Combine(folder, ".previous-" + Guid.NewGuid().ToString("N"));
        Directory.Move(Target, previous);
        try { Directory.Move(staging, Target); }
        catch
        {
            Directory.Move(previous, Target);
            throw;
        }
        TryDeleteOwned(previous);
    }

    private static void Extract(string root, string archiveName, Func<string, string?> keep, CancellationToken ct)
    {
        var archivePath = Path.Combine(root, archiveName);
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            long bytes = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Name.Length == 0 || keep(entry.FullName) is not { } relative) continue;
                if (relative.Split('/').Any(part => part.Length == 0 || part is "." or ".."
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains('\\')))
                    throw new InvalidDataException("The voice package has an unsafe file path.");
                bytes += entry.Length;
                if (bytes > 256L << 20) throw new InvalidDataException("The voice package expands past its size limit.");
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path);
            }
        }
        File.Delete(archivePath);
    }

    private static void TryDeleteOwned(string path)
    {
        try { DeleteOwned(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteOwned(string path)
    {
        if (!Directory.Exists(path)) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("A speech folder is a link.");
        Directory.Delete(path, recursive: true);
    }
}
