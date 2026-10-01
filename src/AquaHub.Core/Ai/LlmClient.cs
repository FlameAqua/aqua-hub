using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai;

public enum LlmPriority { Background, Interactive }

public sealed record LlmMessage(string Role, string Content)
{
    /// <summary>Images for vision models (PNG or JPEG bytes).</summary>
    public IReadOnlyList<byte[]>? Images { get; init; }
    /// <summary>The tools an assistant turn called (sent back so the model sees its own calls).</summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }
    /// <summary>For role "tool": the tool that produced this result.</summary>
    public string? ToolName { get; init; }
    /// <summary>For role "tool" on OpenAI-compatible servers: the call being answered.</summary>
    public string? ToolCallId { get; init; }
}

/// <summary>A function the model may call; <see cref="Parameters"/> is a JSON schema.</summary>
public sealed record LlmTool(string Name, string Description, JsonObject Parameters);

/// <summary>A call the model made; <see cref="ArgumentsJson"/> is the raw JSON object of arguments.</summary>
public sealed record LlmToolCall(string Name, string ArgumentsJson, string? Id = null);

/// <summary>What a model is able to do, from Ollama's /api/show (other servers: tools yes, vision unknown).</summary>
public sealed record LlmCapabilities(string Model, bool Tools, bool Vision, bool Thinking);

/// <summary>One piece of a streamed chat turn.</summary>
public abstract record LlmDelta;
public sealed record LlmTextDelta(string Text) : LlmDelta;
public sealed record LlmThinkingDelta(string Text) : LlmDelta;
public sealed record LlmToolCallsDelta(IReadOnlyList<LlmToolCall> Calls) : LlmDelta;
/// <summary>
/// The last piece of a streamed turn: tokens in the prompt and the reply, the context window used, and why the model
/// stopped ("stop", or "length" when it ran out of room).
/// </summary>
public sealed record LlmUsageDelta(int PromptTokens, int OutputTokens, int ContextTokens, string DoneReason) : LlmDelta;

public sealed record LlmRequest
{
    public string System { get; init; } = "";
    public List<LlmMessage> Messages { get; init; } = new();
    /// <summary>JSON schema for structured output (null = free text).</summary>
    public JsonObject? Schema { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    /// <summary>Use the configured deep model if one is set.</summary>
    public bool Deep { get; init; }
    public LlmPriority Priority { get; init; } = LlmPriority.Background;
    public string Purpose { get; init; } = "";
    /// <summary>Force reasoning mode on thinking-capable models (Ollama then enforces the JSON grammar strictly).</summary>
    public bool ForceThink { get; init; }
    /// <summary>Allowed to start a stopped local server (scheduled brief, "Regenerate").</summary>
    public bool WakeServer { get; init; }
    /// <summary>Tools the model may call (only sent to models that support tools).</summary>
    public IReadOnlyList<LlmTool>? Tools { get; init; }
    /// <summary>Reasoning on or off for this request; null = the Settings choice.</summary>
    public bool? Think { get; init; }
    /// <summary>
    /// The model to use (Ask's model picker); null, or one that isn't installed, means the active one (or the deep one
    /// for <see cref="Deep"/> requests).
    /// </summary>
    public string? Model { get; init; }
    /// <summary>Context window for this request; null = Settings. A different size reloads the model, so only long jobs raise it.</summary>
    public int? ContextTokens { get; init; }
}

public sealed record LlmResult(string Text, string Model, int PromptTokens, int OutputTokens, TimeSpan Duration)
{
    public double TokensPerSecond => Duration.TotalSeconds > 0 ? OutputTokens / Duration.TotalSeconds : 0;
}

public sealed record LlmModelInfo(string Name, long SizeBytes, string Family, string Parameters, string Quantization);

public sealed record LlmHealth
{
    public bool Enabled { get; init; }
    public bool Available { get; init; }
    public string Provider { get; init; } = "ollama";
    public string Endpoint { get; init; } = "";
    public string? ActiveModel { get; init; }
    public string? DeepModel { get; init; }
    public List<LlmModelInfo> Models { get; init; } = new();
    public string? Error { get; init; }
    public string? Version { get; init; }
    public bool Paused { get; init; }
    public string? PauseReason { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
}

public sealed class LlmStats
{
    public int Calls;
    public int Failures;
    public long OutputTokens;
    public double LastTokensPerSecond;
    public string? LastError;
    public DateTimeOffset? LastCall;
}

public sealed class LlmUnavailableException(string message) : Exception(message);

/// <summary>
/// Client for local model servers. Supports Ollama's native API (structured outputs via JSON schema,
/// thinking control, keep-alive) and any OpenAI-compatible server (LM Studio, llama.cpp, vLLM).
/// Non-loopback endpoints are refused unless explicitly allowed, so personal data stays on this PC.
/// </summary>
public sealed partial class LlmClient : IDisposable
{
    private readonly Func<AiSettings> _settings;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private readonly PriorityGate _gate = new();
    private readonly Dictionary<string, HashSet<string>> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private LlmHealth _health = new() { CheckedAt = DateTimeOffset.MinValue };

