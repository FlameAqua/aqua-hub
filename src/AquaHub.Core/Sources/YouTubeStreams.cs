using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;

namespace AquaHub.Core.Sources;

public sealed class StreamPageException(string message, int httpCode = 0) : Exception(message) { public int HttpCode { get; } = httpCode; }

/// <summary>Bounded parsing of the public channel Streams tab. Unknown markup is a source failure, not "offline".</summary>
public static partial class YouTubeStreams
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    [GeneratedRegex("^UC[A-Za-z0-9_-]{22}$")]
    private static partial Regex ChannelPattern();
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoPattern();
    public static bool ValidChannel(string? id) => id is not null && ChannelPattern().IsMatch(id);

    public static IReadOnlyList<LiveStream> Parse(string html, ChannelRef channel)
    {
        if (!ValidChannel(channel.ChannelId)) throw new StreamPageException("The YouTube channel ID is invalid. Add its channel link again.");
        if (html.Length > MaximumBytes) throw new StreamPageException("YouTube's channel page exceeded the size limit.");
        var start = html.IndexOf("var ytInitialData =", StringComparison.Ordinal);
        var prefix = "var ytInitialData =".Length;
        if (start < 0) { start = html.IndexOf("window[\"ytInitialData\"] =", StringComparison.Ordinal); prefix = "window[\"ytInitialData\"] =".Length; }
        if (start < 0) throw Changed();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html[(start + prefix)..]);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var metadata = Path(root, "metadata", "channelMetadataRenderer");
            if (Text(metadata, "externalId") != channel.ChannelId) throw Changed();
            var tabs = Path(root, "contents", "twoColumnBrowseResultsRenderer", "tabs");
            if (tabs.ValueKind != JsonValueKind.Array) throw Changed();
            JsonElement? selected = null;
            var hasStreams = false;
            foreach (var entry in tabs.EnumerateArray())
            {
                var tab = Path(entry, "tabRenderer");
                var url = Text(Path(tab, "endpoint", "commandMetadata", "webCommandMetadata"), "url");
                if (!url.TrimEnd('/').EndsWith("/streams", StringComparison.Ordinal)) continue;
                hasStreams = true;
                if (Path(tab, "selected").ValueKind == JsonValueKind.True) selected = Path(tab, "content");
            }
            if (!hasStreams) return Array.Empty<LiveStream>(); // A recognised channel without a public Streams tab.
            if (selected is null || selected.Value.ValueKind != JsonValueKind.Object) throw Changed();
            if (Path(selected.Value, "richGridRenderer").ValueKind != JsonValueKind.Object &&
                Path(selected.Value, "sectionListRenderer").ValueKind != JsonValueKind.Object) throw Changed();
            var streams = new List<LiveStream>();
            foreach (var (key, row) in Renderers(selected.Value))
            {
                string id, title;
                if (key == "lockupViewModel")
                {
                    if (Text(row, "contentType") != "LOCKUP_CONTENT_TYPE_VIDEO" || !LiveBadge(Path(row, "contentImage"))) continue;
                    id = Text(row, "contentId");
                    title = Text(Path(row, "metadata", "lockupMetadataViewModel", "title"), "content");
                }
                else if (key == "videoRenderer")
                {
                    if (Path(row, "upcomingEventData").ValueKind != JsonValueKind.Undefined || !LiveBadge(row)) continue;
                    id = Text(row, "videoId");
                    title = Runs(Path(row, "title"));
                }
                else continue;
                if (!VideoPattern().IsMatch(id) || string.IsNullOrWhiteSpace(title)) continue;
                streams.Add(new("youtube:" + id, "youtube", channel.ChannelId,
                    channel.Name is { Length: > 0 } ? channel.Name : "YouTube channel", WebUtility.HtmlDecode(title)[..Math.Min(WebUtility.HtmlDecode(title).Length, 500)],
                    "https://www.youtube.com/watch?v=" + id));
                if (streams.Count >= 20) break;
            }
            return streams.DistinctBy(s => s.Id).ToArray();
        }
        catch (JsonException) { throw Changed(); }
    }

    private static bool LiveBadge(JsonElement root) => Renderers(root).Any(pair => pair.Key switch
    {
        "thumbnailBadgeViewModel" => Text(pair.Value, "badgeStyle") is "THUMBNAIL_OVERLAY_BADGE_STYLE_LIVE" or "THUMBNAIL_BADGE_STYLE_LIVE",
        "thumbnailOverlayTimeStatusRenderer" => Text(pair.Value, "style") == "LIVE",
        "metadataBadgeRenderer" => Text(pair.Value, "style") == "BADGE_STYLE_TYPE_LIVE_NOW",
        _ => false,
    });

    private static IEnumerable<(string Key, JsonElement Value)> Renderers(JsonElement root)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        var visited = 0;
        while (pending.TryPop(out var current))
        {
            if (++visited > 30000) throw new StreamPageException("YouTube's channel page is too complex to read safely.");
            if (current.ValueKind == JsonValueKind.Object)
                foreach (var property in current.EnumerateObject())
                {
                    yield return (property.Name, property.Value);
                    if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) pending.Push(property.Value);
                }
            else if (current.ValueKind == JsonValueKind.Array)
                foreach (var value in current.EnumerateArray())
                    if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) pending.Push(value);
        }
    }

    private static JsonElement Path(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out root)) return default;
        return root;
    }
    private static string Text(JsonElement root, string key) => Path(root, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";
    private static string Runs(JsonElement root) => Text(root, "simpleText") is { Length: > 0 } text ? text
        : Path(root, "runs") is { ValueKind: JsonValueKind.Array } runs ? string.Concat(runs.EnumerateArray().Select(r => Text(r, "text"))) : "";
    private static StreamPageException Changed() => new("YouTube's channel page couldn't be recognised. It may be unavailable or its format may have changed. Aqua will try again later.");
}
