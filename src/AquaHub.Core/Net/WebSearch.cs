using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Feeds;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Net;

public sealed record WebResult(string Title, string Url, string Snippet, string Site, DateTimeOffset? Published = null, bool IsNews = false);

public sealed class WebSearchException(string message) : Exception(message);

/// <summary>
/// Keyless web search for Ask: DuckDuckGo's HTML results by default (or your SearXNG instance, or Brave with your API
/// key), Google News for anything recent, and Wikipedia as a fallback. When a provider asks for a human check the
/// search reports it instead of retrying — it never tries to get around one.
/// </summary>
public sealed partial class WebSearch
{
    private readonly HttpFetcher _http;
    private readonly Func<HubSettings> _settings;
    private readonly ISecretStore _secrets;

    public WebSearch(HttpFetcher http, Func<HubSettings> settings, ISecretStore secrets)
    {
        _http = http; _settings = settings; _secrets = secrets;
    }

    [GeneratedRegex(@"\b(today|tonight|yesterday|latest|recent|recently|breaking|this (week|month|morning|evening)|right now|news|update|announced?|20\d\d)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Timely();

    public static bool LooksTimely(string query) => Timely().IsMatch(query);

    /// <param name="recent">Also search the news (Google News) — for current events.</param>
    public async Task<List<WebResult>> SearchAsync(string query, int max = 8, bool recent = false, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0) return new();
        var s = _settings();
        var results = new List<WebResult>();
        string? problem = null;
        try
        {
            results.AddRange(s.Ask.SearchEngine switch
            {
                "searxng" when s.Ask.SearxngUrl.Length > 0 => await SearxngAsync(s.Ask.SearxngUrl, query, ct).ConfigureAwait(false),
                "brave" when _secrets.Get(SecretKeys.BraveSearchKey) is { Length: > 0 } key => await BraveAsync(key, query, ct).ConfigureAwait(false),
                _ => await DuckDuckGoAsync(query, s.Location, ct).ConfigureAwait(false),
            });
        }
        catch (WebSearchException ex) { problem = ex.Message; }

        if (recent || LooksTimely(query) || results.Count < 3)
        {
            try { results.AddRange((await GoogleNewsAsync(query, s.Location, ct).ConfigureAwait(false)).Take(recent ? 6 : 4)); }
            catch (WebSearchException ex) { problem ??= ex.Message; }
        }
        if (results.Count < 3)
        {
            try { results.AddRange(await WikipediaAsync(query, s.Location, ct).ConfigureAwait(false)); }
            catch (WebSearchException) { }
        }
        if (results.Count == 0 && problem is not null) throw new WebSearchException(problem);
        return Dedupe(results).Take(max).ToList();
    }

    internal static IEnumerable<WebResult> Dedupe(IEnumerable<WebResult> results)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            var key = Normalise(r.Url);
            if (key.Length == 0 || !seen.Add(key)) continue;
            yield return r;
        }
    }

    internal static string Normalise(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        return (u.Host.Replace("www.", "") + u.AbsolutePath.TrimEnd('/')).ToLowerInvariant();
    }

    // ───────────── DuckDuckGo (HTML results page) ─────────────

    private async Task<List<WebResult>> DuckDuckGoAsync(string query, LocationSettings loc, CancellationToken ct)
    {
        var region = loc.Country.Length == 2 ? $"&kl={loc.Country.ToLowerInvariant()}-{(loc.Language.Length >= 2 ? loc.Language[..2] : "en")}" : "";
        var res = await _http.GetAsync($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}{region}", maxBytes: 2 * 1024 * 1024, ct: ct).ConfigureAwait(false);
        if (!res.Ok) throw new WebSearchException("DuckDuckGo didn't answer (" + res.Error + ")");
        return ParseDuckDuckGo(res.Text);
    }

    [GeneratedRegex(@"<a[^>]+class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex DdgLink();

    [GeneratedRegex(@"class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex DdgSnippet();

    internal static List<WebResult> ParseDuckDuckGo(string html)
    {
        if (html.Contains("anomaly-modal", StringComparison.Ordinal) || html.Contains("bots use DuckDuckGo too", StringComparison.Ordinal))
            throw new WebSearchException("DuckDuckGo asked for a human check — try again later, or pick another search engine in Settings › Ask Aqua");
        var list = new List<WebResult>();
        // Each organic result is a "result results_links" block; ads carry "result--ad".
        var blocks = html.Split("<div class=\"result ", StringSplitOptions.None).Skip(1);
        foreach (var block in blocks)
        {
            if (block.StartsWith("result--ad", StringComparison.Ordinal)) continue;
            var link = DdgLink().Match(block);
            if (!link.Success) continue;
            var url = DecodeDdgUrl(WebUtility.HtmlDecode(link.Groups[1].Value));
            if (url is null || url.Contains("duckduckgo.com/y.js", StringComparison.Ordinal)) continue;
            var title = HtmlText.ToPlain(link.Groups[2].Value, 200);
            var snippet = HtmlText.ToPlain(DdgSnippet().Match(block).Groups[1].Value, 400);
            list.Add(new WebResult(title, url, snippet, Host(url)));
        }
        return list;
    }

    internal static string? DecodeDdgUrl(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var u)) return null;
        if (u.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in u.Query.TrimStart('?').Split('&'))
                if (part.StartsWith("uddg=", StringComparison.Ordinal))
                    return Uri.UnescapeDataString(part[5..]);
            return null;
        }
        return u.Scheme is "http" or "https" ? u.ToString() : null;
    }

    // ───────────── Google News (RSS search) ─────────────

    private async Task<List<WebResult>> GoogleNewsAsync(string query, LocationSettings loc, CancellationToken ct)
    {
        var country = loc.Country.Length == 2 ? loc.Country.ToUpperInvariant() : "US";
        var hl = $"en-{country}";
        var url = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl={hl}&gl={country}&ceid={country}:en";
        var res = await _http.GetAsync(url, maxBytes: 2 * 1024 * 1024, ct: ct).ConfigureAwait(false);
        if (!res.Ok) throw new WebSearchException("Google News didn't answer (" + res.Error + ")");
        return ParseGoogleNews(res.Bytes);
    }

    internal static List<WebResult> ParseGoogleNews(byte[] xml)
    {
        ParsedFeed feed;
        try { feed = FeedParser.Parse(xml); }
        catch (Exception) { return new(); }
        var list = new List<WebResult>();
        foreach (var e in feed.Entries.Take(10))
        {
            if (string.IsNullOrEmpty(e.Link)) continue;
            var title = e.Title;
            var publisher = e.SourceName ?? "";
            if (publisher.Length > 0 && title.EndsWith(" - " + publisher, StringComparison.Ordinal)) title = title[..^(publisher.Length + 3)];
            var when = e.Published is { } p ? TimeText.AgoPhrase(p) : "";
            list.Add(new WebResult(title, e.Link, $"{publisher}{(when.Length > 0 ? " · " + when : "")}", publisher.Length > 0 ? publisher : "Google News", e.Published, IsNews: true));
        }
        return list;
    }

    // ───────────── Wikipedia ─────────────

    private async Task<List<WebResult>> WikipediaAsync(string query, LocationSettings loc, CancellationToken ct)
    {
        using var doc = await _http.GetJsonAsync(
            $"https://en.wikipedia.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&format=json&srlimit=3&utf8=1", ct: ct).ConfigureAwait(false);
        if (doc is null) throw new WebSearchException("Wikipedia didn't answer");
        return ParseWikipedia(doc);
    }

    internal static List<WebResult> ParseWikipedia(JsonDocument doc)
    {
        var list = new List<WebResult>();
        if (!doc.RootElement.TryProp("query", out var q)) return list;
        foreach (var r in q.Arr("search"))
        {
            var title = r.Str("title");
            if (string.IsNullOrEmpty(title)) continue;
            list.Add(new WebResult(title, "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')),
                HtmlText.ToPlain(r.Str("snippet"), 300), "Wikipedia"));
        }
        return list;
    }

    // ───────────── SearXNG / Brave ─────────────

    private async Task<List<WebResult>> SearxngAsync(string baseUrl, string query, CancellationToken ct)
    {
        using var doc = await _http.GetJsonAsync($"{baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json", ct: ct).ConfigureAwait(false);
        if (doc is null) throw new WebSearchException("Your SearXNG instance didn't answer (is its JSON format enabled?)");
        var list = new List<WebResult>();
        foreach (var r in doc.RootElement.Arr("results").Take(10))
            if (r.Str("url") is { Length: > 0 } url && r.Str("title") is { Length: > 0 } title)
                list.Add(new WebResult(HtmlText.ToPlain(title, 200), url, HtmlText.ToPlain(r.Str("content"), 400), Host(url), TimeText.ParseLenient(r.Str("publishedDate"))));
        return list;
    }

    private async Task<List<WebResult>> BraveAsync(string key, string query, CancellationToken ct)
    {
        using var doc = await _http.GetJsonAsync($"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count=10",
            new Dictionary<string, string> { ["X-Subscription-Token"] = key, ["Accept"] = "application/json" }, ct: ct).ConfigureAwait(false);
        if (doc is null) throw new WebSearchException("Brave Search didn't answer (check the API key in Settings › Ask Aqua)");
        var list = new List<WebResult>();
        if (doc.RootElement.TryProp("web", out var web))
            foreach (var r in web.Arr("results").Take(10))
                if (r.Str("url") is { Length: > 0 } url && r.Str("title") is { Length: > 0 } title)
                    list.Add(new WebResult(HtmlText.ToPlain(title, 200), url, HtmlText.ToPlain(r.Str("description"), 400), Host(url)));
        return list;
    }

    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;
}