    public LlmStats Stats { get; } = new();
    public LlmHealth Health => _health;

    /// <summary>Most recent raw model outputs (for the Agents diagnostics view).</summary>
    public IReadOnlyList<(DateTimeOffset Time, string Purpose, string Model, string Output)> RecentOutputs => _recent.ToArray();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(DateTimeOffset, string, string, string)> _recent = new();

    private void Remember(string purpose, string model, string output)
    {
        _recent.Enqueue((DateTimeOffset.Now, purpose, model, output.Length > 6000 ? output[..6000] : output));
        while (_recent.Count > 30 && _recent.TryDequeue(out _)) { }
    }
    public event Action<LlmHealth>? HealthChanged;

    /// <summary>Returns a reason string when background AI work should pause (e.g. a full-screen game is running).</summary>
    public Func<string?> PauseReason { get; set; } = () => null;

    /// <summary>
    /// Set by the app: starts the local model server when a request finds it stopped (interactive requests and
    /// requests flagged <see cref="LlmRequest.WakeServer"/> only, so background work never undoes an idle stop).
    /// Returns true once the server answers.
    /// </summary>
    public Func<CancellationToken, Task<bool>>? StartServer { get; set; }
    public bool UserPaused { get; set; }

    public LlmClient(Func<AiSettings> settings, ISecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(4),
            UseProxy = false, // local traffic must never be routed through a proxy
        })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool InteractiveWaiting => _gate.InteractiveWaiting;

    /// <summary>True while a request is running on the model (used to tell our GPU load apart from a game's).</summary>
    public bool IsBusy => _gate.IsBusy;

    public const string OfflineReason = "Model server offline";

    public string? BackgroundBlockReason()
    {
        var s = _settings();
        if (!s.Enabled) return "AI disabled";
        if (UserPaused) return "Paused by you";
        if (s.PauseWhenFullscreen && PauseReason() is { } reason) return reason;
        if (!_health.Available) return OfflineReason;
        return null;
    }

