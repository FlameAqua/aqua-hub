using System.Runtime.InteropServices;
using AquaHub.Core.Ai.Local;
using AquaHub.Core.Util;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>
/// The one API the AI settings and the Models tab use for Aqua's own runtime (llama.cpp): what's installed, the graphics
/// card, the models and how each fits it, the suggested downloads and what's running, and the actions (install or remove
/// the runtime, download or remove a model, give a model a role, switch Ask Aqua to the runtime). Installs, downloads and
/// removals are only recorded in E2E dry runs. It also frees the card for a game (the "Stop Ollama for games" setting
/// applies to the runtime too) and keeps the model list current. <see cref="Changed"/> and the progress events are raised
/// on the UI thread.
/// </summary>
public sealed class LocalAi : IDisposable
{
    public static readonly string[] Roles = ["main", "creative", "background", "search"];

    private readonly CancellationTokenSource _stop = new();
    private GpuFacts _gpu = GpuFacts.None;
    private string _folder = "";

    public LocalAi()
    {
        Hub.Core.Models.Changed += OnLibraryChanged;
        Hub.Core.LlamaSetup.Changed += Raise;
        Hub.Core.LlamaSetup.Progress += p => Hub.OnUi(() => { InstallProgress = p; InstallProgressChanged?.Invoke(p); });
        Hub.Core.ModelDownloads.Changed += Raise;
        Hub.Core.ModelDownloads.Progress += p => Hub.OnUi(() => DownloadProgressChanged?.Invoke(p));
        Hub.Llama.Changed += Raise;
    }

    /// <summary>Something here changed: the runtime, the models, a download, a server starting or stopping.</summary>
    public event Action? Changed;
    public event Action<LlamaProgress>? InstallProgressChanged;
    public event Action<ModelDownloadProgress>? DownloadProgressChanged;

    /// <summary>Ask Aqua runs on Aqua's runtime (Settings' provider is "llamacpp").</summary>
    public bool InUse => Hub.S.Ai.Provider == "llamacpp";
    /// <summary>The runtime Aqua installed (null: none). <see cref="RuntimeReady"/> also counts a custom server path in Settings.</summary>
    public LlamaInstall? Installed => Hub.Core.LlamaSetup.Installed();
    /// <summary>"b11387", "custom" (Settings' own server) or "" (not installed).</summary>
    public string Build => Hub.Llama.Build;
    public bool RuntimeReady => Hub.Llama.ServerPath is not null;
    public bool Installing => Hub.Core.LlamaSetup.Busy;
    public LlamaProgress? InstallProgress { get; private set; }
    /// <summary>The graphics card, as last read (every 15 seconds).</summary>
    public GpuFacts Gpu => _gpu.Present || _gpu.TotalBytes > 0 ? _gpu : _gpu = GpuReader.Read(Hub.System);
    /// <summary>The build that suits this PC (Settings' backend choice, else picked from the card and its driver).</summary>
    public LlamaBuild RecommendedBuild => LlamaCatalog.Pick(Gpu, RuntimeInformation.OSArchitecture, Hub.S.Ai.Llama.Backend);
    public IReadOnlyList<LlamaBuild> Builds => LlamaCatalog.All(RuntimeInformation.OSArchitecture).ToList();

    public string ModelsFolder => Hub.Core.Models.Folder;
    public IReadOnlyList<LocalModel> Models => Hub.Core.Models.Models;
    /// <summary>Files in the models folders that couldn't be read, in plain words.</summary>
    public IReadOnlyList<string> Problems => Hub.Core.Models.Problems;
    public IReadOnlyList<ModelRecommendation> Recommended => ModelDownloads.RecommendedFor(Gpu);
    /// <summary>Whether a suggestion is already in the library.</summary>
    public bool Have(ModelRecommendation r) => Hub.Core.Models.Find(r.Id) is not null;
    public bool Downloading => Hub.Core.ModelDownloads.Busy;
    public ModelDownloadProgress? DownloadProgress => Hub.Core.ModelDownloads.Current;

    /// <summary>The model servers running now (one on the card, background ones on the processor).</summary>
    public IReadOnlyList<LlamaInstanceInfo> Running => Hub.Llama.Instances;
    /// <summary>Measured writing speeds and MTP's choice for a model, if any.</summary>
    public SpeedBook Speed => Hub.Llama.Speed;

