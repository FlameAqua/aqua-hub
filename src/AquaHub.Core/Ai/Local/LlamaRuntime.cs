using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Local;

/// <summary>A program the runtime started (a model's server); disposing it ends it.</summary>
public interface ILlamaProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    void Kill();
}

/// <summary>
/// Starts processes for the runtime. The app's host ties every server to Aqua (a Windows job object), so none outlives it,
/// and starts it with no inherited LLAMA_* or GGML_* settings, only the environment it's given.
/// </summary>
public interface ILlamaProcessHost
{
    /// <summary>Starts <paramref name="program"/> hidden; each line it writes (stdout and stderr) goes to <paramref name="output"/>.</summary>
    ILlamaProcess Start(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string workingDirectory, Action<string> output);
    /// <summary>A free loopback port.</summary>
    int FreePort();
}

/// <summary>Where a request goes: one model's server, its key, and what it was started with.</summary>
public sealed record LlamaEndpoint(Uri BaseUri, string Key, string Alias, int ContextTokens, LocalModel Model, LlamaLane Lane, int MtpDraft);

public enum LlamaInstanceState { Starting, Ready, Failed, Stopped }

/// <summary>One model's server as the UI shows it.</summary>
public sealed record LlamaInstanceInfo(
    string ModelId,
    string ModelName,
    LlamaLane Lane,
    LlamaInstanceState State,
    int ContextTokens,
    int MtpDraft,
    int? GpuLayers,
    int? TotalLayers,
    string? Error,
    DateTimeOffset Started)
{
    /// <summary>Some layers run on the processor (slower): llama.cpp moved them there to fit.</summary>
    public bool PartlyOnCpu => Lane == LlamaLane.Gpu && GpuLayers is int g && TotalLayers is int t && g < t;
}

/// <summary>Why a model's server couldn't be started or reached, in plain words.</summary>
public sealed class LlamaRuntimeException(string message) : Exception(message);