    private Uri BaseUri()
    {
        var s = _settings();
        if (!Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new LlmUnavailableException("Invalid AI endpoint");
        if (!(uri.IsLoopback || uri.Host == "localhost") && !s.AllowRemoteEndpoint)
            throw new LlmUnavailableException("Remote AI endpoints are disabled (privacy). Enable 'Allow remote endpoint' to use one.");
        return uri;
    }

    private bool IsOllama => !_settings().Provider.Equals("openai", StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── Health & models ─────────────────────────────

    public async Task<LlmHealth> CheckAsync(CancellationToken ct = default)
    {
        var s = _settings();
        LlmHealth health;
        if (!s.Enabled)
        {
            health = new LlmHealth { Enabled = false, Provider = s.Provider, Endpoint = s.Endpoint, CheckedAt = DateTimeOffset.Now };
        }
        else
        {
            try
            {
                var models = await ListModelsAsync(ct).ConfigureAwait(false);
                string? version = null;
                if (IsOllama)
                {
                    using var vdoc = await GetJsonAsync("api/version", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    version = vdoc?.RootElement.Str("version");
                }
                var active = Pick(s.Model, s.PreferredModels, models);
                var deep = string.IsNullOrWhiteSpace(s.DeepModel) ? active : Pick(s.DeepModel, s.PreferredModels, models) ?? active;
                health = new LlmHealth
                {
                    Enabled = true,
                    Available = models.Count > 0 && active is not null,
                    Provider = s.Provider,
                    Endpoint = s.Endpoint,
                    ActiveModel = active,
                    DeepModel = deep,
                    Models = models,
                    Version = version,
                    Error = models.Count == 0 ? "No models installed — run: ollama pull qwen3.5:9b" : active is null ? $"Model '{s.Model}' is not installed" : null,
                    CheckedAt = DateTimeOffset.Now,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                health = new LlmHealth
                {
                    Enabled = true, Available = false, Provider = s.Provider, Endpoint = s.Endpoint,
                    Error = ex is LlmUnavailableException ? ex.Message : $"Can't reach {s.Endpoint} — is {(IsOllama ? "Ollama" : "the model server")} running?",
                    CheckedAt = DateTimeOffset.Now,
                };
            }
        }
        var reason = BackgroundBlockReasonFor(health);
        health = health with { Paused = reason is not null && health.Available, PauseReason = health.Available ? reason : null };
        _health = health;
        try { HealthChanged?.Invoke(health); } catch { }
        return health;
    }

    private string? BackgroundBlockReasonFor(LlmHealth h)
    {
        if (UserPaused) return "Paused by you";
        return _settings().PauseWhenFullscreen ? PauseReason() : null;
    }

    private static string? Pick(string configured, IEnumerable<string> preferred, List<LlmModelInfo> installed)
    {
        if (installed.Count == 0) return null;
        bool Has(string name) => installed.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                                    m.Name.Equals(name + ":latest", StringComparison.OrdinalIgnoreCase));
        string Canonical(string name) => installed.First(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                                              m.Name.Equals(name + ":latest", StringComparison.OrdinalIgnoreCase)).Name;
        if (!configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return Has(configured) ? Canonical(configured) : null;
        foreach (var p in preferred)
            if (Has(p)) return Canonical(p);
        // Fall back to the smallest general model that isn't an embedding/coder model.
        return installed.Where(m => !Regex.IsMatch(m.Name, "embed|coder|code|vision|ocr", RegexOptions.IgnoreCase))
                        .OrderBy(m => m.SizeBytes).FirstOrDefault()?.Name ?? installed[0].Name;
    }

    public async Task<List<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var list = new List<LlmModelInfo>();
        if (IsOllama)
        {
            using var doc = await GetJsonAsync("api/tags", TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            if (doc is null) throw new LlmUnavailableException("Ollama not reachable");
            foreach (var m in doc.RootElement.Arr("models"))
            {
                var details = m.TryProp("details", out var d) ? d : default;
                list.Add(new LlmModelInfo(m.Str("name") ?? "?", m.Lng("size") ?? 0,
                    details.ValueKind == JsonValueKind.Object ? details.Str("family") ?? "" : "",
                    details.ValueKind == JsonValueKind.Object ? details.Str("parameter_size") ?? "" : "",
                    details.ValueKind == JsonValueKind.Object ? details.Str("quantization_level") ?? "" : ""));
            }
        }
        else
        {
            using var doc = await GetJsonAsync("v1/models", TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            if (doc is null) throw new LlmUnavailableException("Model server not reachable");
            foreach (var m in doc.RootElement.Arr("data"))
                list.Add(new LlmModelInfo(m.Str("id") ?? "?", 0, "", "", ""));
        }
        return list.OrderBy(m => m.Name).ToList();
    }

    /// <summary>Loaded models and their VRAM usage (Ollama only).</summary>
    public async Task<List<(string Name, long VramBytes, DateTimeOffset? ExpiresAt)>> RunningModelsAsync(CancellationToken ct = default)
    {
        var list = new List<(string, long, DateTimeOffset?)>();
        if (!IsOllama) return list;
        try
        {
            using var doc = await GetJsonAsync("api/ps", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (doc is null) return list;
            foreach (var m in doc.RootElement.Arr("models"))
                list.Add((m.Str("name") ?? "?", m.Lng("size_vram") ?? 0, TimeText.ParseLenient(m.Str("expires_at"))));
        }
        catch { }
        return list;
    }

    /// <summary>Frees VRAM immediately (useful before gaming).</summary>
    public async Task UnloadAsync(CancellationToken ct = default)
    {
        if (!IsOllama) return;
        foreach (var (name, _, _) in await RunningModelsAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var body = new JsonObject { ["model"] = name, ["keep_alive"] = 0 };
                using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "api/generate"))
                { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var _ = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Log.Warn("ai", $"Unload of {name} failed", ex); }
        }
    }

    /// <summary>
    /// Whether an installed model can reason step by step (Ollama's "thinking" capability): null when that can't be
    /// told without waking the model server, or the server isn't Ollama. Doesn't start anything.
    /// </summary>
    public async Task<bool?> CanThinkAsync(string model, CancellationToken ct = default)
    {
        if (!IsOllama || !_health.Available || !_health.Models.Any(m => m.Name.Equals(model, StringComparison.OrdinalIgnoreCase))) return null;
        var caps = await CapabilitiesAsync(model, ct).ConfigureAwait(false);
        return caps.Count == 0 ? null : caps.Contains("thinking");
    }

    /// <summary>The model a request runs on: the one it names if that's installed, else the deep or the active one.</summary>
    private string ModelFor(string? requested, bool deep)
    {
        if (!string.IsNullOrWhiteSpace(requested) &&
            _health.Models.FirstOrDefault(m => m.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)) is { } named) return named.Name;
        return (deep ? _health.DeepModel : null) ?? _health.ActiveModel!;
    }

    private async Task<HashSet<string>> CapabilitiesAsync(string model, CancellationToken ct)
    {
        lock (_capabilities)
            if (_capabilities.TryGetValue(model, out var cached)) return cached;
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var answered = false;
        try
        {
            var body = new JsonObject { ["model"] = model };
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "api/show"))
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                foreach (var c in doc.RootElement.Arr("capabilities"))
                    if (c.GetString() is { } s) caps.Add(s);
                answered = true;
            }
        }
        catch { }
        // Only a real answer is remembered: a server that was starting up is asked again next time.
        if (answered) lock (_capabilities) _capabilities[model] = caps;
        return caps;
    }

    /// <summary>
    /// What the active model can do. Ollama reports it per model; OpenAI-compatible servers are assumed to take tools
    /// and not images. Throws <see cref="LlmUnavailableException"/> when no model is available (it may start the server).
    /// </summary>
    public Task<LlmCapabilities> CapabilitiesAsync(bool deep = false, CancellationToken ct = default) => CapabilitiesForAsync(null, deep, ct);

