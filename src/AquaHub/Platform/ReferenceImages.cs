using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaHub.Core.Ai.Imaging;
using AquaHub.Core.Util;

namespace AquaHub.Platform;

/// <summary>
/// Prepares an image for Image Gen to edit, read with Windows' own decoders (JPEG, PNG, WebP, BMP, GIF, TIFF, and HEIC
/// with Microsoft's HEIF extension): turned upright from its camera orientation, converted to sRGB from an embedded
/// colour profile, scaled to about a megapixel (<see cref="ImageReferenceSizing"/>), laid on white where it's
/// transparent, and saved as a PNG with none of the original's metadata (location, camera, dates). The first frame of
/// an animation or a multi-page file is used. Safe to call off the UI thread.
/// </summary>
public static class ReferenceImages
{
    public const long MaxFileBytes = 150L * 1024 * 1024;
    public const long MaxSourcePixels = 200_000_000;

    public sealed record Prepared(byte[] Png, int Width, int Height);

    /// <exception cref="InvalidDataException">The image can't be read or is out of bounds; the message tells the user why.</exception>
    public static Prepared Prepare(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength > MaxFileBytes) throw new InvalidDataException(TooLarge(name));
        try { return Build(bytes, colourManaged: true); }
        catch (Exception ex) when (ex is not (InvalidDataException or OutOfMemoryException))
        {
            // Windows can't apply some embedded colour profiles: read the pixels as they are.
            Log.Debug("images", $"Preparing {name} with its colour profile failed: {ex.Message}");
            try { return Build(bytes, colourManaged: false); }
            catch (Exception inner) when (inner is not (InvalidDataException or OutOfMemoryException))
            {
                Log.Info("images", $"Couldn't read {name} as an image: {inner.Message}");
                throw new InvalidDataException(Unreadable(name), inner);
            }
        }
    }

    public static string TooLarge(string name) => $"“{ImageReference.ShortName(name)}” is over 150 MB. Use a smaller copy of the image.";

    private static Prepared Build(byte[] bytes, bool colourManaged)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes, writable: false),
            colourManaged ? BitmapCreateOptions.None : BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var (width, height) = (frame.PixelWidth, frame.PixelHeight);
        if (width < 16 || height < 16)
            throw new InvalidDataException("That image is too small to edit: it needs at least 16 × 16 pixels.");
        if ((long)width * height > MaxSourcePixels)
            throw new InvalidDataException("That image is over 200 megapixels, too large to prepare. Use a smaller copy.");

        var orientation = Orientation(frame);
        var turned = orientation >= 5; // 5 to 8 swap width and height
        var fit = ImageReferenceSizing.Fit(turned ? height : width, turned ? width : height);
        // Crop and scale the image as stored, then turn the small result upright: turning the full-size image would
        // hold all of it in memory.
        var (cropWidth, cropHeight) = turned ? (fit.CropHeight, fit.CropWidth) : (fit.CropWidth, fit.CropHeight);
        var (scaledWidth, scaledHeight) = turned ? (fit.Height, fit.Width) : (fit.Width, fit.Height);

        BitmapSource image = frame;
        if (colourManaged && frame.ColorContexts is { Count: > 0 } profiles)
            image = new ColorConvertedBitmap(image, profiles[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
        if (cropWidth < width || cropHeight < height)
            image = new CroppedBitmap(image, new Int32Rect((width - cropWidth) / 2, (height - cropHeight) / 2, cropWidth, cropHeight));
        image = new TransformedBitmap(image, new ScaleTransform(scaledWidth / (double)cropWidth, scaledHeight / (double)cropHeight));
        image = new CachedBitmap(image, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return new Prepared(Images.Png(OnWhite(Upright(image, orientation))), fit.Width, fit.Height);
    }

    /// <summary>The EXIF orientation (1 to 8), or 1 when the format has none.</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("System.Photo.Orientation") &&
                metadata.GetQuery("System.Photo.Orientation") is ushort value && value is >= 1 and <= 8)
                return value;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Debug("images", "No readable orientation: " + ex.Message);
        }
        return 1;
    }

    /// <summary>Turns a stored image upright for its EXIF orientation: 6 and 8 are a quarter turn, 5 and 7 a turn and a flip.</summary>
    private static BitmapSource Upright(BitmapSource image, int orientation)
    {
        var turn = orientation switch { 3 => 180, 5 or 6 or 7 => 90, 8 => 270, _ => 0 };
        if (turn != 0) image = new TransformedBitmap(image, new RotateTransform(turn));
        return orientation switch
        {
            2 or 5 => new TransformedBitmap(image, new ScaleTransform(-1, 1)),
            4 or 7 => new TransformedBitmap(image, new ScaleTransform(1, -1)),
            _ => image
        };
    }

    /// <summary>
    /// 24-bit RGB laid on white. ComfyUI drops an image's alpha channel, which would turn transparent areas black or
    /// show whatever colour they happen to hold.
    /// </summary>
    private static BitmapSource OnWhite(BitmapSource image)
    {
        var premultiplied = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        int width = premultiplied.PixelWidth, height = premultiplied.PixelHeight;
        var bgra = new byte[width * height * 4];
        premultiplied.CopyPixels(bgra, width * 4, 0);
        var bgr = new byte[width * height * 3];
        for (int i = 0, o = 0; i < bgra.Length; i += 4, o += 3)
        {
            // Premultiplied colour plus (1 - alpha) of white.
            var clear = 255 - bgra[i + 3];
            bgr[o] = (byte)Math.Min(255, bgra[i] + clear);
            bgr[o + 1] = (byte)Math.Min(255, bgra[i + 1] + clear);
            bgr[o + 2] = (byte)Math.Min(255, bgra[i + 2] + clear);
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bgr, width * 3);
    }

    private static string Unreadable(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase)
            ? "Windows reads HEIC photos only with the HEIF Image Extensions from the Microsoft Store. Install them, or save the photo as a JPEG first."
            : $"Aqua couldn't read “{ImageReference.ShortName(name)}” as an image. Try a JPEG, PNG, WebP, BMP, GIF or TIFF file.";
    }
}
