using System.Security.Cryptography;

namespace AquaHub.Core.Ai.Imaging;

public enum ImageReferenceSource
{
    /// <summary>An image you added: a file, a drop or a paste.</summary>
    Photo,
    /// <summary>One of your generated images, edited with Edit image.</summary>
    Gallery
}

/// <summary>
/// The image an edit starts from. Aqua keeps its own copy in the gallery's <c>references</c> folder: a PNG that is
/// upright, sRGB, about one megapixel and free of the original's metadata, named by its content (<see cref="Id"/>), so
/// the same image is kept once and Regenerate can repeat an edit after ComfyUI has cleared its copy.
/// </summary>
/// <param name="Id">Content name: the first 128 bits of the PNG's SHA-256, in lowercase hex.</param>
/// <param name="Name">What to call it: the file name, or the prompt of a gallery image.</param>
/// <param name="Width">Pixel width of the kept PNG, also the "match" canvas width.</param>
/// <param name="Height">Pixel height of the kept PNG, also the "match" canvas height.</param>
public sealed record ImageReference(string Id, string Name, int Width, int Height)
{
    public const int MaxNameLength = 80;
    public const int MaxPngBytes = 16 * 1024 * 1024;

    public ImageReferenceSource Source { get; init; } = ImageReferenceSource.Photo;

    public static string IdFor(ReadOnlySpan<byte> png) => Convert.ToHexStringLower(SHA256.HashData(png)[..16]);

    public static bool IsValidId(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>A name short enough for the banner and for archive records (which stay under 64 KiB).</summary>
    public static string ShortName(string? name)
    {
        var text = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return "Image";
        if (text.Length <= MaxNameLength) return text;
        // Never split an emoji's surrogate pair: the JSON writer rejects half of one.
        var cut = MaxNameLength - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>Checks a record read back from disk, where the profile folder could have been edited.</summary>
    public bool IsWellFormed =>
        IsValidId(Id) && Name is { Length: > 0 and <= MaxNameLength } && Enum.IsDefined(Source) &&
        ImageReferenceSizing.IsCanvasSize(Width) && ImageReferenceSizing.IsCanvasSize(Height);
}

/// <param name="CropWidth">Centred crop of the upright source, which only differs from it beyond 8:1.</param>
/// <param name="CropHeight">See <paramref name="CropWidth"/>.</param>
/// <param name="Width">Canvas width: a multiple of 16 from 256 through 2048.</param>
/// <param name="Height">Canvas height: a multiple of 16 from 256 through 2048.</param>
public readonly record struct ReferenceFit(int CropWidth, int CropHeight, int Width, int Height);

/// <summary>
/// The size an added image is scaled to, as in ComfyUI's own FLUX.2 Klein edit workflow (scale to one megapixel, then
/// make the canvas the same size): the shape is kept, rounded to multiples of 16 as Klein needs. Small images are
/// scaled up too. Beyond 4:1 the long side stops at 2048, and beyond 8:1 the centre is cropped so the short side keeps
/// 256 pixels.
/// </summary>
public static class ImageReferenceSizing
{
    public const int TargetPixels = 1024 * 1024;
    public const int MinSide = 256;
    public const int MaxSide = 2048;
    public const int MaxAspect = 8;

    public static bool IsCanvasSize(int side) => side is >= MinSide and <= MaxSide && side % 16 == 0;

    public static ReferenceFit Fit(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(width < 1 ? nameof(width) : nameof(height), "Image sides must be positive.");
        var cropWidth = (int)Math.Min(width, (long)height * MaxAspect);
        var cropHeight = (int)Math.Min(height, (long)width * MaxAspect);
        var aspect = (double)cropWidth / cropHeight;
        var w = Math.Sqrt(TargetPixels * aspect);
        var h = Math.Sqrt(TargetPixels / aspect);
        if (w > MaxSide) (w, h) = (MaxSide, MaxSide / aspect);
        if (h > MaxSide) (w, h) = (MaxSide * aspect, MaxSide);
        return new ReferenceFit(cropWidth, cropHeight, Snap(w), Snap(h));
    }

    private static int Snap(double side) =>
        Math.Clamp((int)Math.Round(side / 16, MidpointRounding.AwayFromZero) * 16, MinSide, MaxSide);
}
