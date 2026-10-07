using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AquaHub.Core.Ai.Local;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;

namespace AquaHub.Tests;

public sealed class Phase4LibraryTests
{
    [Fact]
    public void GgufReadsAttentionShapesAndMtpHeads()
    {
        using var stream = TinyGguf(
            [
                ("general.architecture", "qwen35"), ("qwen35.block_count", 3L),
                ("qwen35.embedding_length", 8L), ("qwen35.attention.head_count", 2L),
                ("qwen35.context_length", 32768L), ("tokenizer.chat_template", "{{ enable_thinking }} {{ tools }}"),
            ],
            [("blk.0.attn_k.weight", 4L, 2L), ("blk.0.attn_v.weight", 4L, 2L),
             ("blk.1.attn_k.weight", 4L, 2L), ("blk.1.attn_v.weight", 4L, 2L),
             ("blk.2.attn_k.weight", 4L, 2L), ("blk.2.attn_v.weight", 4L, 2L),
             ("blk.2.nextn.eh_proj.weight", 4L, 2L)]);

        var info = Gguf.Read(stream);

        Assert.Equal("qwen35", info.Architecture);
        Assert.Equal(3, info.Layers);
        Assert.Equal(2, info.KvLayers); // MTP block is not counted as a regular KV layer.
        Assert.Equal(8, info.KvElementsPerToken);
        Assert.True(info.HasMtp);
        Assert.True(info.TemplateThinks);
        Assert.True(info.TemplateTools);
        Assert.Equal(32768, info.ContextLength);
        Assert.Equal("IQ2_S", Gguf.QuantFromName("Qwen3.8-27B-IQ2_S-mtp.gguf"));
    }

    [Theory]
    [InlineData("huge-string")]
    [InlineData("huge-array")]
    [InlineData("too-many-tensors")]
    [InlineData("bad-dimensions-zero")]
    [InlineData("bad-dimensions-nine")]
    [InlineData("truncated")]
    public void GgufRejectsHostileOrTruncatedHeaders(string shape)
    {
        using var stream = HostileGguf(shape);
        Assert.Throws<GgufFormatException>(() => Gguf.Read(stream));
    }

    [Fact]
    public void OllamaStoreDeduplicatesNamesAndModelLibraryFindsAliasesWithoutRemovingOllamaFiles()
    {
        using var temp = new TempFolder();
        var store = Path.Combine(temp.Path, "ollama");
        var digest = new string('a', 64);
        var blob = Path.Combine(store, "blobs", "sha256-" + digest);
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        File.WriteAllBytes(blob, ValidGguf());
        WriteManifest(store, "registry.ollama.ai/library/demo/7b", digest, File.ReadAllBytes(blob).Length);
        WriteManifest(store, "registry.ollama.ai/library/demo/latest", digest, File.ReadAllBytes(blob).Length);
        WriteManifest(store, "registry.ollama.ai/library/bad/latest", new string('b', 64), File.ReadAllBytes(blob).Length + 1);

        var listed = OllamaStore.List(store);
        Assert.Single(listed);
        Assert.Equal(new[] { "demo:7b", "demo:latest" }.OrderBy(x => x.Length).ThenBy(x => x, StringComparer.Ordinal), listed[0].Names);

        var library = new ModelLibrary(() => new LlamaSettings { UseOllamaModels = true }, Path.Combine(temp.Path, "aqua"), () => store);
        var models = library.Refresh();
        var model = Assert.Single(models);
        Assert.Equal("ollama:demo:7b", model.Id);
        Assert.Same(model, library.Find("demo:latest"));
        Assert.Same(model, library.Find("ollama:demo:latest"));
        Assert.Same(model, library.Find(Path.GetFileName(blob)));
        Assert.False(library.Remove(model.Id));
        Assert.True(File.Exists(blob));
    }

