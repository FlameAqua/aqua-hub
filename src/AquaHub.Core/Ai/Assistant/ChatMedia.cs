using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// The pictures a chat keeps, in its own folder (<see cref="ChatStore.MediaFolder"/>, deleted with the chat):
/// screenshots whole, and only thumbnails of pictures on the PC, which stay where they are and are linked by their path.
/// Files are named from their content ("s-…" a picture, "t-…" a thumbnail), so the same one is kept once and a name read
/// back from the database can only ever be a file in that folder. Pure file handling; unit-tested.
/// </summary>
public static partial class ChatMedia
{
    /// <summary>Saves a picture in <paramref name="folder"/> and returns its file name (null when it can't be written).</summary>
    public static string? Save(string folder, byte[] bytes, bool thumbnail)
    {
        if (bytes.Length == 0) return null;
        var name = (thumbnail ? "t-" : "s-") + Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant() + Extension(bytes);
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            if (!File.Exists(path))
            {
                // Written whole or not at all: a half-written picture never shows.
                var part = path + ".part";
                File.WriteAllBytes(part, bytes);
                File.Move(part, path, overwrite: true);
            }
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("ask", "Couldn't keep a picture with the chat", ex);
            return null;
        }
    }

    /// <summary>The kind of image by its first bytes (".png" when unsure; Aqua makes PNG and JPEG).</summary>
    internal static string Extension(byte[] b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? ".jpg"
        : b.Length >= 4 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' ? ".gif"
        : b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M' ? ".bmp"
        : b.Length >= 12 && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P' ? ".webp"
        : ".png";

    [GeneratedRegex(@"^[st]-[0-9a-f]{16}\.(?:png|jpg|gif|bmp|webp)\z")]
    private static partial Regex NameRx();

    /// <summary>A kept picture's full path — only for a name <see cref="Save"/> makes, so nothing outside the folder is reached.</summary>
    public static string? PathOf(string? folder, string? name) => folder is not null && name is not null && NameRx().IsMatch(name) ? Path.Combine(folder, name) : null;
}