    /// <summary>How <paramref name="model"/> fits the card at <paramref name="context"/> tokens (0: the context Aqua would give it).</summary>
    public FitEstimate Fit(LocalModel model, int context = 0)
    {
        var llama = Hub.S.Ai.Llama;
        var ctx = context > 0 ? context : HardwareFit.Context(model, llama.CacheType, Gpu, llama.ContextTokens);
        return HardwareFit.Estimate(model, ctx, llama.CacheType, Gpu, model.Vision);
    }

    /// <summary>The model holding <paramref name="role"/> (main is Ask Aqua's model), or null.</summary>
    public LocalModel? ForRole(string role) => Hub.Core.Models.Find(role switch
    {
        "main" => Hub.S.Ai.Model,
        "creative" => Hub.S.Ai.Llama.CreativeModel,
        "background" => Hub.S.Ai.Llama.BackgroundModel,
        "search" => Hub.S.Ai.Llama.SearchModel,
        _ => null,
    });

    /// <summary>Gives <paramref name="role"/> to a model (null: none — main then picks the largest that fits).</summary>
    public void SetRole(string role, LocalModel? model)
    {
        if (!Roles.Contains(role)) throw new ArgumentException("Unknown role: " + role, nameof(role));
        // An Ollama model is kept by its Ollama name, so the setting still works if Ask Aqua goes back to Ollama.
        var value = model is null ? "" : model.Id.StartsWith("ollama:", StringComparison.Ordinal) ? model.Id[7..] : model.Id;
        Hub.Core.Settings.Update(s =>
        {
            switch (role)
            {
                case "main": s.Ai.Model = value; break;
                case "creative": s.Ai.Llama.CreativeModel = value; break;
                case "background": s.Ai.Llama.BackgroundModel = value; break;
                default: s.Ai.Llama.SearchModel = value; break;
            }
        });
        Raise();
    }

    /// <summary>Switches Ask Aqua (and the agents) to Aqua's runtime, or back to Ollama; the graphics card's model is stopped when leaving.</summary>
    public async Task UseRuntimeAsync(bool on)
    {
        Hub.Core.Settings.Update(s => s.Ai.Provider = on ? "llamacpp" : "ollama");
        if (!on) await Hub.Llama.UnloadAsync(null, "Ask Aqua uses Ollama again").ConfigureAwait(false);
        Recheck();
        Raise();
    }

    /// <summary>Downloads and installs <paramref name="build"/> (default: the recommended one). Null in an E2E dry run.</summary>
    public async Task<LlamaInstall?> InstallRuntimeAsync(LlamaBuild? build = null, CancellationToken ct = default)
    {
        build ??= RecommendedBuild;
        if (Sandbox.Intercept("llama-install", build.Backend)) return null;
        var install = await Hub.Core.LlamaSetup.InstallAsync(build, ct).ConfigureAwait(false);
        Recheck();
        return install;
    }

    public void CancelInstall() => Hub.Core.LlamaSetup.Cancel();

    /// <summary>Stops the model servers and deletes the runtime (models are kept).</summary>
    public async Task RemoveRuntimeAsync()
    {
        if (Sandbox.Intercept("llama-remove")) return;
        await Hub.Llama.UnloadAsync(null, "the runtime is being removed").ConfigureAwait(false);
        Hub.Core.LlamaSetup.Remove();
        Recheck();
    }

    /// <summary>
    /// The devices the runtime sees ("CUDA0: NVIDIA GeForce RTX 4070 SUPER (12281 MiB, 11069 MiB free)"), from
    /// <c>llama-server --list-devices</c>: an empty list means it would run on the processor only.
    /// </summary>
    public async Task<IReadOnlyList<string>> DevicesAsync(CancellationToken ct = default)
    {
        if (Hub.Llama.ServerPath is not { } server) throw new LlamaRuntimeException("Aqua's runtime isn't installed.");
        var lines = new List<string>();
        using var process = Hub.LlamaHost.Start(server, ["--list-devices"], new Dictionary<string, string>(), Path.GetDirectoryName(server)!,
            line => { lock (lines) lines.Add(line); });
        for (var i = 0; i < 100 && !process.HasExited; i++) await Task.Delay(200, ct).ConfigureAwait(false);
        if (!process.HasExited) process.Kill();
        lock (lines)
            return lines.SkipWhile(l => !l.StartsWith("Available devices", StringComparison.OrdinalIgnoreCase)).Skip(1)
                .Select(l => l.Trim()).Where(l => l.Contains(':')).ToList();
    }

