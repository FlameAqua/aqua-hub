using AquaHub.Core.Ai.Local;
using AquaHub.Core.Settings;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AquaHub.Tests;

/// <summary>Pure checks for the llama.cpp launch contract and the MTP auto policy.</summary>
public sealed class Phase4RuntimeTests
{
    private static void AssertPair(IReadOnlyList<string> args, string flag, string value)
    {
        var index = Array.IndexOf(args.ToArray(), flag);
        Assert.True(index >= 0, $"Missing argument: {flag}");
        Assert.Equal(value, args[index + 1]);
    }

    private static LocalModel Model(bool mtp = false, bool embedding = false) => new()
    {
        Id = "hf:owner/model/model.gguf",
        Name = "model",
        Source = ModelSource.Aqua,
        Path = @"C:\models\model.gguf",
        Info = new GgufInfo { ContextLength = 32768, HasMtp = mtp, IsEmbedding = embedding },
        Sampling = new Dictionary<string, double> { ["top_k"] = 40, ["top_p"] = 0.9 },
    };

    [Fact]
    public void GpuLaunchKeepsSecretOutOfArgumentsAndBindsToLoopback()
    {
        var launch = LlamaArgs.Build(Model(), LlamaLane.Gpu, new LlamaSettings(), GpuFacts.None, 12345,
            "secret-value", contextTokens: 8192);

        Assert.DoesNotContain("secret-value", launch.Arguments);
        Assert.Equal("secret-value", launch.Environment[LlamaArgs.KeyVariable]);
        Assert.Contains("127.0.0.1", launch.Arguments);
        Assert.Contains("--no-webui", launch.Arguments);
        Assert.Contains("--offline", launch.Arguments);
        Assert.Contains("--no-slots", launch.Arguments);
        Assert.Equal(8192, launch.ContextTokens);
    }

    [Fact]
    public void CpuLaunchDisablesGpuAndUsesInvariantSamplingValues()
    {
        var settings = new LlamaSettings { CpuThreads = 3 };
        var launch = LlamaArgs.Build(Model(), LlamaLane.Cpu, settings, GpuFacts.None, 12345, "key", cpuCores: 12);

        AssertPair(launch.Arguments, "--device", "none");
        AssertPair(launch.Arguments, "-ngl", "0");
        AssertPair(launch.Arguments, "-t", "3");
        AssertPair(launch.Arguments, "--top-k", "40");
        AssertPair(launch.Arguments, "--top-p", "0.9");
    }

    [Fact]
    public void MtpFlagsRequireModelHeadsAndEmbeddingSkipsChatFlags()
    {
        var settings = new LlamaSettings();
        var withMtp = LlamaArgs.Build(Model(mtp: true), LlamaLane.Gpu, settings, GpuFacts.None, 12345, "key", mtpDraft: 2);
        var withoutMtp = LlamaArgs.Build(Model(), LlamaLane.Gpu, settings, GpuFacts.None, 12345, "key", mtpDraft: 2);
        var embedding = LlamaArgs.Build(Model(mtp: true, embedding: true), LlamaLane.Gpu, settings, GpuFacts.None, 12345, "key", mtpDraft: 2);

        AssertPair(withMtp.Arguments, "--spec-type", "draft-mtp");
        AssertPair(withMtp.Arguments, "--spec-draft-n-max", "2");
        Assert.DoesNotContain("--spec-type", withoutMtp.Arguments);
        Assert.Contains("--embeddings", embedding.Arguments);
        Assert.DoesNotContain("--jinja", embedding.Arguments);
        Assert.DoesNotContain("--spec-type", embedding.Arguments);
    }

