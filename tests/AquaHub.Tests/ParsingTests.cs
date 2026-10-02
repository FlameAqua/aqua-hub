using AquaHub.Core.Feeds;
using AquaHub.Core.Models;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

public class FeedParserTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:media="http://search.yahoo.com/mrss/" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <channel>
            <title>Example Wire</title>
            <link>https://example.org</link>
            <item>
              <title>Central bank holds rates steady &amp; signals patience</title>
              <link>https://example.org/a</link>
              <guid>abc-1</guid>
              <description><![CDATA[<p>The bank <b>kept</b> its key rate unchanged.</p><script>alert(1)</script>]]></description>
              <pubDate>Sun, 27 Sep 2026 09:15:00 GMT</pubDate>
              <dc:creator>Jane Reporter</dc:creator>
              <media:thumbnail url="https://img.example.org/a.jpg" />
            </item>
            <item>
              <title>Second story</title>
              <link>https://example.org/b</link>
              <pubDate>Sun, 27 Sep 2026 10:00:00 +0100</pubDate>
              <enclosure url="https://img.example.org/b.png" type="image/png" />
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void ParsesRssItemsWithImagesDatesAndCleanText()
    {
        var feed = FeedParser.Parse(Rss);
        Assert.Equal("Example Wire", feed.Title);
        Assert.Equal(2, feed.Entries.Count);
        var a = feed.Entries[0];
        Assert.Equal("Central bank holds rates steady & signals patience", a.Title);
        Assert.Equal("https://img.example.org/a.jpg", a.ImageUrl);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 9, 15, 0, TimeSpan.Zero), a.Published);
        var plain = HtmlText.ToPlain(a.SummaryHtml);
        Assert.Equal("The bank kept its key rate unchanged.", plain);
        Assert.DoesNotContain("alert", plain);
        Assert.Equal("https://img.example.org/b.png", feed.Entries[1].ImageUrl);
        Assert.Equal(TimeSpan.FromHours(1), feed.Entries[1].Published!.Value.Offset);
    }

    [Fact]
    public void ParsesAtomFeeds()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:media="http://search.yahoo.com/mrss/">
              <title>Sub</title>
              <entry>
                <title>Bus lanes coming to the quays</title>
                <link rel="alternate" href="https://www.reddit.com/r/Dublin/comments/x1/bus_lanes/" />
                <id>t3_x1</id>
                <updated>2026-09-27T08:00:00+00:00</updated>
                <author><name>/u/someone</name></author>
                <content type="html">&lt;p&gt;Council approves plan&lt;/p&gt; submitted by /u/someone &lt;a href="https://www.rte.ie/news/x"&gt;[link]&lt;/a&gt;</content>
                <media:thumbnail url="https://b.thumbs.redditmedia.com/x.jpg" />
              </entry>
            </feed>
            """;
        var feed = FeedParser.Parse(atom);
        var e = Assert.Single(feed.Entries);
        Assert.Equal("Bus lanes coming to the quays", e.Title);
        Assert.StartsWith("https://www.reddit.com/", e.Link);
        Assert.Equal("https://b.thumbs.redditmedia.com/x.jpg", e.ImageUrl);

        var items = RssSource.Map(feed, new FeedSpec { Id = "reddit:dublin", Name = "r/Dublin", Url = "https://www.reddit.com/r/Dublin/hot/.rss", Platform = "reddit", Kind = ItemKind.Social });
        var item = Assert.Single(items);
        Assert.Equal("https://www.rte.ie/news/x", item.Url);         // external link extracted
        Assert.StartsWith("https://www.reddit.com/", item.CommentsUrl);
        Assert.Equal("Council approves plan", item.Summary);          // boilerplate removed
        Assert.Equal("u/someone", item.Author);
    }

    [Fact]
    public void ParsesRdfFeeds()
    {
        const string rdf = """
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/">
              <channel rdf:about="https://dw.example"><title>DW</title><link>https://dw.example</link></channel>
              <item rdf:about="https://dw.example/1"><title>Item one</title><link>https://dw.example/1</link><dc:date>2026-09-27T07:00:00Z</dc:date></item>
            </rdf:RDF>
            """;
        var feed = FeedParser.Parse(rdf);
        Assert.Equal("DW", feed.Title);
        Assert.Equal("Item one", Assert.Single(feed.Entries).Title);
    }

    /// <summary>
    /// A DTD with external entities (pointing at a listener here, and at a file with a marker in it), a parameter entity
    /// that would pull in more, and nested ones that would multiply: none of it is fetched or expanded.
    /// </summary>
    private static string HostileDtd(string root, int port, string secretFile) =>
        $"""<!DOCTYPE {root} [ <!ENTITY web SYSTEM "http://127.0.0.1:{port}/entity"> <!ENTITY file SYSTEM "{new Uri(secretFile).AbsoluteUri}"> """ +
        $"""<!ENTITY % remote SYSTEM "http://127.0.0.1:{port}/dtd"> %remote; <!ENTITY lol "lol"> <!ENTITY lots "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"> ]>""";

    [Fact]
    public void AFeedsDtdIsNeverFetchedOrExpanded()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var secret = Path.Combine(AppContext.BaseDirectory, $"xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "TOP-SECRET-MARKER");
        try
        {
            var dtd = HostileDtd("rss", port, secret);
            // Declared but unused: the DTD is skipped and the feed reads as usual.
            var quiet = FeedParser.Parse($"""<?xml version="1.0"?>{dtd}<rss version="2.0"><channel><title>t</title><item><title>Safe headline</title><link>https://x.example/</link></item></channel></rss>""");
            Assert.Equal("Safe headline", Assert.Single(quiet.Entries).Title);
            // Used: the entities were never defined, so the feed is refused rather than filled in — from text and from bytes.
            foreach (var use in new[] { "&web;", "&file;", "&lots;" })
            {
                var xml = $"""<?xml version="1.0"?>{dtd}<rss version="2.0"><channel><title>t</title><item><title>{use} safe</title></item></channel></rss>""";
                Assert.Throws<System.Xml.XmlException>(() => FeedParser.Parse(xml));
                Assert.Throws<System.Xml.XmlException>(() => FeedParser.Parse(System.Text.Encoding.UTF8.GetBytes(xml)));
            }
            Assert.False(listener.Pending(), "the feed parser connected to the address in the DTD");
        }
        finally
        {
            File.Delete(secret);
            listener.Stop();
        }
    }

    [Fact]
    public void AWordFilesDtdIsRefusedUnread()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var dir = new TempDir();
        var secret = Path.Combine(dir.Path, "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET-MARKER");
        var docx = Path.Combine(dir.Path, "letter.docx");
        using (var zip = System.IO.Compression.ZipFile.Open(docx, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
            writer.Write($"""<?xml version="1.0"?>{HostileDtd("w:document", port, secret)}<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>&file; &web; &lots;</w:t></w:r></w:p></w:body></w:document>""");

        // Office never writes a DTD, so a document with one isn't read at all.
        Assert.Throws<System.Xml.XmlException>(() => AquaHub.Core.Ai.Assistant.Documents.ReadText(docx));
        Assert.False(listener.Pending(), "the document reader connected to the address in the DTD");
        listener.Stop();
    }

    [Fact]
    public void GoogleNewsTitlesLoseTheirPublisherSuffix()
    {
        const string xml = """
            <rss version="2.0"><channel><title>Google News</title>
            <item><title>Markets rally on rate hopes - Reuters</title><link>https://news.google.com/rss/articles/abc</link>
            <source url="https://www.reuters.com">Reuters</source><pubDate>Sun, 27 Sep 2026 09:00:00 GMT</pubDate></item>
            </channel></rss>
            """;
        var items = RssSource.Map(FeedParser.Parse(xml), new FeedSpec { Id = "gnews", Name = "Local", Url = "https://news.google.com/rss/search?q=x", Aggregator = true });
        var item = Assert.Single(items);
        Assert.Equal("Markets rally on rate hopes", item.Title);
        Assert.Equal("Reuters", item.SourceName);
    }

    [Theory]
    [InlineData("Sun, 27 Sep 2026 09:15:00 GMT", 9, 0)]
    [InlineData("Sun, 27 Sep 2026 09:15:00 EDT", 9, -4)]
    [InlineData("27 Sep 2026 09:15:00 +0100", 9, 1)]
    [InlineData("2026-09-27T09:15:00Z", 9, 0)]
    public void ParsesCommonDateFormats(string input, int hour, int offsetHours)
    {
        var d = TimeText.ParseLenient(input);
        Assert.NotNull(d);
        Assert.Equal(hour, d!.Value.Hour);
        Assert.Equal(TimeSpan.FromHours(offsetHours), d.Value.Offset);
    }

    [Fact]
    public void HtmlToPlainHandlesEntitiesListsAndTruncation()
    {
        Assert.Equal("Tom & Jerry’s “best”", HtmlText.ToPlain("Tom &amp;amp; Jerry&#8217;s &ldquo;best&rdquo;"));
        var list = HtmlText.ToPlain("<ul><li>One</li><li>Two</li></ul>", keepLines: true);
        Assert.Contains("• One", list);
        var longText = string.Join(' ', Enumerable.Repeat("word", 100));
        Assert.True(HtmlText.ToPlain(longText, 50).Length <= 51);
    }
}

