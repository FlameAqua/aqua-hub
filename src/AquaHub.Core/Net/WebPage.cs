using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Analysis;
using AquaHub.Core.Util;

namespace AquaHub.Core.Net;

/// <summary>A link on a page: its absolute address and its text.</summary>
public sealed record PageLink(string Url, string Text);

/// <summary>The readable part of a web page.</summary>
public sealed partial record WebPage
{
    public required string Url { get; init; }
    public string Title { get; init; } = "";
    public string Site { get; init; } = "";
    public DateTimeOffset? Published { get; init; }
    /// <summary>Main text, paragraphs separated by blank lines.</summary>
    public string Text { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Set for PDFs (and other binary documents): the raw bytes for the platform to extract.</summary>
    public byte[]? Pdf { get; init; }
    /// <summary>The page's links (absolute http(s), in page order, fragments dropped), for following them through a site.</summary>
    public IReadOnlyList<PageLink> Links { get; init; } = Array.Empty<PageLink>();

    /// <summary>
    /// The links most relevant to <paramref name="focus"/> (by their text and address), on the same site by default.
    /// Navigation boilerplate (log in, privacy, cookies…) is left out.
    /// </summary>
    public List<PageLink> RelevantLinks(string focus, int max = 12, bool sameSite = true)
    {
        var terms = TextTools.Signature(focus);
        var host = Uri.TryCreate(Url, UriKind.Absolute, out var u) ? Root(u.Host) : "";
        var scored = new List<(PageLink Link, double Score, int Index)>();
        for (var i = 0; i < Links.Count; i++)
        {
            var link = Links[i];
            if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var lu)) continue;
            if (sameSite && host.Length > 0 && Root(lu.Host) != host) continue;
            if (link.Url.TrimEnd('/').Equals(Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;
            if (Chrome().IsMatch(link.Text) || Chrome().IsMatch(lu.AbsolutePath)) continue;
            var words = TextTools.Signature(link.Text + " " + Uri.UnescapeDataString(lu.AbsolutePath).Replace('/', ' ').Replace('-', ' ').Replace('_', ' '));
            var hits = terms.Count(words.Contains);
            scored.Add((link, hits * 2 + (link.Text.Length > 20 ? 0.3 : 0) - i * 0.001, i));
        }
        return scored.Where(x => terms.Count == 0 || x.Score >= 2).OrderByDescending(x => x.Score).ThenBy(x => x.Index).Take(max).Select(x => x.Link).ToList();
    }

    /// <summary>The site a host belongs to: blog.example.com → example.com, www.bbc.co.uk → bbc.co.uk.</summary>
    internal static string Root(string host)
    {
        var parts = host.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return string.Join('.', parts);
        // Two-part public suffixes (co.uk, com.au, gov.ie…): keep three labels.
        var twoPart = parts[^2] is "co" or "com" or "org" or "net" or "gov" or "ac" or "edu" && parts[^1].Length == 2;
        return string.Join('.', parts[^(twoPart ? 3 : 2)..]);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(log ?in|sign ?in|sign ?up|register|privacy|cookies?|terms|contact( us)?|about( us)?|careers|advertis\w*|subscribe|newsletter|help|faq|skip to\b.*|home|menu|search|share|rss|feed|next|previous|more|login|signin|signup|accessibility|sitemap)$|/(login|signin|signup|privacy|cookies|terms|account|cart|checkout|share)(/|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Chrome();

    /// <summary>
    /// The parts of <see cref="Text"/> most relevant to <paramref name="focus"/> (in page order, lead paragraph first),
    /// up to <paramref name="maxChars"/>. With no focus it is simply the beginning.
    /// </summary>
    public string Excerpt(string? focus, int maxChars)
    {
        var paragraphs = Chunks(Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (paragraphs.Count == 0) return HtmlText.Truncate(Description, maxChars);
        var total = paragraphs.Sum(p => p.Length + 2);
        if (total <= maxChars || string.IsNullOrWhiteSpace(focus)) return HtmlText.Truncate(string.Join("\n\n", paragraphs), maxChars);

        var terms = TextTools.Signature(focus);
        var scored = paragraphs.Select((p, i) =>
        {
            var words = TextTools.Signature(p);
            var hits = terms.Count(words.Contains);
            // Earlier paragraphs carry the story; matches carry the question.
            return (Index: i, Score: hits * 2.0 + (i == 0 ? 3 : i < 3 ? 1 : 0) - i * 0.02);
        }).OrderByDescending(x => x.Score).ToList();
        var chosen = new SortedSet<int>();
        var used = 0;
        foreach (var (index, _) in scored)
        {
            var len = paragraphs[index].Length + 2;
            if (used + len > maxChars) { if (used > maxChars * 0.7) break; continue; }
            chosen.Add(index);
            used += len;
        }
        var sb = new StringBuilder();
        var last = -1;
        foreach (var i in chosen)
        {
            if (sb.Length > 0) sb.Append(i == last + 1 ? "\n\n" : "\n\n…\n\n");
            sb.Append(paragraphs[i]);
            last = i;
        }
        return sb.ToString();
    }

    /// <summary>Very long paragraphs (OCR'd pages, plain-text files) are cut into ~700-character pieces at line or sentence ends.</summary>
    private static IEnumerable<string> Chunks(IEnumerable<string> paragraphs)
    {
        foreach (var p in paragraphs)
        {
            if (p.Length <= 900) { yield return p; continue; }
            var byLine = p.Contains('\n');
            var pieces = byLine ? p.Split('\n') : p.Split(". ");
            var sb = new StringBuilder();
            foreach (var piece in pieces)
            {
                if (sb.Length > 0 && sb.Length + piece.Length > 700) { yield return sb.ToString().Trim(); sb.Clear(); }
                if (sb.Length > 0) sb.Append(byLine ? "\n" : ". ");
                sb.Append(piece);
            }
            if (sb.Length > 0) yield return sb.ToString().Trim();
        }
    }
}

/// <summary>
/// Pulls the readable article out of an HTML page: JSON-LD <c>articleBody</c> when the publisher provides it, else the
/// paragraphs of the page's &lt;article&gt; (or &lt;main&gt;, or the body) minus navigation, footers, forms and
/// boilerplate such as cookie and newsletter prompts. Regex-based by design: no HTML engine, nothing executed.
/// </summary>
public static partial class ArticleExtractor
{
    [GeneratedRegex(@"<(script|style|noscript|svg|iframe|template|head|nav|header|footer|aside|form|button|select|figcaption)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Noise();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<article\b[^>]*>(.*?)</article\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Article();

    [GeneratedRegex(@"<main\b[^>]*>(.*?)</main\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Main();

    [GeneratedRegex(@"<body\b[^>]*>(.*)</body\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Body();

    [GeneratedRegex(@"<(p|h[1-4]|li|blockquote|pre)\b[^>]*>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"<script[^>]+type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex JsonLd();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<time\b[^>]*datetime\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"\b(cookie|cookies|subscribe|subscription|newsletter|sign up|sign in|log in|advertisement|all rights reserved|copyright|©|share this|follow us|read more|related articles|click here|enable javascript|your browser)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Boilerplate();

    public static WebPage Extract(string html, string url)
    {
        var meta = Meta(html);
        var title = FirstNonEmpty(meta.GetValueOrDefault("og:title"), meta.GetValueOrDefault("twitter:title"),
            HtmlText.ToPlain(TitleTag().Match(html).Groups[1].Value, 300));
        var site = FirstNonEmpty(meta.GetValueOrDefault("og:site_name"), Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "");
        var description = HtmlText.ToPlain(FirstNonEmpty(meta.GetValueOrDefault("og:description"), meta.GetValueOrDefault("description"), meta.GetValueOrDefault("twitter:description")), 600);
        var published = TimeText.ParseLenient(FirstNonEmpty(meta.GetValueOrDefault("article:published_time"), meta.GetValueOrDefault("date"),
            meta.GetValueOrDefault("pubdate"), TimeTag().Match(html).Groups[1].Value));

        string text = "";
        foreach (Match m in JsonLd().Matches(html))
        {
            var (body, date) = FromJsonLd(m.Groups[1].Value);
            published ??= date;
            if (body.Length > text.Length) text = body;
        }
        if (text.Length < 400)
        {
            var fromHtml = FromBlocks(html);
            if (fromHtml.Length > text.Length) text = fromHtml;
        }
        return new WebPage
        {
            Url = url, Title = HtmlText.Truncate(title, 300), Site = site, Published = published,
            Text = text.Length > 0 ? text : description, Description = description, Links = ExtractLinks(html, url),
        };
    }

    [GeneratedRegex(@"<a\b([^>]*)>(.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"\b(?:aria-label|title)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex LinkLabel();

    /// <summary>The page's links: absolute http(s) addresses with their text (or label), fragments dropped, at most 400.</summary>
    internal static List<PageLink> ExtractLinks(string html, string url)
    {
        var list = new List<PageLink>();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scope = html.Length > 2_000_000 ? html[..2_000_000] : html;
        foreach (Match m in Anchor().Matches(scope))
        {
            var h = Href().Match(m.Groups[1].Value);
            if (!h.Success) continue;
            var raw = WebUtility.HtmlDecode(h.Groups[1].Success ? h.Groups[1].Value : h.Groups[2].Success ? h.Groups[2].Value : h.Groups[3].Value).Trim();
            if (raw.Length == 0 || raw.StartsWith('#') || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUri, raw, out var abs) || abs.Scheme is not ("http" or "https")) continue;
            var clean = abs.GetLeftPart(UriPartial.Query);
            var text = HtmlText.ToPlain(m.Groups[2].Value, 160);
            if (text.Length == 0 && LinkLabel().Match(m.Groups[1].Value) is { Success: true } l) text = HtmlText.ToPlain(l.Groups[1].Success ? l.Groups[1].Value : l.Groups[2].Value, 160);
            // Only a link with words counts: listing pages link each article twice, the picture first (no text) and then
            // the headline, and the headline is the one worth keeping.
            if (text.Length == 0 || !seen.Add(clean)) continue;
            list.Add(new PageLink(clean, text));
            if (list.Count >= 400) break;
        }
        return list;
    }

    private static string FromBlocks(string html)
    {
        var cleaned = Comments().Replace(html.Length > 3_000_000 ? html[..3_000_000] : html, " ");
        cleaned = Noise().Replace(cleaned, " ");
        // The largest <article> wins (pages list teasers as articles too); else <main>; else the body.
        var scope = Article().Matches(cleaned).Select(m => m.Groups[1].Value).OrderByDescending(ParagraphChars).FirstOrDefault();
        if (scope is null || ParagraphChars(scope) < 300)
        {
            var main = Main().Match(cleaned);
            scope = main.Success && ParagraphChars(main.Groups[1].Value) >= 300 ? main.Groups[1].Value : Body().Match(cleaned) is { Success: true } b ? b.Groups[1].Value : cleaned;
        }
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Blocks().Matches(scope))
        {
            var tag = m.Groups[1].Value.ToLowerInvariant();
            var text = HtmlText.ToPlain(m.Groups[2].Value, 4000);
            if (text.Length == 0 || !seen.Add(text)) continue;
            var heading = tag.StartsWith('h');
            if (heading ? text.Length < 4 : text.Length < (tag == "li" ? 25 : 40)) continue;
            // Short lines that are mostly boilerplate ("Subscribe to our newsletter") go; long paragraphs that merely mention a word stay.
            if (text.Length < 160 && Boilerplate().IsMatch(text)) continue;
            parts.Add(heading ? "## " + text : tag == "li" ? "• " + text : text);
        }
        // Drop trailing headings with nothing under them.
        while (parts.Count > 0 && parts[^1].StartsWith("## ", StringComparison.Ordinal)) parts.RemoveAt(parts.Count - 1);
        return string.Join("\n\n", parts);
    }

    private static int ParagraphChars(string html) =>
        Regex.Matches(html, @"<p\b[^>]*>(.*?)</p\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Sum(m => Math.Min(2000, m.Groups[1].Length));

    private static (string Body, DateTimeOffset? Published) FromJsonLd(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.Trim(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var best = ("", (DateTimeOffset?)null);
            void Visit(JsonElement e, int depth)
            {
                if (depth > 6) return;
                if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Visit(x, depth + 1); return; }
                if (e.ValueKind != JsonValueKind.Object) return;
                if (e.Str("articleBody") is { Length: > 0 } body && body.Length > best.Item1.Length)
                    best = (HtmlText.ToPlain(body.Replace("\n", "\n\n"), 60_000, keepLines: true).Replace("\n", "\n\n"), TimeText.ParseLenient(e.Str("datePublished")));
                if (e.TryProp("@graph", out var graph)) Visit(graph, depth + 1);
            }
            Visit(doc.RootElement, 0);
            return best;
        }
        catch (JsonException) { return ("", null); }
    }

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex(@"\b(property|name|itemprop)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaKey();

    [GeneratedRegex(@"\bcontent\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase)]
    private static partial Regex MetaContent();

    internal static Dictionary<string, string> Meta(string html)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Meta tags live in <head>, which can be huge (YouTube's is ~760 KB of inline script before og:title): read up to
        // its end, within reason.
        var end = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        var head = end > 0 && end <= 3_000_000 ? html[..end] : html.Length > 400_000 ? html[..400_000] : html;
        foreach (Match m in MetaTag().Matches(head))
        {
            var key = MetaKey().Match(m.Value);
            var content = MetaContent().Match(m.Value);
            if (!key.Success || !content.Success) continue;
            var value = WebUtility.HtmlDecode(content.Groups[1].Success ? content.Groups[1].Value : content.Groups[2].Value).Trim();
            map.TryAdd(key.Groups[2].Value.Trim(), value);
        }
        return map;
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";
}

/// <summary>
/// Reads public web pages for Ask. Separate from <see cref="HttpFetcher"/> because the address comes from the model or
/// a search result: every connection (including redirects) is checked at connect time and refused when it resolves to
/// this PC or the local network (loopback, private, link-local, CGNAT and unique-local ranges), and only http(s) is
/// accepted. No cookies are kept and responses are capped.
/// </summary>
public sealed class WebReader : IDisposable
{
    private readonly HttpClient _http;
    public const int MaxBytes = 5 * 1024 * 1024;

    public WebReader()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseCookies = false,
            // Direct connections only: through a proxy the connect-time check would see the proxy, not the page's host.
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectPublicOnlyAsync,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpFetcher.UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,application/pdf;q=0.8,*/*;q=0.5");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en;q=0.9");
    }

    public HttpClient Client => _http;

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException($"{host} is on this PC or the local network, which Ask doesn't read");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>False for loopback, private, link-local, CGNAT, multicast and IPv6 unique/site-local addresses.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224 ||
                     (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 169 && b[1] == 254) ||
                     (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 0));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
        }
        return false;
    }

    /// <summary>Only absolute http(s) URLs without credentials; hosts named localhost or with no dot are refused early.</summary>
    public static bool IsReadableUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        if (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (!u.Host.Contains('.') && !u.Host.Contains(':')) return false;
        uri = u;
        return true;
    }

    /// <summary>Downloads and extracts a page (PDFs come back with <see cref="WebPage.Pdf"/> set for the platform to read).</summary>
    public async Task<WebPage> ReadAsync(string url, CancellationToken ct)
    {
        if (!IsReadableUrl(url, out var uri)) throw new InvalidOperationException("Only public http(s) pages can be read");
        if (WikipediaApi(uri!) is { } api)
        {
            var wiki = await ReadWikipediaAsync(uri!, api, ct).ConfigureAwait(false);
            if (wiki is not null) return wiki;
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        var bytes = await ReadCappedAsync(resp.Content, ct).ConfigureAwait(false);
        if (type.Contains("pdf", StringComparison.OrdinalIgnoreCase) || (bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F'))
            return new WebPage { Url = finalUrl, Title = Path.GetFileName(uri!.LocalPath), Site = uri.Host.Replace("www.", ""), Pdf = bytes };
        var charset = resp.Content.Headers.ContentType?.CharSet;
        string text;
        try { text = (string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'))).GetString(bytes); }
        catch (ArgumentException) { text = Encoding.UTF8.GetString(bytes); }
        if (type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
            return new WebPage { Url = finalUrl, Title = Path.GetFileName(uri!.LocalPath), Site = uri.Host.Replace("www.", ""), Text = HtmlText.Truncate(text, 60_000) };
        if (!type.Contains("html", StringComparison.OrdinalIgnoreCase) && !type.Contains("xml", StringComparison.OrdinalIgnoreCase) && type.Length > 0)
            throw new InvalidOperationException($"Can't read {type} pages");
        return ArticleExtractor.Extract(text, finalUrl);
    }

    private static string? WikipediaApi(Uri uri)
    {
        var m = Regex.Match(uri.Host, @"^(?<lang>[a-z\-]{2,12})(\.m)?\.wikipedia\.org$", RegexOptions.IgnoreCase);
        return m.Success && uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal) ? m.Groups["lang"].Value : null;
    }

    /// <summary>Wikipedia pages as plain text through its API (the HTML is mostly navigation and tables).</summary>
    private async Task<WebPage?> ReadWikipediaAsync(Uri uri, string lang, CancellationToken ct)
    {
        var title = Uri.UnescapeDataString(uri.AbsolutePath["/wiki/".Length..]);
        var api = $"https://{lang}.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&redirects=1&format=json&titles={Uri.EscapeDataString(title)}";
        try
        {
            using var resp = await _http.GetAsync(api, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await ReadCappedAsync(resp.Content, ct).ConfigureAwait(false));
            if (!doc.RootElement.TryProp("query", out var q) || !q.TryProp("pages", out var pages)) return null;
            foreach (var p in pages.EnumerateObject())
            {
                var extract = p.Value.Str("extract");
                if (string.IsNullOrWhiteSpace(extract)) continue;
                var text = Regex.Replace(extract, @"\n(={2,})\s*(.+?)\s*\1", "\n## $2");
                text = Regex.Replace(text, @"\n{2,}", "\n\n").Replace("\n", "\n\n").Replace("\n\n\n\n", "\n\n");
                return new WebPage { Url = uri.ToString(), Title = p.Value.Str("title") ?? title, Site = "Wikipedia", Text = HtmlText.Truncate(text, 80_000) };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { }
        return null;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long len && len > MaxBytes) throw new InvalidOperationException("The page is too large to read");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxBytes) break; // keep what fits: the article is near the top
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