    [Fact]
    public void AutomaticMtpNeedsThreeSamplesOfEachAndFivePercentGain()
    {
        var book = new SpeedBook(null);
        var model = Model(mtp: true);
        var start = DateTimeOffset.UtcNow;

        Assert.Equal(SpeedBook.Draft, book.Choose(model, "auto", "build", start));
        for (var i = 0; i < 3; i++) book.Record(model, 2, "build", 64, 100, start.AddMinutes(i));
        Assert.Equal(0, book.Choose(model, "auto", "build", start.AddMinutes(4)));
        for (var i = 0; i < 3; i++) book.Record(model, 0, "build", 64, 100, start.AddMinutes(5 + i));
        Assert.Equal(0, book.Choose(model, "auto", "build", start.AddMinutes(9)));

        for (var i = 0; i < 3; i++) book.Record(model, 2, "build", 64, 112, start.AddMinutes(10 + i));
        Assert.Equal(SpeedBook.Draft, book.Choose(model, "auto", "build", start.AddMinutes(14)));
    }

    [Fact]
    public void SpeedBookIgnoresShortInvalidAndStaleBuildSamples()
    {
        var book = new SpeedBook(null);
        var model = Model(mtp: true);
        var now = DateTimeOffset.UtcNow;
        book.Record(model, 0, "b1", 63, 200, now);
        book.Record(model, 0, "b1", 100, double.NaN, now);
        book.Record(model, 0, "b1", 100, 0, now);
        Assert.Equal((0d, 0d), book.Measured(model.Id));

        for (var i = 0; i < 3; i++) book.Record(model, 2, "b1", 64, 120, now.AddMinutes(i));
        Assert.Equal(SpeedBook.Draft, book.Choose(model, "auto", "b2", now.AddMinutes(5)));
        Assert.Equal(0, book.Choose(model, "off", "b1", now.AddMinutes(5)));
    }

    [Fact]
    public async Task RuntimeReusesReadyModelAndStopsOldGpuModelOnSwitch()
    {
        using var fixture = new RuntimeFixture();
        fixture.Host.OutputOnStart = "offloaded 999999999999999999999999/4 layers to GPU";
        var first = Model() with { Id = "model-one", Name = "one" };
        var second = Model() with { Id = "model-two", Name = "two" };

        var endpoint1 = await fixture.Runtime.EnsureAsync(first, LlamaLane.Gpu, 8192, CancellationToken.None);
        var reused = await fixture.Runtime.EnsureAsync(first, LlamaLane.Gpu, 8192, CancellationToken.None);
        Assert.Same(endpoint1, reused);
        Assert.Null(Assert.Single(fixture.Runtime.Instances).GpuLayers);
        Assert.Single(fixture.Host.Starts);

        await fixture.Runtime.EnsureAsync(second, LlamaLane.Gpu, 8192, CancellationToken.None);
        Assert.True(fixture.Host.Starts[0].HasExited);
        Assert.Equal("model-two", Assert.Single(fixture.Runtime.Instances).ModelId);
        Assert.Equal(2, fixture.Host.Starts.Count);
    }

    [Fact]
    public async Task RuntimeAllowsCpuBesideGpuAndGpuUnloadLeavesCpuRunning()
    {
        using var fixture = new RuntimeFixture();
        var gpu = Model() with { Id = "gpu-model", Name = "gpu" };
        var cpu = Model() with { Id = "cpu-model", Name = "cpu" };
        await fixture.Runtime.EnsureAsync(gpu, LlamaLane.Gpu, 4096, CancellationToken.None);
        await fixture.Runtime.EnsureAsync(cpu, LlamaLane.Cpu, 4096, CancellationToken.None);

        Assert.Equal(2, fixture.Runtime.Instances.Count);
        await fixture.Runtime.UnloadGpuAsync("test unload");
        Assert.Equal("cpu-model", Assert.Single(fixture.Runtime.Instances).ModelId);
        Assert.True(fixture.Host.Starts[0].HasExited);
        Assert.False(fixture.Host.Starts[1].HasExited);
    }