/// <summary>
/// Aqua's own model runtime: one llama-server process per loaded model, each listening only on 127.0.0.1 with a fresh
/// random key, so another program — or a web page reaching for localhost — can't use it. Only one model is on the graphics
/// card at a time: starting another stops the one there. Background and search models run on the processor beside it.
/// A model that goes unused gives its memory back (--sleep-idle-seconds) and reloads on the next request. llama.cpp's
/// output is read for what Aqua needs (load progress, layers on the card, errors) and never written to Aqua's log
/// wholesale; the last lines are kept in memory for a failure's details.
/// </summary>
public sealed partial class LlamaRuntime : IDisposable
{
    private readonly Func<HubSettings> _settings;
    private readonly LlamaSetup _setup;
    private readonly ILlamaProcessHost _host;
    private readonly Func<GpuFacts> _gpu;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);

    public LlamaRuntime(Func<HubSettings> settings, LlamaSetup setup, ModelLibrary library, ILlamaProcessHost host, Func<GpuFacts> gpu, SpeedBook speed)
    {
        _settings = settings;
        _setup = setup;
        Library = library;
        _host = host;
        _gpu = gpu;
        Speed = speed;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public ModelLibrary Library { get; }
    public SpeedBook Speed { get; }
    /// <summary>The graphics card as the app reads it now.</summary>
    public GpuFacts Gpu => _gpu();
    /// <summary>A server started or stopped, became ready or failed (on the thread that did it).</summary>
    public event Action? Changed;

    /// <summary>The server program: Settings' own llama-server.exe when it's set and there, else Aqua's install (null: none).</summary>
    public string? ServerPath
    {
        get
        {
            var custom = _settings().Ai.Llama.ServerPath;
            if (custom.Length > 0) return File.Exists(custom) ? custom : null;
            return _setup.Installed()?.Server;
        }
    }

    /// <summary>The build models run on ("b11387", or "custom" for Settings' own program).</summary>
    public string Build => _settings().Ai.Llama.ServerPath.Length > 0 ? "custom" : _setup.Installed()?.Build ?? "";

    /// <summary>A server program is there to run models with.</summary>
    public bool Ready => ServerPath is not null;

    public IReadOnlyList<LlamaInstanceInfo> Instances
    {
        get { lock (_instances) return _instances.Values.Select(i => i.Info()).ToList(); }
    }

    /// <summary>The model on the graphics card now (started or starting), or null.</summary>
    public LlamaEndpoint? OnGpu
    {
        get { lock (_instances) return _instances.Values.FirstOrDefault(i => i.Lane == LlamaLane.Gpu && i.State is LlamaInstanceState.Ready && !i.Process.HasExited)?.Endpoint; }
    }

    /// <summary>
    /// The server for <paramref name="model"/> on <paramref name="lane"/>, started when it isn't running (or was started with
    /// a smaller context than <paramref name="contextTokens"/>). On the graphics card, whatever model was there is stopped
    /// first. Waits until the model has loaded.
    /// </summary>
    public async Task<LlamaEndpoint> EnsureAsync(LocalModel model, LlamaLane lane, int? contextTokens, CancellationToken ct)
    {
        lock (_instances)
            if (Reusable(model, lane, contextTokens) is { } ready) return ready;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_instances)
                if (Reusable(model, lane, contextTokens) is { } ready) return ready;
            var server = ServerPath ?? throw new LlamaRuntimeException("Aqua's model runtime isn't installed — install it in Settings › AI & models.");
            List<Instance> stopping;
            lock (_instances)
                stopping = _instances.Values.Where(i => i.ModelId == model.Id || (lane == LlamaLane.Gpu && i.Lane == LlamaLane.Gpu)).ToList();
            foreach (var old in stopping) Stop(old, old.ModelId == model.Id ? "to restart it" : "to make room for " + model.Name);
            return await StartAsync(server, model, lane, contextTokens, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private LlamaEndpoint? Reusable(LocalModel model, LlamaLane lane, int? contextTokens) =>
        _instances.TryGetValue(model.Id, out var i) && i.Lane == lane && i.State == LlamaInstanceState.Ready && !i.Process.HasExited
        && i.Path == model.Path && (contextTokens is not > 0 || i.Context >= contextTokens) ? i.Endpoint : null;

    private async Task<LlamaEndpoint> StartAsync(string server, LocalModel model, LlamaLane lane, int? contextTokens, CancellationToken ct)
    {
        var s = _settings();
        var gpu = lane == LlamaLane.Gpu ? _gpu() : GpuFacts.None;
        var mtp = lane == LlamaLane.Gpu ? Speed.Choose(model, s.Ai.Llama.Mtp, Build, DateTimeOffset.Now) : 0;
        var port = _host.FreePort();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var launch = LlamaArgs.Build(model, lane, s.Ai.Llama, gpu, port, key, contextTokens, mtp);
        var endpoint = new LlamaEndpoint(new Uri($"http://127.0.0.1:{port}/"), key, launch.Alias, launch.ContextTokens, model, lane, launch.MtpDraft);
        var instance = new Instance(model, lane, endpoint);
        Log.Info("llama", $"Starting {model.Name} on the {(lane == LlamaLane.Gpu ? "graphics card" : "processor")} ({launch.ContextTokens / 1024}K context{(launch.MtpDraft > 0 ? ", MTP" : "")})");
        try
        {
            instance.Process = _host.Start(server, launch.Arguments, launch.Environment, Path.GetDirectoryName(server)!, instance.Output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("llama", "Couldn't start llama-server", ex);
            throw new LlamaRuntimeException("Aqua's model runtime couldn't be started: " + ex.Message);
        }
        lock (_instances) _instances[model.Id] = instance;
        Changed?.Invoke();

        // A big model takes a while to read from disk the first time: 2 minutes, plus 30 s for each GB.
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(120 + 30 * model.Bytes / 1_000_000_000.0);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (instance.Process.HasExited) throw new LlamaRuntimeException(instance.Failure() ?? "The model's server stopped while loading it.");
                if (await HealthyAsync(endpoint, ct).ConfigureAwait(false)) break;
                if (DateTimeOffset.Now > deadline) throw new LlamaRuntimeException("The model took too long to load.");
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            instance.State = LlamaInstanceState.Ready;
            Log.Info("llama", $"{model.Name} is ready" + (instance.Info().PartlyOnCpu ? $" ({instance.GpuLayers} of {instance.TotalLayers} layers on the graphics card)" : ""));
            Changed?.Invoke();
            return endpoint;
        }
        catch (Exception ex)
        {
            instance.State = LlamaInstanceState.Failed;
            instance.Error = ex is LlamaRuntimeException ? ex.Message : ex is OperationCanceledException ? "Cancelled." : ex.Message;
            KillQuietly(instance);
            Log.Warn("llama", $"{model.Name} didn't start: {instance.Error}" + (instance.Tail() is { Length: > 0 } tail ? "\n" + tail : ""));
            Changed?.Invoke();
            if (ex is OperationCanceledException) throw;
            throw ex as LlamaRuntimeException ?? new LlamaRuntimeException(instance.Error);
        }
    }

    private async Task<bool> HealthyAsync(LlamaEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(endpoint.BaseUri, "health"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Asks a model's server what it's doing (sleeping after being idle, the context it holds), with its key; null when it
    /// doesn't answer.
    /// </summary>
    public async Task<(bool Sleeping, int Context)?> PropsAsync(LlamaEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.BaseUri, "props"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Key);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var ctx = doc.RootElement.TryProp("default_generation_settings", out var g) ? (int)(g.Lng("n_ctx") ?? 0) : 0;
            return (doc.RootElement.Bool("is_sleeping") == true, ctx);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Stops one model's server (null: all of them), e.g. to free the card for a game.</summary>
    public async Task UnloadAsync(string? modelId, string why, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Instance> stopping;
            lock (_instances) stopping = _instances.Values.Where(i => modelId is null || i.ModelId == modelId).ToList();
            foreach (var i in stopping) Stop(i, why);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stops the graphics card's model (for a game or another app that needs the card); the next request starts it again.</summary>
    public Task UnloadGpuAsync(string why, CancellationToken ct = default)
    {
        string? id;
        lock (_instances) id = _instances.Values.FirstOrDefault(i => i.Lane == LlamaLane.Gpu)?.ModelId;
        return id is null ? Task.CompletedTask : UnloadAsync(id, why, ct);
    }

    /// <summary>An answer's writing speed, for MTP's "auto" choice.</summary>
    public void Record(LlamaEndpoint endpoint, int tokens, double tokensPerSecond) =>
        Speed.Record(endpoint.Model, endpoint.MtpDraft, Build, tokens, tokensPerSecond, DateTimeOffset.Now);

    private void Stop(Instance instance, string why)
    {
        lock (_instances)
            if (_instances.TryGetValue(instance.ModelId, out var current) && current == instance) _instances.Remove(instance.ModelId);
        KillQuietly(instance);
        instance.State = LlamaInstanceState.Stopped;
        Log.Info("llama", $"Stopped {instance.Name} {why}");
        Changed?.Invoke();
    }

    private static void KillQuietly(Instance instance)
    {
        try
        {
            if (instance.ProcessOrNull is { } p)
            {
                if (!p.HasExited) p.Kill();
                p.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Log.Debug("llama", "stop: " + ex.Message); }
    }

    /// <summary>Stops every server (Aqua is quitting).</summary>
    public void Dispose()
    {
        List<Instance> all;
        lock (_instances)
        {
            all = _instances.Values.ToList();
            _instances.Clear();
        }
        foreach (var i in all) KillQuietly(i);
        _http.Dispose();
    }

    [GeneratedRegex(@"offloaded (\d+)/(\d+) layers to GPU")]
    private static partial Regex OffloadedRx();

    /// <summary>A line of llama.cpp's output that says why a model didn't load, turned into plain words.</summary>
    internal static string? Reason(string line)
    {
        var l = line.ToLowerInvariant();
        if (l.Contains("out of memory") || l.Contains("failed to allocate") || l.Contains("cudamalloc failed"))
            return "There wasn't enough memory to load it — close other programs using the graphics card, or pick a smaller model.";
        if (l.Contains("unknown model architecture"))
            return "This version of llama.cpp doesn't know this kind of model yet" + (ArchRx().Match(line) is { Success: true } m ? $" ('{m.Groups[1].Value}')" : "") + ".";
        if (l.Contains("failed to load model") || l.Contains("error loading model")) return "llama.cpp couldn't load the model file.";
        if (l.Contains("couldn't bind") || l.Contains("address already in use")) return "The port Aqua picked was taken — try again.";
        return null;
    }

    [GeneratedRegex(@"architecture:?\s*'?([A-Za-z0-9_.-]+)")]
    private static partial Regex ArchRx();

    private sealed class Instance(LocalModel model, LlamaLane lane, LlamaEndpoint endpoint)
    {
        private readonly Queue<string> _tail = new();
        public string ModelId => model.Id;
        public string Name => model.Name;
        public string Path => model.Path;
        public LlamaLane Lane => lane;
        public LlamaEndpoint Endpoint => endpoint;
        public int Context => endpoint.ContextTokens;
        public ILlamaProcess? ProcessOrNull { get; private set; }
        public ILlamaProcess Process { get => ProcessOrNull!; set => ProcessOrNull = value; }
        public LlamaInstanceState State { get; set; } = LlamaInstanceState.Starting;
        public string? Error { get; set; }
        public int? GpuLayers { get; private set; }
        public int? TotalLayers { get; private set; }
        private string? _reason;
        public DateTimeOffset Started { get; } = DateTimeOffset.Now;

        public void Output(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (_tail)
            {
                _tail.Enqueue(line.Length > 400 ? line[..400] : line);
                while (_tail.Count > 60) _tail.Dequeue();
            }
            if (OffloadedRx().Match(line) is { Success: true } m)
            {
                GpuLayers = int.Parse(m.Groups[1].ValueSpan);
                TotalLayers = int.Parse(m.Groups[2].ValueSpan);
            }
            if (Reason(line) is { } reason) _reason ??= reason;
        }

        /// <summary>Why it failed, from its output, or null.</summary>
        public string? Failure() => _reason;

        /// <summary>Its last lines of output (for the log when it fails to start).</summary>
        public string Tail()
        {
            lock (_tail) return string.Join('\n', _tail.TakeLast(12));
        }

        public LlamaInstanceInfo Info() => new(model.Id, model.Name, lane, State, endpoint.ContextTokens, endpoint.MtpDraft, GpuLayers, TotalLayers, Error, Started);
    }
}
