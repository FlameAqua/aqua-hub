using System.Text.RegularExpressions;

namespace AquaHub.Core.Util;

/// <summary>
/// Text for sharing with someone helping with a problem (Settings › Debug › Copy summary): your user name, PC name, folder
/// and file names and web addresses are masked; what helps debugging stays — the known folder ("Documents"), a file's
/// extension, a site's host. Pure; unit-tested.
/// </summary>
public static partial class Redact
{
    public static string ForSharing(string text, string userName, string machineName, string profilePath)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = text;
        if (profilePath.Length > 3) s = s.Replace(profilePath.TrimEnd('\\'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        // Web addresses: the host only.
        s = UrlRx().Replace(s, m => m.Groups["scheme"].Value + "://" + m.Groups["host"].Value + (m.Groups["rest"].Length > 1 ? "/…" : ""));
        // File names (with an extension, at the end of a path): the extension only.
        s = FileRx().Replace(s, m => @"\<file>" + m.Groups["ext"].Value);
        // Folders between a known folder (or a drive's first folder) and the file: collapsed.
        s = MiddleRx().Replace(s, @"$1\…\");
        if (userName.Length >= 3) s = Regex.Replace(s, @"\b" + Regex.Escape(userName) + @"\b", "<user>", RegexOptions.IgnoreCase);
        if (machineName.Length >= 3) s = Regex.Replace(s, @"\b" + Regex.Escape(machineName) + @"\b", "<pc>", RegexOptions.IgnoreCase);
        return s;
    }

    [GeneratedRegex(@"(?<scheme>https?)://(?<host>[^/\s""“”'<>)]+)(?<rest>[^\s""“”'<>)]*)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"\\[^\\\r\n""“”'<>|]*?(?<ext>\.[A-Za-z0-9]{1,5})(?=$|[\s""“”'<>|,;:)])", RegexOptions.Multiline)]
    private static partial Regex FileRx();

    [GeneratedRegex(@"((?:%USERPROFILE%|[A-Za-z]:)\\[^\\\r\n""“”'<>|]+?)\\(?:[^\\\r\n""“”'<>|]+?\\)+(?=<file>)")]
    private static partial Regex MiddleRx();
}