    /// <summary>What <paramref name="model"/> can do (the active or deep one when it's null or not installed).</summary>
    public async Task<LlmCapabilities> CapabilitiesForAsync(string? model, bool deep = false, CancellationToken ct = default)
    {
        if (!_settings().Enabled) throw new LlmUnavailableException("AI is disabled in settings");
        await EnsureAvailableAsync(wake: true, ct).ConfigureAwait(false);
        model = ModelFor(model, deep);
        if (!IsOllama) return new LlmCapabilities(model, Tools: true, Vision: false, Thinking: false);
        var caps = await CapabilitiesAsync(model, ct).ConfigureAwait(false);
        return new LlmCapabilities(model, caps.Contains("tools"), caps.Contains("vision"), caps.Contains("thinking"));
    }

    /// <summary>
    /// Holds the GPU for a multi-step interactive job (Ask with tools), so background agents don't slip in between its
    /// model calls. Pass <c>reserved: true</c> to <see cref="ChatStreamAsync"/> while holding it.
    /// </summary>
    public Task<IDisposable> ReserveAsync(CancellationToken ct) => _gate.AcquireAsync(interactive: true, ct);

    // ───────────────────────────── Completion ─────────────────────────────

    private async Task EnsureAvailableAsync(bool wake, CancellationToken ct)
    {
        if (_health.Available && _health.ActiveModel is not null) return;
        await CheckAsync(ct).ConfigureAwait(false);
        if ((!_health.Available || _health.ActiveModel is null) && wake && StartServer is { } start && await start(ct).ConfigureAwait(false))
            await CheckAsync(ct).ConfigureAwait(false);
        if (!_health.Available || _health.ActiveModel is null) throw new LlmUnavailableException(_health.Error ?? "No model available");
    }

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var s = _settings();
        if (!s.Enabled) throw new LlmUnavailableException("AI is disabled in settings");
        await EnsureAvailableAsync(request.Priority == LlmPriority.Interactive || request.WakeServer, ct).ConfigureAwait(false);
        var model = ModelFor(request.Model, request.Deep);

