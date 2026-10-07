using AquaHub.Core.Ai.Assistant;

namespace AquaHub.Tests;

public sealed class LocalFilesPathTests
{
    [Fact]
    public void NonexistentMultiSegmentPathKeepsItsEntirePathAndMatchesItsAllowedRoot()
    {
        using var temp = new TempFolder();
        var root = Path.Combine(temp.Path, "allowed");
        Directory.CreateDirectory(root);
        var files = new LocalFiles(() => new[] { root });
        var missing = Path.Combine(root, "missing", "nested", "notes.txt");

        Assert.True(files.CanRead(missing, out var full, out var reason));
        Assert.Equal(Path.GetFullPath(missing), full);
        Assert.Equal("", reason);
        Assert.True(LocalFiles.IsUnder(missing, root));
    }

    [Fact]
    public void AUnresolvable8Dot3LookingPathFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempFolder();
        var files = new LocalFiles(() => new[] { temp.Path });
        var missing = Path.Combine(temp.Path, "UNKNOWN~98765", "nested", "notes.txt");

        Assert.False(files.CanRead(missing, out _, out _));
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aquahub-localfiles-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