    [Theory]
    [InlineData("mmproj")]
    [InlineData("adapter")]
    public void ModelLibraryRejectsProjectorsAndAdaptersOnEveryRefresh(string type)
    {
        using var temp = new TempFolder();
        var root = Path.Combine(temp.Path, "models");
        Directory.CreateDirectory(root);
        var file = "unsupported.gguf";
        var path = Path.Combine(root, file);
        using (var stream = TinyGguf([("general.architecture", "llama"), ("general.type", type)], []))
            File.WriteAllBytes(path, stream.ToArray());
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var library = new ModelLibrary(() => new LlamaSettings { ModelsFolder = root, UseOllamaModels = false }, root);

        library.AddDownload("owner/repo", new string('a', 40), file, path, bytes.Length, sha, null, DateTimeOffset.UtcNow);
        Assert.Empty(library.Models);
        Assert.Contains(library.Problems, p => p.Contains("not a model on its own", StringComparison.Ordinal));

        Assert.Empty(library.Refresh());
        Assert.Contains(library.Problems, p => p.Contains("not a model on its own", StringComparison.Ordinal));
    }

    [Fact]
    public void LibraryDeduplicationRetainsOllamaNamesIdsAndDefaultLatestLookup()
    {
        using var temp = new TempFolder();
        var root = Path.Combine(temp.Path, "models");
        var bytes = ValidGguf();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var store = Path.Combine(temp.Path, "ollama");
        var blob = Path.Combine(store, "blobs", "sha256-" + sha);
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        File.WriteAllBytes(blob, bytes);
        WriteManifest(store, "registry.ollama.ai/library/demo/7b", sha, bytes.Length);
        WriteManifest(store, "registry.ollama.ai/library/demo/latest", sha, bytes.Length);
        Directory.CreateDirectory(Path.Combine(root, "owner__repo"));
        var downloaded = Path.Combine(root, "owner__repo", "same-bytes.gguf");
        File.WriteAllBytes(downloaded, bytes);

        var library = new ModelLibrary(() => new LlamaSettings { ModelsFolder = root, UseOllamaModels = true }, root, () => store);
        library.AddDownload("owner/repo", new string('a', 40), "same-bytes.gguf", downloaded, bytes.Length, sha, null, DateTimeOffset.UtcNow);

        var model = Assert.Single(library.Models);
        Assert.Equal(ModelSource.Aqua, model.Source);
        Assert.Same(model, library.Find("demo:7b"));
        Assert.Same(model, library.Find("ollama:demo:7b"));
        Assert.Same(model, library.Find("demo:latest"));
        Assert.Same(model, library.Find("ollama:demo:latest"));
        Assert.Same(model, library.Find("demo"));
        Assert.Same(model, library.Find("ollama:demo"));
    }

    [Fact]
    public void SettingsValidationRestrictsProviderAndLlamaPathsAndClampsValues()
    {
        var settings = new HubSettings();
        settings.Ai.Provider = "  EXTERNAL  ";
        settings.Ai.Llama.ServerPath = "\\\\server\\share\\llama-server.exe";
        settings.Ai.Llama.ModelsFolder = "relative\\models";
        settings.Ai.Llama.ContextTokens = 999999;
        settings.Ai.Llama.CacheType = "anything";
        settings.Ai.Llama.IdleUnloadMinutes = -2;
        settings.Ai.Llama.CpuThreads = 999;

        SettingsStore.Validate(settings);

        Assert.Equal("ollama", settings.Ai.Provider);
        Assert.Equal("", settings.Ai.Llama.ServerPath);
        Assert.Equal("", settings.Ai.Llama.ModelsFolder);
        Assert.Equal(262144, settings.Ai.Llama.ContextTokens);
        Assert.Equal("q8_0", settings.Ai.Llama.CacheType);
        Assert.Equal(0, settings.Ai.Llama.IdleUnloadMinutes);
        Assert.Equal(256, settings.Ai.Llama.CpuThreads);
    }

    [Fact]
    public async Task VerifiedDownloadChecksDigestAndDeletesMismatchedPayload()
    {
        using var temp = new TempFolder();
        var payload = Encoding.UTF8.GetBytes("not-the-published-file");
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var target = Path.Combine(temp.Path, "model.gguf");

        var ex = await Assert.ThrowsAsync<DownloadException>(() => VerifiedDownload.DownloadAsync(http,
            new DownloadSpec("https://huggingface.co/model", payload.Length, new string('0', 64)), target, _ => { },
            TimeSpan.FromSeconds(2), resume: true, CancellationToken.None));

        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task VerifiedDownloadResumesByRangeAndHashesTheExistingPart()
    {
        using var temp = new TempFolder();
        var bytes = Encoding.UTF8.GetBytes("verified bytes");
        var target = Path.Combine(temp.Path, "model.gguf");
        await File.WriteAllBytesAsync(target + ".part", bytes[..5]);
        var ranged = false;
        using var http = Client(request =>
        {
            ranged = request.Headers.Range?.Ranges.SingleOrDefault()?.From == 5;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[5..]) };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(5, bytes.Length - 1, bytes.Length);
            return response;
        });

