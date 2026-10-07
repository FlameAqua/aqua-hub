using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AquaHub.Core.Ai.Imaging;

namespace AquaHub.Tests;

public sealed class ComfyImageClientTests
{
    [Fact]
    public void BaseGraphUsesOfficialKleinBaseSamplingNodesAndConfiguredModelFiles()
    {
        var client = CreateClient(new DelegateHandler(_ => Json(HttpStatusCode.OK, "[]")));
        var graph = client.BuildPromptGraph(new ImageGenerationRequest("portrait", Seed: 17), 17);

        Assert.Equal("UNETLoader", graph["1"]?["class_type"]?.GetValue<string>());
        Assert.Equal("flux-2-klein-base-4b-fp8.safetensors", graph["1"]?["inputs"]?["unet_name"]?.GetValue<string>());
        Assert.Equal("qwen_3_4b.safetensors", graph["2"]?["inputs"]?["clip_name"]?.GetValue<string>());
        Assert.Equal("flux2", graph["2"]?["inputs"]?["type"]?.GetValue<string>());
        Assert.Equal("CFGGuider", graph["12"]?["class_type"]?.GetValue<string>());
        Assert.Equal(5f, graph["12"]?["inputs"]?["cfg"]?.GetValue<float>());
        Assert.Equal(20, graph["10"]?["inputs"]?["steps"]?.GetValue<int>());
        Assert.Equal(17UL, graph["8"]?["inputs"]?["noise_seed"]?.GetValue<ulong>());
        Assert.Equal("SaveImage", graph["15"]?["class_type"]?.GetValue<string>());
    }

    [Fact]
    public void DistilledGraphUsesFourStepBasicGuider()
    {
        var options = new ImageGenerationOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8188/"),
            Variant = ImageModelVariant.Distilled4B
        };
        var client = new ComfyImageClient(new HttpClient(new DelegateHandler(_ => Json(HttpStatusCode.OK, "[]"))), options);

        var graph = client.BuildPromptGraph(new ImageGenerationRequest("city at dusk"), 91);