    [Fact]
    public async Task RuntimeMarksEarlyServerExitFailedAndDisposeKillsRemainingInstances()
    {
        using (var failed = new RuntimeFixture())
        {
            failed.Host.ExitOnStart = true;
            var ex = await Assert.ThrowsAsync<LlamaRuntimeException>(() =>
                failed.Runtime.EnsureAsync(Model(), LlamaLane.Gpu, 4096, CancellationToken.None));
            Assert.Contains("stopped while loading", ex.Message);
            Assert.Equal(LlamaInstanceState.Failed, Assert.Single(failed.Runtime.Instances).State);
        }

        using var fixture = new RuntimeFixture();
        await fixture.Runtime.EnsureAsync(Model(), LlamaLane.Gpu, 4096, CancellationToken.None);
        await fixture.Runtime.EnsureAsync(Model() with { Id = "cpu", Name = "cpu" }, LlamaLane.Cpu, 4096, CancellationToken.None);
        fixture.Runtime.Dispose();
        Assert.All(fixture.Host.Starts, p => Assert.True(p.HasExited));
        Assert.Empty(fixture.Runtime.Instances);
    }

    [Fact]
    public async Task Qwen35WrongRopeSectionsExplainsReadOnlyOllamaWorkaroundAndDoesNotRelaunch()
    {
        using var fixture = new RuntimeFixture();
        fixture.Host.ExitOnStart = true;
        fixture.Host.OutputOnStart = "llama_model_loader: key qwen35.rope.dimension_sections has wrong array length; expected 4, got 3";
        var model = Model() with { Id = "ollama:qwen3.5:9b", Name = "qwen3.5:9b", Source = ModelSource.Ollama };

        var first = await Assert.ThrowsAsync<LlamaRuntimeException>(() =>
            fixture.Runtime.EnsureAsync(model, LlamaLane.Gpu, 8192, CancellationToken.None));
        var second = await Assert.ThrowsAsync<LlamaRuntimeException>(() =>
            fixture.Runtime.EnsureAsync(model, LlamaLane.Gpu, 8192, CancellationToken.None));

        Assert.Contains("three RoPE sections", first.Message);
        Assert.Contains("leaves Ollama's model file untouched", first.Message);
        Assert.Equal(first.Message, second.Message);
        Assert.Single(fixture.Host.Starts);
        Assert.Equal(LlamaInstanceState.Failed, Assert.Single(fixture.Runtime.Instances).State);
    }

