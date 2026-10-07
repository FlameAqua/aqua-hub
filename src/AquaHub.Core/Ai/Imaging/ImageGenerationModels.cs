using System.Collections.Immutable;

namespace AquaHub.Core.Ai.Imaging;

public enum ImageModelVariant
{
    Base4B,
    Distilled4B
}

/// <summary>Settings for the loopback ComfyUI server and the three FLUX.2 Klein model files.</summary>
public sealed record ImageGenerationOptions
{
    public required Uri Endpoint { get; init; }
    public ImageModelVariant Variant { get; init; } = ImageModelVariant.Base4B;
    public string? DiffusionModel { get; init; }
    public string TextEncoderModel { get; init; } = "qwen_3_4b.safetensors";
    public string VaeModel { get; init; } = "flux2-vae.safetensors";
    public bool UnloadModelAfterGeneration { get; init; } = true;
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ReleaseConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaximumImageBytes { get; init; } = 40 * 1024 * 1024;

    public string SelectedDiffusionModel => DiffusionModel ?? (Variant switch
    {
        ImageModelVariant.Base4B => "flux-2-klein-base-4b-fp8.safetensors",
        ImageModelVariant.Distilled4B => "flux-2-klein-4b-fp8.safetensors",
        _ => throw new ArgumentOutOfRangeException(nameof(Variant))
    });
}

public sealed record ImageGenerationRequest(
    string Prompt,
    int Width = 1024,
    int Height = 1024,
    int? Steps = null,
    ulong? Seed = null,
    float? Guidance = null)
{
    /// <summary>A safe relative path to an image already stored in ComfyUI's output folder.</summary>
    public string? ReferenceImage { get; init; }
}

public sealed record ImageGenerationResult(byte[] PngBytes, ulong Seed, TimeSpan Elapsed, string FileName)
{
    private string? _comfyOutputPath;

    /// <summary>Relative subfolder/name path of the generated image in ComfyUI's output folder.</summary>
    public string? ComfyOutputPath
    {
        get => _comfyOutputPath;
        init
        {
            if (value is not null && !ComfyImageClient.IsSafeRelativeImagePath(value))
                throw new ArgumentException("Comfy output paths must be safe relative subfolder/name paths.", nameof(value));
            _comfyOutputPath = value;
        }
    }
}

public sealed record ImageModelInventory(
    ImmutableArray<string> DiffusionModels,
    ImmutableArray<string> TextEncoders,
    ImmutableArray<string> Vaes,
    ImmutableArray<string> MissingRequiredNodes,
    ImmutableArray<string> MissingRequiredModels)
{
    /// <summary>
    /// The chosen files don't belong together (a Klein 9B model with the 4B text encoder, FLUX.1's VAE, a model that isn't
    /// Klein at all), in plain words: ComfyUI would accept the job and fail it part-way with a matrix-shape error.
    /// </summary>
    public ImmutableArray<string> Mismatches { get; init; } = ImmutableArray<string>.Empty;

    public bool IsReady => MissingRequiredNodes.IsEmpty && MissingRequiredModels.IsEmpty && Mismatches.IsEmpty;
}

public class ImageGenerationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>ComfyUI may still be using the GPU; callers must keep their GPU reservation and recover.</summary>
public sealed class ImageJobUncertainException(string? promptId, string message, Exception? innerException = null)
    : ImageGenerationException(message, innerException)
{
    public string? PromptId { get; } = promptId;
}
