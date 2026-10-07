using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AquaHub.Core.Ai.Imaging;

/// <summary>
/// Minimal ComfyUI HTTP API client for native FLUX.2 Klein 4B text-to-image graphs.
/// It talks only to an explicitly configured loopback endpoint.
/// </summary>
public sealed class ComfyImageClient
{
    private static readonly string[] RequiredNodes =
    [
        "UNETLoader", "CLIPLoader", "VAELoader", "CLIPTextEncode", "RandomNoise",
        "KSamplerSelect", "Flux2Scheduler", "EmptyFlux2LatentImage", "SamplerCustomAdvanced",
        "VAEDecode", "SaveImage"
    ];

    private const string PromptOutputNode = "15";
    private readonly HttpClient _http;
    private readonly ImageGenerationOptions _options;
    private readonly Uri _endpoint;

    public ComfyImageClient(HttpClient httpClient, ImageGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _http = httpClient;
        _options = options;
        _endpoint = new Uri(options.Endpoint.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public Task<ImageModelInventory> DiscoverAsync(CancellationToken cancellationToken = default) =>
        DiscoverAsync(requiresImageEditing: false, cancellationToken);

    public async Task<ImageModelInventory> DiscoverAsync(bool requiresImageEditing, CancellationToken cancellationToken = default)
    {
        var diffusion = await GetModelNamesAsync("diffusion_models", cancellationToken).ConfigureAwait(false);
        var encoders = await GetModelNamesAsync("text_encoders", cancellationToken).ConfigureAwait(false);
        var vaes = await GetModelNamesAsync("vae", cancellationToken).ConfigureAwait(false);
        using var objectInfo = await GetJsonAsync("object_info", cancellationToken).ConfigureAwait(false);
        var nodeNames = objectInfo.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var requiredNodes = RequiredNodes
            .Concat(_options.Variant == ImageModelVariant.Base4B ? ["CFGGuider"] : ["BasicGuider"])
            .Concat(requiresImageEditing ? ["LoadImage", "VAEEncode", "ReferenceLatent"] : [])
            .ToArray();
        var missingNodes = requiredNodes.Where(n => !nodeNames.Contains(n)).ToImmutableArray();
        var neededModels = new[]
        {
            (diffusion, _options.SelectedDiffusionModel),
            (encoders, _options.TextEncoderModel),
            (vaes, _options.VaeModel)
        };
        var missingModels = neededModels.Where(pair => !pair.Item1.Contains(pair.Item2, StringComparer.Ordinal))
            .Select(pair => pair.Item2).ToImmutableArray();
        return new ImageModelInventory(diffusion, encoders, vaes, missingNodes, missingModels)
        {
            Mismatches = PairingProblems(_options.SelectedDiffusionModel, _options.TextEncoderModel, _options.VaeModel),
        };
    }

    /// <summary>
    /// Files that can't work together in Aqua's FLUX.2 Klein graph, told by their usual names (an unusual name passes):
    /// Klein 4B reads the Qwen3 4B text encoder and Klein 9B the Qwen3 8B one; FLUX.2 has its own VAE (FLUX.1's
    /// ae.safetensors has the wrong number of channels); T5 and CLIP encoders, and other model families, need other graphs.
    /// ComfyUI would otherwise take the job and fail it with "mat1 and mat2 shapes cannot be multiplied".
    /// </summary>
    public static ImmutableArray<string> PairingProblems(string diffusionModel, string textEncoder, string vae)
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        var d = Path.GetFileName(diffusionModel).ToLowerInvariant();
        var e = Path.GetFileName(textEncoder).ToLowerInvariant();
        var v = Path.GetFileName(vae).ToLowerInvariant();
        string[] otherFamilies = ["chroma", "flux1", "flux-1", "flux.1", "sdxl", "sd3", "qwen-image", "qwen_image", "z_image", "z-image", "hidream", "wan2", "ltx"];
        if (!d.Contains("klein") && otherFamilies.FirstOrDefault(d.Contains) is { } family)
            problems.Add($"{diffusionModel} isn't a FLUX.2 Klein model ({family}); Image Gen builds Klein graphs, so pick a flux-2-klein file.");
        var kleinSize = !d.Contains("klein") ? 0 : d.Contains("9b") ? 9 : d.Contains("4b") ? 4 : 0;
        var encoderSize = !e.Contains("qwen") ? 0 : e.Contains("8b") ? 8 : e.Contains("4b") ? 4 : 0;
        if (e.Contains("t5") || e.Contains("clip_l") || e.Contains("clip_g") || e.Contains("llava"))
            problems.Add($"{textEncoder} isn't a Qwen3 text encoder; FLUX.2 Klein reads qwen_3_4b (Klein 4B) or qwen_3_8b (Klein 9B).");
        else if (kleinSize == 4 && encoderSize == 8)
            problems.Add($"{diffusionModel} is Klein 4B, which reads the Qwen3 4B text encoder (qwen_3_4b.safetensors), not {textEncoder}.");
        else if (kleinSize == 9 && encoderSize == 4)
            problems.Add($"{diffusionModel} is Klein 9B, which reads the Qwen3 8B text encoder (qwen_3_8b, about 8 GB in fp8 from Comfy-Org's flux2-klein-9B files), not {textEncoder}.");
        if (v is "ae.safetensors" or "ae.sft" || v.Contains("flux1") || v.Contains("sdxl") || v.Contains("sd3"))
            problems.Add($"{vae} is an older VAE; FLUX.2 reads its own (flux2-vae.safetensors).");
        return problems.ToImmutable();
    }

    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        if (request.ReferenceImage is not null)
            await EnsureReferenceImageExistsAsync(request.ReferenceImage, cancellationToken).ConfigureAwait(false);
        var inventory = await DiscoverAsync(request.ReferenceImage is not null, cancellationToken).ConfigureAwait(false);
        if (!inventory.IsReady)
        {
            var issues = new List<string>();
            if (!inventory.MissingRequiredNodes.IsEmpty)
                issues.Add("ComfyUI is missing required native nodes: " + string.Join(", ", inventory.MissingRequiredNodes));
            if (!inventory.MissingRequiredModels.IsEmpty)
                issues.Add("ComfyUI is missing model files: " + string.Join(", ", inventory.MissingRequiredModels));
            issues.AddRange(inventory.Mismatches);
            throw new ImageGenerationException(string.Join(" ", issues));
        }

        var seed = request.Seed ?? CreateSeed();
        var requestedPromptId = Guid.NewGuid().ToString("D");
        var graph = BuildPromptGraph(request, seed);
        var started = Stopwatch.StartNew();
        progress?.Report("Sending image generation to local ComfyUI…");

        var submitBody = new JsonObject
        {
            ["prompt"] = graph,
            ["prompt_id"] = requestedPromptId
        };
        cancellationToken.ThrowIfCancellationRequested();
        HttpResponseMessage submitResponse;
        try { submitResponse = await PostJsonAsync("prompt", submitBody, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ImageJobUncertainException(null,
                "The connection ended while submitting the image request. ComfyUI may have accepted it; keep the GPU reserved and wait for its queue to become idle.", ex);
        }
        using (submitResponse)
        {
        if (!submitResponse.IsSuccessStatusCode)
        {
            var apiError = await CreateApiExceptionAsync("submit generation", submitResponse).ConfigureAwait(false);
            if ((int)submitResponse.StatusCode < 400 || (int)submitResponse.StatusCode >= 500)
                throw new ImageJobUncertainException(null,
                    "ComfyUI returned an ambiguous response during submission, so the job may still be queued. Keep the GPU reserved and wait for its queue to become idle.", apiError);
            throw apiError;
        }
        JsonDocument submit;
        try { submit = await ReadResponseJsonAsync(submitResponse, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or ImageGenerationException or IOException)
        {
            throw new ImageJobUncertainException(null,
                "ComfyUI accepted the image request, but Aqua couldn't read its job ID. Keep the GPU reserved and wait for the ComfyUI queue to become idle.", ex);
        }
        using (submit)
        {
        if (submit.RootElement.TryGetProperty("error", out var submitError))
            throw new ImageGenerationException("ComfyUI rejected the image workflow: " + Compact(submitError));
        if (!submit.RootElement.TryGetProperty("prompt_id", out var promptIdElement) ||
            promptIdElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(promptIdElement.GetString()))
            throw new ImageJobUncertainException(null,
                "ComfyUI accepted the image request without returning a job ID. Keep the GPU reserved and wait for the ComfyUI queue to become idle.");
        var promptId = promptIdElement.GetString()!;

        try
        {
            progress?.Report("Waiting for FLUX.2 Klein…");
            using var history = await WaitForHistoryAsync(promptId, progress, cancellationToken).ConfigureAwait(false);
            started.Stop();
            var image = ReadOutputImage(history.RootElement, promptId);
            var png = await ReadImageAsync(image, cancellationToken).ConfigureAwait(false);
            progress?.Report("Image generated.");

            if (_options.UnloadModelAfterGeneration)
                await TryReleaseModelsAsync(progress, cancellationToken).ConfigureAwait(false);

            return new ImageGenerationResult(png, seed, started.Elapsed, image.FileName)
            {
                ComfyOutputPath = image.ComfyOutputPath
            };
        }
        catch (Exception ex) when (ex is not ImageJobUncertainException)
        {
            // Any failure after submit may leave a live ComfyUI workflow. Cancel it with
            // an ID-scoped API where supported, otherwise drain it before releasing GPU.
            if (!await SafelyCancelOrDrainAsync(promptId, progress).ConfigureAwait(false))
                throw new ImageJobUncertainException(promptId,
                    $"Aqua couldn't confirm that ComfyUI job {promptId} stopped. Keep the GPU reserved and retry local image recovery before using Aqua's language model.", ex);
            throw;
        }
        }
        }
    }

    /// <summary>Requests model unloading only when ComfyUI reports both queues empty.</summary>
    public async Task<bool> ReleaseModelsAsync(CancellationToken cancellationToken = default)
    {
        if (await HasQueuedOrRunningJobsAsync(cancellationToken).ConfigureAwait(false))
            return false;

        var body = new JsonObject { ["unload_models"] = true, ["free_memory"] = true };
        using var response = await PostJsonAsync("free", body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync("release idle ComfyUI models", response).ConfigureAwait(false);

        var timer = Stopwatch.StartNew();
        var stableReleasedSamples = 0;
        while (timer.Elapsed < _options.ReleaseConfirmationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HasQueuedOrRunningJobsAsync(cancellationToken).ConfigureAwait(false))
                return false;
            var activeVram = await GetActiveVramAsync(cancellationToken).ConfigureAwait(false);
            stableReleasedSamples = activeVram <= 256L * 1024 * 1024 ? stableReleasedSamples + 1 : 0;
            if (stableReleasedSamples >= 2) return true;
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new ImageGenerationException("ComfyUI accepted the model-unload request, but system_stats still shows GPU memory in active use.");
    }

    /// <summary>Recovery helper for a job whose submit/cancel outcome could not be confirmed.</summary>
    public async Task WaitUntilIdleAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var deadline = Stopwatch.StartNew();
        var stableEmptyChecks = 0;
        while (deadline.Elapsed < _options.CleanupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var queue = await GetQueueAsync(cancellationToken).ConfigureAwait(false);
            if (HasItems(queue.RootElement, "queue_running") || HasItems(queue.RootElement, "queue_pending"))
                stableEmptyChecks = 0;
            else if (++stableEmptyChecks >= 2)
                return;
            progress?.Report("Waiting for ComfyUI to finish all local jobs before releasing the GPU…");
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new ImageJobUncertainException(null,
            "ComfyUI's queue did not become idle in time. Keep the GPU reserved and try image recovery again.");
    }

    internal JsonObject BuildPromptGraph(ImageGenerationRequest request, ulong seed)
    {
        ValidateRequest(request);
        var steps = request.Steps ?? (_options.Variant == ImageModelVariant.Distilled4B ? 4 : 20);
        var nodes = new JsonObject
        {
            ["1"] = Node("UNETLoader", new JsonObject
            {
                ["unet_name"] = _options.SelectedDiffusionModel,
                ["weight_dtype"] = "default"
            }),
            ["2"] = Node("CLIPLoader", new JsonObject
            {
                ["clip_name"] = _options.TextEncoderModel,
                ["type"] = "flux2",
                ["device"] = "default"
            }),
            ["3"] = Node("VAELoader", new JsonObject { ["vae_name"] = _options.VaeModel }),
            ["4"] = Node("CLIPTextEncode", new JsonObject
            {
                ["clip"] = Link("2", 0),
                ["text"] = request.Prompt.Trim()
            }),
            ["8"] = Node("RandomNoise", new JsonObject { ["noise_seed"] = seed }),
            ["9"] = Node("KSamplerSelect", new JsonObject { ["sampler_name"] = "euler" }),
            ["10"] = Node("Flux2Scheduler", new JsonObject
            {
                ["steps"] = steps,
                ["width"] = request.Width,
                ["height"] = request.Height
            }),
            ["11"] = Node("EmptyFlux2LatentImage", new JsonObject
            {
                ["width"] = request.Width,
                ["height"] = request.Height,
                ["batch_size"] = 1
            }),
            ["13"] = Node("SamplerCustomAdvanced", new JsonObject
            {
                ["noise"] = Link("8", 0),
                ["guider"] = Link("12", 0),
                ["sampler"] = Link("9", 0),
                ["sigmas"] = Link("10", 0),
                ["latent_image"] = Link("11", 0)
            }),
            ["14"] = Node("VAEDecode", new JsonObject
            {
                ["samples"] = Link("13", 0),
                ["vae"] = Link("3", 0)
            }),
            [PromptOutputNode] = Node("SaveImage", new JsonObject
            {
                ["images"] = Link("14", 0),
                ["filename_prefix"] = "AquaHub/" + Guid.NewGuid().ToString("N")
            })
        };

        if (_options.Variant == ImageModelVariant.Base4B)
        {
            nodes["5"] = Node("CLIPTextEncode", new JsonObject
            {
                ["clip"] = Link("2", 0),
                ["text"] = ""
            });
            nodes["12"] = Node("CFGGuider", new JsonObject
            {
                ["model"] = Link("1", 0),
                ["positive"] = Link("4", 0),
                ["negative"] = Link("5", 0),
                ["cfg"] = request.Guidance ?? 5f
            });
        }
        else
        {
            nodes["12"] = Node("BasicGuider", new JsonObject
            {
                ["model"] = Link("1", 0),
                ["conditioning"] = Link("4", 0)
            });
        }

        if (request.ReferenceImage is not null)
        {
            nodes["16"] = Node("LoadImage", new JsonObject
            {
                ["image"] = request.ReferenceImage + " [output]"
            });
            nodes["17"] = Node("VAEEncode", new JsonObject
            {
                ["pixels"] = Link("16", 0),
                ["vae"] = Link("3", 0)
            });
            if (_options.Variant == ImageModelVariant.Base4B)
            {
                nodes["18"] = Node("ReferenceLatent", new JsonObject
                {
                    ["conditioning"] = Link("4", 0),
                    ["latent"] = Link("17", 0)
                });
                nodes["19"] = Node("ReferenceLatent", new JsonObject
                {
                    ["conditioning"] = Link("5", 0),
                    ["latent"] = Link("17", 0)
                });
                nodes["12"]!["inputs"]!["positive"] = Link("18", 0);
                nodes["12"]!["inputs"]!["negative"] = Link("19", 0);
            }
            else
            {
                nodes["18"] = Node("ReferenceLatent", new JsonObject
                {
                    ["conditioning"] = Link("4", 0),
                    ["latent"] = Link("17", 0)
                });
                nodes["12"]!["inputs"]!["conditioning"] = Link("18", 0);
            }
        }

        return nodes;
    }

    private async Task EnsureReferenceImageExistsAsync(string relativePath, CancellationToken cancellationToken)
    {
        var separator = relativePath.LastIndexOf('/');
        var fileName = separator < 0 ? relativePath : relativePath[(separator + 1)..];
        var subfolder = separator < 0 ? "" : relativePath[..separator];
        var query = "view?filename=" + Uri.EscapeDataString(fileName) +
                    "&subfolder=" + Uri.EscapeDataString(subfolder) + "&type=output";
        using var response = await _http.GetAsync(new Uri(_endpoint, query), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ImageGenerationException("The reference image is no longer available in ComfyUI's output folder. Restore its exported PNG there with the same name, or clear the reference and regenerate from a full description.");
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync("check the selected reference image in ComfyUI", response).ConfigureAwait(false);
    }

    private async Task<ImmutableArray<string>> GetModelNamesAsync(string folder, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("models/" + folder, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ImageGenerationException($"ComfyUI returned an invalid model list for {folder}.");
        return document.RootElement.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToImmutableArray();
    }

    private async Task<JsonDocument> WaitForHistoryAsync(string promptId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var expires = Stopwatch.StartNew();
        while (expires.Elapsed < _options.JobTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var history = await GetJsonAsync("history/" + Uri.EscapeDataString(promptId), cancellationToken).ConfigureAwait(false);
            if (history.RootElement.TryGetProperty(promptId, out var entry))
            {
                if (entry.TryGetProperty("status", out var status))
                {
                    if (status.TryGetProperty("status_str", out var statusStr) &&
                        string.Equals(statusStr.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        throw new ImageGenerationException("ComfyUI image generation failed: " + Compact(status));
                    if (status.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True)
                        return JsonDocument.Parse(entry.GetRawText());
                }
                if (entry.TryGetProperty("outputs", out _))
                    return JsonDocument.Parse(entry.GetRawText());
            }

            progress?.Report("FLUX.2 Klein is working…");
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"ComfyUI did not finish image generation within {_options.JobTimeout}.");
    }

    private async Task<bool> SafelyCancelOrDrainAsync(string promptId, IProgress<string>? progress)
    {
        var timer = Stopwatch.StartNew();
        var cancelMessageSent = false;
        var connectionMessageSent = false;
        var pendingDeleteConfirmed = false;
        var targetedCancelSent = false;
        var targetedApiUnavailable = false;
        while (timer.Elapsed < _options.CleanupTimeout)
        {
            try
            {
                using var history = await GetJsonAsync("history/" + Uri.EscapeDataString(promptId), CancellationToken.None).ConfigureAwait(false);
                if (history.RootElement.TryGetProperty(promptId, out _))
                    return true;

                using var queue = await GetQueueAsync(CancellationToken.None).ConfigureAwait(false);
                var pending = QueueContains(queue.RootElement, "queue_pending", promptId);
                var running = QueueContains(queue.RootElement, "queue_running", promptId);
                if (pending)
                {
                    var deleteBody = new JsonObject { ["delete"] = new JsonArray(JsonValue.Create(promptId)) };
                    using var deleteResponse = await PostJsonAsync("queue", deleteBody, CancellationToken.None).ConfigureAwait(false);
                    if (!deleteResponse.IsSuccessStatusCode)
                        throw await CreateApiExceptionAsync("remove the canceled image from ComfyUI's queue", deleteResponse).ConfigureAwait(false);
                    pendingDeleteConfirmed = true;
                    continue;
                }

                // Recent ComfyUI releases expose an explicitly job-scoped cancel API. Probe
                // the submitted job first: an older server returns 404, so we never risk
                // falling back to its global /interrupt behavior.
                if (running && !targetedCancelSent && !targetedApiUnavailable)
                {
                    using var jobStatus = await _http.GetAsync(new Uri(_endpoint, "api/jobs/" + Uri.EscapeDataString(promptId)),
                        HttpCompletionOption.ResponseHeadersRead, CancellationToken.None).ConfigureAwait(false);
                    if (jobStatus.IsSuccessStatusCode)
                    {
                        using var cancel = await _http.PostAsync(new Uri(_endpoint, "api/jobs/" + Uri.EscapeDataString(promptId) + "/cancel"),
                            content: null, CancellationToken.None).ConfigureAwait(false);
                        if (cancel.IsSuccessStatusCode)
                        {
                            progress?.Report("Cancelling this image job…");
                            cancelMessageSent = true;
                            targetedCancelSent = true;
                        }
                    }
                    else if (jobStatus.StatusCode == HttpStatusCode.NotFound)
                        targetedApiUnavailable = true;
                }

                if (pendingDeleteConfirmed && !pending && !running)
                    return true;

                // Older ComfyUI versions only have global /interrupt. Drain our own job so
                // cancellation cannot interrupt a different workflow. Keep the caller's GPU
                // lease until history confirms this job has ended.
                if (!cancelMessageSent)
                {
                    progress?.Report("Cancel requested. This ComfyUI version has no safe targeted cancel API, so Aqua is waiting for this job to finish.");
                    cancelMessageSent = true;
                }
                await Task.Delay(_options.PollInterval, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or ImageGenerationException or JsonException or TaskCanceledException)
            {
                if (!connectionMessageSent)
                {
                    progress?.Report("Still waiting to confirm this image job has stopped. Keeping the GPU reserved while ComfyUI reconnects: " + ex.Message);
                    connectionMessageSent = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
        }
        return false;
    }

    private async Task TryReleaseModelsAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            if (!await ReleaseModelsAsync(cancellationToken).ConfigureAwait(false))
                progress?.Report("Another ComfyUI job is queued or running; keeping its models loaded.");
        }
        catch (Exception ex) when (ex is HttpRequestException or ImageGenerationException or TaskCanceledException)
        {
            progress?.Report("Image is ready, but ComfyUI could not unload its models: " + ex.Message);
        }
    }

    private async Task<bool> HasQueuedOrRunningJobsAsync(CancellationToken cancellationToken)
    {
        using var queue = await GetQueueAsync(cancellationToken).ConfigureAwait(false);
        return HasItems(queue.RootElement, "queue_running") || HasItems(queue.RootElement, "queue_pending");
    }

    private async Task<long> GetActiveVramAsync(CancellationToken cancellationToken)
    {
        using var stats = await GetJsonAsync("system_stats", cancellationToken).ConfigureAwait(false);
        if (!stats.RootElement.TryGetProperty("devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array || devices.GetArrayLength() == 0 ||
            !devices[0].TryGetProperty("type", out var type) || type.GetString() == "cpu" ||
            !devices[0].TryGetProperty("torch_vram_total", out var total) || !total.TryGetInt64(out var totalBytes) ||
            !devices[0].TryGetProperty("torch_vram_free", out var free) || !free.TryGetInt64(out var freeBytes))
            throw new ImageGenerationException("ComfyUI did not report accelerator allocator statistics, so Aqua cannot verify that its models were unloaded.");
        // ComfyUI reports its allocator's reserved bytes and allocator-free bytes here;
        // their difference is the active allocated memory owned by this process.
        return Math.Max(0, totalBytes - freeBytes);
    }

    private Task<JsonDocument> GetQueueAsync(CancellationToken cancellationToken) => GetJsonAsync("queue", cancellationToken);

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(_endpoint, path), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync("GET " + path, response).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException ex) { throw new ImageGenerationException($"ComfyUI returned invalid JSON for {path}.", ex); }
    }

    private static async Task<JsonDocument> ReadResponseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(response.Content, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException ex) { throw new ImageGenerationException("ComfyUI returned invalid JSON.", ex); }
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(body);
        return await _http.PostAsync(new Uri(_endpoint, path), content, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadImageAsync(OutputImage image, CancellationToken cancellationToken)
    {
        var query = "view?filename=" + Uri.EscapeDataString(image.FileName) +
                    "&subfolder=" + Uri.EscapeDataString(image.Subfolder) +
                    "&type=" + Uri.EscapeDataString(image.Type);
        using var response = await _http.GetAsync(new Uri(_endpoint, query), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateApiExceptionAsync("retrieve generated image", response).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(response.Content, _options.MaximumImageBytes, cancellationToken).ConfigureAwait(false);
        byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < pngSignature.Length || !bytes.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature))
            throw new ImageGenerationException("ComfyUI returned an image that is not a valid PNG.");
        return bytes;
    }

    private static OutputImage ReadOutputImage(JsonElement entry, string promptId)
    {
        if (!entry.TryGetProperty("outputs", out var outputs) ||
            !outputs.TryGetProperty(PromptOutputNode, out var output) ||
            !output.TryGetProperty("images", out var images) || images.GetArrayLength() == 0)
            throw new ImageGenerationException("ComfyUI finished the workflow but returned no saved image.");
        var first = images[0];
        var fileName = first.GetProperty("filename").GetString();
        var subfolder = first.TryGetProperty("subfolder", out var subfolderElement) ? subfolderElement.GetString() ?? "" : "";
        var type = first.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "output" : "output";
        if (string.IsNullOrWhiteSpace(fileName) || !IsSafeRelativeImagePath(string.IsNullOrEmpty(subfolder) ? fileName : subfolder + "/" + fileName))
            throw new ImageGenerationException("ComfyUI returned an unsafe image filename.");
        if (type != "output")
            throw new ImageGenerationException("ComfyUI saved the generated image outside its output folder; refusing to return an unsafe output path.");
        return new OutputImage(fileName, subfolder, type,
            string.IsNullOrEmpty(subfolder) ? fileName : subfolder + "/" + fileName);
    }

    internal static bool IsSafeRelativeImagePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 && !path.StartsWith('/') &&
        !path.Contains('\\') && !path.Contains('[') && !path.Contains(']') &&
        !path.Any(char.IsControl) && path.Split('/').All(part =>
            !string.IsNullOrWhiteSpace(part) && part is not ("." or "..") &&
            part.Trim() == part && !part.Contains(':'));

    private static JsonObject Node(string classType, JsonObject inputs) => new()
    {
        ["class_type"] = classType,
        ["inputs"] = inputs
    };

    private static JsonArray Link(string nodeId, int output) => new(JsonValue.Create(nodeId), JsonValue.Create(output));

    private static bool HasItems(JsonElement root, string key) =>
        root.TryGetProperty(key, out var queue) && queue.ValueKind == JsonValueKind.Array && queue.GetArrayLength() > 0;

    private static bool QueueContains(JsonElement root, string key, string promptId)
    {
        if (!root.TryGetProperty(key, out var queue) || queue.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in queue.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 1 && item[1].GetString() == promptId) return true;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("prompt_id", out var id) && id.GetString() == promptId) return true;
        }
        return false;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
            throw new ImageGenerationException($"ComfyUI response exceeds the {maximumBytes / (1024 * 1024)} MiB limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maximumBytes)
                throw new ImageGenerationException($"ComfyUI response exceeds the {maximumBytes / (1024 * 1024)} MiB limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task<ImageGenerationException> CreateApiExceptionAsync(string action, HttpResponseMessage response)
    {
        var bytes = await ReadBoundedAsync(response.Content, 4 * 1024, CancellationToken.None).ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (text.Length > 1200) text = text[..1200] + "…";
        return new ImageGenerationException($"Could not {action} ({(int)response.StatusCode} {response.ReasonPhrase}): {text}");
    }

    private static string Compact(JsonElement element)
    {
        var text = element.GetRawText();
        return text.Length > 1200 ? text[..1200] + "…" : text;
    }

    private static ulong CreateSeed()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes) & 0x001f_ffff_ffff_ffff;
    }

    private static void ValidateOptions(ImageGenerationOptions options)
    {
        if (options.Endpoint is null || !options.Endpoint.IsAbsoluteUri || !options.Endpoint.IsLoopback ||
            options.Endpoint.Scheme is not ("http" or "https") || options.Endpoint.UserInfo.Length != 0 ||
            options.Endpoint.Query.Length != 0 || options.Endpoint.Fragment.Length != 0)
            throw new ArgumentException("The ComfyUI endpoint must be an absolute loopback HTTP or HTTPS URL.", nameof(options));
        if (options.JobTimeout <= TimeSpan.Zero || options.PollInterval <= TimeSpan.Zero || options.CleanupTimeout <= TimeSpan.Zero || options.ReleaseConfirmationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Job, cleanup, release-confirmation timeouts and polling interval must be positive.");
        if (options.MaximumImageBytes is < 1024 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum image size must be between 1 KiB and 256 MiB.");
        ValidateModelName(options.SelectedDiffusionModel, nameof(options.DiffusionModel));
        ValidateModelName(options.TextEncoderModel, nameof(options.TextEncoderModel));
        ValidateModelName(options.VaeModel, nameof(options.VaeModel));
    }

    private static void ValidateRequest(ImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 16_000)
            throw new ArgumentException("Prompt must contain between 1 and 16,000 characters.", nameof(request));
        if (request.Width is < 256 or > 2048 || request.Height is < 256 or > 2048 || request.Width % 16 != 0 || request.Height % 16 != 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Width and height must be multiples of 16 from 256 through 2048.");
        if (request.Steps is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request), "Steps must be from 1 through 100.");
        if (request.Seed is ulong seed && seed > 0x7fff_ffff_ffff_ffff) throw new ArgumentOutOfRangeException(nameof(request), "Seed must be a non-negative signed 64-bit value.");
        if (request.ReferenceImage is not null && !IsSafeRelativeImagePath(request.ReferenceImage))
            throw new ArgumentException("Reference images must be safe relative ComfyUI output paths (subfolder/name).", nameof(request));
        if (request.Guidance is float guidance && (guidance is < 0 or > 20 || float.IsNaN(guidance) || float.IsInfinity(guidance)))
            throw new ArgumentOutOfRangeException(nameof(request), "Guidance must be from 0 through 20.");
    }

    private static void ValidateModelName(string modelName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Length > 260 || modelName.StartsWith('/') ||
            modelName.Contains('\\') || modelName.Split('/').Any(part => part is ".." or "."))
            throw new ArgumentException("Model names must be safe relative ComfyUI model paths.", parameterName);
    }

    private sealed record OutputImage(string FileName, string Subfolder, string Type, string ComfyOutputPath);
}
