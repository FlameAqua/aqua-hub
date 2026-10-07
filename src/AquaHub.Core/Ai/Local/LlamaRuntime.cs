using System.Collections.Concurrent;
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
    /// <summary>The program it runs on, for people ("llama.cpp b11387 · cuda13", "Ollama's engine").</summary>
    public string? Server { get; init; }
    /// <summary>Which kind of program that is.</summary>
    public LlamaServerKind ServerKind { get; init; }
    /// <summary>A failed start's kind: a file the server can't read is never retried until something changes.</summary>
    public LlamaFailureKind Failure { get; init; }
}

/// <summary>Why a model's server couldn't be started or reached, in plain words.</summary>
public sealed class LlamaRuntimeException(string message) : Exception(message);

/// <summary>
/// Aqua's own model runtime: one llama-server process per loaded model, each listening only on 127.0.0.1 with a fresh
/// random key, so another program — or a web page reaching for localhost — can't use it. Only one model is on the graphics
/// card at a time: starting another stops the one there. Embedding models run on the processor beside it.
/// <para>
/// Most models run on Aqua's pinned llama.cpp build. Ollama converts some models itself, in a form only its own engine
/// reads (Qwen3.5's three M-RoPE sections, where llama.cpp requires four); those run on the llama-server that comes with
/// Ollama, when Ollama is installed, and its servers are stopped after the idle time so they never hold Ollama's files
/// while it updates. Every start is written to the compatibility record (<see cref="Compat"/>): a file a server can't read
/// is never started on it again until the file or the server changes, and other failures make background work wait (1, 5,
/// then 15 minutes) instead of starting the same server every minute. Asking something always tries again.
/// </para>
/// A model that goes unused gives its memory back (--sleep-idle-seconds) and reloads on the next request. llama.cpp's
/// output is read for what Aqua needs (load progress, layers and memory on the card, errors) and never written to Aqua's log
/// wholesale; the last lines are kept in memory for a failure's details.
/// </summary>
public sealed partial class LlamaRuntime : IDisposable
{
    private readonly Func<HubSettings> _settings;
    private readonly LlamaSetup _setup;
    private readonly ILlamaProcessHost _host;
    private readonly Func<GpuFacts> _gpu;
    private readonly Func<string?>? _ollamaEngine;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Failures, DateTimeOffset Until, string Error)> _backoff = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _versions = new(StringComparer.Ordinal);

    public LlamaRuntime(Func<HubSettings> settings, LlamaSetup setup, ModelLibrary library, ILlamaProcessHost host, Func<GpuFacts> gpu, SpeedBook speed,
        CompatBook? compat = null, Func<string?>? ollamaEngine = null)
    {
        _settings = settings;
        _setup = setup;
        Library = library;
        _host = host;
        _gpu = gpu;
        Speed = speed;
        Compat = compat ?? new CompatBook();
        _ollamaEngine = ollamaEngine;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public ModelLibrary Library { get; }
    public SpeedBook Speed { get; }
    /// <summary>Each model file on each server program it was started with: whether it loaded, how, and how fast it ran.</summary>
    public CompatBook Compat { get; }
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

    /// <summary>The llama-server that comes with Ollama, when Ollama is installed and Settings let Aqua use it (null: not used).</summary>
    public string? OllamaEngine => _settings().Ai.Llama.UseOllamaEngine && _settings().Ai.Llama.ServerPath.Length == 0 ? _ollamaEngine?.Invoke() : null;

    /// <summary>The build models run on ("b11387", or "custom" for Settings' own program).</summary>
    public string Build => _settings().Ai.Llama.ServerPath.Length > 0 ? "custom" : _setup.Installed()?.Build ?? "";

    /// <summary>A server program is there to run models with: Aqua's, Settings' own, or Ollama's engine for Ollama's models.</summary>
    public bool Ready => ServerPath is not null || OllamaEngine is not null;

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
    /// The program that would run <paramref name="model"/>: Settings' own server when one is set; else Aqua's build, unless
    /// it's one of Ollama's models that Aqua's build can't read (or Aqua's isn't installed) and Ollama's engine is there.
    /// Null when nothing on this PC can run it.
    /// </summary>
    public LlamaServerChoice? ServerFor(LocalModel model)
    {
        var custom = _settings().Ai.Llama.ServerPath;
        if (custom.Length > 0) return File.Exists(custom) ? new LlamaServerChoice(custom, LlamaServerKind.Custom, "your llama-server") : null;
        var install = _setup.Installed();
        var aqua = install is null ? null : new LlamaServerChoice(install.Server, LlamaServerKind.Aqua, $"llama.cpp {install.Build} · {install.Backend}");
        var engine = OllamaEngine is { } path ? new LlamaServerChoice(path, LlamaServerKind.Ollama, "Ollama's engine" + VersionSuffix(path, LlamaServerKind.Ollama)) : null;
        if (model.Source == ModelSource.Ollama && engine is not null && (aqua is null || Blocked(model, aqua) is not null))
            return engine with
            {
                Note = aqua is null ? "Aqua's runtime isn't installed, so Ollama's own engine runs Ollama's models."
                    : "llama.cpp can't read this copy (Ollama converted it for its own engine), so Ollama's engine runs it.",
            };
        return aqua ?? (engine is null ? null : engine with { Note = "Aqua's runtime isn't installed, so Ollama's engine runs this model." });
    }

    /// <summary>Something on this PC can run <paramref name="model"/> (as far as is known before trying).</summary>
    public bool CanRun(LocalModel model) => ServerFor(model) is { } choice && Blocked(model, choice) is null;

    /// <summary>Why <paramref name="model"/> can't run here, in plain words (null: nothing known stops it).</summary>
    public string? Problem(LocalModel? model)
    {
        if (model is null) return Ready ? null : NotInstalled;
        if (ServerFor(model) is not { } choice) return NotInstalled;
        return Blocked(model, choice) is { } blocked ? blocked + Advice(model) : null;
    }

    private const string NotInstalled = "Aqua's model runtime isn't installed — install it in Settings › AI & models.";

    /// <summary>Why this server won't load this file: a known rule for Aqua's build, or an earlier start that couldn't read it.</summary>
    private string? Blocked(LocalModel model, LlamaServerChoice choice)
    {
        if (choice.Kind == LlamaServerKind.Aqua && LlamaCompat.UpstreamProblem(model.Info) is { } known) return known;
        return Compat.Get(ModelKey(model), ServerKey(choice)) is { Failure: LlamaFailureKind.Format } e
            ? e.Error ?? "This model file can't be read by " + choice.Label + "."
            : null;
    }

    private string Advice(LocalModel model) =>
        model.Source != ModelSource.Ollama ? ""
        : !_settings().Ai.Llama.UseOllamaEngine ? " Turn on “Run Ollama's own models on Ollama's engine”, or download a llama.cpp copy of the model from the Models page."
        : _ollamaEngine?.Invoke() is null ? " Ollama's engine isn't on this PC: use the model through Ollama, or download a llama.cpp copy from the Models page."
        : "";

    /// <summary>
    /// The server for <paramref name="model"/> on <paramref name="lane"/>, started when it isn't running (or was started with
    /// a smaller context than <paramref name="contextTokens"/>). On the graphics card, whatever model was there is stopped
    /// first. Waits until the model has loaded. <paramref name="background"/> work doesn't start a server that failed a
    /// short while ago (it waits 1, 5, then 15 minutes); a file the server can't read fails at once, every time.
    /// </summary>
    public async Task<LlamaEndpoint> EnsureAsync(LocalModel model, LlamaLane lane, int? contextTokens, CancellationToken ct, bool background = false)
    {
        lock (_instances)
            if (Reusable(model, lane, contextTokens) is { } ready) return ready;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_instances)
                if (Reusable(model, lane, contextTokens) is { } ready) return ready;
            var choice = ServerFor(model) ?? throw new LlamaRuntimeException(NotInstalled);
            if (Blocked(model, choice) is { } blocked)
            {
                // Known not to load: keep it visible as failed (with Clear failure) instead of starting a doomed server.
                var reason = blocked + Advice(model);
                lock (_instances)
                    if (!_instances.TryGetValue(model.Id, out var shown) || shown.State != LlamaInstanceState.Failed || shown.Error != reason)
                        _instances[model.Id] = Instance.FailedBeforeStart(model, lane, choice, ModelKey(model), ServerKey(choice), reason);
                Changed?.Invoke();
                throw new LlamaRuntimeException(reason);
            }
            if (background)
                lock (_backoff)
                    if (_backoff.TryGetValue(model.Id, out var wait) && wait.Until > DateTimeOffset.Now)
                        throw new LlamaRuntimeException($"{model.Name} couldn't start ({wait.Error.TrimEnd('.')}); Aqua tries again at {wait.Until.ToLocalTime():HH:mm}.");
            StopFor(model, lane);
            try
            {
                return await StartAsync(choice, model, lane, contextTokens, ct).ConfigureAwait(false);
            }
            catch (LlamaRuntimeException) when (choice.Kind != LlamaServerKind.Ollama && model.Source == ModelSource.Ollama
                                                && LastFailure(model.Id) == LlamaFailureKind.Format && OllamaEngine is { } engine)
            {
                // Aqua's build couldn't read one of Ollama's files: Ollama's own engine reads what Ollama wrote.
                Log.Info("llama", $"llama.cpp couldn't read {model.Name}; starting it on Ollama's engine instead");
                var fallback = new LlamaServerChoice(engine, LlamaServerKind.Ollama, "Ollama's engine" + VersionSuffix(engine, LlamaServerKind.Ollama),
                    "llama.cpp can't read this copy (Ollama converted it for its own engine), so Ollama's engine runs it.");
                return await StartAsync(fallback, model, lane, contextTokens, ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private LlamaEndpoint? Reusable(LocalModel model, LlamaLane lane, int? contextTokens)
    {
        if (!_instances.TryGetValue(model.Id, out var i) || i.Lane != lane || i.State != LlamaInstanceState.Ready || i.Process.HasExited
            || i.Path != model.Path || (contextTokens is > 0 && i.Context < contextTokens))
            return null;
        i.Touch();
        return i.Endpoint;
    }

    private void StopFor(LocalModel model, LlamaLane lane)
    {
        List<Instance> stopping;
        lock (_instances)
            stopping = _instances.Values.Where(i => i.ModelId == model.Id || (lane == LlamaLane.Gpu && i.Lane == LlamaLane.Gpu && i.State != LlamaInstanceState.Failed)).ToList();
        foreach (var old in stopping) Stop(old, old.ModelId == model.Id ? "to restart it" : "to make room for " + model.Name);
    }

    private LlamaFailureKind LastFailure(string modelId)
    {
        lock (_instances) return _instances.TryGetValue(modelId, out var i) && i.State == LlamaInstanceState.Failed ? i.FailureKind : LlamaFailureKind.None;
    }

    private async Task<LlamaEndpoint> StartAsync(LlamaServerChoice choice, LocalModel model, LlamaLane lane, int? contextTokens, CancellationToken ct)
    {
        var s = _settings();
        var gpu = lane == LlamaLane.Gpu ? _gpu() : GpuFacts.None;
        var speedBuild = choice.Kind switch { LlamaServerKind.Aqua => Build, LlamaServerKind.Ollama => "ollama", _ => "custom" };
        var mtp = lane == LlamaLane.Gpu ? Speed.Choose(model, s.Ai.Llama.Mtp, speedBuild, DateTimeOffset.Now) : 0;
        var port = _host.FreePort();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var launch = LlamaArgs.Build(model, lane, s.Ai.Llama, gpu, port, key, contextTokens, mtp);
        var endpoint = new LlamaEndpoint(new Uri($"http://127.0.0.1:{port}/"), key, launch.Alias, launch.ContextTokens, model, lane, launch.MtpDraft);
        var instance = new Instance(model, lane, endpoint, choice, ModelKey(model), ServerKey(choice), speedBuild);
        // Ollama's engine changes with Ollama's updates: its version goes into the compatibility record.
        if (choice.Kind == LlamaServerKind.Ollama) ProbeVersion(choice.Path, choice.Kind);
        Log.Info("llama", $"Starting {model.Name} on the {(lane == LlamaLane.Gpu ? "graphics card" : "processor")} with {choice.Label} ({launch.ContextTokens / 1024}K context{(launch.MtpDraft > 0 ? ", MTP" : "")})");
        var started = DateTimeOffset.Now;
        try
        {
            instance.Process = _host.Start(choice.Path, launch.Arguments, launch.Environment, Path.GetDirectoryName(choice.Path)!, instance.Output);
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
            lock (_backoff) _backoff.Remove(model.Id);
            Compat.Put(instance.Entry("ok", null, s.Ai.Llama.CacheType, DateTimeOffset.Now - started, gpu));
            Log.Info("llama", $"{model.Name} is ready in {(DateTimeOffset.Now - started).TotalSeconds:0.0} s"
                              + (instance.Info().PartlyOnCpu ? $" ({instance.GpuLayers} of {instance.TotalLayers} layers on the graphics card)" : "")
                              + (instance.GpuMiB > 0 ? $", {instance.GpuMiB:0} MiB on the card" : ""));
            Changed?.Invoke();
            return endpoint;
        }
        catch (Exception ex)
        {
            instance.State = LlamaInstanceState.Failed;
            instance.Error = ex is LlamaRuntimeException ? ex.Message : ex is OperationCanceledException ? "Cancelled." : ex.Message;
            KillQuietly(instance);
            if (ex is not OperationCanceledException)
            {
                var kind = instance.FailureKind;
                // An unreadable file is always recorded; a passing failure (memory a game held, a crash) doesn't erase a record of it loading fine.
                if (kind == LlamaFailureKind.Format || Compat.Get(instance.ModelKey, instance.ServerKey) is not { Ok: true })
                    Compat.Put(instance.Entry(kind switch { LlamaFailureKind.Format => "format", LlamaFailureKind.Memory => "memory", _ => "failed" },
                        instance.Error, s.Ai.Llama.CacheType, DateTimeOffset.Now - started, gpu));
                // People see the reason and what to do about it, the same on the first failure as on every fast one after it.
                if (kind == LlamaFailureKind.Format) instance.Error += Advice(model);
                if (kind != LlamaFailureKind.Format)
                    lock (_backoff)
                    {
                        var failures = (_backoff.TryGetValue(model.Id, out var b) ? b.Failures : 0) + 1;
                        var wait = TimeSpan.FromMinutes(failures switch { 1 => 1, 2 => 5, _ => 15 });
                        _backoff[model.Id] = (failures, DateTimeOffset.Now + wait, instance.Error);
                    }
            }
            Log.Warn("llama", $"{model.Name} didn't start on {choice.Label}: {instance.Error}" + (instance.Tail() is { Length: > 0 } tail ? "\n" + tail : ""));
            Changed?.Invoke();
            if (ex is OperationCanceledException) throw;
            throw new LlamaRuntimeException(instance.Error);
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

    /// <summary>
    /// Stops one model's server (null: all of them), e.g. to free the card for a game. Naming a model also forgets its
    /// failures, so the next request is a fresh try (Models › Clear failure).
    /// </summary>
    public async Task UnloadAsync(string? modelId, string why, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Instance> stopping;
            lock (_instances) stopping = _instances.Values.Where(i => modelId is null || i.ModelId == modelId).ToList();
            foreach (var i in stopping) Stop(i, why);
            if (modelId is not null) ForgetFailures(modelId);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Forgets a model's failed starts (recorded and waiting), so the next request tries again.</summary>
    public void ForgetFailures(string modelId)
    {
        lock (_backoff) _backoff.Remove(modelId);
        Compat.ForgetFailures(modelId);
        bool removed;
        lock (_instances)
            removed = _instances.TryGetValue(modelId, out var i) && i.State == LlamaInstanceState.Failed && _instances.Remove(modelId);
        if (removed) Changed?.Invoke();
    }

    /// <summary>Stops the graphics card's model (for a game or another app that needs the card); the next request starts it again.</summary>
    public Task UnloadGpuAsync(string why, CancellationToken ct = default)
    {
        string? id;
        lock (_instances) id = _instances.Values.FirstOrDefault(i => i.Lane == LlamaLane.Gpu && i.State != LlamaInstanceState.Failed)?.ModelId;
        return id is null ? Task.CompletedTask : StopRunningAsync(id, why, ct);
    }

    private async Task StopRunningAsync(string modelId, string why, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Instance? running;
            lock (_instances) running = _instances.GetValueOrDefault(modelId);
            if (running is not null && running.State != LlamaInstanceState.Failed) Stop(running, why);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Stops Ollama's engine's servers unused for <paramref name="idle"/>: all their memory goes back, and Ollama's files
    /// aren't held open when Ollama updates itself. Skipped while a model is loading.
    /// </summary>
    public async Task StopIdleAsync(TimeSpan idle)
    {
        if (idle <= TimeSpan.Zero || !await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            List<Instance> idleOnes;
            lock (_instances)
                idleOnes = _instances.Values.Where(i => i.Kind == LlamaServerKind.Ollama && i.State == LlamaInstanceState.Ready
                                                        && DateTimeOffset.Now - i.LastUsed > idle).ToList();
            foreach (var i in idleOnes) Stop(i, $"after {idle.TotalMinutes:0} minutes unused");
        }
        finally { _gate.Release(); }
    }

    /// <summary>An answer's writing speed, for MTP's "auto" choice.</summary>
    public void Record(LlamaEndpoint endpoint, int tokens, double tokensPerSecond)
    {
        Instance? i;
        lock (_instances) i = _instances.GetValueOrDefault(endpoint.Model.Id);
        var current = i is not null && ReferenceEquals(i.Endpoint, endpoint) ? i : null;
        current?.Touch();
        Speed.Record(endpoint.Model, endpoint.MtpDraft, current?.SpeedBuild ?? Build, tokens, tokensPerSecond, DateTimeOffset.Now);
    }

    /// <summary>An answer's timings: MTP's choice, and the compatibility record's prompt-reading and writing speeds.</summary>
    public void Record(LlamaEndpoint endpoint, LlmTimings timings)
    {
        Record(endpoint, timings.OutputTokens, timings.OutputPerSecond);
        Instance? i;
        lock (_instances) i = _instances.GetValueOrDefault(endpoint.Model.Id);
        if (i is null || !ReferenceEquals(i.Endpoint, endpoint)) return;
        var prompt = timings.PromptTokens >= 64 ? timings.PromptPerSecond : 0;
        var output = timings.OutputTokens >= 32 ? timings.OutputPerSecond : 0;
        if (prompt > 0 || output > 0) Compat.AddSpeed(i.ModelKey, i.ServerKey, prompt, output);
    }

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
        Compat.Flush();
        _http.Dispose();
    }

    // ───────────── Server versions (Ollama's engine and custom servers say theirs on request) ─────────────

    private string VersionSuffix(string path, LlamaServerKind kind) =>
        _versions.TryGetValue(ServerKey(path, kind), out var v) && v.Length > 0 ? $" ({v})" : "";

    /// <summary>Asks a server program for its version once (in the background); the label shows it from then on.</summary>
    private void ProbeVersion(string path, LlamaServerKind kind)
    {
        var key = ServerKey(path, kind);
        if (!_versions.TryAdd(key, "")) return;
        _ = Task.Run(async () =>
        {
            var lines = new List<string>();
            try
            {
                using var process = _host.Start(path, ["--version"], new Dictionary<string, string>(), Path.GetDirectoryName(path)!, l => { lock (lines) lines.Add(l); });
                for (var i = 0; i < 50 && !process.HasExited; i++) await Task.Delay(200).ConfigureAwait(false);
                if (!process.HasExited) process.Kill();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Debug("llama", "version: " + ex.Message);
                return;
            }
            string? version;
            lock (lines) version = lines.Select(l => VersionRx().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            if (version is { Length: > 0 }) _versions[key] = version.Length > 40 ? version[..40] : version;
        });
    }

    [GeneratedRegex(@"^\s*version:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRx();

    // ───────────── Keys for the compatibility record ─────────────

    private static string ModelKey(LocalModel model)
    {
        if (model.Sha256 is { Length: 64 } sha) return sha.ToLowerInvariant();
        var (length, ticks) = FileStamp(model.Path);
        return $"{model.Path}|{length}|{ticks}";
    }

    private static string ServerKey(LlamaServerChoice choice) => ServerKey(choice.Path, choice.Kind);

    private static string ServerKey(string path, LlamaServerKind kind)
    {
        var (length, ticks) = FileStamp(path);
        return $"{kind}|{length}|{ticks}";
    }

    [GeneratedRegex(@"offloaded (\d+)/(\d+) layers to GPU")]
    private static partial Regex OffloadedRx();

    /// <summary>A line of llama.cpp's output that says why a model didn't load, turned into plain words.</summary>
    internal static string? Reason(string line)
    {
        var l = line.ToLowerInvariant();
        if (WrongArrayLengthRx().Match(line) is { Success: true } mismatch)
        {
            var key = mismatch.Groups[1].Value;
            var expected = mismatch.Groups[2].Value;
            var actual = mismatch.Groups[3].Value;
            if (key.Equals("qwen35.rope.dimension_sections", StringComparison.OrdinalIgnoreCase) && expected == "4" && actual == "3")
                return "This Qwen3.5 GGUF has three RoPE sections, but llama.cpp requires four. Aqua leaves Ollama's model file untouched. Keep using this model through Ollama, or use a GGUF whose qwen35.rope.dimension_sections array has four entries.";
            return $"This model file has {actual} entries for '{key}', but this llama.cpp build requires {expected}. Use a GGUF with matching metadata.";
        }
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

    [GeneratedRegex(@"key\s+([A-Za-z0-9_.-]+)\s+has wrong array length;\s*expected\s+(\d+),\s*got\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WrongArrayLengthRx();

    private static (long Length, long ModifiedTicks) FileStamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : (0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (0, 0);
        }
    }

    private sealed class Instance(LocalModel model, LlamaLane lane, LlamaEndpoint endpoint, LlamaServerChoice server, string modelKey, string serverKey, string speedBuild)
    {
        private readonly Queue<string> _tail = new();
        private readonly object _buffers = new();
        private double _modelMiB, _cacheMiB, _computeMiB;
        private bool _projectorOnGpu;
        public string ModelId => model.Id;
        public string Name => model.Name;
        public string Path => model.Path;
        public LlamaServerKind Kind => server.Kind;
        public string ModelKey => modelKey;
        public string ServerKey => serverKey;
        public string SpeedBuild => speedBuild;
        public LlamaLane Lane => lane;
        public LlamaEndpoint Endpoint => endpoint;
        public int Context => endpoint.ContextTokens;
        public ILlamaProcess? ProcessOrNull { get; private set; }
        public ILlamaProcess Process { get => ProcessOrNull!; set => ProcessOrNull = value; }
        public LlamaInstanceState State { get; set; } = LlamaInstanceState.Starting;
        public string? Error { get; set; }
        public int? GpuLayers { get; private set; }
        public int? TotalLayers { get; private set; }
        /// <summary>The worst failure its output reported: running out of memory outranks an unreadable file.</summary>
        public LlamaFailureKind FailureKind { get; private set; }
        public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.Now;
        private string? _reason;
        public DateTimeOffset Started { get; } = DateTimeOffset.Now;

        public double GpuMiB
        {
            get { lock (_buffers) return _modelMiB + _cacheMiB + _computeMiB; }
        }

        /// <summary>A model known not to load on this server, shown as failed without starting anything.</summary>
        public static Instance FailedBeforeStart(LocalModel model, LlamaLane lane, LlamaServerChoice server, string modelKey, string serverKey, string error) =>
            new(model, lane, new LlamaEndpoint(new Uri("http://127.0.0.1:1/"), "", model.Id, 0, model, lane, 0), server, modelKey, serverKey, "")
            {
                State = LlamaInstanceState.Failed,
                Error = error,
                FailureKind = LlamaFailureKind.Format,
            };

        public void Touch() => LastUsed = DateTimeOffset.Now;

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
                // Server output can come from a user-selected custom executable; never let one malformed log line
                // throw on Process's asynchronous output callback thread.
                if (int.TryParse(m.Groups[1].ValueSpan, out var gpuLayers)
                    && int.TryParse(m.Groups[2].ValueSpan, out var totalLayers)
                    && gpuLayers >= 0 && totalLayers >= gpuLayers)
                {
                    GpuLayers = gpuLayers;
                    TotalLayers = totalLayers;
                }
            }
            // The vision projector says it's going onto the card, then its size: count it with the model's weights.
            if (LlamaCompat.ProjectorOnGpu(line)) _projectorOnGpu = true;
            else if (_projectorOnGpu && LlamaCompat.ProjectorMiB(line) is { } projector)
                lock (_buffers)
                {
                    _modelMiB += projector;
                    _projectorOnGpu = false;
                }
            if (LlamaCompat.GpuBuffer(line) is { } buffer)
                lock (_buffers)
                    switch (buffer.Kind.ToLowerInvariant())
                    {
                        case "model": _modelMiB += buffer.MiB; break;
                        case "kv" or "rs": _cacheMiB += buffer.MiB; break;
                        default: _computeMiB += buffer.MiB; break;
                    }
            if (State == LlamaInstanceState.Starting)
                switch (LlamaCompat.Classify(line))
                {
                    case LlamaFailureKind.Memory: FailureKind = LlamaFailureKind.Memory; break;
                    case LlamaFailureKind.Format when FailureKind != LlamaFailureKind.Memory: FailureKind = LlamaFailureKind.Format; break;
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

        public CompatEntry Entry(string outcome, string? error, string cacheType, TimeSpan load, GpuFacts gpu)
        {
            lock (_buffers)
                return new CompatEntry
                {
                    ModelId = model.Id, ModelName = model.Name, ModelKey = modelKey, ServerKey = serverKey, Server = server.Label,
                    Outcome = outcome, Error = error, When = DateTimeOffset.Now, Context = endpoint.ContextTokens, Cache = cacheType,
                    MtpDraft = endpoint.MtpDraft, GpuLayers = GpuLayers, TotalLayers = TotalLayers, LoadSeconds = Math.Round(load.TotalSeconds, 1),
                    ModelMiB = Math.Round(_modelMiB), CacheMiB = Math.Round(_cacheMiB), ComputeMiB = Math.Round(_computeMiB),
                    Gpu = gpu.Present ? gpu.Name : null, Driver = gpu.Driver,
                };
        }

        public LlamaInstanceInfo Info() => new(model.Id, model.Name, lane, State, endpoint.ContextTokens, endpoint.MtpDraft, GpuLayers, TotalLayers, Error, Started)
        {
            Server = server.Label,
            ServerKind = server.Kind,
            Failure = State == LlamaInstanceState.Failed ? FailureKind : LlamaFailureKind.None,
        };
    }
}
