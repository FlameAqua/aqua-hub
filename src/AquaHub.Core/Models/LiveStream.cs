using System.Text.RegularExpressions;

namespace AquaHub.Core.Models;

/// <summary>A live broadcast observed on a supported provider, never an arbitrary link supplied by a page.</summary>
public sealed partial record LiveStream(string Id, string Platform, string ChannelId, string Channel, string Title, string Url)
{
    public string Category { get; init; } = "";
    public int? Viewers { get; init; }
    public DateTimeOffset? Started { get; init; }
    [GeneratedRegex("^/[A-Za-z0-9_]{1,25}$")]
    private static partial Regex TwitchPath();
    [GeneratedRegex("^youtube:[A-Za-z0-9_-]{11}$")]
    private static partial Regex YouTubeId();
    public static bool IsValid(LiveStream item)
    {
        if (item.Id is not { Length: > 0 and <= 100 } || item.Title is not { Length: > 0 and <= 500 } ||
            item.Channel is not { Length: > 0 and <= 200 } || item.Category is not { Length: <= 200 } || item.Url is not { Length: > 0 and <= 2048 } ||
            !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        return item.Platform == "twitch" ? item.Id.StartsWith("twitch:", StringComparison.Ordinal) && uri.Host == "www.twitch.tv" && uri.Query.Length == 0 && TwitchPath().IsMatch(uri.AbsolutePath)
            : item.Platform == "youtube" && YouTubeId().IsMatch(item.Id) && item.Url == "https://www.youtube.com/watch?v=" + item.Id[8..];
    }
}

/// <summary>Per-source observations. Failure preserves the last known data and its original observation time.</summary>
public sealed record StreamObservation(string SourceId, string Platform, string Label, DateTimeOffset? Checked,
    bool Ok, string Message, IReadOnlyList<LiveStream> Items)
{
    public DateTimeOffset? Attempted { get; init; }
}

public sealed record StreamsSnapshot(IReadOnlyList<StreamObservation> Sources)
{
    public static StreamsSnapshot Empty { get; } = new(Array.Empty<StreamObservation>());
    public static StreamsSnapshot Clean(StreamsSnapshot? snapshot) => new((snapshot?.Sources ?? Array.Empty<StreamObservation>())
        .OfType<StreamObservation>().Where(s => s.Platform is "twitch" or "youtube" && s.SourceId is { Length: > 0 and <= 150 })
        .Take(7).Select(s => s with
        {
            Label = (s.Label ?? "")[..Math.Min(s.Label?.Length ?? 0, 200)],
            Message = (s.Message ?? "")[..Math.Min(s.Message?.Length ?? 0, 500)],
            Items = (s.Items ?? Array.Empty<LiveStream>()).OfType<LiveStream>().Where(LiveStream.IsValid).Take(100).DistinctBy(i => i.Id).ToArray(),
        }).DistinctBy(s => s.SourceId).ToArray());
}