        await VerifiedDownload.DownloadAsync(http, new DownloadSpec("https://huggingface.co/model", bytes.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))), target, _ => { },
            TimeSpan.FromSeconds(2), resume: true, CancellationToken.None);

        Assert.True(ranged);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task VerifiedDownloadRejectsNonHttpsAndOversizePayloads()
    {
        using var temp = new TempFolder();
        var target = Path.Combine(temp.Path, "model.gguf");
        var sent = false;
        using var http = Client(_ => { sent = true; return new HttpResponseMessage(HttpStatusCode.OK); });
        await Assert.ThrowsAsync<DownloadException>(() => VerifiedDownload.DownloadAsync(http,
            new DownloadSpec("http://huggingface.co/model", 1, new string('0', 64)), target, _ => { },
            TimeSpan.FromSeconds(2), resume: true, CancellationToken.None));
        Assert.False(sent);

        using var oversized = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("too many")) });
        await Assert.ThrowsAsync<DownloadException>(() => VerifiedDownload.DownloadAsync(oversized,
            new DownloadSpec("https://huggingface.co/model", 3, new string('0', 64)), target, _ => { },
            TimeSpan.FromSeconds(2), resume: true, CancellationToken.None));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task VerifiedDownloadKeepsReceivedBytesWhenCancelled()
    {
        using var temp = new TempFolder();
        using var cts = new CancellationTokenSource();
        var target = Path.Combine(temp.Path, "model.gguf");
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CancelAfterChunkStream(cts)) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifiedDownload.DownloadAsync(http,
            new DownloadSpec("https://huggingface.co/model", 20, new string('0', 64)), target, _ => { },
            TimeSpan.FromSeconds(2), resume: true, cts.Token));

        Assert.Equal(4, new FileInfo(target + ".part").Length);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ModelDownloadsReportGatedFailuresAndCheckFreeSpaceBeforeWriting()
    {
        using var temp = new TempFolder();
        var root = Path.Combine(temp.Path, "models");
        var library = new ModelLibrary(() => new LlamaSettings { ModelsFolder = root, UseOllamaModels = false }, root);
        using var forbidden = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var gated = new ModelDownloads(library, forbidden);
        var auth = await Assert.ThrowsAsync<DownloadException>(() => gated.FilesAsync("owner/repo"));
        Assert.Contains("account", auth.Message, StringComparison.OrdinalIgnoreCase);

        var writes = 0;
        using var unused = Client(_ => { writes++; return new HttpResponseMessage(HttpStatusCode.OK); });
        var noSpace = new ModelDownloads(library, unused) { FreeSpace = _ => 0 };
        var file = new HfFile("model.gguf", 100, new string('a', 64));
        var ex = await Assert.ThrowsAsync<DownloadException>(() => noSpace.DownloadAsync("owner/repo", new string('b', 40), file, null));
        Assert.Contains("free space", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, writes);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ModelDownloadDoesNotTrustSameSizeExistingFileWithoutCheckingItsFingerprint()
    {
        using var temp = new TempFolder();
        var root = Path.Combine(temp.Path, "models");
        var repo = "owner/repo";
        var name = "model-Q4_K_M.gguf";
        var folder = Path.Combine(root, "owner__repo");
        Directory.CreateDirectory(folder);
        var trusted = ValidGguf();
        var changed = (byte[])trusted.Clone();
        var index = Array.IndexOf(changed, (byte)'l');
        Assert.True(index >= 0);
        changed[index] = (byte)'x'; // Still parseable GGUF, with exactly the same length.
        await File.WriteAllBytesAsync(Path.Combine(folder, name), changed);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(trusted));
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(trusted) });
        var library = new ModelLibrary(() => new LlamaSettings { ModelsFolder = root, UseOllamaModels = false }, root);
        var downloads = new ModelDownloads(library, http) { FreeSpace = _ => long.MaxValue };

        var model = await downloads.DownloadAsync(repo, new string('a', 40), new HfFile(name, trusted.Length, sha), null);

        Assert.Equal(trusted, await File.ReadAllBytesAsync(model.Path));
    }

    [Fact]
    public void HardwareFitUsesWindowCacheAndCapsChosenContext()
    {
        var info = new GgufInfo
        {
            Layers = 24, ContextLength = 262144, KvElementsPerToken = 1000,
            KvWindowElementsPerToken = 2000, SlidingWindow = 4096,
        };
        var model = new LocalModel { Id = "m", Name = "m", Path = "m.gguf", Source = ModelSource.Aqua, Info = info, Size = 1L << 30 };
        var gpu = new GpuFacts("large", "nvidia", 24L << 30, 20L << 30, new Version(13, 4));
        var expected = (long)((1000d * 8192 + 2000d * (4096 + 512)) * HardwareFit.BytesPerElement("q8_0"));

        Assert.Equal(expected, HardwareFit.CacheBytes(info, 8192, "q8_0"));
        Assert.Equal(131072, HardwareFit.Context(model, "q8_0", gpu));
        Assert.Equal(FitVerdict.Fits, HardwareFit.Estimate(model, 8192, "q8_0", gpu).Verdict);
    }

    [Fact]
    public void HardwareFitNeverChoosesFourKForAGpuModelWhenTheModelSupportsEightK()
    {
        var info = new GgufInfo { ContextLength = 32768, KvElementsPerToken = 110_000 };
        var model = new LocalModel { Id = "m", Name = "m", Path = "m.gguf", Source = ModelSource.Aqua, Info = info, Size = 2L << 30 };
        var gpu = new GpuFacts("card", "nvidia", 5L << 30, 4L << 30);

        Assert.Equal(FitVerdict.Fits, HardwareFit.Estimate(model, 4096, "q8_0", gpu).Verdict);
        Assert.NotEqual(FitVerdict.Fits, HardwareFit.Estimate(model, 8192, "q8_0", gpu).Verdict);
        Assert.Equal(8192, HardwareFit.Context(model, "q8_0", gpu));
    }

    [Fact]
    public void GemmaSlidingWindowPatternSeparatesGrowingAndBoundedCache()
    {
        using var stream = TinyGguf(
            [("general.architecture", "gemma4"), ("gemma4.block_count", 4L),
             ("gemma4.attention.sliding_window", 4096L), ("gemma4.attention.sliding_window_pattern", new long[] { 1, 1, 1, 0 })],
            [("blk.0.attn_k.weight", 1L, 2L), ("blk.0.attn_v.weight", 1L, 2L),
             ("blk.1.attn_k.weight", 1L, 2L), ("blk.1.attn_v.weight", 1L, 2L),
             ("blk.2.attn_k.weight", 1L, 2L), ("blk.2.attn_v.weight", 1L, 2L),
             ("blk.3.attn_k.weight", 1L, 2L), ("blk.3.attn_v.weight", 1L, 2L)]);

        var info = Gguf.Read(stream);
        var expected = (long)((4d * 8192 + 12d * 4608) * HardwareFit.BytesPerElement("q8_0"));

        Assert.Equal(4, info.KvElementsPerToken);
        Assert.Equal(12, info.KvWindowElementsPerToken);
        Assert.Equal(expected, HardwareFit.CacheBytes(info, 8192, "q8_0"));
    }

    [Fact]
    public void DownloadRulesRejectTraversalAndRecommendationsTrackVramTiers()
    {
        Assert.True(ModelDownloads.SafePath("subfolder/model-Q4_K_M.gguf"));
        Assert.False(ModelDownloads.SafePath("../outside.gguf"));
        Assert.False(ModelDownloads.SafePath("folder\\model.gguf"));
        Assert.False(ModelDownloads.SafePath("C:/outside.gguf"));

        string? MainQuant(long vram) => ModelDownloads.RecommendedFor(new GpuFacts("card", "nvidia", vram, vram))
            .FirstOrDefault(r => r.Role == "main")?.File.Quant;
        Assert.Equal("Q4_K_M", MainQuant(8L << 30));
        Assert.Equal("Q5_K_M", MainQuant(12L << 30));
        Assert.Equal("IQ3_XXS", MainQuant(16L << 30));
        Assert.Equal("IQ3_S", MainQuant(24L << 30));
        Assert.DoesNotContain(ModelDownloads.RecommendedFor(new GpuFacts("card", "nvidia", 12L << 30, 12L << 30)),
            r => r.Role == "background");
    }

    [Fact]
    public void LlamaCatalogSelectsDriverCompatibleBuildAndHonorsPreference()
    {
        Assert.Equal("cuda13", LlamaCatalog.Pick(new GpuFacts("nvidia", "nvidia", 12L << 30, 10L << 30, new Version(13, 4)), Architecture.X64).Backend);
        Assert.Equal("cuda12", LlamaCatalog.Pick(new GpuFacts("nvidia", "nvidia", 12L << 30, 10L << 30, new Version(12, 4)), Architecture.X64).Backend);
        Assert.Equal("vulkan", LlamaCatalog.Pick(new GpuFacts("amd", "amd", 12L << 30, 10L << 30), Architecture.X64).Backend);
        Assert.Equal("cpu", LlamaCatalog.Pick(GpuFacts.None, Architecture.X64).Backend);
        Assert.Same(LlamaCatalog.Cuda13, LlamaCatalog.Pick(GpuFacts.None, Architecture.X64, "cuda13"));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new DelegateHandler(respond));

    private static void WriteManifest(string root, string relative, string digest, long size)
    {
        var path = Path.Combine(root, "manifests", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { layers = new[] { new { mediaType = "application/vnd.ollama.image.model", digest = "sha256:" + digest, size } } }));
    }

    private static byte[] ValidGguf()
    {
        using var stream = TinyGguf([("general.architecture", "llama")], []);
        return stream.ToArray();
    }

    private static MemoryStream TinyGguf((string Key, object Value)[] metadata, (string Name, long Dim0, long Dim1)[] tensors)
    {
        var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(0x46554747u); w.Write(3u); w.Write((ulong)tensors.Length); w.Write((ulong)metadata.Length);
        foreach (var (key, value) in metadata)
        {
            WriteString(w, key);
            switch (value)
            {
                case string s: w.Write(8u); WriteString(w, s); break;
                case long n: w.Write(10u); w.Write((ulong)n); break;
                case long[] values:
                    w.Write(9u); w.Write(10u); w.Write((ulong)values.Length);
                    foreach (var item in values) w.Write((ulong)item);
                    break;
            }
        }
        foreach (var (name, d0, d1) in tensors)
        {
            WriteString(w, name); w.Write(2u); w.Write((ulong)d0); w.Write((ulong)d1); w.Write(0u); w.Write(0UL);
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream HostileGguf(string shape)
    {
        var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(0x46554747u); w.Write(3u);
        w.Write(shape == "too-many-tensors" ? 1_000_001UL : shape is "bad-dimensions-zero" or "bad-dimensions-nine" ? 1UL : 0UL);
        w.Write(shape is "huge-string" or "huge-array" or "truncated" ? 1UL : 0UL);
        if (shape == "huge-string") w.Write(ulong.MaxValue);
        if (shape == "huge-array")
        {
            WriteString(w, "x"); w.Write(9u); w.Write(10u); w.Write(ulong.MaxValue);
        }
        if (shape is "bad-dimensions-zero" or "bad-dimensions-nine")
        {
            WriteString(w, "tensor"); w.Write(shape == "bad-dimensions-zero" ? 0u : 9u);
        }
        if (shape == "truncated")
        {
            WriteString(w, "general.architecture"); w.Write(8u); w.Write(20UL); w.Write((byte)'x');
        }
        stream.Position = 0;
        return stream;
    }

    private static void WriteString(BinaryWriter w, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        w.Write((ulong)bytes.Length); w.Write(bytes);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class CancelAfterChunkStream(CancellationTokenSource cts) : MemoryStream(Encoding.UTF8.GetBytes("0123456789abcdefghij"))
    {
        private int _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads == 2)
            {
                cts.Cancel();
                return ValueTask.FromCanceled<int>(cts.Token);
            }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 4)], cancellationToken);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aquahub-phase4-" + Guid.NewGuid().ToString("N"));
        public TempFolder() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
