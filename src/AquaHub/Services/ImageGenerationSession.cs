using System.Net.Http;
using AquaHub.Core.Ai.Imaging;
using AquaHub.Core.Util;
using AquaHub.Platform;
using System.Windows.Threading;

namespace AquaHub.Services;

/// <summary>One local image job, surviving navigation away from Ask Aqua.</summary>
public sealed class ImageGenerationSession
{
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<TaskCompletionSource> _operations = new();
    private bool _stopping;
    private CancellationTokenSource? _cancellation;
    private readonly SemaphoreSlim _archiveGate = new(1, 1);
    private DispatcherTimer? _retentionTimer;
    private IDisposable? _gpuLease;
    private ImageGenerationOptions? _recoveryOptions;
    private string? _referenceImage;
    private string? _editingReferenceId;
    public bool RecoveryNeeded => _gpuLease is not null && !Busy;
    public bool HasRecoveryAddress => _recoveryOptions is not null;
    public event Action? Changed;
    public bool Busy { get; private set; }
    public bool Checking { get; private set; }
    public bool ManagingImages { get; private set; }
    /// <summary>An added image is being read and prepared.</summary>
    public bool Attaching { get; private set; }
    public string Draft { get; set; } = "";

    /// <summary>
    /// The image the next generation edits, as a path in ComfyUI's output folder: how gallery records made before
    /// <see cref="Reference"/> are edited again. Setting one replaces <see cref="Reference"/>.
    /// </summary>
    public string? ReferenceImage
    {
        get => _referenceImage;
        set
        {
            _referenceImage = value;
            if (value is not null) Reference = null;
        }
    }

    /// <summary>
    /// The image the next generation edits: one you added, or a copy of a generated image (Edit image). Aqua keeps it,
    /// so the edit doesn't depend on what ComfyUI still has; it's sent to ComfyUI's temporary folder for each job.
    /// </summary>
    public ImageReference? Reference { get; private set; }

    /// <summary>The next generation edits an image rather than starting from the prompt alone.</summary>
    public bool Editing => Reference is not null || ReferenceImage is not null;
    public string Status { get; private set; } = "Connect your local image server below to get started.";
    public bool Error { get; private set; }
    public ImageModelInventory? Inventory { get; private set; }
    public GeneratedImage? Selected { get; set; }
    public IReadOnlyList<GeneratedImage> Recent { get; private set; } = Array.Empty<GeneratedImage>();
    public ImageArchive Archive => new(Path.Combine(Hub.Core.Paths.Root, "generated-images"));
    private string RecoveryPath => Path.Combine(Hub.Core.Paths.Root, "generated-images", "gpu-recovery.txt");