        Assert.Equal("flux-2-klein-4b-fp8.safetensors", graph["1"]?["inputs"]?["unet_name"]?.GetValue<string>());
        Assert.Equal("BasicGuider", graph["12"]?["class_type"]?.GetValue<string>());
        Assert.Null(graph["5"]);
        Assert.Equal(4, graph["10"]?["inputs"]?["steps"]?.GetValue<int>());
    }

    [Fact]
    public void BaseEditGraphReferencesImageInPositiveAndNegativeConditioning()
    {
        var graph = CreateClient(new DelegateHandler(_ => Json(HttpStatusCode.OK, "[]")))
            .BuildPromptGraph(new ImageGenerationRequest("make it brighter") { ReferenceImage = "gallery/seed.png" }, 17);

        Assert.Equal("LoadImage", graph["16"]?["class_type"]?.GetValue<string>());
        Assert.Equal("gallery/seed.png [output]", graph["16"]?["inputs"]?["image"]?.GetValue<string>());
        Assert.Equal("VAEEncode", graph["17"]?["class_type"]?.GetValue<string>());
        Assert.Equal("16", graph["17"]?["inputs"]?["pixels"]?[0]?.GetValue<string>());
        Assert.Equal("ReferenceLatent", graph["18"]?["class_type"]?.GetValue<string>());
        Assert.Equal("4", graph["18"]?["inputs"]?["conditioning"]?[0]?.GetValue<string>());
        Assert.Equal("ReferenceLatent", graph["19"]?["class_type"]?.GetValue<string>());
        Assert.Equal("5", graph["19"]?["inputs"]?["conditioning"]?[0]?.GetValue<string>());
        Assert.Equal("18", graph["12"]?["inputs"]?["positive"]?[0]?.GetValue<string>());
        Assert.Equal("19", graph["12"]?["inputs"]?["negative"]?[0]?.GetValue<string>());
    }

    [Fact]
    public void DistilledEditGraphReferencesSingleConditioningStream()
    {
        var options = new ImageGenerationOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8188/"),
            Variant = ImageModelVariant.Distilled4B
        };
        var client = new ComfyImageClient(new HttpClient(new DelegateHandler(_ => Json(HttpStatusCode.OK, "[]"))), options);

        var graph = client.BuildPromptGraph(new ImageGenerationRequest("make it brighter") { ReferenceImage = "seed.png" }, 17);

        Assert.Equal("ReferenceLatent", graph["18"]?["class_type"]?.GetValue<string>());
        Assert.Equal("4", graph["18"]?["inputs"]?["conditioning"]?[0]?.GetValue<string>());
        Assert.Equal("18", graph["12"]?["inputs"]?["conditioning"]?[0]?.GetValue<string>());
        Assert.Null(graph["19"]);
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("gallery/../../secret.png")]
    [InlineData("gallery/seed.png [output]")]
    [InlineData("gallery\\seed.png")]
    [InlineData("gallery/seed.png\n [output]")]
    public void ReferencePathRejectsTraversalAndAnnotationInjection(string path)
    {
        var client = CreateClient(new DelegateHandler(_ => Json(HttpStatusCode.OK, "[]")));

        Assert.Throws<ArgumentException>(() => client.BuildPromptGraph(
            new ImageGenerationRequest("edit") { ReferenceImage = path }, 1));
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("folder\\image.png")]
    [InlineData("folder/image.png [output]")]
    public void ResultRejectsUnsafeComfyOutputPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => new ImageGenerationResult([], 1, TimeSpan.Zero, "image.png")
        {
            ComfyOutputPath = path
        });
    }

    [Fact]
    public async Task MissingServerReferenceFailsBeforeQueueingWithClearMessage()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/view", StringComparison.Ordinal)
            ? Json(HttpStatusCode.NotFound, "missing")
            : throw new InvalidOperationException("A missing reference must fail before discovery or submission."));

        var error = await Assert.ThrowsAsync<ImageGenerationException>(() => CreateClient(handler).GenerateAsync(
            new ImageGenerationRequest("edit") { ReferenceImage = "gallery/missing.png" }));

        Assert.Contains("no longer available", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
        Assert.EndsWith("filename=missing.png&subfolder=gallery&type=output", handler.Requests[0].RequestUri!.Query);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://192.0.2.1:8188/")]
    public void ClientRejectsNonLoopbackEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => CreateClient(new DelegateHandler(_ => Json(HttpStatusCode.OK, "[]")), endpoint));
    }

    [Fact]
    public async Task ModelReleaseDoesNotTouchComfyWhenAnotherJobIsQueued()
    {
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "{\"queue_running\":[[1,\"other-job\",{},{}]],\"queue_pending\":[]}")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var released = await client.ReleaseModelsAsync();

        Assert.False(released);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/free", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/system_stats", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelReleaseWaitsForTwoIdleSamplesAndAcceptsAlreadyReleasedAllocator()
    {
        var stats = 0;
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/queue", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{\"queue_running\":[],\"queue_pending\":[]}");
            if (path.EndsWith("/free", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK);
            if (path.EndsWith("/system_stats", StringComparison.Ordinal))
            {
                stats++;
                return Json(HttpStatusCode.OK, "{\"devices\":[{\"type\":\"cuda\",\"torch_vram_total\":0,\"torch_vram_free\":0}]}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var released = await CreateClient(handler).ReleaseModelsAsync();

        Assert.True(released);
        Assert.Equal(2, stats);
    }

    [Fact]
    public async Task ModelReleaseFailsWhenComfyStillReportsActiveAllocatorMemory()
    {
        var options = new ImageGenerationOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8188/"),
            PollInterval = TimeSpan.FromMilliseconds(1),
            ReleaseConfirmationTimeout = TimeSpan.FromMilliseconds(20)
        };
        var handler = new DelegateHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("/queue", StringComparison.Ordinal) => Json(HttpStatusCode.OK, "{\"queue_running\":[],\"queue_pending\":[]}"),
            var path when path.EndsWith("/free", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.OK),
            var path when path.EndsWith("/system_stats", StringComparison.Ordinal) => Json(HttpStatusCode.OK, "{\"devices\":[{\"type\":\"cuda\",\"torch_vram_total\":8589934592,\"torch_vram_free\":0}]}") ,
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        await Assert.ThrowsAsync<ImageGenerationException>(() =>
            new ComfyImageClient(new HttpClient(handler), options).ReleaseModelsAsync());

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/free", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoveryReportsMissingNativeNodesAndModelFiles()
    {
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/models/", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[]");
            if (path.EndsWith("/object_info", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var inventory = await CreateClient(handler).DiscoverAsync();

        Assert.False(inventory.IsReady);
        Assert.Contains("CFGGuider", inventory.MissingRequiredNodes);
        Assert.Contains("flux-2-klein-base-4b-fp8.safetensors", inventory.MissingRequiredModels);
    }

    [Fact]
    public async Task DiscoveryRequiresReferenceNodesOnlyForEditRequests()
    {
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/models/", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[]");
            if (path.EndsWith("/object_info", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"UNETLoader\":{},\"CLIPLoader\":{},\"VAELoader\":{},\"CLIPTextEncode\":{},\"RandomNoise\":{},\"KSamplerSelect\":{},\"Flux2Scheduler\":{},\"EmptyFlux2LatentImage\":{},\"SamplerCustomAdvanced\":{},\"VAEDecode\":{},\"SaveImage\":{},\"CFGGuider\":{}}");
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var client = CreateClient(handler);

        var textInventory = await client.DiscoverAsync();
        var editInventory = await client.DiscoverAsync(requiresImageEditing: true);

        Assert.DoesNotContain("LoadImage", textInventory.MissingRequiredNodes);
        Assert.Contains("LoadImage", editInventory.MissingRequiredNodes);
        Assert.Contains("VAEEncode", editInventory.MissingRequiredNodes);
        Assert.Contains("ReferenceLatent", editInventory.MissingRequiredNodes);
    }

    [Fact]
    public async Task AcceptedSubmissionWithoutServerJobIdRaisesUncertainState()
    {
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models/diffusion_models", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux-2-klein-base-4b-fp8.safetensors\"]");
            if (path.EndsWith("/models/text_encoders", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"qwen_3_4b.safetensors\"]");
            if (path.EndsWith("/models/vae", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux2-vae.safetensors\"]");
            if (path.EndsWith("/object_info", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"UNETLoader\":{},\"CLIPLoader\":{},\"VAELoader\":{},\"CLIPTextEncode\":{},\"RandomNoise\":{},\"KSamplerSelect\":{},\"Flux2Scheduler\":{},\"EmptyFlux2LatentImage\":{},\"SamplerCustomAdvanced\":{},\"VAEDecode\":{},\"SaveImage\":{},\"CFGGuider\":{}}");
            if (path.EndsWith("/prompt", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{}");
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<ImageJobUncertainException>(() =>
            client.GenerateAsync(new ImageGenerationRequest("ambiguous submission")));

        Assert.Null(error.PromptId);
        Assert.Contains("keep the GPU reserved", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LostPromptResponseRaisesUncertainStateWithoutAnId()
    {
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models/diffusion_models", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux-2-klein-base-4b-fp8.safetensors\"]");
            if (path.EndsWith("/models/text_encoders", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"qwen_3_4b.safetensors\"]");
            if (path.EndsWith("/models/vae", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux2-vae.safetensors\"]");
            if (path.EndsWith("/object_info", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"UNETLoader\":{},\"CLIPLoader\":{},\"VAELoader\":{},\"CLIPTextEncode\":{},\"RandomNoise\":{},\"KSamplerSelect\":{},\"Flux2Scheduler\":{},\"EmptyFlux2LatentImage\":{},\"SamplerCustomAdvanced\":{},\"VAEDecode\":{},\"SaveImage\":{},\"CFGGuider\":{}}");
            if (path.EndsWith("/prompt", StringComparison.Ordinal)) throw new HttpRequestException("socket closed after request body");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var error = await Assert.ThrowsAsync<ImageJobUncertainException>(() =>
            CreateClient(handler).GenerateAsync(new ImageGenerationRequest("lost response")));

        Assert.Null(error.PromptId);
    }

    [Fact]
    public async Task OversizedImageIsRejectedAfterJobIsSafelyTerminal()
    {
        const string promptId = "136951f1-3ff3-47d5-ab82-fdb86a93a5fc";
        var handler = new DelegateHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models/diffusion_models", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux-2-klein-base-4b-fp8.safetensors\"]");
            if (path.EndsWith("/models/text_encoders", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"qwen_3_4b.safetensors\"]");
            if (path.EndsWith("/models/vae", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux2-vae.safetensors\"]");
            if (path.EndsWith("/object_info", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"UNETLoader\":{},\"CLIPLoader\":{},\"VAELoader\":{},\"CLIPTextEncode\":{},\"RandomNoise\":{},\"KSamplerSelect\":{},\"Flux2Scheduler\":{},\"EmptyFlux2LatentImage\":{},\"SamplerCustomAdvanced\":{},\"VAEDecode\":{},\"SaveImage\":{},\"CFGGuider\":{}}");
            if (path.EndsWith("/prompt", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{\"prompt_id\":\"" + promptId + "\",\"number\":1}");
            if (path.EndsWith("/history/" + promptId, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"" + promptId + "\":{\"status\":{\"completed\":true},\"outputs\":{\"15\":{\"images\":[{\"filename\":\"large.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}");
            if (path.EndsWith("/view", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var options = new ImageGenerationOptions { Endpoint = new Uri("http://127.0.0.1:8188/"), MaximumImageBytes = 1024 };

        var error = await Assert.ThrowsAsync<ImageGenerationException>(() =>
            new ComfyImageClient(new HttpClient(handler), options).GenerateAsync(new ImageGenerationRequest("big image")));

        Assert.Contains("exceeds", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateSubmitsGraphRetrievesPngAndUnloadsOnlyAfterIdleQueue()
    {
        string? submittedId = null;
        const string serverPromptId = "f267ba8d-70b8-4c55-8818-771f91af2296";
        var statsRequests = 0;
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models/diffusion_models", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "[\"flux-2-klein-base-4b-fp8.safetensors\"]");
            if (path.EndsWith("/models/text_encoders", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "[\"qwen_3_4b.safetensors\"]");
            if (path.EndsWith("/models/vae", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "[\"flux2-vae.safetensors\"]");
            if (path.EndsWith("/object_info", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"UNETLoader\":{},\"CLIPLoader\":{},\"VAELoader\":{},\"CLIPTextEncode\":{},\"RandomNoise\":{},\"KSamplerSelect\":{},\"Flux2Scheduler\":{},\"EmptyFlux2LatentImage\":{},\"SamplerCustomAdvanced\":{},\"VAEDecode\":{},\"SaveImage\":{},\"CFGGuider\":{}}");
            if (path.EndsWith("/prompt", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var post = JsonNode.Parse(body)!;
                Assert.Equal("CFGGuider", post["prompt"]?["12"]?["class_type"]?.GetValue<string>());
                submittedId = post["prompt_id"]!.GetValue<string>();
                return Json(HttpStatusCode.OK, "{\"prompt_id\":\"" + serverPromptId + "\",\"number\":1}");
            }
            if (path.EndsWith("/history/" + serverPromptId, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"" + serverPromptId + "\":{\"status\":{\"completed\":true,\"status_str\":\"success\"},\"outputs\":{\"15\":{\"images\":[{\"filename\":\"AquaHub_00001_.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}");
            if (path.EndsWith("/view", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) };
            if (path.EndsWith("/queue", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "{\"queue_running\":[],\"queue_pending\":[]}");
            if (path.EndsWith("/free", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (path.EndsWith("/system_stats", StringComparison.Ordinal))
            {
                statsRequests++;
                return Json(HttpStatusCode.OK, "{\"devices\":[{\"type\":\"cuda\",\"torch_vram_total\":8589934592,\"torch_vram_free\":8480000000}]}");
            }
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var client = CreateClient(handler);

        var result = await client.GenerateAsync(new ImageGenerationRequest("a glasshouse in the rain", Seed: 123));

        Assert.Equal(123UL, result.Seed);
        Assert.Equal("AquaHub_00001_.png", result.FileName);
        Assert.Equal("AquaHub_00001_.png", result.ComfyOutputPath);
        Assert.Equal(png, result.PngBytes);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.NotNull(submittedId);
        Assert.NotEqual(serverPromptId, submittedId);
        Assert.True(handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal)) >= 3);
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/free", StringComparison.Ordinal));
        Assert.True(statsRequests >= 2);
    }

    [Fact]
    public async Task CancellationUsesJobScopedEndpointAndNeverTheGlobalInterruptRoute()
    {
        const string promptId = "6d9bf028-15f3-4555-b6db-77e5596a19ae";
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetedCancel = false;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models/diffusion_models", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux-2-klein-base-4b-fp8.safetensors\"]");
            if (path.EndsWith("/models/text_encoders", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"qwen_3_4b.safetensors\"]");
            if (path.EndsWith("/models/vae", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux2-vae.safetensors\"]");
            if (path.EndsWith("/object_info", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"UNETLoader\":{},\"CLIPLoader\":{},\"VAELoader\":{},\"CLIPTextEncode\":{},\"RandomNoise\":{},\"KSamplerSelect\":{},\"Flux2Scheduler\":{},\"EmptyFlux2LatentImage\":{},\"SamplerCustomAdvanced\":{},\"VAEDecode\":{},\"SaveImage\":{},\"CFGGuider\":{}}");
            if (path.EndsWith("/prompt", StringComparison.Ordinal))
            {
                submitted.TrySetResult();
                return Json(HttpStatusCode.OK, "{\"prompt_id\":\"" + promptId + "\",\"number\":1}");
            }
            if (path.EndsWith("/history/" + promptId, StringComparison.Ordinal))
                return targetedCancel
                    ? Json(HttpStatusCode.OK, "{\"" + promptId + "\":{\"status\":{\"completed\":true,\"status_str\":\"interrupted\"},\"outputs\":{}}}")
                    : Json(HttpStatusCode.OK, "{}");
            if (path.EndsWith("/queue", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"queue_running\":[[1,\"" + promptId + "\",{},{}]],\"queue_pending\":[]}");
            if (path.EndsWith("/api/jobs/" + promptId, StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, "{\"status\":\"in_progress\"}");
            if (path.EndsWith("/api/jobs/" + promptId + "/cancel", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                targetedCancel = true;
                return Json(HttpStatusCode.OK, "{\"cancelled\":true}");
            }
            if (path.EndsWith("/interrupt", StringComparison.Ordinal)) throw new InvalidOperationException("Global interrupt must never be used.");
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var options = new ImageGenerationOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8188/"),
            PollInterval = TimeSpan.FromMilliseconds(10),
            CleanupTimeout = TimeSpan.FromSeconds(2)
        };
        var client = new ComfyImageClient(new HttpClient(handler), options);
        using var cancellation = new CancellationTokenSource();
        var generation = client.GenerateAsync(new ImageGenerationRequest("test cancel"), cancellationToken: cancellation.Token);

        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);

        Assert.True(targetedCancel);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("/interrupt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OlderComfyCancellationWaitsForOwnJobInsteadOfCallingGlobalInterrupt()
    {
        const string promptId = "a2a135e8-0cde-4c18-a536-31f4564e2f1a";
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingForDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = false;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models/diffusion_models", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux-2-klein-base-4b-fp8.safetensors\"]");
            if (path.EndsWith("/models/text_encoders", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"qwen_3_4b.safetensors\"]");
            if (path.EndsWith("/models/vae", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "[\"flux2-vae.safetensors\"]");
            if (path.EndsWith("/object_info", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"UNETLoader\":{},\"CLIPLoader\":{},\"VAELoader\":{},\"CLIPTextEncode\":{},\"RandomNoise\":{},\"KSamplerSelect\":{},\"Flux2Scheduler\":{},\"EmptyFlux2LatentImage\":{},\"SamplerCustomAdvanced\":{},\"VAEDecode\":{},\"SaveImage\":{},\"CFGGuider\":{}}");
            if (path.EndsWith("/prompt", StringComparison.Ordinal))
            {
                submitted.TrySetResult();
                return Json(HttpStatusCode.OK, "{\"prompt_id\":\"" + promptId + "\",\"number\":1}");
            }
            if (path.EndsWith("/history/" + promptId, StringComparison.Ordinal))
                return terminal
                    ? Json(HttpStatusCode.OK, "{\"" + promptId + "\":{\"status\":{\"completed\":true,\"status_str\":\"success\"},\"outputs\":{}}}")
                    : Json(HttpStatusCode.OK, "{}");
            if (path.EndsWith("/queue", StringComparison.Ordinal))
                return terminal
                    ? Json(HttpStatusCode.OK, "{\"queue_running\":[],\"queue_pending\":[]}")
                    : Json(HttpStatusCode.OK, "{\"queue_running\":[[1,\"" + promptId + "\",{},{}]],\"queue_pending\":[]}");
            if (path.EndsWith("/api/jobs/" + promptId, StringComparison.Ordinal)) return Json(HttpStatusCode.NotFound, "{}");
            if (path.EndsWith("/interrupt", StringComparison.Ordinal)) throw new InvalidOperationException("Global interrupt must never be used.");
            return Json(HttpStatusCode.NotFound, "{}");
        });
        var options = new ImageGenerationOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8188/"),
            PollInterval = TimeSpan.FromMilliseconds(10),
            CleanupTimeout = TimeSpan.FromSeconds(2)
        };
        var client = new ComfyImageClient(new HttpClient(handler), options);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<string>(message =>
        {
            if (message.Contains("no safe targeted cancel API", StringComparison.OrdinalIgnoreCase))
                waitingForDrain.TrySetResult();
        });
        var generation = client.GenerateAsync(new ImageGenerationRequest("test old server cancel"), progress, cancellation.Token);

        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await waitingForDrain.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(generation.IsCompleted);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath.EndsWith("/interrupt", StringComparison.Ordinal));
        terminal = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
    }

    private static ComfyImageClient CreateClient(DelegateHandler handler, string endpoint = "http://127.0.0.1:8188/") =>
        new(new HttpClient(handler), new ImageGenerationOptions { Endpoint = new Uri(endpoint) });

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public List<HttpRequestMessage> Requests { get; } = [];

        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await _respond(request, cancellationToken);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