    public Task<IReadOnlyList<HfRepo>> SearchAsync(string query, CancellationToken ct = default) => Hub.Core.ModelDownloads.SearchAsync(query, ct);
    public Task<HfRepoFiles> FilesAsync(string repo, CancellationToken ct = default) => Hub.Core.ModelDownloads.FilesAsync(repo, ct);

    /// <summary>Downloads a suggested model. Null in an E2E dry run.</summary>
    public async Task<LocalModel?> DownloadAsync(ModelRecommendation r, CancellationToken ct = default)
    {
        if (Sandbox.Intercept("model-download", r.Id)) return null;
        return await Hub.Core.ModelDownloads.DownloadAsync(r, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads a file a search found (and its projector, for pictures). Null in an E2E dry run.</summary>
    public async Task<LocalModel?> DownloadAsync(HfRepoFiles repo, HfFile file, HfFile? projector, CancellationToken ct = default)
    {
        if (Sandbox.Intercept("model-download", "hf:" + repo.Repo + "/" + file.Name)) return null;
        return await Hub.Core.ModelDownloads.DownloadAsync(repo.Repo, repo.Revision, file, projector, ct).ConfigureAwait(false);
    }

    public void CancelDownload() => Hub.Core.ModelDownloads.Cancel();

    /// <summary>Deletes unfinished downloads; false while one runs.</summary>
    public bool DiscardUnfinished() => Sandbox.Intercept("model-discard-unfinished") || Hub.Core.ModelDownloads.DiscardUnfinished();

    /// <summary>Deletes one of Aqua's own downloads (Ollama's models are Ollama's to remove); its roles are cleared.</summary>
    public async Task<bool> RemoveModelAsync(LocalModel model)
    {
        if (model.Source != ModelSource.Aqua) return false;
        if (Sandbox.Intercept("model-remove", model.Id)) return false;
        await Hub.Llama.UnloadAsync(model.Id, "it's being removed").ConfigureAwait(false);
        foreach (var role in Roles)
            if (ForRole(role)?.Id == model.Id) SetRole(role, null);
        return Hub.Core.Models.Remove(model.Id);
    }

    /// <summary>Stops a running model's server now (null: all of them).</summary>
    public Task StopAsync(string? modelId) => Hub.Llama.UnloadAsync(modelId, "you stopped it");

    /// <summary>Reads the models folders again (after the folder or the Ollama switch changes, or on request).</summary>
    public Task RefreshAsync() => Task.Run(() =>
    {
        _folder = Hub.Core.Models.Folder;
        Hub.Core.Models.Refresh();
    });

    public void Start() => _ = Task.Run(() => LoopAsync(_stop.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        await RefreshAsync().ConfigureAwait(false);
        if (InUse) Recheck();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    _gpu = GpuReader.Read(Hub.System);
                    if (++tick % 8 == 0 || Hub.Core.Models.Folder != _folder) await RefreshAsync().ConfigureAwait(false);
                    // A game or another app needs the card: stop the model on it (the next question starts it again).
                    if (InUse && Hub.S.Ai.StopOllamaForGpu && Hub.Llama.OnGpu is not null && !Hub.Core.Llm.IsBusy
                        && Hub.Platform.GpuContention(serverHasModelLoaded: true) is { } why)
                        await Hub.Llama.UnloadGpuAsync("to free the graphics card for " + why, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn("llama", "Model check failed", ex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnLibraryChanged()
    {
        if (InUse) Recheck();
        Raise();
    }

    /// <summary>Ask Aqua's status (which model, ready or not) follows the runtime and the library.</summary>
    private static void Recheck()
    {
        if (Hub.S.Ai.Provider != "llamacpp" && Hub.S.Ai.Provider != "ollama") return;
        _ = Task.Run(async () =>
        {
            try { await Hub.Core.Llm.CheckAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug("llama", "health check: " + ex.Message); }
        });
    }

    private void Raise() => Hub.OnUi(() => Changed?.Invoke());

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
