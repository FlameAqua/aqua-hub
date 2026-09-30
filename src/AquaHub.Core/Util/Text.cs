using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AquaHub.Core.Util;

/// <summary>Publisher identity for counting sources.</summary>
public static partial class Publishers
{
    [GeneratedRegex(@"\.(com|co\.uk|ie|org|net|co|uk|us|au|ca|in|eu|de|fr)$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainSuffix();

    // "The Journal", "www.", and the camel-case "TheJournal" (but not "Thetford").
    [GeneratedRegex(@"^(?:(?i:www\.|the\s+)|The(?=[A-Z]))")]
    private static partial Regex LeadingThe();

    [GeneratedRegex(@"\s+(news|online|newspaper|world|business|sport|sports|politics|technology|tech|europe|international)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSection();

    /// <summary>"TheJournal.ie", "The Journal" → "journal"; "RTÉ News", "RTE" → "rte"; "BBC Business" → "bbc".</summary>
    public static string Key(string name)
    {
        var s = Analysis.TextTools.Fold(name).Trim();
        s = DomainSuffix().Replace(s, "");
        s = LeadingThe().Replace(s, "");
        for (var i = 0; i < 3; i++)
        {
            var shorter = TrailingSection().Replace(s, "");
            if (shorter == s || shorter.Length == 0) break;
            s = shorter;
        }
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.Length > 0 ? sb.ToString() : name;
    }
}

public static partial class Hash
{
    /// <summary>Stable 16-hex-char identifier derived from the given parts.</summary>
    public static string Short(params string?[] parts)
    {
        var joined = string.Join("\u001f", parts.Select(p => p ?? string.Empty));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}

/// <summary>
/// HTML → plain text conversion. Remote HTML is never rendered anywhere in the app; it is reduced to
/// text so that feed content cannot carry scripts, trackers or active content into the UI.
/// </summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style|noscript|iframe|svg|head)[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousBlocks();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr|/blockquote)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreaks();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    /// <summary>Inline wrappers that sit inside words (e.g. Mastodon's #&lt;span&gt;tag&lt;/span&gt;) are removed without a space.</summary>
    [GeneratedRegex(@"</?\s*(span|wbr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineTags();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\f\v ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"<img[^>]+src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();

    public static string ToPlain(string? html, int maxLength = 600, bool keepLines = false)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var s = html;
        if (s.Length > 200_000) s = s[..200_000];
        s = DangerousBlocks().Replace(s, " ");
        s = InlineTags().Replace(s, "");
        s = BlockBreaks().Replace(s, "\n");
        s = ListItems().Replace(s, "\n• ");
        s = Tags().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        // Some feeds double-encode entities.
        if (s.Contains("&amp;", StringComparison.Ordinal) || s.Contains("&#", StringComparison.Ordinal))
            s = WebUtility.HtmlDecode(s);
        s = s.Replace("\r", "");
        s = FoldStyledLetters(s);
        s = Spaces().Replace(s, " ");
        if (keepLines)
        {
            s = BlankLines().Replace(s, "\n");
            s = string.Join('\n', s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        else
        {
            s = Regex.Replace(s, @"\s+", " ");
        }
        s = s.Trim();
        return Truncate(s, maxLength);
    }

    /// <summary>
    /// Social posts often fake bold/italic with Unicode "mathematical" letters (𝐁𝐨𝐥𝐝, 𝘪𝘵𝘢𝘭𝘪𝘤), which render in a serif
    /// fallback font and defeat search. Fold just that block back to plain letters (other symbols are left alone).
    /// </summary>
    public static string FoldStyledLetters(string s)
    {
        if (!s.Any(char.IsHighSurrogate)) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                var cp = char.ConvertToUtf32(s[i], s[i + 1]);
                if (cp is >= 0x1D400 and <= 0x1D7FF)
                {
                    sb.Append(char.ConvertFromUtf32(cp).Normalize(NormalizationForm.FormKC));
                    i++;
                    continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public static string? FirstImage(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = ImgSrc().Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^\s*(RE|RT|QT)\s*:?\s*(?=https?://)", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyToLink();

    /// <summary>
    /// A readable title for a social post: links removed ("RE: https://…" isn't a title), falling back to the linked
    /// card's title or the link's site, and the first sentence when the text is long.
    /// </summary>
    public static string PostTitle(string text, string? fallback = null, int max = 160)
    {
        var firstUrl = UrlPattern().Match(text);
        var t = UrlPattern().Replace(ReplyToLink().Replace(text, ""), " ");
        t = string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '—', '-', ':', '·', '|');
        if (t.Length < 3)
        {
            t = fallback is { Length: >= 3 } ? fallback
                : firstUrl.Success && Uri.TryCreate(firstUrl.Value, UriKind.Absolute, out var u) ? "Link to " + u.Host
                : text;
        }
        if (t.Length > max)
        {
            var end = t.IndexOfAny(new[] { '.', '!', '?' }, Math.Min(40, t.Length - 1));
            t = end > 0 && end < max ? t[..(end + 1)] : Truncate(t, max);
        }
        return t;
    }

    public static string Truncate(string s, int max)
    {
        if (max <= 0 || s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', Math.Max(0, max - 1), Math.Min(40, max));
        if (cut < max * 0.6) cut = max - 1;
        return s[..cut].TrimEnd(',', ';', ':', '-', ' ') + "…";
    }
}

/// <summary>How long model-written text may stand in while the model is unavailable.</summary>
public static class AiShelfLife
{
    public static bool Expired(DateTimeOffset generated, TimeSpan maxAge, bool sameDay = false, DateTimeOffset? now = null)
    {
        var n = now ?? DateTimeOffset.Now;
        return n - generated > maxAge || (sameDay && generated.ToLocalTime().Date != n.ToLocalTime().Date);
    }
}

public static class TimeText
{
    /// <summary>"21:14" today, "yesterday 21:14", "Sun 21:14" this week, else "12 Sep 21:14".</summary>
    public static string Stamp(DateTimeOffset time, bool use24 = true, DateTimeOffset? now = null)
    {
        var local = time.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        var clock = local.ToString(use24 ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);
        if (local.Date == today) return clock;
        if (local.Date == today.AddDays(-1)) return "yesterday " + clock;
        if (local.Date > today.AddDays(-7) && local.Date < today) return local.ToString("ddd", CultureInfo.CurrentCulture) + " " + clock;
        return local.ToString("d MMM", CultureInfo.CurrentCulture) + " " + clock;
    }

    public static string Ago(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var span = (now ?? DateTimeOffset.Now) - time;
        if (span.TotalSeconds < 0) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d";
        return time.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }

    /// <summary>"just now", "5m ago", "3h ago", "on 2 Sep".</summary>
    public static string AgoPhrase(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var span = (now ?? DateTimeOffset.Now) - time;
        if (span.TotalMinutes < 1) return "just now";
        return span.TotalDays < 7 ? Ago(time, now) + " ago" : "on " + Ago(time, now);
    }

    /// <summary>
    /// For the model: "Mon 28 Sep 2026 22:05 (20h ago)" — the day spelled out, so an answer doesn't call yesterday's
    /// launch "today".
    /// </summary>
    public static string Dated(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var stamp = time.ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        var span = (now ?? DateTimeOffset.Now) - time;
        return span.TotalMinutes >= 0 && span.TotalDays < 7 ? stamp + " (" + AgoPhrase(time, now) + ")" : stamp;
    }

    public static string Until(DateTimeOffset time, DateTimeOffset? now = null)
    {
        var span = time - (now ?? DateTimeOffset.Now);
        if (span.TotalSeconds <= 0) return "now";
        if (span.TotalMinutes < 1) return "in under a minute";
        if (span.TotalMinutes < 60) return $"in {(int)Math.Ceiling(span.TotalMinutes)} min";
        if (span.TotalHours < 24) return $"in {(int)Math.Round(span.TotalHours)} h";
        var days = (int)Math.Round(span.TotalDays);
        return days == 1 ? "tomorrow" : $"in {days} days";
    }

    public static DateTimeOffset FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);

    /// <summary>Lenient date parser for RSS (RFC 822 variants) and ISO-8601.</summary>
    public static DateTimeOffset? ParseLenient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dto))
            return dto;

        // RFC 822 with named zones (e.g. "Sat, 27 Sep 2026 10:15:00 GMT", "EDT", "+0100").
        var zones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GMT"] = "+00:00", ["UT"] = "+00:00", ["UTC"] = "+00:00", ["Z"] = "+00:00",
            ["EST"] = "-05:00", ["EDT"] = "-04:00", ["CST"] = "-06:00", ["CDT"] = "-05:00",
            ["MST"] = "-07:00", ["MDT"] = "-06:00", ["PST"] = "-08:00", ["PDT"] = "-07:00",
            ["BST"] = "+01:00", ["IST"] = "+01:00", ["CET"] = "+01:00", ["CEST"] = "+02:00",
        };
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].EndsWith(',')) parts.RemoveAt(0);
        if (parts.Count >= 4)
        {
            var zone = parts[^1];
            if (zones.TryGetValue(zone, out var offset)) parts[^1] = offset;
            else if ((zone.StartsWith('+') || zone.StartsWith('-')) && zone.Length == 5) parts[^1] = zone[..3] + ":" + zone[3..];
            var candidate = string.Join(' ', parts);
            string[] formats =
            {
                "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMMM yyyy HH:mm:ss zzz",
                "d MMM yy HH:mm:ss zzz", "d MMM yyyy H:mm:ss zzz",
            };
            if (DateTimeOffset.TryParseExact(candidate, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dto))
                return dto;
        }
        return null;
    }
}

/// <summary>Count + noun with the right plural ("1 story", "3 stories", "1 pt", "4 pts").</summary>
public static class Plural
{
    public static string Of(int count, string singular, string? plural = null) =>
        $"{count.ToString("N0", CultureInfo.CurrentCulture)} {Word(count, singular, plural)}";

    public static string Word(int count, string singular, string? plural = null) =>
        count == 1 ? singular : plural ?? Pluralise(singular);

    public static string Pluralise(string word)
    {
        if (word.Length > 1 && word[^1] == 'y' && !"aeiou".Contains(word[^2])) return word[..^1] + "ies";
        if (word.EndsWith('s') || word.EndsWith('x') || word.EndsWith("ch", StringComparison.Ordinal) || word.EndsWith("sh", StringComparison.Ordinal)) return word + "es";
        return word + "s";
    }
}

public static class Money
{
    /// <summary>
    /// One price in full, for hover read-outs: always cents (€234.56, $6,512.30), four decimals below 1, pence for GBp,
    /// and the currency code for currencies without a symbol here.
    /// </summary>
    public static string Exact(double v, string currency, IFormatProvider culture)
    {
        var s = v.ToString("N" + (Math.Abs(v) < 1 ? 4 : 2), culture);
        return currency switch
        {
            "USD" => "$" + s,
            "EUR" => "€" + s,
            "GBP" => "£" + s,
            "GBp" or "GBX" => s + "p",
            "" => s,
            _ => s + " " + currency,
        };
    }
}
