using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Analysis;
using AquaHub.Core.Feeds;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Sources;

public sealed record FeedSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Url { get; init; }
    public ItemKind Kind { get; init; } = ItemKind.News;
    public string Platform { get; init; } = "rss";
    public string Category { get; init; } = "world";
    public int Tier { get; init; } = 2;
    public bool Local { get; init; }
    public bool Aggregator { get; init; }
    public int MaxItems { get; init; } = 40;
    /// <summary>
    /// For a local news search: a publisher must match one of these (".ie" domain, "irish", "dublin"…) or the item
    /// is dropped — a search for "Dublin" otherwise returns Dublin, California. Null = keep everything.
    /// </summary>
    public IReadOnlyList<string>? LocalPublisherHints { get; init; }
}

public sealed record FetchOutcome(string SourceId, bool Ok, bool NotModified, List<FeedItem> Items, string? Error);

/// <summary>RSS / Atom based sources: news outlets, Google News queries, Reddit, YouTube and custom feeds.</summary>
public static partial class RssSource
{
    public static string GoogleNewsUrl(string query, LocationSettings loc)
    {
        var lang = loc.Language.Split('-')[0];
        var country = loc.Country.ToUpperInvariant();
        // English results for a country without an English Google News edition come from the international one.
        if (lang == "en" && !LocalePacks.HasEnglishEdition(country)) country = "US";
        var hl = $"{lang}-{country}";
        var ceid = $"{country}:{lang}";
        if (string.IsNullOrWhiteSpace(query))
            return $"https://news.google.com/rss?hl={hl}&gl={country}&ceid={ceid}";
        // Older profiles have the unqualified local query; the country keeps a city name from matching elsewhere.
        if (query.Trim() == "{city} when:1d") query = "{city} {country} when:1d";
        var q = query.Replace("{city}", loc.City, StringComparison.OrdinalIgnoreCase)
                     .Replace("{country}", LocalePacks.CountryName(loc.Country), StringComparison.OrdinalIgnoreCase);
        return $"https://news.google.com/rss/search?q={Uri.EscapeDataString(q)}&hl={hl}&gl={country}&ceid={ceid}";
    }

    public static FeedSpec ToSpec(NewsSource src, LocationSettings loc) => new()
    {
        Id = src.Id,
        Name = src.Name,
        Url = src.Kind == "google" ? GoogleNewsUrl(src.Query, loc) : src.Url,
        Kind = ItemKind.News,
        Platform = "rss",
        Category = src.Category,
        Tier = src.Tier,
        Local = src.Local,
        Aggregator = src.Kind == "google" && (string.IsNullOrEmpty(src.Query) || !src.Query.Contains("site:", StringComparison.OrdinalIgnoreCase)),
        LocalPublisherHints = src.Kind == "google" && src.Local && (src.Query ?? "").Contains("{city}", StringComparison.OrdinalIgnoreCase)
            ? LocalePacks.PublisherHints(loc) : null,
    };