public class IcsCalendarTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExpandsWeeklyRecurrenceWithExceptions()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:standup-1
            SUMMARY:Team stand-up
            DTSTART;TZID=Europe/Dublin:20260901T093000
            DTEND;TZID=Europe/Dublin:20260901T094500
            RRULE:FREQ=WEEKLY;BYDAY=TU,TH;COUNT=20
            EXDATE;TZID=Europe/Dublin:20261001T093000
            END:VEVENT
            END:VCALENDAR
            """;
        var events = IcsCalendar.Parse(ics, "Work", null, Start, Start.AddDays(14));
        Assert.All(events, e => Assert.Equal("Team stand-up", e.Title));
        Assert.All(events, e => Assert.Contains(e.Start.DayOfWeek, new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }));
        Assert.DoesNotContain(events, e => e.Start.Date == new DateTime(2026, 10, 1));
        Assert.Equal(3, events.Count); // Tue 29 Sep, (Thu 1 Oct excluded), Tue 6 Oct, Thu 8 Oct
        Assert.All(events, e => Assert.Equal(TimeSpan.FromMinutes(15), e.End!.Value - e.Start));
        // Local wall-clock time is preserved in the event's zone.
        var dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");
        Assert.All(events, e => Assert.Equal(new TimeSpan(9, 30, 0), TimeZoneInfo.ConvertTime(e.Start, dublin).TimeOfDay));
    }

    [Fact]
    public void HandlesAllDayUtcFoldedLinesAndMonthlyNthWeekday()
    {
        const string ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Bin day with a very long title that is\r\n  folded\r\nDTSTART;VALUE=DATE:20260930\r\nEND:VEVENT\r\n" +
                           "BEGIN:VEVENT\r\nUID:b\r\nSUMMARY:Board meeting\r\nDTSTART:20260910T140000Z\r\nDURATION:PT2H\r\nRRULE:FREQ=MONTHLY;BYDAY=2TH\r\nEND:VEVENT\r\nEND:VCALENDAR";
        var events = IcsCalendar.Parse(ics, "Home", "#ff0000", Start, Start.AddDays(20));
        var bin = Assert.Single(events, e => e.Title.StartsWith("Bin day"));
        Assert.True(bin.AllDay);
        Assert.EndsWith("folded", bin.Title);
        var board = Assert.Single(events, e => e.Title == "Board meeting");
        Assert.Equal(new DateTime(2026, 10, 8), board.Start.ToUniversalTime().Date); // 2nd Thursday of October
        Assert.Equal(TimeSpan.FromHours(2), board.End!.Value - board.Start);
    }
}
