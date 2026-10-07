using System.Text.Json;

namespace AquaHub.Core.Ai.Imaging;

public sealed record GeneratedImage(string Id, string Prompt, ulong Seed, int Width, int Height,
    string Model, DateTimeOffset Created, double Seconds)
{
    public int Steps { get; init; }
    public string Variant { get; init; } = "base";
    public string TextEncoder { get; init; } = "";
    public string Vae { get; init; } = "";
    /// <summary>Validated relative path of the original ComfyUI output (subfolder/name).</summary>
    public string? ComfyOutputPath { get; init; }
    /// <summary>Validated relative ComfyUI path of the reference image used to create this image.</summary>
    public string? ReferenceImage { get; init; }
}

public sealed record ImageArchiveFailure(string ImageId, string Path, string Message);
public sealed record ImageArchivePruneResult(int DeletedCount, IReadOnlyList<ImageArchiveFailure> Failures);

/// <summary>Generated images belong to this profile, with reproducible settings beside the PNG.</summary>
public sealed class ImageArchive(string folder)
{
    public string Folder { get; } = Path.GetFullPath(folder);
    public string ImagePath(GeneratedImage image) => Path.Combine(Folder, SafeId(image.Id) + ".png");

    private static string SafeId(string id) => Guid.TryParseExact(id, "N", out _) ? id
        : throw new ArgumentException("Invalid image identifier.", nameof(id));

    public async Task SaveAsync(GeneratedImage item, byte[] png, CancellationToken ct = default)
    {
        if (png.Length is < 8 or > 64 * 1024 * 1024 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("The image server did not return a PNG image.");
        Directory.CreateDirectory(Folder);
        var path = ImagePath(item);
        // Unique IDs and create-new keep an old image from ever being replaced.
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            try { await file.WriteAsync(png, ct).ConfigureAwait(false); }
            catch { file.Close(); File.Delete(path); throw; }
        }
        try
        {
            await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(item), ct).ConfigureAwait(false);
        }
        catch { File.Delete(path); if (File.Exists(path + ".json")) File.Delete(path + ".json"); throw; }
    }

    /// <summary>Deletes one archived image and, when configured, its tracked ComfyUI output copy.</summary>
    /// <remarks>The metadata sidecar is deleted last so an interrupted delete can be retried.</remarks>
    public void Delete(GeneratedImage image, string? configuredComfyOutputRoot = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var id = SafeId(image.Id);
        EnsureNoReparsePoints(Folder);
        var png = Path.Combine(Folder, id + ".png");
        var metadata = png + ".json";

        // Validate every target before deleting any of them.
        EnsureRegularFileOrMissing(png);
        EnsureRegularFileOrMissing(metadata);
        string? comfyFile = null;
        if (!string.IsNullOrWhiteSpace(configuredComfyOutputRoot) && image.ComfyOutputPath is not null)
            comfyFile = ResolveComfyOutput(configuredComfyOutputRoot, image.ComfyOutputPath);

        if (comfyFile is not null) DeleteFileIfPresent(comfyFile);
        DeleteFileIfPresent(png);
        DeleteFileIfPresent(metadata);
    }

    /// <summary>Removes images whose metadata creation time is past the configured retention.</summary>
    /// <param name="retentionDays">Zero disables pruning; negative values are invalid.</param>
    /// <param name="now">Current time, supplied by the caller for deterministic behavior.</param>
    /// <param name="serverOutputFolder">Optional configured ComfyUI output directory.</param>
    public ImageArchivePruneResult Prune(int retentionDays, DateTimeOffset now, string? serverOutputFolder = null)
    {
        if (retentionDays < 0) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (retentionDays == 0 || !Directory.Exists(Folder))
            return new ImageArchivePruneResult(0, Array.Empty<ImageArchiveFailure>());

        var failures = new List<ImageArchiveFailure>();
        var deleted = 0;
        DateTimeOffset cutoff;
        try { cutoff = now.AddDays(-retentionDays); }
        catch (ArgumentOutOfRangeException) { cutoff = DateTimeOffset.MinValue; }

        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.png.json"))
        {
            GeneratedImage? image;
            try
            {
                if (file.Length > 64 * 1024) throw new InvalidDataException("Metadata file is too large.");
                image = JsonSerializer.Deserialize<GeneratedImage>(File.ReadAllText(file.FullName));
                if (image is null) throw new JsonException("Metadata is empty.");
                if (Path.GetFileName(file.FullName) != SafeId(image.Id) + ".png.json")
                    throw new InvalidDataException("Metadata identifier does not match its filename.");
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
            {
                failures.Add(new ImageArchiveFailure(Path.GetFileName(file.FullName), file.FullName, ex.Message));
                continue;
            }

            if (image.Created > cutoff) continue;
            try
            {
                Delete(image, serverOutputFolder);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures.Add(new ImageArchiveFailure(image.Id, file.FullName, ex.Message));
            }
        }
        return new ImageArchivePruneResult(deleted, failures);
    }

    public IReadOnlyList<GeneratedImage> Recent(int count = 30)
    {
        if (!Directory.Exists(Folder)) return Array.Empty<GeneratedImage>();
        var result = new List<GeneratedImage>();
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.png.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(Math.Clamp(count, 1, 100)))
        {
            try
            {
                if (file.Length > 64 * 1024) continue;
                var image = JsonSerializer.Deserialize<GeneratedImage>(File.ReadAllText(file.FullName));
                if (image is not null && Path.GetFileName(file.FullName) == SafeId(image.Id) + ".png.json" && File.Exists(ImagePath(image))) result.Add(image);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(x => x.Created).ToList();
    }

    private static string ResolveComfyOutput(string configuredRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\'))
            throw new ArgumentException("Invalid ComfyUI output path.", nameof(relativePath));
        var segments = relativePath.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.Contains(':')))
            throw new ArgumentException("Invalid ComfyUI output path.", nameof(relativePath));

        if (!Path.IsPathFullyQualified(configuredRoot))
            throw new ArgumentException("The configured ComfyUI output folder must be an absolute path.", nameof(configuredRoot));
        var root = Path.GetFullPath(configuredRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The configured ComfyUI output folder is unavailable.");
        EnsureNoReparsePoints(root);
        var target = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("ComfyUI output path escapes its configured folder.", nameof(relativePath));
        EnsureNoReparsePoints(target);
        EnsureRegularFileOrMissing(target);
        return target;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException("Path has no filesystem root.");
        var current = root;
        Check(current);
        foreach (var segment in full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Check(current);
        }

        static void Check(string item)
        {
            if (!File.Exists(item) && !Directory.Exists(item)) return;
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to access an image path through a reparse point.");
        }
    }

    private static void EnsureRegularFileOrMissing(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        if (Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to delete a directory or reparse point as an image file.");
    }

    private static void DeleteFileIfPresent(string path)
    {
        EnsureRegularFileOrMissing(path);
        if (File.Exists(path)) File.Delete(path);
    }
}