    /// <summary>True when a Google News item's publisher looks local (see <see cref="FeedSpec.LocalPublisherHints"/>).</summary>
    internal static bool IsLocalPublisher(string? name, string? sourceUrl, IReadOnlyList<string> hints)
    {
        var host = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
        var text = TextTools.Fold((name ?? "") + " " + host).ToLowerInvariant();
        foreach (var h in hints)
        {
            if (h.StartsWith('.') ? host.EndsWith(h, StringComparison.Ordinal) : text.Contains(h, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static async Task<FetchOutcome> FetchAsync(HttpFetcher http, FeedSpec spec, CancellationToken ct)
    {
        var res = await http.GetAsync(spec.Url, conditional: true, maxBytes: 6 * 1024 * 1024, ct: ct).ConfigureAwait(false);
        if (res.Status == FetchStatus.NotModified) return new FetchOutcome(spec.Id, true, true, new(), null);
        if (!res.Ok) return new FetchOutcome(spec.Id, false, false, new(), res.Error);
        try
        {
            var feed = FeedParser.Parse(res.Bytes);
            var items = Map(feed, spec);
            return new FetchOutcome(spec.Id, true, false, items, null);
        }
        catch (Exception ex)
        {
            return new FetchOutcome(spec.Id, false, false, new(), "Parse error: " + ex.Message);
        }
    }

    [GeneratedRegex(@"<a href=""([^""]+)"">\[link\]</a>", RegexOptions.IgnoreCase)]
    private static partial Regex RedditLink();

    [GeneratedRegex(@"submitted by\s+/u/\S+.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RedditBoilerplate();

    internal static List<FeedItem> Map(ParsedFeed feed, FeedSpec spec)
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<FeedItem>();
        foreach (var e in feed.Entries.Take(spec.MaxItems))
        {
            var title = e.Title;
            var sourceName = spec.Name;
            var url = e.Link;
            string summaryHtml = e.SummaryHtml.Length >= 60 || string.IsNullOrEmpty(e.ContentHtml) ? e.SummaryHtml : e.ContentHtml!;
            string? author = e.Author;
            string? commentsUrl = e.CommentsUrl;
            var summary = HtmlText.ToPlain(summaryHtml, 600);

            if (spec.Url.Contains("news.google.com", StringComparison.OrdinalIgnoreCase))
            {
                // "Headline - Publisher" → "Headline", publisher from <source>.
                var publisher = e.SourceName;
                if (spec.LocalPublisherHints is { Count: > 0 } hints && !IsLocalPublisher(publisher, e.SourceUrl, hints)) continue;
                if (!string.IsNullOrEmpty(publisher))
                {
                    var suffix = " - " + publisher;
                    if (title.EndsWith(suffix, StringComparison.Ordinal)) title = title[..^suffix.Length];
                    if (spec.Aggregator) sourceName = publisher;
                }
                else
                {
                    var dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
                    if (dash > 20) title = title[..dash];
                }
                summary = ""; // Google News descriptions are just link lists.
            }
            else if (spec.Platform == "reddit")
            {
                // Multi-subreddit feeds (r/a+b) tag each entry with its community, e.g. label "r/Dublin".
                var community = e.Categories.FirstOrDefault(c => c.StartsWith("r/", StringComparison.OrdinalIgnoreCase));
                if (community is not null) sourceName = community;
                var html = e.ContentHtml ?? e.SummaryHtml;
                var ext = RedditLink().Match(html ?? "");
                commentsUrl = e.Link;
                if (ext.Success)
                {
                    var target = WebUtility.HtmlDecode(ext.Groups[1].Value);
                    if (!target.Contains("reddit.com", StringComparison.OrdinalIgnoreCase) && target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        url = target;
                }
                summary = RedditBoilerplate().Replace(HtmlText.ToPlain(html, 900), "").Trim();
                summary = HtmlText.Truncate(summary, 500);
                author = author?.Replace("/u/", "u/");
            }

            if (string.IsNullOrWhiteSpace(title)) continue;
            if (url is not null && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = null;

            var published = e.Published ?? now;
            if (published > now.AddHours(2)) published = now; // clamp bogus future dates

            var sourceId = spec.Platform == "reddit" && sourceName.StartsWith("r/", StringComparison.OrdinalIgnoreCase)
                ? "reddit:" + sourceName[2..].ToLowerInvariant()
                : spec.Id;
            list.Add(new FeedItem
            {
                Id = "i" + Hash.Short(sourceId, e.Guid ?? e.Link ?? title),
                Kind = spec.Kind,
                SourceId = sourceId,
                SourceName = sourceName,
                Platform = spec.Platform,
                Category = spec.Category,
                Tier = spec.Tier,
                Title = HtmlText.Truncate(title, 220),
                Summary = summary,
                Url = url,
                ImageUrl = e.ImageUrl,
                Author = author,
                Published = published,
                CommentsUrl = commentsUrl,
                IsLocal = spec.Local,
            });
        }
        return list;
    }
}

/// <summary>Mastodon public hashtag timelines (no account needed).</summary>
public static class MastodonSource
{
    public static async Task<FetchOutcome> FetchHashtagAsync(HttpFetcher http, string instance, string tag, CancellationToken ct)
    {
        var id = $"mastodon:{instance}:{tag}".ToLowerInvariant();
        var url = $"https://{instance}/api/v1/timelines/tag/{Uri.EscapeDataString(tag)}?limit=30";
        using var doc = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        if (doc is null) return new FetchOutcome(id, false, false, new(), "Mastodon request failed");
        return new FetchOutcome(id, true, false, Map(doc.RootElement, instance, id, $"#{tag}", local: true), null);
    }

    /// <summary>
    /// Your own home timeline (people you follow). Needs an access token with the read:statuses scope
    /// (Mastodon → Preferences → Development → New application), kept in Windows Credential Manager.
    /// </summary>
    public static async Task<FetchOutcome> FetchHomeAsync(HttpFetcher http, string instance, string token, CancellationToken ct)
    {
        var id = $"mastodon:{instance}:home".ToLowerInvariant();
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        var res = await http.GetAsync($"https://{instance}/api/v1/timelines/home?limit=40", headers: headers, ct: ct).ConfigureAwait(false);
        if (!res.Ok) return new FetchOutcome(id, false, false, new(), res.HttpCode == 401 ? "Mastodon token was rejected" : res.Error ?? "Mastodon request failed");
        using var doc = JsonDocument.Parse(res.Bytes, new JsonDocumentOptions { MaxDepth = 64 });
        return new FetchOutcome(id, true, false, Map(doc.RootElement, instance, id, "Home", local: false), null);
    }

    /// <summary>Checks a token; returns the account name or null.</summary>
    public static async Task<string?> VerifyAsync(HttpFetcher http, string instance, string token, CancellationToken ct = default)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        using var doc = await http.GetJsonAsync($"https://{instance}/api/v1/accounts/verify_credentials", headers, ct: ct).ConfigureAwait(false);
        return doc?.RootElement.Str("acct");
    }

    private static List<FeedItem> Map(JsonElement statuses, string instance, string id, string sourceName, bool local)
    {
        var items = new List<FeedItem>();
        if (statuses.ValueKind != JsonValueKind.Array) return items;
        foreach (var raw in statuses.EnumerateArray())
        {
            // Boosts carry the original post in "reblog".
            var s = raw.TryProp("reblog", out var boosted) && boosted.ValueKind == JsonValueKind.Object ? boosted : raw;
            if (s.Bool("sensitive") == true) continue;
            var text = HtmlText.ToPlain(s.Str("content"), 700);
            if (text.Length < 12) continue;
            var account = s.TryProp("account", out var acc) ? acc : default;
            var card = s.TryProp("card", out var cd) ? cd : default;
            string? image = null;
            foreach (var m in s.Arr("media_attachments"))
                if (m.Str("type") == "image") { image = m.Str("preview_url"); break; }
            image ??= card.ValueKind == JsonValueKind.Object ? card.Str("image") : null;
            var created = TimeText.ParseLenient(s.Str("created_at")) ?? DateTimeOffset.UtcNow;
            items.Add(new FeedItem
            {
                Id = "m" + Hash.Short(instance, s.Str("id")),
                Kind = ItemKind.Social,
                SourceId = id,
                SourceName = sourceName,
                Platform = "mastodon",
                Category = "social",
                Tier = 3,
                Title = HtmlText.PostTitle(text, card.ValueKind == JsonValueKind.Object ? card.Str("title") : null),
                Summary = text,
                Url = s.Str("url") ?? s.Str("uri"),
                ImageUrl = image,
                Author = account.ValueKind == JsonValueKind.Object ? (account.Str("display_name") is { Length: > 0 } dn ? dn : account.Str("acct")) : null,
                Published = created,
                Score = (int)((s.Lng("favourites_count") ?? 0) + (s.Lng("reblogs_count") ?? 0)),
                Comments = (int)(s.Lng("replies_count") ?? 0),
                IsLocal = local,
            });
        }
        return items;
    }
}

/// <summary>Bluesky via the public AppView: trending topics, account feeds and custom feeds.</summary>
public static class BlueskySource
{
    private const string Api = "https://public.api.bsky.app/xrpc/";

    public static async Task<FetchOutcome> FetchTrendingAsync(HttpFetcher http, CancellationToken ct)
    {
        using var doc = await http.GetJsonAsync(Api + "app.bsky.unspecced.getTrendingTopics?limit=12", ct: ct).ConfigureAwait(false);
        if (doc is null) return new FetchOutcome("bluesky:trending", false, false, new(), "Bluesky trending unavailable");
        var items = new List<FeedItem>();
        var now = DateTimeOffset.UtcNow;
        var rank = 0;
        foreach (var t in doc.RootElement.Arr("topics"))
        {
            var topic = t.Str("displayName") ?? t.Str("topic");
            if (string.IsNullOrWhiteSpace(topic)) continue;
            var link = t.Str("link");
            rank++;
            items.Add(new FeedItem
            {
                Id = "bt" + Hash.Short(topic, now.ToString("yyyyMMdd")),
                Kind = ItemKind.Social,
                SourceId = "bluesky:trending",
                SourceName = "Bluesky trending",
                Platform = "bluesky",
                Category = "trending",
                Tier = 3,
                Title = topic,
                Summary = t.Str("description") ?? "",
                Url = link is null ? "https://bsky.app/" : "https://bsky.app" + (link.StartsWith('/') ? link : "/" + link),
                Published = now.AddMinutes(-rank),
                Score = Math.Max(1, 20 - rank),
            });
        }
        return new FetchOutcome("bluesky:trending", true, false, items, null);
    }

    private sealed record Session(string Handle, string AccessJwt, string Service, DateTimeOffset Created);
    private static Session? _session;
    private static DateTimeOffset _lastLoginFailure = DateTimeOffset.MinValue;

    /// <summary>
    /// Your own "Following" timeline. Signs in with an app password (Bluesky → Settings → Privacy and security →
    /// App passwords) kept in Windows Credential Manager; the session token lives only in memory.
    /// </summary>
    public static async Task<FetchOutcome> FetchTimelineAsync(HttpFetcher http, string handle, string appPassword, CancellationToken ct)
    {
        const string id = "bluesky:timeline";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = await EnsureSessionAsync(http, handle, appPassword, forceNew: attempt > 0, ct).ConfigureAwait(false);
            if (session is null) return new FetchOutcome(id, false, false, new(), "Bluesky sign-in failed — check your handle and app password");
            var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + session.AccessJwt };
            var res = await http.GetAsync(session.Service + "/xrpc/app.bsky.feed.getTimeline?limit=50", headers: headers, ct: ct).ConfigureAwait(false);
            if (res.HttpCode is 400 or 401 && attempt == 0) continue;   // expired token: sign in again once
            if (!res.Ok) return new FetchOutcome(id, false, false, new(), res.Error ?? "Bluesky timeline unavailable");
            using var doc = JsonDocument.Parse(res.Bytes, new JsonDocumentOptions { MaxDepth = 64 });
            return new FetchOutcome(id, true, false, MapFeed(doc.RootElement, id, "Following"), null);
        }
        return new FetchOutcome(id, false, false, new(), "Bluesky session expired");
    }

    /// <summary>Signs in (or reuses the in-memory session). Returns null and backs off for 30 minutes after a failure.</summary>
    public static async Task<string?> TestSignInAsync(HttpFetcher http, string handle, string appPassword, CancellationToken ct = default)
    {
        _lastLoginFailure = DateTimeOffset.MinValue;
        var s = await EnsureSessionAsync(http, handle, appPassword, forceNew: true, ct).ConfigureAwait(false);
        return s is null ? null : s.Handle;
    }

    private static async Task<Session?> EnsureSessionAsync(HttpFetcher http, string handle, string appPassword, bool forceNew, CancellationToken ct)
    {
        var current = _session;
        if (!forceNew && current is not null && current.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - current.Created < TimeSpan.FromMinutes(90)) return current;
        // Sign-in is rate limited by Bluesky: never hammer it with a wrong password.
        if (DateTimeOffset.UtcNow - _lastLoginFailure < TimeSpan.FromMinutes(30)) return null;
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["identifier"] = handle, ["password"] = appPassword });
        var (status, json) = await http.PostJsonAsync("https://bsky.social/xrpc/com.atproto.server.createSession", body, ct: ct).ConfigureAwait(false);
        using (json)
        {
            var jwt = status == 200 ? json?.RootElement.Str("accessJwt") : null;
            if (jwt is null)
            {
                _lastLoginFailure = DateTimeOffset.UtcNow;
                Log.Warn("bluesky", $"Sign-in failed (HTTP {status})");
                return null;
            }
            // Use the account's own PDS when the session names one (self-hosted accounts), else the bsky.social entryway.
            var service = "https://bsky.social";
            if (json!.RootElement.TryProp("didDoc", out var didDoc))
                foreach (var svc in didDoc.Arr("service"))
                    if (svc.Str("serviceEndpoint") is { } ep && HttpFetcher.IsAllowed(ep, out _)) { service = ep.TrimEnd('/'); break; }
            return _session = new Session(json.RootElement.Str("handle") ?? handle, jwt, service, DateTimeOffset.UtcNow);
        }
    }

    public static Task<FetchOutcome> FetchAuthorAsync(HttpFetcher http, string handle, CancellationToken ct) =>
        FetchPostsAsync(http, $"bluesky:@{handle}", "@" + handle,
            Api + $"app.bsky.feed.getAuthorFeed?actor={Uri.EscapeDataString(handle)}&limit=25&filter=posts_no_replies", ct);

    public static async Task<FetchOutcome> FetchFeedAsync(HttpFetcher http, string feedUri, CancellationToken ct)
    {
        var resolved = await ResolveAuthorityAsync(http, feedUri, ct).ConfigureAwait(false);
        return await FetchPostsAsync(http, "bluesky:" + Hash.Short(feedUri), "Bluesky feed",
            Api + $"app.bsky.feed.getFeed?feed={Uri.EscapeDataString(resolved)}&limit=30", ct).ConfigureAwait(false);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Dids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>at://handle/… → at://did:plc:…/… (feed links copied from bsky.app use the handle; getFeed needs the DID).</summary>
    private static async Task<string> ResolveAuthorityAsync(HttpFetcher http, string atUri, CancellationToken ct)
    {
        if (!atUri.StartsWith("at://", StringComparison.Ordinal)) return atUri;
        var rest = atUri[5..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return atUri;
        var authority = rest[..slash];
        if (authority.StartsWith("did:", StringComparison.Ordinal)) return atUri;
        if (!Dids.TryGetValue(authority, out var did))
        {
            using var doc = await http.GetJsonAsync(Api + "com.atproto.identity.resolveHandle?handle=" + Uri.EscapeDataString(authority), ct: ct).ConfigureAwait(false);
            did = doc?.RootElement.Str("did");
            if (did is null) return atUri;
            Dids[authority] = did;
        }
        return "at://" + did + rest[slash..];
    }

    private static async Task<FetchOutcome> FetchPostsAsync(HttpFetcher http, string id, string name, string url, CancellationToken ct)
    {
        using var doc = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        if (doc is null) return new FetchOutcome(id, false, false, new(), "Bluesky request failed");
        return new FetchOutcome(id, true, false, MapFeed(doc.RootElement, id, name), null);
    }

    private static List<FeedItem> MapFeed(JsonElement root, string id, string name)
    {
        var items = new List<FeedItem>();
        foreach (var entry in root.Arr("feed"))
        {
            if (!entry.TryProp("post", out var post)) continue;
            var uri = post.Str("uri") ?? "";
            var author = post.TryProp("author", out var a) ? a : default;
            var handle = author.ValueKind == JsonValueKind.Object ? author.Str("handle") ?? "" : "";
            var record = post.TryProp("record", out var r) ? r : default;
            var text = HtmlText.FoldStyledLetters(record.ValueKind == JsonValueKind.Object ? record.Str("text") ?? "" : "");
            string? image = null, external = null, extTitle = null;
            if (post.TryProp("embed", out var embed))
            {
                if (embed.TryProp("external", out var ext)) { external = ext.Str("uri"); extTitle = ext.Str("title"); image = ext.Str("thumb"); }
                foreach (var img in embed.Arr("images")) { image ??= img.Str("thumb"); break; }
            }
            if (text.Length < 3 && extTitle is null) continue;
            var rkey = uri.Split('/').LastOrDefault() ?? "";
            items.Add(new FeedItem
            {
                Id = "b" + Hash.Short(uri),
                Kind = ItemKind.Social,
                SourceId = id,
                SourceName = name,
                Platform = "bluesky",
                Category = "social",
                Tier = 3,
                Title = HtmlText.PostTitle(text.Replace('\n', ' '), extTitle),
                Summary = HtmlText.Truncate(text, 600),
                Url = handle.Length > 0 ? $"https://bsky.app/profile/{handle}/post/{rkey}" : external,
                ImageUrl = image,
                Author = author.ValueKind == JsonValueKind.Object ? author.Str("displayName") ?? handle : null,
                Published = TimeText.ParseLenient(record.ValueKind == JsonValueKind.Object ? record.Str("createdAt") : null) ?? DateTimeOffset.UtcNow,
                Score = (int)((post.Lng("likeCount") ?? 0) + (post.Lng("repostCount") ?? 0)),
                Comments = (int)(post.Lng("replyCount") ?? 0),
            });
        }
        return items;
    }
}

/// <summary>Hacker News front page via the Algolia API (one request).</summary>
public static class HackerNewsSource
{
    public static async Task<FetchOutcome> FetchFrontPageAsync(HttpFetcher http, int count, CancellationToken ct)
    {
        using var doc = await http.GetJsonAsync($"https://hn.algolia.com/api/v1/search?tags=front_page&hitsPerPage={count}", ct: ct).ConfigureAwait(false);
        if (doc is null) return new FetchOutcome("hackernews", false, false, new(), "Hacker News unavailable");
        var items = new List<FeedItem>();
        foreach (var h in doc.RootElement.Arr("hits"))
        {
            var id = h.Str("objectID");
            var title = h.Str("title");
            if (id is null || string.IsNullOrWhiteSpace(title)) continue;
            var discussion = $"https://news.ycombinator.com/item?id={id}";
            items.Add(new FeedItem
            {
                Id = "h" + id,
                Kind = ItemKind.Social,
                SourceId = "hackernews",
                SourceName = "Hacker News",
                Platform = "hackernews",
                Category = "tech",
                Tier = 3,
                Title = title,
                Url = h.Str("url") ?? discussion,
                CommentsUrl = discussion,
                Author = h.Str("author"),
                Published = TimeText.FromUnix(h.Lng("created_at_i") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Score = (int)(h.Lng("points") ?? 0),
                Comments = (int)(h.Lng("num_comments") ?? 0),
            });
        }
        return new FetchOutcome("hackernews", true, false, items, null);
    }
}
