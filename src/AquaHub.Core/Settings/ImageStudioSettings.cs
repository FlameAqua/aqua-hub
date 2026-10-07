namespace AquaHub.Core.Settings;

/// <summary>The local ComfyUI connection. Prompts and image history are kept separately from settings.</summary>
public sealed class ImageStudioSettings
{
    public string Endpoint { get; set; } = "http://127.0.0.1:8188";
    public string Variant { get; set; } = "base";
    public string DiffusionModel { get; set; } = "flux-2-klein-base-4b-fp8.safetensors";
    public string TextEncoder { get; set; } = "qwen_3_4b.safetensors";
    public string Vae { get; set; } = "flux2-vae.safetensors";
    /// <summary>Days to retain archived images; zero keeps them indefinitely.</summary>
    public int RetentionDays { get; set; } = 30;
    /// <summary>Optional ComfyUI launcher path used to locate its output directory.</summary>
    public string LauncherPath { get; set; } = "";
    /// <summary>Optional ComfyUI output directory used to remove tracked server copies.</summary>
    public string OutputFolder { get; set; } = "";
}
