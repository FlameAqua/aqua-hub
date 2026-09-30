using System.Net;
using System.Text.RegularExpressions;
using AquaHub.Core.Feeds;
using AquaHub.Core.Net;

namespace AquaHub.Core.Sources;

public sealed record YouTubeChannelInfo(string ChannelId, string Name, string? Handle, string? AvatarUrl);

/// <summary>
/// Turns whatever you paste — a channel ID, youtube.com/channel/UC…, youtube.com/@handle, @handle, /c/ or /user/ links,
/// or a video link — into the channel's ID, name and avatar, by reading the public channel page (with the "reject all"
/// consent cookie, so EU visitors don't get the consent wall). Falls back to the channel's RSS feed for the name.
/// </summary>
public static partial class YouTubeChannels
{
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(UC[A-Za-z0-9_-]{22})(?![A-Za-z0-9_-])")]
    private static partial Regex ChannelId();

    [GeneratedRegex(@"^@?([A-Za-z0-9._-]{3,30})$")]
    private static partial Regex BareHandle();

    [GeneratedRegex(@"youtube\.com/(@[A-Za-z0-9._-]{3,30}|c/[^/?#\s]+|user/[^/?#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPath();

    [GeneratedRegex(@"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex VideoId();

    /// <summary>What the input names: a channel ID straight away, or the page that will tell us (null when it's not YouTube).</summary>
    public static (string? ChannelId, string? PageUrl) Parse(string input)
    {
        var s = input.Trim().Trim('<', '>', '"', '\'');
        if (s.Length == 0) return (null, null);
        if (s.Contains("youtube.com/channel/", StringComparison.OrdinalIgnoreCase) || !s.Contains('/') && ChannelId().IsMatch(s))
        {
            var m = ChannelId().Match(s);
            if (m.Success) return (m.Groups[1].Value, null);
        }
        var path = ChannelPath().Match(s);
        if (path.Success) return (null, "https://www.youtube.com/" + path.Groups[1].Value);
        var video = VideoId().Match(s);
        if (video.Success) return (null, "https://www.youtube.com/watch?v=" + video.Groups[1].Value);
        if (!s.Contains('/') && !s.Contains(' ') && BareHandle().Match(s) is { Success: true } h && (s.StartsWith('@') || !s.Contains('.')))
            return (null, "https://www.youtube.com/@" + h.Groups[1].Value);
        return (null, null);
    }

    /// <summary>Consent-free request headers for YouTube pages ("reject all" consent cookie).</summary>
    public static readonly IReadOnlyDictionary<string, string> PageHeaders = new Dictionary<string, string>
    {
        ["Cookie"] = "SOCS=CAI; CONSENT=PENDING+987",
        ["Accept-Language"] = "en-GB,en;q=0.9",
    };

    public static async Task<YouTubeChannelInfo?> ResolveAsync(HttpFetcher http, string input, CancellationToken ct = default)
    {
        var (id, page) = Parse(input);
        if (id is null && page is null) return null;
        YouTubeChannelInfo? info = null;
        if (page is not null)
        {
            var res = await http.GetAsync(page, maxBytes: 4 * 1024 * 1024, headers: PageHeaders, ct: ct).ConfigureAwait(false);
            if (res.Ok)
            {
                info = ParsePage(res.Text);
                // A video page names the channel; its own page has the avatar and handle.
                if (info is not null && page.Contains("watch?v=", StringComparison.Ordinal)) { id = info.ChannelId; info = null; }
            }
        }
        if (info is null && id is not null)
        {
            var res = await http.GetAsync($"https://www.youtube.com/channel/{id}", maxBytes: 4 * 1024 * 1024, headers: PageHeaders, ct: ct).ConfigureAwait(false);
            if (res.Ok) info = ParsePage(res.Text);
            if (info is null || info.ChannelId != id)
            {
                // The channel page may be unavailable; the feed still names the channel.
                var feed = await http.GetAsync($"https://www.youtube.com/feeds/videos.xml?channel_id={id}", ct: ct).ConfigureAwait(false);
                string? name = null;
                if (feed.Ok) { try { name = FeedParser.Parse(feed.Bytes).Title; } catch { } }
                info = name is { Length: > 0 } ? new YouTubeChannelInfo(id, name, null, null) : info?.ChannelId == id ? info : null;
            }
        }
        return info;
    }

    [GeneratedRegex(@"<meta\s+itemprop=""identifier""\s+content=""(UC[A-Za-z0-9_-]{22})""", RegexOptions.IgnoreCase)]
    private static partial Regex IdentifierMeta();

    [GeneratedRegex(@"""(?:externalId|channelId|browseId)""\s*:\s*""(UC[A-Za-z0-9_-]{22})""")]
    private static partial Regex IdJson();

    [GeneratedRegex(@"<link\s+rel=""canonical""\s+href=""https?://www\.youtube\.com/channel/(UC[A-Za-z0-9_-]{22})""", RegexOptions.IgnoreCase)]
    private static partial Regex Canonical();

    [GeneratedRegex(@"""vanityChannelUrl""\s*:\s*""https?://www\.youtube\.com/(@[^""/]+)""|""canonicalBaseUrl""\s*:\s*""/(@[^""/]+)""")]
    private static partial Regex HandleJson();

    [GeneratedRegex(@"""ownerChannelName""\s*:\s*""([^""]+)""")]
    private static partial Regex OwnerName();

    [GeneratedRegex(@"""channelMetadataRenderer""\s*:\s*\{\s*""title""\s*:\s*""((?:[^""\\]|\\.)+)""")]
    private static partial Regex MetadataTitle();

    [GeneratedRegex(@"""channelMetadataRenderer""[\s\S]{0,4000}?""avatar""\s*:\s*\{\s*""thumbnails""\s*:\s*\[\s*\{\s*""url""\s*:\s*""([^""]+)""")]
    private static partial Regex MetadataAvatar();

    [GeneratedRegex(@"<title>\s*([^<]+?)\s*-\s*YouTube\s*</title>", RegexOptions.IgnoreCase)]
    private static partial Regex PageTitle();

    /// <summary>Reads a channel (or video) page: ID, name, handle and avatar. Null when the page isn't a channel's.</summary>
    public static YouTubeChannelInfo? ParsePage(string html)
    {
        var id = IdentifierMeta().Match(html) is { Success: true } a ? a.Groups[1].Value
            : Canonical().Match(html) is { Success: true } b ? b.Groups[1].Value
            : IdJson().Match(html) is { Success: true } c ? c.Groups[1].Value : null;
        if (id is null) return null;
        var meta = ArticleExtractor.Meta(html);
        var isVideo = meta.GetValueOrDefault("og:type")?.StartsWith("video", StringComparison.OrdinalIgnoreCase) == true;
        // The page's own data as a fallback for the meta tags (their position in the page varies).
        var name = isVideo
            ? (OwnerName().Match(html) is { Success: true } o ? Regex.Unescape(o.Groups[1].Value) : "")
            : meta.GetValueOrDefault("og:title")
              ?? (MetadataTitle().Match(html) is { Success: true } t ? Regex.Unescape(t.Groups[1].Value)
                  : PageTitle().Match(html) is { Success: true } pt ? pt.Groups[1].Value : "");
        var handleMatch = HandleJson().Match(html);
        var handle = handleMatch.Success ? WebUtility.UrlDecode(handleMatch.Groups[1].Success ? handleMatch.Groups[1].Value : handleMatch.Groups[2].Value) : null;
        var avatar = isVideo ? null
            : meta.GetValueOrDefault("og:image") ?? (MetadataAvatar().Match(html) is { Success: true } av ? Regex.Unescape(av.Groups[1].Value) : null);
        // Ask for a small avatar (the page offers 900 px).
        if (avatar is not null) avatar = Regex.Replace(avatar, @"=s\d+-", "=s176-");
        return new YouTubeChannelInfo(id, WebUtility.HtmlDecode(name).Trim(), handle, avatar);
    }
}