    [Fact]
    public async Task LlamaClientRoutesBackgroundGenerationToGpuAndSendsSchemaThinkingUsageAndTimings()
    {
        using var fixture = new RuntimeFixture(withLibraryModel: true);
        var modelId = Assert.Single(fixture.Library.Models).Id;
        fixture.Settings.Ai.Provider = "llamacpp";
        fixture.Settings.Ai.Model = modelId;
        fixture.Settings.Ai.Think = true;
        fixture.Settings.Ai.Llama.BackgroundModel = modelId;
        fixture.Settings.Ai.Llama.ReasoningEffort = "high";
        using var client = new AquaHub.Core.Ai.LlmClient(() => fixture.Settings.Ai, new InMemorySecretStore()) { Llama = fixture.Runtime };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["answer"] = new JsonObject { ["type"] = "string" } } };
        var request = new AquaHub.Core.Ai.LlmRequest
        {
            Messages = [new AquaHub.Core.Ai.LlmMessage("user", "hello")],
            Schema = schema,
            Priority = AquaHub.Core.Ai.LlmPriority.Background,
            Purpose = "phase4-test",
        };

        var deltas = new List<AquaHub.Core.Ai.LlmDelta>();
        await foreach (var delta in client.ChatStreamAsync(request, reserved: false)) deltas.Add(delta);

        var gpuProcess = Assert.Single(fixture.Host.Starts);
        var call = gpuProcess.LastRequest;
        Assert.NotNull(call);
        Assert.Equal("Bearer", call.AuthorizationScheme);
        Assert.False(string.IsNullOrWhiteSpace(call.AuthorizationParameter));
        Assert.Null(PairValue(call.Arguments, "--device"));
        Assert.Equal("true", call.Body!["chat_template_kwargs"]!["enable_thinking"]!.ToJsonString());
        Assert.Equal("high", call.Body["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("true", call.Body["stream_options"]!["include_usage"]!.ToJsonString());
        Assert.Equal("json_schema", call.Body["response_format"]!["type"]!.GetValue<string>());
        var usage = Assert.IsType<AquaHub.Core.Ai.LlmUsageDelta>(deltas.Last());
        Assert.Equal(64, usage.OutputTokens);
        Assert.Equal(10, usage.PromptTokens);
        Assert.Equal(4096, usage.ContextTokens);
        Assert.Equal(64, usage.Timings!.OutputTokens);
        Assert.Equal(32, usage.Timings.CachedTokens);

        var gpuDeltas = new List<AquaHub.Core.Ai.LlmDelta>();
        await foreach (var delta in client.ChatStreamAsync(request with { Model = modelId, Priority = AquaHub.Core.Ai.LlmPriority.Interactive }, reserved: false))
            gpuDeltas.Add(delta);
        var sameGpuProcess = fixture.Host.Starts.Single(p => PairValue(p.Arguments, "--device") is null);
        Assert.NotNull(sameGpuProcess.LastRequest);
        Assert.Equal("true", sameGpuProcess.LastRequest!.Body!["stream_options"]!["include_usage"]!.ToJsonString());
        Assert.Equal(64, Assert.IsType<AquaHub.Core.Ai.LlmUsageDelta>(gpuDeltas.Last()).OutputTokens);
    }

    [Fact]
    public async Task EditorialUsesConfiguredGpuModelWhileDeepUsesConfiguredDeepModel()
    {
        using var fixture = new RuntimeFixture(withLibraryModel: true);
        var main = Assert.Single(fixture.Library.Models).Id;
        var editor = fixture.AddLibraryModel("editor.gguf");
        var deep = fixture.AddLibraryModel("deep.gguf");
        fixture.Settings.Ai.Provider = "llamacpp";
        fixture.Settings.Ai.Model = main;
        fixture.Settings.Ai.DeepModel = deep;
        fixture.Settings.Ai.Llama.BackgroundModel = editor;
        using var client = new AquaHub.Core.Ai.LlmClient(() => fixture.Settings.Ai, new InMemorySecretStore()) { Llama = fixture.Runtime };
        await client.CheckAsync();
        Assert.Equal(deep, client.Health.DeepModel);

        await foreach (var _ in client.ChatStreamAsync(new AquaHub.Core.Ai.LlmRequest
        {
            Purpose = "story", Editorial = true, Priority = AquaHub.Core.Ai.LlmPriority.Background,
            Messages = [new AquaHub.Core.Ai.LlmMessage("user", "summarise")],
        }, reserved: false)) { }
        Assert.Single(fixture.Host.Starts);
        Assert.Null(PairValue(fixture.Host.Starts[0].Arguments, "--device"));

        await foreach (var _ in client.ChatStreamAsync(new AquaHub.Core.Ai.LlmRequest
        {
            Purpose = "ordinary-background", Priority = AquaHub.Core.Ai.LlmPriority.Background,
            Messages = [new AquaHub.Core.Ai.LlmMessage("user", "background task")],
        }, reserved: false)) { }
        Assert.Equal(2, fixture.Host.Starts.Count);
        Assert.True(fixture.Host.Starts[0].HasExited);
        Assert.Null(PairValue(fixture.Host.Starts[1].Arguments, "--device"));

        await foreach (var _ in client.ChatStreamAsync(new AquaHub.Core.Ai.LlmRequest
        {
            Purpose = "brief", Deep = true, Priority = AquaHub.Core.Ai.LlmPriority.Background,
            Messages = [new AquaHub.Core.Ai.LlmMessage("user", "brief")],
        }, reserved: false)) { }
        Assert.Equal(3, fixture.Host.Starts.Count);
        Assert.True(fixture.Host.Starts[1].HasExited);
        Assert.Null(PairValue(fixture.Host.Starts[2].Arguments, "--device"));

        fixture.Settings.Ai.Llama.BackgroundModel = "";
        await foreach (var _ in client.ChatStreamAsync(new AquaHub.Core.Ai.LlmRequest
        {
            Purpose = "pulse", Editorial = true, Priority = AquaHub.Core.Ai.LlmPriority.Background,
            Messages = [new AquaHub.Core.Ai.LlmMessage("user", "posts")],
        }, reserved: false)) { }
        Assert.Equal(4, fixture.Host.Starts.Count);
        Assert.True(fixture.Host.Starts[2].HasExited);
        Assert.Null(PairValue(fixture.Host.Starts[3].Arguments, "--device"));
    }

    [Fact]
    public async Task ImageReservationUnloadsGpuModelAndKeepsInteractiveGateHeld()
    {
        using var fixture = new RuntimeFixture(withLibraryModel: true);
        var modelId = Assert.Single(fixture.Library.Models).Id;
        fixture.Settings.Ai.Provider = "llamacpp";
        fixture.Settings.Ai.Model = modelId;
        using var client = new AquaHub.Core.Ai.LlmClient(() => fixture.Settings.Ai, new InMemorySecretStore()) { Llama = fixture.Runtime };
        await fixture.Runtime.EnsureAsync(fixture.Library.Find(modelId)!, LlamaLane.Gpu, 4096, CancellationToken.None);

        using (await client.ReserveForImageAsync())
        {
            Assert.Empty(fixture.Runtime.Instances);
            Assert.True(client.IsBusy);
            Assert.False(client.InteractiveWaiting);
            await client.CheckAsync();
            var capabilities = await client.CapabilitiesAsync();
            Assert.Equal(modelId, capabilities.Model);
            Assert.Empty(fixture.Runtime.Instances);
            Assert.Single(fixture.Host.Starts);
        }

        Assert.False(client.IsBusy);
    }

    private static string? PairValue(IReadOnlyList<string> args, string flag)
    {
        var index = Array.IndexOf(args.ToArray(), flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AquaHub-Phase4-" + Guid.NewGuid().ToString("N"));
        public HubSettings Settings { get; } = new();
        public ModelLibrary Library { get; }
        public FakeLlamaProcessHost Host { get; } = new();
        public LlamaRuntime Runtime { get; }

        public RuntimeFixture(bool withLibraryModel = false)
        {
            Directory.CreateDirectory(_folder);
            var server = Path.Combine(_folder, "llama-server.exe");
            File.WriteAllBytes(server, []);
            Settings.Ai.Provider = "llamacpp";
            Settings.Ai.Llama.ServerPath = server;
            Settings.Ai.Llama.UseOllamaModels = false;
            Library = new ModelLibrary(() => Settings.Ai.Llama, Path.Combine(_folder, "models"));
            if (withLibraryModel)
            {
                Directory.CreateDirectory(Library.Folder);
                var modelPath = Path.Combine(Library.Folder, "model.gguf");
                WriteTestGguf(modelPath);
                Library.AddDownload("owner/repo", new string('a', 40), "model.gguf", modelPath, new FileInfo(modelPath).Length,
                    new string('b', 64), null, DateTimeOffset.UtcNow);
            }
            Runtime = new LlamaRuntime(() => Settings, new LlamaSetup(Path.Combine(_folder, "install"), new HttpClient()), Library,
                Host, () => GpuFacts.None, new SpeedBook(null));
        }

        public string AddLibraryModel(string name)
        {
            var modelPath = Path.Combine(Library.Folder, name);
            WriteTestGguf(modelPath);
            var repo = "owner/" + Path.GetFileNameWithoutExtension(name);
            Library.AddDownload(repo, new string('c', 40), name, modelPath, new FileInfo(modelPath).Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name))), null, DateTimeOffset.UtcNow);
            return $"hf:{repo}/{name}";
        }

        public void Dispose()
        {
            Runtime.Dispose();
            Host.Dispose();
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        }

        private static void WriteTestGguf(string path)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            writer.Write(0x46554747u);
            writer.Write(3u);
            writer.Write(0ul); // tensors
            writer.Write(3ul); // metadata entries
            WriteString(writer, "general.architecture"); writer.Write(8u); WriteString(writer, "llama");
            WriteString(writer, "llama.context_length"); writer.Write(4u); writer.Write(4096u);
            WriteString(writer, "tokenizer.chat_template"); writer.Write(8u);
            WriteString(writer, "enable_thinking reasoning_effort 'low' 'medium' 'high'");
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }
    }

    private sealed class FakeLlamaProcessHost : ILlamaProcessHost, IDisposable
    {
        public List<FakeLlamaProcess> Starts { get; } = new();
        public bool ExitOnStart { get; set; }
        public string? OutputOnStart { get; set; }

        public int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public ILlamaProcess Start(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
            string workingDirectory, Action<string> output)
        {
            if (OutputOnStart is { } line) output(line);
            var process = new FakeLlamaProcess(arguments, ExitOnStart);
            Starts.Add(process);
            return process;
        }

        public void Dispose()
        {
            foreach (var process in Starts) process.Dispose();
        }
    }

    private sealed class FakeLlamaProcess : ILlamaProcess, IDisposable
    {
        private readonly TcpListener? _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private int _exited;
        public int Id { get; } = Random.Shared.Next(1, int.MaxValue);
        public bool HasExited => Volatile.Read(ref _exited) != 0;
        public CapturedRequest? LastRequest { get; private set; }
        public IReadOnlyList<string> Arguments { get; }

        public FakeLlamaProcess(IReadOnlyList<string> arguments, bool exitOnStart)
        {
            Arguments = arguments.ToArray();
            if (exitOnStart)
            {
                _listener = null;
                _exited = 1;
                _serve = Task.CompletedTask;
                return;
            }
            var portAt = Array.IndexOf(Arguments.ToArray(), "--port");
            var port = int.Parse(Arguments[portAt + 1], System.Globalization.CultureInfo.InvariantCulture);
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _serve = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener!.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                    using var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, _stop.Token).ConfigureAwait(false);
                    if (request.Path == "/health") await WriteResponseAsync(stream, 200, "ok", "text/plain").ConfigureAwait(false);
                    else if (request.Path == "/v1/chat/completions")
                    {
                        LastRequest = new CapturedRequest(request.Headers.TryGetValue("authorization", out var auth) ? auth.Split(' ', 2).FirstOrDefault() : null,
                            request.Headers.TryGetValue("authorization", out auth) ? auth.Split(' ', 2).Skip(1).FirstOrDefault() : null,
                            JsonNode.Parse(request.Body)!.AsObject(), Arguments);
                        var events = "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n\n"
                            + "data: {\"choices\":[{\"finish_reason\":\"stop\",\"delta\":{}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":64},\"timings\":{\"cache_n\":32,\"prompt_n\":10,\"prompt_ms\":5,\"predicted_n\":64,\"predicted_ms\":1000}}\n\n"
                            + "data: [DONE]\n\n";
                        await WriteResponseAsync(stream, 200, events, "text/event-stream").ConfigureAwait(false);
                    }
                    else await WriteResponseAsync(stream, 404, "not found", "text/plain").ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException or OperationCanceledException) { }
        }

        private sealed record FakeRequest(string Path, Dictionary<string, string> Headers, string Body);

        private static async Task<FakeRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            using var data = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) throw new IOException("Client closed before sending headers");
                data.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(data.GetBuffer(), (int)data.Length);
            }
            var all = data.ToArray();
            var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', 3);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
                if (line.IndexOf(':') is var colon && colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            var contentLength = headers.TryGetValue("content-length", out var length) ? int.Parse(length, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var bodyAt = headerEnd + 4;
            while (data.Length - bodyAt < contentLength)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) throw new IOException("Client closed before sending body");
                data.Write(buffer, 0, read);
            }
            all = data.ToArray();
            return new FakeRequest(requestLine[1], headers, Encoding.UTF8.GetString(all, bodyAt, contentLength));
        }

        private static int FindHeaderEnd(byte[] bytes, int length)
        {
            for (var i = 0; i <= length - 4; i++)
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
            return -1;
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int status, string value, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header).ConfigureAwait(false);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
        }

        public void Kill()
        {
            if (Interlocked.Exchange(ref _exited, 1) == 0)
            {
                _stop.Cancel();
                _listener?.Stop();
            }
        }

        public void Dispose()
        {
            Kill();
            _stop.Dispose();
        }
    }

    private sealed record CapturedRequest(string? AuthorizationScheme, string? AuthorizationParameter, JsonObject? Body, IReadOnlyList<string> Arguments);
}
