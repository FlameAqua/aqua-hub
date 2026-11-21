using AquaHub.Core.Search;
using AquaHub.Platform;
using Microsoft.Data.Sqlite;

namespace AquaHub.Tests;

public sealed class LocalIndexTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("aqua-search-").FullName;
    private SearchStore Store => new(Path.Combine(_temp, "search.sqlite"));
    private readonly SearchStamp _stamp = new("vol-file", 32, DateTime.UtcNow.ToFileTimeUtc());

    [Fact]
    public void ConsentReplacementInvalidatesOldWriterAndVectorCompletion()
    {
        var db = Store;
        db.Add(_temp, "volume", true);
        var root = Assert.Single(db.Roots());
        var path = Path.Combine(_temp, "plan.txt");
        db.Replace(root, path, _stamp, ["winter ceramics"]);
        var work = db.NextPassage(0)!.Value;
        db.ChangeContent(root, false);
        db.SaveVector(work.Id, new float[768]);
        db.Replace(root, path, _stamp, ["stale writer"]);
        Assert.Null(db.NextPassage(0));
        Assert.Empty(db.Candidates("ceramics", new(), null, default));
        Assert.Equal(0, db.Count());
        var replacement = Assert.Single(db.Roots());
        Assert.NotEqual(root.Id, replacement.Id);
        db.Replace(replacement, path, _stamp, ["unexpected text"]);
        Assert.Empty(db.Candidates("unexpected", new(), null, default));
        db.Remove(replacement.Id);
        Assert.Empty(Store.Roots());
        Assert.Equal(0, Store.Count());
    }

    [Fact]
    public void RankingUsesExactNamesMatchesPassageAndSeparatesCopies()
    {
        var db = Store;
        db.Add(_temp, "volume", true);
        var root = Assert.Single(db.Roots());
        string PathFor(string name) => Path.Combine(_temp, name);
        db.Replace(root, PathFor("Autumn.txt"), _stamp, ["The kiln cools overnight."]);
        db.Replace(root, PathFor("other.txt"), _stamp with { Identity = "copy" }, ["The kiln cools overnight in autumn.txt.", "Second note about a kiln."]);
        List<SearchHit> Find(string q) => db.Candidates(q, new(), null, default);
        Assert.Equal("Exact filename", Find("AUTUMN.TXT")[0].Reason);
        Assert.Equal("Exact path", Find(PathFor("Autumn.txt"))[0].Reason);
        Assert.Equal("Similar filename", Find("Autmun")[0].Reason);
        var hits = Find("kiln cools");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Contains("kiln cools", h.Snippet));
        db.Replace(root, PathFor("renamed.txt"), _stamp, ["Changed passage"]);
        Assert.Single(Find("kiln cools"));
        db.Forget(root.Id, PathFor("other.txt"));
        Assert.Empty(Find("kiln cools"));
        Assert.Single(Store.Candidates("Changed", new(), null, default));
        Assert.Throws<InvalidOperationException>(() => db.Add(PathFor("child"), "nested", true));
    }

    [Fact]
    public void IncompatibleSpaceOrExtractionNeverReappearsAfterReopen()
    {
        var db = Store;
        db.Add(_temp, "v", true);
        var root = db.Roots()[0];
        db.Replace(root, Path.Combine(_temp, "alpha.md"), _stamp, ["Hiking"]);

        using var c = new SqliteConnection("Pooling=False;Data Source=" + Path.Combine(_temp, "search.sqlite"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE chunks SET vector=zeroblob(3072),space='retired'";
        cmd.ExecuteNonQuery();
        Assert.NotNull(Store.NextPassage(0));
        cmd.CommandText = "UPDATE docs SET version=-1";
        cmd.ExecuteNonQuery();
        Assert.Equal(0, Store.Count());
    }

    [Fact]
    public void ExtractionCapsPlainTextAndIgnoresUnsupportedContainers()
    {
        using var large = new MemoryStream(new byte[SearchText.InputLimit + 1]);
        Assert.Empty(SearchText.Extract("large.txt", large, default));
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("kiln notes ", 8000))));
        Assert.Empty(SearchText.Extract("archive.zip", input, default));
        var chunks = SearchText.Extract("notes.md", input, default);
        Assert.InRange(chunks.Sum(s => s.Length), 1, SearchText.CharacterLimit);
        Assert.All(chunks, s => Assert.InRange(s.Length, 1, 641));
        using var bad = new MemoryStream([0, 1, 2]);
        Assert.Empty(SearchText.Extract("bad.txt", bad, default));
    }

    [Theory]
    [InlineData((int)FileAttributes.Offline)]
    [InlineData((int)FileAttributes.Encrypted)]
    [InlineData((int)FileAttributes.ReparsePoint)]
    [InlineData(0x40000)]
    [InlineData(0x400000)]
    public void RefusesCloudAndEncryptedMetadataBeforeOpeningData(int flags) =>
        Assert.True(WindowsSearchFiles.Excluded((FileAttributes)flags));

    [System.Runtime.InteropServices.DllImport("kernel32", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string alias, string original, IntPtr security);

    [Fact]
    public void WindowsAdapterRefusesAliasesSecretsOverlapsAndReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "AquaHub.sln"))) repo = repo.Parent;
        var folder = Path.Combine(repo!.FullName, "TestResults", "search-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var policy = new WindowsSearchFiles(_temp);
        try
        {
            var root = new SearchRoot(1, folder, policy.Approve(folder), true);
            foreach (var blocked in new[] { @"\\server\share", folder + @"\..\sibling", folder + @"\UNKNOW~1", folder + @"\file.txt:stream", Path.GetPathRoot(folder)! })
                Assert.Throws<UnauthorizedAccessException>(() => policy.Approve(blocked));
            var good = Path.Combine(folder, "notes.txt");
            File.WriteAllText(good, "ordinary");
            using (var lease = policy.Open(good, root))
            {
                using var stream = lease.Read();
                Assert.Equal(8, stream.Length);
                Assert.Throws<IOException>(() => File.Delete(good));
            }
            using (File.Open(good, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => policy.Open(good, root));
            Assert.Throws<UnauthorizedAccessException>(() => policy.Open(good, root with { Identity = "replaced" }));
            Assert.Throws<UnauthorizedAccessException>(() => policy.Open(Path.Combine(folder, "secret.key"), root));
            Assert.Throws<UnauthorizedAccessException>(() => policy.Open(Path.Combine(folder, "AppData", "notes.txt"), root));
            Assert.Throws<UnauthorizedAccessException>(() => policy.Open(folder + "-other.txt", root));
            var hardLink = Path.Combine(folder, "alias.txt");
            Assert.True(CreateHardLink(hardLink, good, IntPtr.Zero));
            try { Assert.Throws<UnauthorizedAccessException>(() => policy.Open(hardLink, root)); }
            finally { File.Delete(hardLink); }
            var target = Directory.CreateDirectory(Path.Combine(folder, "target")).FullName;
            var link = Path.Combine(folder, "junction");
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/d /c mklink /J junction target")
            { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true };
            try
            {
                using var process = System.Diagnostics.Process.Start(start)!;
                Assert.True(process.WaitForExit(5000));
                Assert.Equal(0, process.ExitCode);
                Assert.Throws<UnauthorizedAccessException>(() => policy.Approve(link));
                Assert.Throws<UnauthorizedAccessException>(() => policy.Open(Path.Combine(link, "child.txt"), root));
            }
            finally { if (Directory.Exists(link)) Directory.Delete(link); Directory.Delete(target); }
        }
        finally { File.Delete(Path.Combine(folder, "notes.txt")); Directory.Delete(folder); }
    }

    [Theory]
    [InlineData(".docx", "word/document.xml", "<p><t>kiln repairs</t></p>")]
    [InlineData(".pptx", "ppt/slides/slide1.xml", "<p><t>kiln repairs</t></p>")]
    [InlineData(".xlsx", "xl/worksheets/sheet1.xml", "<row><c t='inlineStr'><is><t>kiln repairs</t></is></c></row>")]
    [InlineData(".odt", "content.xml", "<t:p xmlns:t='urn:oasis:names:tc:opendocument:xmlns:text:1.0'>kiln repairs</t:p>")]
    [InlineData(".ods", "content.xml", "<t:p xmlns:t='urn:oasis:names:tc:opendocument:xmlns:text:1.0'>kiln repairs</t:p>")]
    [InlineData(".odp", "content.xml", "<t:p xmlns:t='urn:oasis:names:tc:opendocument:xmlns:text:1.0'>kiln repairs</t:p>")]
    public void ReadsOnlyDocumentParts(string extension, string part, string xml)
    {
        using var input = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(zip.CreateEntry(part).Open())) w.Write(xml);
            using (var w = new StreamWriter(zip.CreateEntry("scripts/macro.xml").Open())) w.Write("<t>run this</t>");
        }
        input.Position = 0;
        Assert.Equal("kiln repairs", Assert.Single(SearchText.Extract("file" + extension, input, default)));
    }

    [Fact]
    public async Task RemovedFolderCannotReturnWhileOldEmbeddingFinishesAndQuitJoinsIt()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_temp, "corpus")).FullName;
        var path = Path.Combine(dir, "lecture.md");
        File.WriteAllText(path, "How the kiln cools.");
        var db = Store;
        var embeddings = new HeldEmbedding();
        var svc = new FileSearchService(db, new FixtureFiles(), embeddings);
        try
        {
            await svc.AddAsync(dir, true);
            await embeddings.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var hit = Assert.Single(await svc.FindAsync("kiln", new(), false, default));
            await svc.RemoveAsync(hit.RootId);
            Assert.Empty(await svc.FindAsync("kiln", new(), false, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ActAsync(hit, _ => Assert.Fail("Revoked action ran")));
            var stopping = svc.StopAsync();
            Assert.False(stopping.IsCompleted);
            Assert.True(embeddings.Cancelled.Task.IsCompleted);
            embeddings.Finish.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(embeddings.Disposed);
            Assert.Null(Store.NextPassage(0));
        }
        finally { embeddings.Finish.TrySetResult(); await svc.StopAsync(); }
    }

    [Fact]
    public async Task SupersededSemanticQueryCannotPublishALateResponse()
    {
        var db = Store;
        db.Paused = true;
        db.Add(_temp, _temp, true);
        var model = new HeldEmbedding();
        var service = new FileSearchService(db, new FixtureFiles(), model);
        using var request = new CancellationTokenSource();
        try
        {
            var old = service.FindAsync("earlier wording", new(), true, request.Token);
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            request.Cancel();
            Assert.Empty(await service.FindAsync("new wording", new(), false, default));
            model.Finish.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        }
        finally { model.Finish.TrySetResult(); await service.StopAsync(); }
    }

    [Fact]
    public void FolderExtensionAndDateFiltersConstrainEverySignal()
    {
        var db = Store;
        var a = Path.Combine(_temp, "college");
        var b = Path.Combine(_temp, "work");
        db.Add(a, "a", true);
        db.Add(b, "b", true);
        var roots = db.Roots();
        var vector = new float[768];
        vector[0] = 1;
        foreach (var root in roots)
        {
            foreach (var ext in new[] { ".md", ".txt" })
            {
                var name = "kiln" + ext;
                db.Replace(root, Path.Combine(root.Path, name), _stamp with { Identity = name }, ["kiln repair"]);
                db.SaveVector(db.NextPassage(0)!.Value.Id, vector);
            }
        }
        var filter = new SearchFilter(roots[0].Id, ".MD", DateTime.UtcNow.AddDays(-1));
        Assert.Equal(Path.Combine(a, "kiln.md"), Assert.Single(db.Candidates("kiln", filter, vector, default)).Path);
        Assert.Empty(db.Candidates("kiln", filter with { ModifiedSince = DateTime.UtcNow.AddDays(1) }, vector, default));
    }

    [Fact]
    public void ExplorerScopeMatchesNestedFilesAcrossSearchSignals()
    {
        var db = Store;
        var approved = Path.Combine(_temp, "College");
        db.Add(approved, "root", true);
        var root = Assert.Single(db.Roots());
        var folder = Path.Combine(approved, "Café");
        var expected = Path.Combine(folder, "glaze.txt");
        var sibling = Path.Combine(approved, "Café backup", "glaze.txt");
        var vector = new float[768];
        vector[0] = 1;
        db.Replace(root, sibling, _stamp with { Identity = "copy" }, ["the kiln cools overnight"]);
        db.SaveVector(db.NextPassage(0)!.Value.Id, vector);
        db.Replace(root, expected, _stamp, ["the kiln cools overnight"]);
        db.SaveVector(db.NextPassage(0)!.Value.Id, vector);
        var scope = new SearchFilter(Folder: folder.ToUpperInvariant());
        Assert.True(scope.Intersects(root));
        Assert.False(new SearchFilter(Folder: approved + " copy").Intersects(root));
        Assert.Equal(expected, Assert.Single(db.Candidates("glaze.txt", scope, null, default)).Path);
        Assert.Equal(expected, Assert.Single(db.Candidates("kiln cools", scope, null, default)).Path);
        Assert.Equal(expected, Assert.Single(db.Candidates("paraphrase", scope, vector, default)).Path);
    }

    [Fact]
    public async Task ReopenReconcilesChangesMissedWhilePaused()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_temp, "notes")).FullName;
        var db = Store;
        db.Paused = true;
        var files = new FixtureFiles();
        var service = new FileSearchService(db, files, new HeldEmbedding { Configured = false });
        try { await service.AddAsync(dir, true); }
        finally { await service.StopAsync(); }
        File.WriteAllText(Path.Combine(dir, "new.md"), "repair the telescope");
        var restarted = new FileSearchService(Store, files, new HeldEmbedding { Configured = false });
        var indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        restarted.Changed += () => { if (restarted.Status.Files == 1 && restarted.Status.Activity == "Index up to date") indexed.TrySetResult(); };
        try
        {
            Assert.True(db.Paused);
            await restarted.PauseAsync(false);
            await indexed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var hit = Assert.Single(await restarted.FindAsync("telescope", new(), false, default));
            File.WriteAllText(hit.Path, "replacement with a different length");
            Assert.Empty(await restarted.FindAsync("telescope", new(), false, default));
            await Assert.ThrowsAsync<IOException>(() => restarted.ActAsync(hit, _ => Assert.Fail("Stale action ran")));
        }
        finally { await restarted.StopAsync(); }
    }

    private sealed class FixtureFiles : ISearchFiles
    {
        public string Approve(string path) => path;
        public IDisposable DirectoryLease(string path, SearchRoot root) => Directory.Exists(path) ? new MemoryStream() : throw new DirectoryNotFoundException();
        public SearchFile Open(string path, SearchRoot root) => new FixtureFile(path);
        private sealed class FixtureFile(string path) : SearchFile
        {
            public override SearchStamp Stamp => new(path, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path).ToFileTimeUtc());
            public override Stream Read() => File.OpenRead(path);
            public override void Dispose() { }
        }
    }
    private sealed class HeldEmbedding : ISearchEmbeddings
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Configured { get; init; } = true;
        public bool Disposed { get; private set; }
        public async Task<float[]> EncodeAsync(string text, CancellationToken ct)
        {
            using var reg = ct.Register(() => Cancelled.TrySetResult());
            Started.TrySetResult();
            await Finish.Task;
            return new float[768];
        }
        public void Release() { }
        public void Dispose() => Disposed = true;
    }

    public void Dispose()
    {
        Store.ReleaseConnections();
        Directory.Delete(_temp, true);
    }
}