        using var lease = await _gate.AcquireAsync(request.Priority == LlmPriority.Interactive, ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            var result = IsOllama
                ? await OllamaChatAsync(model, request, s, ct).ConfigureAwait(false)
                : await OpenAiChatAsync(model, request, s, ct).ConfigureAwait(false);
            Interlocked.Increment(ref Stats.Calls);
            Interlocked.Add(ref Stats.OutputTokens, result.OutputTokens);
            Stats.LastTokensPerSecond = result.TokensPerSecond;
            Stats.LastCall = DateTimeOffset.Now;
            Remember(request.Purpose, model, result.Text);
            Log.Info("ai", $"{request.Purpose} via {model}: {result.OutputTokens} tok in {sw.Elapsed.TotalSeconds:0.0}s ({result.TokensPerSecond:0} tok/s)");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref Stats.Failures);
            Stats.LastError = ex.Message;
            if (ex is HttpRequestException) _health = _health with { Available = false, Error = ex.Message };
            throw;
        }
    }

    /// <summary>
    /// Structured completion. The schema is sent both as the server-side constraint (<c>format</c>) and restated in
    /// the prompt, because some server/model combinations (e.g. Ollama 0.30 with thinking disabled) ignore the
    /// constraint. Invalid output gets exactly one repair attempt before a <see cref="FormatException"/>.
    /// </summary>
    public async Task<(JsonDocument Json, LlmResult Result)> CompleteJsonAsync(LlmRequest request, CancellationToken ct = default)
    {
        var instructed = WithJsonInstruction(request);
        var result = await CompleteAsync(instructed, ct).ConfigureAwait(false);
        if (TryParseOrRepair(result.Text, request.Schema, out var doc)) return (doc!, result);

        Log.Warn("ai", $"{request.Purpose}: invalid JSON ({result.Text.Length} chars: {HtmlText.Truncate(result.Text.Replace('\n', ' '), 160)}) — retrying");
        LlmRequest retry;
        var model = _health.ActiveModel is null ? "" : ModelFor(request.Model, request.Deep);
        if (IsOllama && !_settings().Think && (await CapabilitiesAsync(model, ct).ConfigureAwait(false)).Contains("thinking"))
        {
            // Last resort: reasoning mode makes Ollama enforce the JSON grammar (slower, but valid).
            retry = instructed with { ForceThink = true, MaxTokens = (instructed.MaxTokens ?? 1200) + 3000 };
        }
        else
        {
            retry = instructed with
            {
                Temperature = 0.2,
                Messages = instructed.Messages
                    .Append(new LlmMessage("assistant", HtmlText.Truncate(result.Text, 1500)))
                    .Append(new LlmMessage("user", "That reply was not a valid JSON object. Reply again with ONLY the JSON object that matches the shape — every string value in double quotes, inner quotes escaped, no markdown fences, no commentary."))
                    .ToList(),
            };
        }
        result = await CompleteAsync(retry, ct).ConfigureAwait(false);
        if (TryParseOrRepair(result.Text, request.Schema, out doc)) return (doc!, result);
        throw new FormatException("Model returned invalid JSON");
    }

    private static bool TryParseOrRepair(string text, JsonObject? schema, out JsonDocument? doc)
    {
        if (TryParseJson(text, schema, out doc)) return true;
        var repaired = JsonRepair.Repair(ExtractJson(text));
        if (TryParseJson(repaired, schema, out doc))
        {
            Log.Info("ai", "Repaired near-miss JSON from the model");
            return true;
        }
        return false;
    }

    private static LlmRequest WithJsonInstruction(LlmRequest request)
    {
        if (request.Schema is null || request.Messages.Count == 0) return request;
        var messages = request.Messages.ToList();
        var last = messages[^1];
        messages[^1] = last with
        {
            Content = last.Content + "\n\nRespond with ONLY a JSON object (no markdown fences, no commentary) with exactly this shape:\n" +
                      Schema.Shape(request.Schema),
        };
        return request with { Messages = messages };
    }

    /// <summary>
    /// Parses model output as a JSON object and normalises common deviations: echoed schema wrappers
    /// ({"type":"object","properties":{…}}) and single-key envelopes ({"brief":{…}}). Outputs that contain
    /// none of the required keys (e.g. {"error": …}) are rejected.
    /// </summary>
    internal static bool TryParseJson(string text, JsonObject? schema, out JsonDocument? doc)
    {
        doc = null;
        var json = ExtractJson(text);
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true }); }
        catch (JsonException) { return false; }

        if (parsed.RootElement.ValueKind != JsonValueKind.Object) { parsed.Dispose(); return false; }
        parsed = MergeDuplicateKeys(parsed);
        if (schema is not null) parsed = MatchSchemaKeys(parsed, schema);
        var required = (schema?["required"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() ?? new();
        if (required.Count == 0) { doc = parsed; return true; }

        bool HasAny(JsonElement e) => e.ValueKind == JsonValueKind.Object && required.Any(r => e.TryGetProperty(r, out _));
        var root = parsed.RootElement;
        if (HasAny(root)) { doc = parsed; return true; }

        JsonElement? inner = null;
        if (root.TryGetProperty("properties", out var props) && HasAny(props)) inner = props;
        else
        {
            foreach (var p in root.EnumerateObject())
                if (HasAny(p.Value)) { inner = p.Value; break; }
        }
        if (inner is { } found)
        {
            doc = JsonDocument.Parse(found.GetRawText());
            parsed.Dispose();
            return true;
        }
        parsed.Dispose();
        return false;
    }

    /// <summary>
    /// Keys the model wrote in another case or style ("Name", "SkillArgs", "file-terms") renamed to the schema's own
    /// ("name", "skill_args", "file_terms"), at every level, so a reply isn't rejected over its spelling.
    /// </summary>
    internal static JsonDocument MatchSchemaKeys(JsonDocument doc, JsonObject schema)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        void Collect(JsonNode? node)
        {
            if (node is not JsonObject o) return;
            if (o["properties"] is JsonObject props)
                foreach (var (key, value) in props)
                {
                    names.TryAdd(Squash(key), key);
                    Collect(value);
                }
            Collect(o["items"]);
        }
        Collect(schema);
        if (names.Count == 0 || !NeedsRenaming(doc.RootElement)) return doc;

        bool NeedsRenaming(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().Any(p => !names.ContainsValue(p.Name) && names.ContainsKey(Squash(p.Name)) || NeedsRenaming(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().Any(NeedsRenaming),
            _ => false,
        };
        JsonNode? Rename(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    var copy = new JsonObject();
                    foreach (var (key, value) in o.ToList())
                    {
                        var name = names.ContainsValue(key) ? key : names.GetValueOrDefault(Squash(key), key);
                        if (!copy.ContainsKey(name)) copy[name] = Rename(value?.DeepClone());
                    }
                    return copy;
                case JsonArray a:
                    return new JsonArray(a.Select(x => Rename(x?.DeepClone())).ToArray());
                default:
                    return node;
            }
        }
        var renamed = Rename(JsonNode.Parse(doc.RootElement.GetRawText()));
        doc.Dispose();
        Log.Info("ai", "Matched the model's JSON keys to the schema");
        return JsonDocument.Parse(renamed!.ToJsonString());
    }

    private static string Squash(string key) => new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Small models sometimes repeat a key ({"bullets":[…], "bullets":[…]}). System.Text.Json keeps both, but a lookup
    /// sees only one, silently dropping content. Merge repeats: arrays are concatenated, objects merged, else the first wins.
    /// </summary>
    internal static JsonDocument MergeDuplicateKeys(JsonDocument doc)
    {
        if (!HasDuplicateKeys(doc.RootElement)) return doc;
        var merged = ToMergedNode(doc.RootElement);
        doc.Dispose();
        Log.Info("ai", "Merged repeated keys in model JSON");
        return JsonDocument.Parse(merged!.ToJsonString());
    }

    private static bool HasDuplicateKeys(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1) || e.EnumerateObject().Any(p => HasDuplicateKeys(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Any(HasDuplicateKeys),
        _ => false,
    };

    private static JsonNode? ToMergedNode(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var p in e.EnumerateObject())
                {
                    var value = ToMergedNode(p.Value);
                    if (!obj.TryGetPropertyValue(p.Name, out var existing)) { obj[p.Name] = value; continue; }
                    if (existing is JsonArray into && value is JsonArray more)
                        foreach (var item in more.ToList()) { more.Remove(item); into.Add(item); }
                    else if (existing is JsonObject target && value is JsonObject extra)
                        foreach (var (k, v) in extra.ToList()) { extra.Remove(k); if (!target.ContainsKey(k)) target[k] = v; }
                }
                return obj;
            case JsonValueKind.Array:
                var arr = new JsonArray();
                foreach (var item in e.EnumerateArray()) arr.Add(ToMergedNode(item));
                return arr;
            default:
                return JsonNode.Parse(e.GetRawText());
        }
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    public static string StripThinking(string text) => ThinkBlock().Replace(text, "").Trim();

    internal static string ExtractJson(string text)
    {
        var t = StripThinking(text);
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = t.IndexOf('\n');
            var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNl > 0 && lastFence > firstNl) t = t[(firstNl + 1)..lastFence];
        }
        var start = t.IndexOfAny(new[] { '{', '[' });
        var end = Math.Max(t.LastIndexOf('}'), t.LastIndexOf(']'));
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }

    internal static JsonArray BuildMessages(LlmRequest request, bool ollama)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        foreach (var m in request.Messages)
        {
            var msg = new JsonObject { ["role"] = m.Role };
            var images = m.Images is { Count: > 0 } imgs ? imgs : null;
            if (ollama)
            {
                msg["content"] = m.Content;
                if (images is not null) msg["images"] = new JsonArray(images.Select(b => (JsonNode)Convert.ToBase64String(b)).ToArray());
                if (m.ToolName is not null) msg["tool_name"] = m.ToolName;
            }
            else if (images is not null)
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content } };
                foreach (var b in images)
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:" + ImageMime(b) + ";base64," + Convert.ToBase64String(b) } });
                msg["content"] = parts;
            }
            else
            {
                msg["content"] = m.Content;
                if (m.ToolCallId is not null) msg["tool_call_id"] = m.ToolCallId;
            }
            if (m.ToolCalls is { Count: > 0 } calls)
            {
                var arr = new JsonArray();
                foreach (var c in calls)
                {
                    var args = ParseArguments(c.ArgumentsJson);
                    arr.Add(ollama
                        ? new JsonObject { ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = args } }
                        : new JsonObject
                        {
                            ["id"] = c.Id ?? "call_" + c.Name, ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = args.ToJsonString() },
                        });
                }
                msg["tool_calls"] = arr;
            }
            messages.Add(msg);
        }
        return messages;
    }

    private static JsonObject ParseArguments(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string ImageMime(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? "image/jpeg" : "image/png";

    private static JsonArray ToolsJson(IReadOnlyList<LlmTool> tools) => new(tools.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
    }).ToArray());

    private async Task<JsonObject> OllamaBodyAsync(string model, LlmRequest request, AiSettings s, bool stream, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(request, ollama: true),
            ["stream"] = stream,
            ["keep_alive"] = s.KeepAlive,
            ["options"] = new JsonObject
            {
                ["temperature"] = request.Temperature ?? s.Temperature,
                ["num_ctx"] = request.ContextTokens ?? s.ContextTokens,
                ["num_predict"] = request.MaxTokens ?? 1200,
            },
        };
        if (request.Schema is not null) body["format"] = request.Schema.DeepClone();
        var caps = await CapabilitiesAsync(model, ct).ConfigureAwait(false);
        if (caps.Contains("thinking")) body["think"] = request.Think ?? (s.Think || request.ForceThink);
        if (request.Tools is { Count: > 0 } tools && caps.Contains("tools")) body["tools"] = ToolsJson(tools);
        return body;
    }

    private async Task<LlmResult> OllamaChatAsync(string model, LlmRequest request, AiSettings s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var body = await OllamaBodyAsync(model, request, s, stream: false, ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "api/chat"))
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(4));
        using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama error {(int)resp.StatusCode}: {HtmlText.Truncate(json, 200)}");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var content = root.TryProp("message", out var msg) ? msg.Str("content") ?? "" : "";
        var evalCount = (int)(root.Lng("eval_count") ?? 0);
        var evalNs = root.Lng("eval_duration") ?? 0;
        var duration = evalNs > 0 ? TimeSpan.FromTicks(evalNs / 100) : sw.Elapsed;
        return new LlmResult(StripThinking(content), model, (int)(root.Lng("prompt_eval_count") ?? 0), evalCount, duration);
    }

    private async Task<LlmResult> OpenAiChatAsync(string model, LlmRequest request, AiSettings s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var body = OpenAiBody(model, request, s, stream: false);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "v1/chat/completions"))
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        AddAuth(req);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(4));
        using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Model server error {(int)resp.StatusCode}: {HtmlText.Truncate(json, 200)}");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var choice = root.Arr("choices").FirstOrDefault();
        var content = choice.ValueKind == JsonValueKind.Object && choice.TryProp("message", out var m) ? m.Str("content") ?? "" : "";
        var usage = root.TryProp("usage", out var u) ? u : default;
        var outTok = usage.ValueKind == JsonValueKind.Object ? (int)(usage.Lng("completion_tokens") ?? 0) : content.Length / 4;
        var inTok = usage.ValueKind == JsonValueKind.Object ? (int)(usage.Lng("prompt_tokens") ?? 0) : 0;
        return new LlmResult(StripThinking(content), model, inTok, outTok, sw.Elapsed);
    }

    private JsonObject OpenAiBody(string model, LlmRequest request, AiSettings s, bool stream)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = BuildMessages(request, ollama: false),
            ["temperature"] = request.Temperature ?? s.Temperature,
            ["max_tokens"] = request.MaxTokens ?? 1200,
            ["stream"] = stream,
        };
        if (request.Tools is { Count: > 0 } tools) body["tools"] = ToolsJson(tools);
        if (request.Schema is not null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "result", ["schema"] = request.Schema.DeepClone(), ["strict"] = true },
            };
        }
        return body;
    }

    private void AddAuth(HttpRequestMessage req)
    {
        var key = _secrets.Get(SecretKeys.OpenAiCompatibleKey);
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <summary>Streams a free-text answer token by token (interactive priority).</summary>
    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var delta in ChatStreamAsync(request, reserved: false, ct).ConfigureAwait(false))
            if (delta is LlmTextDelta t) yield return t.Text;
    }

    /// <summary>
    /// Streams one chat turn at interactive priority: answer text, reasoning ("thinking") and, when the request offers
    /// <see cref="LlmRequest.Tools"/>, the tool calls the model makes (reported once complete). Inline &lt;think&gt;
    /// sections from reasoning models are reported as thinking, never as answer text.
    /// </summary>
    /// <param name="reserved">The caller holds <see cref="ReserveAsync"/> (don't queue for the GPU again).</param>
    public async IAsyncEnumerable<LlmDelta> ChatStreamAsync(LlmRequest request, bool reserved, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var s = _settings();
        if (!s.Enabled) throw new LlmUnavailableException("AI is disabled in settings");
        await EnsureAvailableAsync(wake: true, ct).ConfigureAwait(false);
        var model = ModelFor(request.Model, request.Deep);
        using var lease = reserved ? null : await _gate.AcquireAsync(interactive: true, ct).ConfigureAwait(false);

        HttpRequestMessage req;
        if (IsOllama)
        {
            var body = await OllamaBodyAsync(model, request, s, stream: true, ct).ConfigureAwait(false);
            req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "api/chat"))
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }
        else
        {
            req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "v1/chat/completions"))
            { Content = new StringContent(OpenAiBody(model, request, s, stream: true).ToJsonString(), Encoding.UTF8, "application/json") };
            AddAuth(req);
        }

        using (req)
        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref Stats.Failures);
                Stats.LastError = $"Model error {(int)resp.StatusCode}";
                throw new HttpRequestException($"Model error {(int)resp.StatusCode}: {HtmlText.Truncate(err, 200)}");
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var inThink = false;
            var tokens = 0;
            var promptTokens = 0;
            var doneReason = "";
            var sw = Stopwatch.StartNew();
            var answer = new StringBuilder();
            // OpenAI-compatible servers stream tool calls in pieces, keyed by index.
            var partial = new SortedDictionary<int, (string? Id, string Name, StringBuilder Args)>();
            List<LlmToolCall>? calls = null;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                if (!IsOllama)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    line = line[5..].Trim();
                    if (line == "[DONE]") break;
                }
                string? piece = null, thinking = null;
                var done = false;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (IsOllama)
                    {
                        if (root.TryProp("message", out var m))
                        {
                            piece = m.Str("content");
                            thinking = m.Str("thinking");
                            foreach (var call in m.Arr("tool_calls"))
                                if (call.TryProp("function", out var fn) && fn.Str("name") is { Length: > 0 } name)
                                    (calls ??= new()).Add(new LlmToolCall(name,
                                        fn.TryProp("arguments", out var a) ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText()) : "{}",
                                        call.Str("id")));
                        }
                        done = root.Bool("done") == true;
                        if (done && root.Lng("eval_count") is { } n) tokens = (int)n;
                        if (done && root.Lng("prompt_eval_count") is { } pn) promptTokens = (int)pn;
                        if (done) doneReason = root.Str("done_reason") ?? "";
                    }
                    else
                    {
                        var choice = root.Arr("choices").FirstOrDefault();
                        if (choice.ValueKind == JsonValueKind.Object && choice.Str("finish_reason") is { Length: > 0 } finish) doneReason = finish;
                        if (choice.ValueKind == JsonValueKind.Object && choice.TryProp("delta", out var delta))
                        {
                            piece = delta.Str("content");
                            thinking = delta.Str("reasoning_content") ?? delta.Str("reasoning");
                            foreach (var tc in delta.Arr("tool_calls"))
                            {
                                var index = (int)(tc.Lng("index") ?? 0);
                                partial.TryGetValue(index, out var entry);
                                entry.Args ??= new StringBuilder();
                                if (tc.Str("id") is { Length: > 0 } id) entry.Id = id;
                                if (tc.TryProp("function", out var fn))
                                {
                                    if (fn.Str("name") is { Length: > 0 } name) entry.Name = name;
                                    if (fn.Str("arguments") is { } args) entry.Args.Append(args);
                                }
                                partial[index] = entry;
                            }
                        }
                    }
                }
                catch (JsonException) { continue; }

                if (!string.IsNullOrEmpty(thinking)) yield return new LlmThinkingDelta(thinking);
                if (!string.IsNullOrEmpty(piece))
                {
                    // Reasoning models without a separate thinking channel put it inline in <think> tags.
                    var text = piece;
                    var reasoning = "";
                    if (text.Contains("<think>", StringComparison.Ordinal))
                    {
                        var at = text.IndexOf("<think>", StringComparison.Ordinal);
                        reasoning = text[(at + 7)..];
                        text = text[..at];
                        inThink = true;
                        if (reasoning.Contains("</think>", StringComparison.Ordinal))
                        {
                            var end = reasoning.IndexOf("</think>", StringComparison.Ordinal);
                            text += reasoning[(end + 8)..];
                            reasoning = reasoning[..end];
                            inThink = false;
                        }
                    }
                    else if (inThink)
                    {
                        if (text.Contains("</think>", StringComparison.Ordinal))
                        {
                            var end = text.IndexOf("</think>", StringComparison.Ordinal);
                            reasoning = text[..end];
                            text = text[(end + 8)..];
                            inThink = false;
                        }
                        else { reasoning = text; text = ""; }
                    }
                    if (reasoning.Length > 0) yield return new LlmThinkingDelta(reasoning);
                    if (text.Length > 0)
                    {
                        answer.Append(text);
                        yield return new LlmTextDelta(text);
                    }
                }
                if (done) break;
            }
            foreach (var (_, p) in partial)
                if (p.Name is { Length: > 0 }) (calls ??= new()).Add(new LlmToolCall(p.Name, p.Args?.ToString() ?? "{}", p.Id));
            if (calls is { Count: > 0 }) yield return new LlmToolCallsDelta(calls);

            if (tokens == 0) tokens = answer.Length / 4;
            yield return new LlmUsageDelta(promptTokens, tokens, request.ContextTokens ?? s.ContextTokens, doneReason);
            Interlocked.Increment(ref Stats.Calls);
            Interlocked.Add(ref Stats.OutputTokens, tokens);
            if (sw.Elapsed.TotalSeconds > 0) Stats.LastTokensPerSecond = tokens / sw.Elapsed.TotalSeconds;
            Stats.LastCall = DateTimeOffset.Now;
            Remember(request.Purpose, model, answer.ToString() + (calls is { Count: > 0 } ? "\n[tool calls] " + string.Join(", ", calls.Select(c => c.Name + c.ArgumentsJson)) : ""));
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri(), path));
        if (!IsOllama) AddAuth(req);
        try
        {
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return JsonDocument.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Single-slot gate for the (single) local GPU: interactive requests (Ask, command palette) jump the
/// queue ahead of background agent work so the UI never feels blocked by summarisation.
/// </summary>
public sealed class PriorityGate
{
    private readonly object _lock = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> _interactive = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> _background = new();
    private bool _busy;

    public bool InteractiveWaiting { get { lock (_lock) return _interactive.Count > 0; } }
    public bool IsBusy { get { lock (_lock) return _busy; } }

    public Task<IDisposable> AcquireAsync(bool interactive, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_busy)
            {
                _busy = true;
                return Task.FromResult<IDisposable>(new Lease(this));
            }
            var tcs = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            var node = (interactive ? _interactive : _background).AddLast(tcs);
            if (ct.CanBeCanceled)
            {
                ct.Register(() =>
                {
                    lock (_lock)
                    {
                        if (node.List is not null) node.List.Remove(node);
                    }
                    tcs.TrySetCanceled(ct);
                });
            }
            return tcs.Task;
        }
    }

    private void Release()
    {
        TaskCompletionSource<IDisposable>? next = null;
        lock (_lock)
        {
            while (next is null)
            {
                var list = _interactive.Count > 0 ? _interactive : _background.Count > 0 ? _background : null;
                if (list is null) { _busy = false; return; }
                var candidate = list.First!.Value;
                list.RemoveFirst();
                if (!candidate.Task.IsCompleted) next = candidate;
            }
        }
        if (!next.TrySetResult(new Lease(this))) Release();
    }

    private sealed class Lease(PriorityGate gate) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
        }
    }
}