    private static HttpClient CreateHttp() => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(30) };

    public void Message(string text, bool error = false)
    {
        Status = text;
        Error = error;
        Changed?.Invoke();
    }

    public async Task LoadAsync()
    {
        if (_stopping) return;
        using var operation = OwnOperation();
        await _archiveGate.WaitAsync();
        try
        {
            await ReloadGalleryAsync();
        }
        catch (Exception ex) { Message("Couldn't read the image gallery: " + ex.Message, true); }
        finally { _archiveGate.Release(); }
    }

    private async Task ReloadGalleryAsync()
    {
        Recent = await Task.Run(() => Archive.Recent());
        Selected = Recent.FirstOrDefault(x => x.Id == Selected?.Id) ?? Recent.FirstOrDefault();
        Changed?.Invoke();
    }

    public void Start()
    {
        if (_retentionTimer is not null) return;
        if (File.Exists(RecoveryPath))
        {
            _gpuLease = Hub.Core.Llm.ReserveImageRecovery();
            try
            {
                using var stream = new FileStream(RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 2048) throw new InvalidDataException("Recovery address is too large.");
                using var reader = new StreamReader(stream);
                _recoveryOptions = new ImageGenerationOptions { Endpoint = new Uri(reader.ReadToEnd().Trim(), UriKind.Absolute) };
                using var http = CreateHttp();
                _ = new ComfyImageClient(http, _recoveryOptions);
                Message("The previous image job's GPU release was not confirmed. Choose Check GPU release before chat or generation can continue.", true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException or ImageGenerationException)
            {
                _recoveryOptions = null;
                Message("Image recovery information couldn't be read. Set the address of the same ComfyUI server below, then choose Check GPU release. " + ex.Message, true);
            }
        }
        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _retentionTimer.Tick += async (_, _) => await ApplyRetentionAsync();
        _retentionTimer.Start();
        _ = ApplyRetentionAsync();
    }

    public void Stop() { _stopping = true; _retentionTimer?.Stop(); _stop.Cancel(); _cancellation?.Cancel(); }

    public async Task StopAsync()
    {
        Stop();
        await Task.WhenAll(_operations.Select(o => o.Task).ToArray());
        if (RecoveryNeeded) Log.Warn("images", "Exiting with unconfirmed ComfyUI work or memory release; the GPU reservation was retained until exit.");
    }

    private IDisposable OwnOperation()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.Add(completion);
        return new Operation(() => { _operations.Remove(completion); completion.TrySetResult(); });
    }

    private sealed class Operation(Action finish) : IDisposable { public void Dispose() => finish(); }

    private static string InventoryProblems(ImageModelInventory inventory) => string.Join("; ",
        inventory.MissingRequiredNodes.Select(n => "Missing ComfyUI node: " + n)
            .Concat(inventory.MissingRequiredModels.Select(m => "Select or install model: " + m))
            .Concat(inventory.Mismatches));

    public async Task ApplyRetentionAsync(bool report = false)
    {
        if (_stopping || Busy || Attaching || ManagingImages) return;
        using var operation = OwnOperation();
        ManagingImages = true;
        Changed?.Invoke();
        await _archiveGate.WaitAsync();
        try
        {
            var settings = Hub.S.ImageGeneration;
            var result = await Task.Run(() => Archive.Prune(settings.RetentionDays, DateTimeOffset.Now, settings.OutputFolder));
            await PruneReferencesAsync();
            await ReloadGalleryAsync();
            if (result.Failures.Count > 0)
                Message($"Couldn't remove {result.Failures.Count} expired image(s). Check the ComfyUI output folder and file permissions; Aqua will retry.", true);
            else if (report) Message(settings.RetentionDays == 0 ? "Images will be kept until you delete them."
                : $"Images expire after {settings.RetentionDays} days. Removed {result.DeletedCount} expired image(s).");
        }
        catch (Exception ex) { Log.Warn("images", "Image retention failed", ex); Message("Couldn't apply image expiry: " + ex.Message, true); }
        finally { _archiveGate.Release(); ManagingImages = false; Changed?.Invoke(); }
    }

    public async Task DeleteAsync(GeneratedImage image)
    {
        if (_stopping || Busy || Attaching || ManagingImages) return;
        using var operation = OwnOperation();
        ManagingImages = true;
        Changed?.Invoke();
        await _archiveGate.WaitAsync();
        try
        {
            var outputFolder = Hub.S.ImageGeneration.OutputFolder;
            await Task.Run(() => Archive.Delete(image, outputFolder));
            if (ReferenceImage == image.ComfyOutputPath) ReferenceImage = null;
            await PruneReferencesAsync();
            await ReloadGalleryAsync();
            Message(string.IsNullOrWhiteSpace(outputFolder) && image.ComfyOutputPath is not null
                ? "Deleted from Aqua. The ComfyUI copy remains because its output folder isn't configured." : "Image deleted.");
        }
        catch (Exception ex) { Message("Couldn't delete the image: " + ex.Message, true); }
        finally { _archiveGate.Release(); ManagingImages = false; Changed?.Invoke(); }
    }

    /// <summary>Makes an image file (picked or dropped) the image to edit. False, with a message, when it can't be used.</summary>
    public Task<bool> AttachFileAsync(string path)
    {
        var name = Path.GetFileName(path);
        return AttachAsync(name, ImageReferenceSource.Photo, async () =>
        {
            return await ReadImageFileAsync(path, name, _stop.Token);
        });
    }

    /// <summary>Makes image data (a paste, or a picture dragged from another app) the image to edit.</summary>
    public Task<bool> AttachImageAsync(byte[] bytes, string name) =>
        AttachAsync(name, ImageReferenceSource.Photo, () => Task.FromResult(bytes));

    /// <summary>
    /// Edit image: a copy of one of your generated images becomes the image to edit. Unlike <see cref="ReferenceImage"/>,
    /// this works for any image in the gallery, whether or not ComfyUI still has its output.
    /// </summary>
    public Task<bool> EditGalleryImageAsync(GeneratedImage item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return AttachAsync(item.Prompt, ImageReferenceSource.Gallery, () => ReadImageFileAsync(Archive.ImagePath(item), item.Prompt, _stop.Token));
    }

    private static async Task<byte[]> ReadImageFileAsync(string path, string name, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        var length = stream.Length;
        if (length > ReferenceImages.MaxFileBytes) throw new InvalidDataException(ReferenceImages.TooLarge(name));
        var bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, ct);
        if (stream.ReadByte() != -1) throw new InvalidDataException("The image changed while Aqua was reading it. Add it again.");
        return bytes;
    }

    public Task<bool> AttachClipboardAsync() => AttachPreparedAsync("Pasted image", ImageReferenceSource.Photo, async () =>
    {
        var bitmap = System.Windows.Clipboard.GetImage() ?? throw new InvalidDataException("The clipboard no longer has an image. Copy it again.");
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > ReferenceImages.MaxSourcePixels)
            throw new InvalidDataException("That clipboard image is over 200 megapixels. Copy a smaller image.");
        bitmap.Freeze();
        return await Task.Run(() => ReferenceImages.Prepare(bitmap), _stop.Token);
    });

    private Task<bool> AttachAsync(string name, ImageReferenceSource source, Func<Task<byte[]>> read) =>
        AttachPreparedAsync(name, source, async () =>
        {
            var bytes = await read();
            return await Task.Run(() => ReferenceImages.Prepare(bytes, name), _stop.Token);
        });

    private async Task<bool> AttachPreparedAsync(string name, ImageReferenceSource source, Func<Task<ReferenceImages.Prepared>> prepare)
    {
        if (_stopping || Busy || Attaching || ManagingImages) return false;
        using var operation = OwnOperation();
        Attaching = true;
        Message(source == ImageReferenceSource.Gallery ? "Preparing a copy of this image…" : "Preparing your image…");
        try
        {
            var prepared = await prepare();
            _stop.Token.ThrowIfCancellationRequested();
            string id;
            await _archiveGate.WaitAsync();
            try { id = await Archive.SaveReferenceAsync(prepared.Png, _stop.Token); }
            finally { _archiveGate.Release(); }
            var replaced = Reference?.Id;
            _referenceImage = null;
            Reference = new ImageReference(id, ImageReference.ShortName(name), prepared.Width, prepared.Height) { Source = source };
            Message(source == ImageReferenceSource.Gallery
                ? "Describe your changes. Aqua edits a copy of this image and saves the result as a new one."
                : $"Added {Reference.Name} · {prepared.Width} × {prepared.Height}. Describe what should change, and what should stay.");
            if (replaced is not null && replaced != id) _ = TidyReferencesAsync();
            return true;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Message(ex is InvalidDataException ? ex.Message : $"Couldn't open “{ImageReference.ShortName(name)}”: {ex.Message}", true);
            return false;
        }
        finally { Attaching = false; Changed?.Invoke(); }
    }

    /// <summary>
    /// For Regenerate: makes the image <paramref name="item"/> was edited from the image to edit again, or clears it
    /// for an image made from a prompt alone. False, with a message, when Aqua no longer has that image: regenerating
    /// would then ignore it, so don't.
    /// </summary>
    public bool RestoreReference(GeneratedImage item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var replaced = Reference?.Id;
        _referenceImage = null;
        Reference = null;
        var restored = true;
        if (item.EditedFrom is { } from)
        {
            if (from.IsWellFormed && File.Exists(Archive.ReferencePath(from.Id))) Reference = from;
            else
            {
                restored = false;
                Message("Aqua no longer has the image this one was edited from, so it can't repeat the edit. Add that image again to edit it.", true);
            }
        }
        else _referenceImage = item.ReferenceImage;
        Changed?.Invoke();
        if (replaced is not null && replaced != Reference?.Id) _ = TidyReferencesAsync();
        return restored;
    }

    /// <summary>Back to creating from the prompt alone.</summary>
    public void ClearReference()
    {
        var replaced = Reference?.Id;
        _referenceImage = null;
        Reference = null;
        Changed?.Invoke();
        if (replaced is not null) _ = TidyReferencesAsync();
    }

    private async Task TidyReferencesAsync()
    {
        if (_stopping) return;
        using var operation = OwnOperation();
        await _archiveGate.WaitAsync();
        try { await PruneReferencesAsync(); }
        finally { _archiveGate.Release(); }
    }

    /// <summary>
    /// Deletes Aqua's kept copies of images to edit once no gallery record, waiting edit or running job needs them, so
    /// they follow the gallery's expiry and deletion. Callers hold the archive gate.
    /// </summary>
    private async Task PruneReferencesAsync()
    {
        string?[] keep = [Reference?.Id, _editingReferenceId];
        try
        {
            var result = await Task.Run(() => Archive.PruneReferences(keep));
            foreach (var failure in result.Failures) Log.Warn("images", $"Kept reference image {failure.ImageId}: {failure.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("images", "Couldn't tidy the kept reference images", ex);
        }
    }

    public async Task StartServerAsync(ImageGenerationOptions options)
    {
        if (_stopping || Busy || Checking) return;
        using var operation = OwnOperation();
        if (Sandbox.Intercept("comfy-start", Hub.S.ImageGeneration.LauncherPath))
        { Message("ComfyUI startup was intercepted in this test profile."); return; }
        Checking = true;
        Message("Checking the local image server…");
        try
        {
            // Starting twice must not create a second GPU server. An already-ready server is reused.
            using var http = CreateHttp();
            var client = new ComfyImageClient(http, options);
            async Task<bool> Ready()
            {
                try
                {
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    probe.CancelAfter(TimeSpan.FromSeconds(3));
                    using var response = await http.GetAsync(new Uri(options.Endpoint.AbsoluteUri.TrimEnd('/') + "/system_stats"), probe.Token);
                    return response.IsSuccessStatusCode;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
            }
            if (!await Ready())
            {
                _stop.Token.ThrowIfCancellationRequested();
                if (!ComfyServerLauncher.Launch(Hub.S.ImageGeneration.LauncherPath))
                { Message("Couldn't launch ComfyUI. Choose its installed executable or NVIDIA startup script below.", true); return; }
                Message("Starting ComfyUI… waiting for its local server.");
                var started = System.Diagnostics.Stopwatch.StartNew();
                while (!await Ready())
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    if (started.Elapsed > TimeSpan.FromSeconds(60))
                    { Message("ComfyUI is still starting, or uses another port. Check its address, then choose Check connection.", true); return; }
                    await Task.Delay(1500, _stop.Token);
                }
            }
            Inventory = await client.DiscoverAsync(_stop.Token);
            Message(RecoveryNeeded ? "ComfyUI is running. Choose Check GPU release to resume Aqua's queue."
                : Inventory.IsReady ? "ComfyUI is running · models ready."
                : "ComfyUI is running. " + InventoryProblems(Inventory), !Inventory.IsReady);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Message("Couldn't start ComfyUI: " + Friendly(ex), true); }
        finally { Checking = false; Changed?.Invoke(); }
    }

    public async Task CheckAsync(ImageGenerationOptions options)
    {
        if (_stopping || Busy || Checking || ManagingImages || RecoveryNeeded) return;
        using var operation = OwnOperation();
        Checking = true;
        Message("Checking ComfyUI and the selected model files…");
        try
        {
            using var http = CreateHttp();
            var client = new ComfyImageClient(http, options);
            Inventory = await client.DiscoverAsync(_stop.Token);
            Message(Inventory.IsReady ? "Connected · models ready. You can generate an image."
                : "Connected. " + InventoryProblems(Inventory), !Inventory.IsReady);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Inventory = null; Message(Friendly(ex), true); }
        finally { Checking = false; Changed?.Invoke(); }
    }

    /// <summary>
    /// Generates an image, or edits one: the request's own ComfyUI output path when it has one (older gallery records),
    /// otherwise <see cref="Reference"/>, which is sent to ComfyUI's temporary folder before the GPU is reserved.
    /// </summary>
    public async Task GenerateAsync(ImageGenerationOptions options, ImageGenerationRequest request)
    {
        if (_stopping || Busy || Checking || ManagingImages || RecoveryNeeded || Attaching) return;
        using var operation = OwnOperation();
        if (Sandbox.Intercept("image-generate", options.SelectedDiffusionModel))
        { Message("Image generation is intercepted in this test profile."); return; }
        if (Hub.S.Ai.Enabled && Hub.S.Ai.Provider != "llamacpp")
        {
            Message("Select Aqua's own runtime in AI settings for shared GPU use, or turn AI off before using a separately managed image server.", true);
            return;
        }
        var reference = request.ReferenceImage is null ? Reference : null;
        Busy = true;
        _editingReferenceId = reference?.Id;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _cancellation = cts;
        Message("Checking local image models…");
        try
        {
            using var http = CreateHttp();
            var client = new ComfyImageClient(http, options with { UnloadModelAfterGeneration = false });
            var referencePng = reference is null ? null
                : await Task.Run(() => Archive.ReadReference(reference.Id), cts.Token)
                  ?? throw new ImageGenerationException("Aqua no longer has the image to edit. Add it again, or clear it to create from your description alone.");
            Inventory = await client.DiscoverAsync(request.ReferenceImage is not null || reference is not null, cts.Token);
            if (!Inventory.IsReady)
                throw new ImageGenerationException(InventoryProblems(Inventory));
            if (referencePng is not null)
            {
                Message("Sending your image to ComfyUI…");
                request = request with
                {
                    ReferenceImage = await client.UploadReferenceAsync(referencePng, cts.Token),
                    ReferenceFolder = ComfyImageFolder.Temp,
                };
            }
            Message("Waiting for the GPU. Any current answer will finish first…");
            _gpuLease = await Hub.Core.Llm.ReserveForImageAsync(cts.Token);
            _recoveryOptions = options;
            var uncertain = false;
            try
            {
                // Persist before submission: a crash after an ambiguous HTTP response must not allow another GPU owner on restart.
                Directory.CreateDirectory(Path.GetDirectoryName(RecoveryPath)!);
                var temporary = RecoveryPath + ".tmp";
                await File.WriteAllTextAsync(temporary, options.Endpoint.AbsoluteUri, cts.Token);
                File.Move(temporary, RecoveryPath, overwrite: true);
                var progress = new Progress<string>(message => Message(message));
                var result = await client.GenerateAsync(request, progress, cts.Token);
                var item = new GeneratedImage(Guid.NewGuid().ToString("N"), request.Prompt, result.Seed,
                    request.Width, request.Height, options.SelectedDiffusionModel, DateTimeOffset.Now, result.Elapsed.TotalSeconds)
                {
                    Steps = request.Steps ?? (options.Variant == ImageModelVariant.Base4B ? 20 : 4),
                    Variant = options.Variant == ImageModelVariant.Base4B ? "base" : "distilled",
                    TextEncoder = options.TextEncoderModel,
                    Vae = options.VaeModel,
                    ComfyOutputPath = result.ComfyOutputPath,
                    ReferenceImage = reference is null ? request.ReferenceImage : null,
                    EditedFrom = reference,
                };
                await Archive.SaveAsync(item, result.PngBytes, cts.Token);
                Recent = new[] { item }.Concat(Recent).Take(30).ToArray();
                Selected = item;
                Message($"Saved · {request.Width} × {request.Height} · seed {result.Seed} · {result.Elapsed.TotalSeconds:0}s");
            }
            catch (ImageJobUncertainException) { uncertain = true; throw; }
            finally
            {
                // Never let a transport failure start a second GPU model over a possibly running image job.
                if (!uncertain)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        if (await client.ReleaseModelsAsync(cleanup.Token)) ReleaseLease();
                    }
                    catch (Exception ex) { Log.Warn("images", "Couldn't confirm ComfyUI released its GPU memory", ex); }
                }
            }
        }
        catch (OperationCanceledException) { Message("Generation cancelled."); }
        catch (Exception ex) { Log.Warn("images", "Image generation failed", ex); Message(Friendly(ex), true); }
        finally
        {
            _cancellation = null; Busy = false; _editingReferenceId = null;
            if (RecoveryNeeded) Message(Status + " GPU stays reserved until ComfyUI finishes and releases its models. Use Check GPU release below.", true);
            else Changed?.Invoke();
        }
    }

    public async Task RecoverAsync(ImageGenerationOptions? options = null)
    {
        if (_stopping || !RecoveryNeeded || Checking) return;
        _recoveryOptions ??= options;
        if (_recoveryOptions is null) { Message("Choose the original ComfyUI server address before checking GPU release.", true); return; }
        using var operation = OwnOperation();
        Checking = true;
        Message("Checking ComfyUI's queue and GPU memory…");
        try
        {
            using var http = CreateHttp();
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            ct.CancelAfter(TimeSpan.FromSeconds(45));
            var client = new ComfyImageClient(http, _recoveryOptions);
            await client.WaitUntilIdleAsync(new Progress<string>(message => Message(message)), ct.Token);
            if (await client.ReleaseModelsAsync(ct.Token))
            {
                ReleaseLease();
                Message("GPU released. Chat and editorial jobs can continue.");
            }
            else Message("ComfyUI still has queued work. Wait for it to finish, then check again.", true);
        }
        catch (Exception ex) { Message(Friendly(ex) + " GPU remains reserved. If ComfyUI stopped, restart it and check again.", true); }
        finally { Checking = false; Changed?.Invoke(); }
    }

    private void ReleaseLease()
    {
        // A failed marker removal keeps the reservation, so restart never silently loses unresolved state.
        if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath);
        _gpuLease?.Dispose();
        _gpuLease = null;
        _recoveryOptions = null;
    }

    public void Cancel()
    {
        if (_cancellation is null) return;
        _cancellation.Cancel();
        Message("Cancelling this image; waiting for the server to release the job…");
    }

    private static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException => "Couldn't connect to ComfyUI. Start it on this PC and check its address below.",
        TaskCanceledException => "ComfyUI did not respond in time. Check that the server is running.",
        _ => ex.Message,
    };
}
