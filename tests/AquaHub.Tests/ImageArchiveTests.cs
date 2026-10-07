using AquaHub.Core.Ai.Imaging;

namespace AquaHub.Tests;

public sealed class ImageArchiveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aqua-images-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aD1sAAAAASUVORK5CYII=");
    private static GeneratedImage Item() => new(Guid.NewGuid().ToString("N"), "A green valley", 42, 1024, 1024, "test.safetensors", DateTimeOffset.Now, 2.5);

    [Fact]
    public async Task GalleryKeepsSettingsAcrossReloadAndCannotOverwriteAnImage()
    {
        var archive = new ImageArchive(_folder);
        var item = Item();
        await archive.SaveAsync(item, Png);
        Assert.Equal(item, Assert.Single(new ImageArchive(_folder).Recent()));
        Assert.Equal(Png, await File.ReadAllBytesAsync(archive.ImagePath(item)));
        await Assert.ThrowsAsync<IOException>(() => archive.SaveAsync(item, Png));
        Assert.Equal(item, Assert.Single(archive.Recent()));
    }

    [Fact]
    public async Task RejectsNonImagesAndEscapingIdsAndSkipsBrokenMetadata()
    {
        var archive = new ImageArchive(_folder);
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.SaveAsync(Item(), "not an image"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => archive.ImagePath(Item() with { Id = "../outside" }));
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "bad.png.json"), "{invalid}");
        await File.WriteAllTextAsync(Path.Combine(_folder, "escaped.png.json"), System.Text.Json.JsonSerializer.Serialize(Item() with { Id = "../outside" }));
        Assert.Empty(archive.Recent());
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheImageSidecarAndTrackedComfyOutput()
    {
        var archive = new ImageArchive(_folder);
        var comfy = Path.Combine(_folder, "comfy-output");
        Directory.CreateDirectory(Path.Combine(comfy, "subfolder"));
        var item = Item() with { ComfyOutputPath = "subfolder/render.png" };
        await archive.SaveAsync(item, Png);
        var serverCopy = Path.Combine(comfy, "subfolder", "render.png");
        await File.WriteAllBytesAsync(serverCopy, Png);
        var unrelated = Path.Combine(_folder, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep");

        archive.Delete(item, comfy);

        Assert.False(File.Exists(archive.ImagePath(item)));
        Assert.False(File.Exists(archive.ImagePath(item) + ".json"));
        Assert.False(File.Exists(serverCopy));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task DeleteTreatsEmptyComfyOutputFolderAsUnconfigured()
    {
        var archive = new ImageArchive(_folder);
        var item = Item() with { ComfyOutputPath = "render.png" };
        await archive.SaveAsync(item, Png);

        archive.Delete(item, "");

        Assert.False(File.Exists(archive.ImagePath(item)));
        Assert.False(File.Exists(archive.ImagePath(item) + ".json"));
    }

    [Fact]
    public async Task DeleteRejectsComfyTraversalBeforeRemovingAnyArchiveFile()
    {
        var archive = new ImageArchive(_folder);
        var comfy = Path.Combine(_folder, "comfy-output");
        Directory.CreateDirectory(comfy);
        var outside = Path.Combine(_folder, "outside.png");
        await File.WriteAllBytesAsync(outside, Png);
        var item = Item() with { ComfyOutputPath = "../outside.png" };
        await archive.SaveAsync(item, Png);

        Assert.Throws<ArgumentException>(() => archive.Delete(item, comfy));

        Assert.True(File.Exists(archive.ImagePath(item)));
        Assert.True(File.Exists(archive.ImagePath(item) + ".json"));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task PruneUsesCreatedMetadataAndScansBeyondRecentGalleryLimit()
    {
        var archive = new ImageArchive(_folder);
        var now = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        var expired = new List<GeneratedImage>();
        for (var i = 0; i < 35; i++)
        {
            var item = Item() with { Created = now.AddDays(-40) };
            expired.Add(item);
            await archive.SaveAsync(item, Png);
            File.SetLastWriteTimeUtc(archive.ImagePath(item) + ".json", now.UtcDateTime);
        }
        var retained = Item() with { Created = now.AddDays(-10) };
        await archive.SaveAsync(retained, Png);
        File.SetLastWriteTimeUtc(archive.ImagePath(retained) + ".json", now.AddDays(-80).UtcDateTime);
        var unrelated = Path.Combine(_folder, "notes.txt");
        await File.WriteAllTextAsync(unrelated, "preserve");

        var result = archive.Prune(30, now);

        Assert.Equal(35, result.DeletedCount);
        Assert.Empty(result.Failures);
        Assert.All(expired, item => Assert.False(File.Exists(archive.ImagePath(item))));
        Assert.True(File.Exists(archive.ImagePath(retained)));
        Assert.True(File.Exists(archive.ImagePath(retained) + ".json"));
        Assert.Equal("preserve", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task PruneReportsUnavailableComfyFolderAndKeepsMetadataForRetry()
    {
        var archive = new ImageArchive(_folder);
        var item = Item() with { Created = DateTimeOffset.UtcNow.AddDays(-60), ComfyOutputPath = "render.png" };
        await archive.SaveAsync(item, Png);

        var result = archive.Prune(30, DateTimeOffset.UtcNow, Path.Combine(_folder, "missing-output"));

        Assert.Equal(0, result.DeletedCount);
        Assert.Single(result.Failures);
        Assert.Equal(item.Id, result.Failures[0].ImageId);
        Assert.True(File.Exists(archive.ImagePath(item)));
        Assert.True(File.Exists(archive.ImagePath(item) + ".json"));
    }

    [Fact]
    public async Task ZeroRetentionKeepsImagesForever()
    {
        var archive = new ImageArchive(_folder);
        var item = Item() with { Created = DateTimeOffset.UnixEpoch };
        await archive.SaveAsync(item, Png);

        var result = archive.Prune(0, DateTimeOffset.UtcNow);

        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(result.Failures);
        Assert.True(File.Exists(archive.ImagePath(item)));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
