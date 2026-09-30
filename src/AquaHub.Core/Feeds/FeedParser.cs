using System.Text;
using System.Xml;
using System.Xml.Linq;
using AquaHub.Core.Util;

namespace AquaHub.Core.Feeds;

public sealed record ParsedEntry
{
    public string Title { get; init; } = "";
    public string? Link { get; init; }
    public string? Guid { get; init; }
    public string SummaryHtml { get; init; } = "";
    public string? ContentHtml { get; init; }
    public DateTimeOffset? Published { get; init; }
    public string? Author { get; init; }
    public string? ImageUrl { get; init; }
    public string? SourceName { get; init; }
    /// <summary>The publisher's site from an RSS &lt;source url="…"&gt; (Google News), e.g. https://www.rte.ie.</summary>
    public string? SourceUrl { get; init; }
    public string? CommentsUrl { get; init; }
    public List<string> Categories { get; init; } = new();
}

public sealed record ParsedFeed(string Title, string? Link, List<ParsedEntry> Entries);

/// <summary>
/// Lenient, dependency-free RSS 2.0 / RSS 1.0 (RDF) / Atom parser.
/// Security: DTDs are ignored and external resolution is disabled (no XXE / entity expansion attacks),
/// document size is capped.
/// </summary>
public static class FeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Rss1 = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    static FeedParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static XmlReaderSettings SecureSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = 20_000_000,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
    };

    public static ParsedFeed Parse(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var reader = XmlReader.Create(ms, SecureSettings());
        var doc = XDocument.Load(reader, LoadOptions.None);
        return Parse(doc);
    }

    public static ParsedFeed Parse(string xml)
    {
        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, SecureSettings());
        return Parse(XDocument.Load(reader, LoadOptions.None));
    }

    private static ParsedFeed Parse(XDocument doc)
    {
        var root = doc.Root ?? throw new FormatException("Empty feed");
        var name = root.Name.LocalName.ToLowerInvariant();
        return name switch
        {
            "rss" => ParseRss(root.Element("channel") ?? root),
            "feed" => ParseAtom(root),
            "rdf" => ParseRdf(root),
            _ => throw new FormatException($"Unknown feed root <{root.Name.LocalName}>"),
        };
    }

    private static ParsedFeed ParseRss(XElement channel)
    {
        var entries = channel.Elements("item").Select(item =>
        {
            var link = Val(item.Element("link")) ?? Val(item.Element("guid"));
            var description = Val(item.Element("description")) ?? "";
            var content = Val(item.Element(Content + "encoded"));
            var source = item.Element("source");
            return new ParsedEntry
            {
                Title = Clean(Val(item.Element("title"))),
                Link = link?.Trim(),
                Guid = Val(item.Element("guid"))?.Trim(),
                SummaryHtml = description,
                ContentHtml = content,
                Published = TimeText.ParseLenient(Val(item.Element("pubDate")) ?? Val(item.Element(Dc + "date"))),
                Author = Val(item.Element(Dc + "creator")) ?? Val(item.Element("author")),
                ImageUrl = FindImage(item) ?? HtmlText.FirstImage(content) ?? HtmlText.FirstImage(description),
                SourceName = source is null ? null : Clean(source.Value),
                SourceUrl = source?.Attribute("url")?.Value,
                CommentsUrl = Val(item.Element("comments")),
                Categories = item.Elements("category").Select(c => Clean(c.Value)).Where(c => c.Length > 0).ToList(),
            };
        }).Where(e => e.Title.Length > 0).ToList();

        return new ParsedFeed(Clean(Val(channel.Element("title"))), Val(channel.Element("link")), entries);
    }

    private static ParsedFeed ParseRdf(XElement root)
    {
        var channel = root.Element(Rss1 + "channel");
        var entries = root.Elements(Rss1 + "item").Select(item =>
        {
            var description = Val(item.Element(Rss1 + "description")) ?? "";
            var content = Val(item.Element(Content + "encoded"));
            return new ParsedEntry
            {
                Title = Clean(Val(item.Element(Rss1 + "title"))),
                Link = Val(item.Element(Rss1 + "link"))?.Trim(),
                Guid = (string?)item.Attribute(XName.Get("about", "http://www.w3.org/1999/02/22-rdf-syntax-ns#")),
                SummaryHtml = description,
                ContentHtml = content,
                Published = TimeText.ParseLenient(Val(item.Element(Dc + "date"))),
                Author = Val(item.Element(Dc + "creator")),
                ImageUrl = FindImage(item) ?? HtmlText.FirstImage(content) ?? HtmlText.FirstImage(description),
                Categories = item.Elements(Dc + "subject").Select(c => Clean(c.Value)).ToList(),
            };
        }).Where(e => e.Title.Length > 0).ToList();
        return new ParsedFeed(Clean(Val(channel?.Element(Rss1 + "title"))), Val(channel?.Element(Rss1 + "link")), entries);
    }

    private static ParsedFeed ParseAtom(XElement feed)
    {
        var ns = feed.Name.Namespace == XNamespace.None ? XNamespace.None : Atom;
        var entries = feed.Elements(ns + "entry").Select(entry =>
        {
            var links = entry.Elements(ns + "link").ToList();
            var alternate = links.FirstOrDefault(l => ((string?)l.Attribute("rel") ?? "alternate") == "alternate") ?? links.FirstOrDefault();
            var summary = Val(entry.Element(ns + "summary")) ?? "";
            var content = Val(entry.Element(ns + "content"));
            var mediaGroup = entry.Element(Media + "group");
            if (summary.Length == 0 && mediaGroup is not null)
                summary = Val(mediaGroup.Element(Media + "description")) ?? "";
            return new ParsedEntry
            {
                Title = Clean(Val(entry.Element(ns + "title"))),
                Link = ((string?)alternate?.Attribute("href"))?.Trim(),
                Guid = Val(entry.Element(ns + "id")),
                SummaryHtml = summary,
                ContentHtml = content,
                Published = TimeText.ParseLenient(Val(entry.Element(ns + "published")) ?? Val(entry.Element(ns + "updated"))),
                Author = Val(entry.Element(ns + "author")?.Element(ns + "name")),
                ImageUrl = FindImage(entry) ?? (mediaGroup is null ? null : FindImage(mediaGroup))
                           ?? HtmlText.FirstImage(content) ?? HtmlText.FirstImage(summary),
                Categories = entry.Elements(ns + "category").Select(c => (string?)c.Attribute("label") ?? (string?)c.Attribute("term") ?? "")
                    .Where(c => c.Length > 0).ToList(),
            };
        }).Where(e => e.Title.Length > 0).ToList();

        var feedLink = feed.Elements(ns + "link").FirstOrDefault(l => ((string?)l.Attribute("rel") ?? "alternate") == "alternate");
        return new ParsedFeed(Clean(Val(feed.Element(ns + "title"))), (string?)feedLink?.Attribute("href"), entries);
    }

    private static string? FindImage(XElement item)
    {
        foreach (var thumb in item.Elements(Media + "thumbnail"))
        {
            var url = (string?)thumb.Attribute("url");
            if (IsHttp(url)) return url;
        }
        foreach (var mc in item.Elements(Media + "content"))
        {
            var url = (string?)mc.Attribute("url");
            var medium = (string?)mc.Attribute("medium");
            var type = (string?)mc.Attribute("type");
            if (IsHttp(url) && (medium == "image" || (type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false) ||
                                (medium is null && type is null)))
                return url;
            var nested = mc.Element(Media + "thumbnail");
            if (nested is not null && IsHttp((string?)nested.Attribute("url"))) return (string?)nested.Attribute("url");
        }
        foreach (var enc in item.Elements("enclosure"))
        {
            var type = (string?)enc.Attribute("type");
            var url = (string?)enc.Attribute("url");
            if (IsHttp(url) && (type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)) return url;
        }
        return null;
    }

    private static bool IsHttp(string? url) =>
        url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string? Val(XElement? e)
    {
        if (e is null) return null;
        var v = e.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static string Clean(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : HtmlText.ToPlain(s, 400);
}
