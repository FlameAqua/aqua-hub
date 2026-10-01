using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

internal static class AskFixtures
{
    public static FeedItem Item(string id, string source, string title, string summary = "", string? url = null, int tier = 2, double hoursAgo = 2) => new()
    {
        Id = id, Kind = ItemKind.News, SourceId = source.ToLowerInvariant().Replace(' ', '-'), SourceName = source, Title = title, Summary = summary,
        Url = url ?? $"https://{source.ToLowerInvariant().Replace(" ", "")}.example/{id}", Tier = tier, Published = DateTimeOffset.UtcNow.AddHours(-hoursAgo),
        Category = "world",
    };

    /// <summary>The SpaceX story as the agents had it on 28 Sep: several outlets, an AI headline that differs from theirs.</summary>
    public static StoryCluster Starship() => new()
    {
        Id = "s-starship",
        Title = "SpaceX's Starship rocket reaches orbit for the first time despite engine failure",
        Category = "world",
        Items = new()
        {
            Item("i1", "Financial Times", "SpaceX's Starship rocket reaches orbit for the first time despite engine failure", "The rocket lost one of its Raptor engines during ascent.", "https://www.ft.com/content/6729bceb", 1),
            Item("i2", "RTÉ News", "SpaceX Starship makes orbit, deploys first payload", "The 14th test flight reached orbit and deployed Starlink simulators.", "https://www.rte.ie/news/2026/0928/1593259-spacex-starship/", 1),
            Item("i3", "Al Jazeera", "SpaceX's showpiece Starship rocket reaches orbit for first time", "Musk hailed the flight as a milestone.", "https://www.aljazeera.com/news/2026/9/28/spacexs-showpiece-starship", 2),
            Item("i4", "AP News", "SpaceX's supersized Starship launches into orbit for the first time but flight ending early", "", "https://news.google.com/rss/articles/CBMi123", 1),
        },
        FirstSeen = DateTimeOffset.UtcNow.AddHours(-6),
        Latest = DateTimeOffset.UtcNow.AddHours(-1),
        Summary = new StorySummary
        {
            Headline = "SpaceX Starship reaches orbit for first time despite engine failure",
            Tldr = "Starship's 14th flight reached orbit for the first time even though an engine failed; the mission was cut short.",
            KeyPoints = { "One Raptor engine shut down during ascent", "Starlink simulators were deployed" },
            WhyItMatters = "Orbit is the step before routine satellite and lunar missions.",
            IsAi = true, Model = "qwen3.5:9b",
        },
    };
}

public class AskRetrievalTests
{
    [Fact]
    public void QuickContextFindsRecentArticles()
    {
        // Regression: the search used "published >= now + 14 days", so nothing was ever retrieved and the model said the
        // context didn't mention SpaceX at all.
        using var dir = new TempDir();
        var db = new HubDatabase(Path.Combine(dir.Path, "hub.db"));
        db.UpsertItems(new[]
        {
            AskFixtures.Item("a1", "DW", "SpaceX launches supersized Starship into orbit", "The Starship rocket reached orbit on its 14th flight."),
            AskFixtures.Item("a2", "CNBC", "Oil prices steady as markets wait for the Fed"),
        });
        var state = new HubState(db);
        var settings = new HubSettings();
        var ask = new AskService(db, state, new LlmClient(() => settings.Ai, new InMemorySecretStore()), () => settings);

        var ctx = ask.BuildContext("Tell me more about: SpaceX Starship reaches orbit for first time despite engine failure");

        Assert.Contains("SpaceX launches supersized Starship into orbit", ctx.Text);
        Assert.Contains(ctx.Citations, c => c.Source == "DW");
        Assert.DoesNotContain(ctx.Citations, c => c.Source == "CNBC");
    }

    [Fact]
    public void HubSearchFindsTheStoryByItsAiHeadline()
    {
        var state = new HubState(null);
        state.SetStories(new List<StoryCluster> { AskFixtures.Starship() });
        var hits = HubSearch.Search(state, null, "Tell me more about: SpaceX Starship reaches orbit for first time despite engine failure");
        var story = Assert.Single(hits, h => h.Kind == "story");
        Assert.Equal("s-starship", story.Id);
        Assert.Contains("Financial Times", story.Source);
    }

    [Fact]
    public void StoryContextListsEveryOutletWithItsLink()
    {
        var book = new SourceBook();
        var text = HubSearch.Story(AskFixtures.Starship(), book);
        Assert.Contains("One Raptor engine shut down", text);
        Assert.Contains("Why it matters", text);
        foreach (var outlet in new[] { "Financial Times", "RTÉ News", "Al Jazeera", "AP News" }) Assert.Contains(outlet, text);
        Assert.Equal(4, book.Count);
        Assert.Contains(book.All, c => c.Url == "https://www.rte.ie/news/2026/0928/1593259-spacex-starship/");
    }

    [Fact]
    public void SourceBookNumbersEachSourceOnce()
    {
        var book = new SourceBook();
        var a = book.Add("A", "Site", "https://a.example/1");
        var b = book.Add("B", "Site", "https://b.example/2");
        var again = book.Add("A (again)", "Site", "https://A.example/1");
        Assert.Equal((1, 2, 1), (a, b, again));
        Assert.Equal(new[] { 1 }, book.CitedIn("See [1] and [9].").Select(c => c.Number));
    }

    [Theory]
    [InlineData("Both agree [1, 3].", "Both agree [1][3].")]
    [InlineData("See [2-4] for detail.", "See [2][3][4] for detail.")]
    [InlineData("One [5].", "One [5].")]
    public void CitationMarkersAreSplitIntoLinks(string input, string expected) => Assert.Equal(expected, CitationText.Normalize(input));
}

public class WebReadingTests
{
    private const string Article = """
        <html><head><title>Starship reaches orbit | Example News</title>
        <meta property="og:site_name" content="Example News"><meta property="article:published_time" content="2026-09-28T14:00:00Z">
        <meta name="description" content="A test flight reached orbit."></head>
        <body><nav><p>Home News Sport Weather — a long navigation paragraph that should never be part of the article text.</p></nav>
        <div class="cookie"><p>We use cookies to improve your experience. Accept all cookies?</p></div>
        <article><h1>Starship reaches orbit</h1>
        <p>SpaceX's Starship reached orbit for the first time on Sunday, despite losing one of its Raptor engines during the climb.</p>
        <p>The company said the vehicle deployed a set of Starlink simulator satellites before the mission was cut short.</p>
        <p>Engineers will study why the engine shut down; the next flight is planned within months, pending a regulator review.</p>
        <p>Subscribe to our newsletter</p>
        </article><footer><p>Copyright Example News 2026. All rights reserved and more footer text here.</p></footer></body></html>
        """;

    [Fact]
    public void ExtractsTheArticleNotTheChrome()
    {
        var page = ArticleExtractor.Extract(Article, "https://news.example/starship");
        Assert.Equal("Example News", page.Site);
        Assert.Contains("Raptor engines", page.Text);
        Assert.Contains("regulator review", page.Text);
        Assert.DoesNotContain("navigation paragraph", page.Text);
        Assert.DoesNotContain("cookies", page.Text);
        Assert.DoesNotContain("Subscribe", page.Text);
        Assert.DoesNotContain("All rights reserved", page.Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero), page.Published);
    }

    [Fact]
    public void PrefersJsonLdArticleBody()
    {
        var html = """
            <html><head><script type="application/ld+json">{"@context":"https://schema.org","@graph":[{"@type":"NewsArticle","datePublished":"2026-09-28T10:00:00Z",
            "articleBody":"First paragraph of the real article body, which is long enough to count as an article on its own merits here.\nSecond paragraph with the key detail: the booster was caught by the tower arms after a flawless return. More words to pass the minimum length for a proper body text, repeated so the body is clearly longer than four hundred characters in total, which is the threshold used by the extractor before it falls back to paragraphs."}]}</script></head>
            <body><p>Teaser text that is not the article but long enough to be counted as a paragraph by the fallback path.</p></body></html>
            """;
        var page = ArticleExtractor.Extract(html, "https://x.example/a");
        Assert.Contains("caught by the tower arms", page.Text);
        Assert.DoesNotContain("Teaser text", page.Text);
    }

    [Fact]
    public void ExcerptKeepsTheLeadAndTheRelevantParts()
    {
        var paragraphs = Enumerable.Range(1, 30).Select(i => $"Paragraph {i} talks about general things in the city, the weather and the traffic in some detail.").ToList();
        paragraphs[17] = "Paragraph 18 explains the Raptor engine failure: a fuel leak shut the engine down during ascent.";
        var page = new WebPage { Url = "u", Text = string.Join("\n\n", paragraphs) };
        var excerpt = page.Excerpt("why did the Raptor engine fail", 600);
        Assert.StartsWith("Paragraph 1 ", excerpt);
        Assert.Contains("fuel leak", excerpt);
        Assert.True(excerpt.Length <= 700);
    }

    [Fact]
    public void ExcerptSplitsOneHugeParagraph()
    {
        var ocr = string.Join("\n", Enumerable.Range(1, 200).Select(i => i == 150 ? "Invoice total due: €1,234.56 by 30 October" : $"Line {i} of a scanned page with ordinary words"));
        var excerpt = new WebPage { Url = "f", Text = ocr }.Excerpt("invoice total due", 800);
        Assert.Contains("€1,234.56", excerpt);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void ReadsOnlyPublicAddresses(string ip, bool allowed) => Assert.Equal(allowed, WebReader.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("https://arstechnica.com/space/", true)]
    [InlineData("http://example.com/page", true)]
    [InlineData("http://localhost:11434/api/tags", false)]
    [InlineData("https://127.0.0.1/", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("https://user:pass@example.com/", false)]
    [InlineData("https://intranet/", false)]
    [InlineData("javascript:alert(1)", false)]
    public void RefusesNonPublicUrls(string url, bool ok) => Assert.Equal(ok, WebReader.IsReadableUrl(url, out _));

    private const string DuckDuckGo = """
        <div class="result results_links results_links_deep result--ad "><a rel="nofollow" class="result__a" href="https://duckduckgo.com/y.js?ad_domain=shop.example">Buy rockets</a></div>
        <div class="result results_links results_links_deep web-result "><div class="links_main">
        <h2 class="result__title"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.abc.net.au%2Fnews%2F2026%2D09%2D29%2Fspacex%2Drocket%2F107205586&amp;rut=abc">SpaceX&#x27;s Starship rocket reaches orbit for first time</a></h2>
        <a class="result__snippet" href="//duckduckgo.com/l/?uddg=x"><b>SpaceX&#x27;s</b> <b>Starship</b> rocket has made it to orbit.</a></div></div>
        <div class="result results_links results_links_deep web-result "><div class="links_main">
        <h2 class="result__title"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.theguardian.com%2Fscience%2F2026%2Fsep%2F28%2Fstarship&amp;rut=def">Starship orbits</a></h2>
        <a class="result__snippet" href="//duckduckgo.com/l/?uddg=y">Blasted off from Starbase.</a></div></div>
        """;

    [Fact]
    public void ParsesDuckDuckGoResultsAndSkipsAds()
    {
        var results = WebSearch.ParseDuckDuckGo(DuckDuckGo);
        Assert.Equal(2, results.Count);
        Assert.Equal("https://www.abc.net.au/news/2026-09-29/spacex-rocket/107205586", results[0].Url);
        Assert.Equal("SpaceX's Starship rocket reaches orbit for first time", results[0].Title);
        Assert.Equal("abc.net.au", results[0].Site);
        Assert.Equal("SpaceX's Starship rocket has made it to orbit.", results[0].Snippet);
    }

    [Fact]
    public void ReportsABotCheckInsteadOfRetrying() =>
        Assert.Throws<WebSearchException>(() => WebSearch.ParseDuckDuckGo("<html><div class=\"anomaly-modal__title\">Unfortunately, bots use DuckDuckGo too.</div></html>"));

    [Fact]
    public void ParsesGoogleNewsResults()
    {
        var rss = """
            <?xml version="1.0"?><rss><channel><title>t</title>
            <item><title>Starship reaches orbit - Reuters</title><link>https://news.google.com/rss/articles/abc</link><pubDate>Mon, 28 Sep 2026 15:00:00 GMT</pubDate><source url="https://www.reuters.com">Reuters</source></item>
            </channel></rss>
            """;
        var r = Assert.Single(WebSearch.ParseGoogleNews(Encoding.UTF8.GetBytes(rss)));
        Assert.Equal("Starship reaches orbit", r.Title);
        Assert.Equal("Reuters", r.Site);
        Assert.True(r.IsNews);
    }

    [Fact]
    public void PicksReadablePagesFromDifferentSites()
    {
        var results = new List<WebResult>
        {
            new("Starship orbit engine failure explained", "https://blog.example/a", "starship engine", "blog.example"),
            new("Starship orbit — Reuters", "https://www.reuters.com/x", "orbit engine", "reuters.com"),
            new("Starship orbit again", "https://blog.example/b", "starship", "blog.example"),
            new("Starship orbit third", "https://blog.example/c", "starship", "blog.example"),
            new("Google link", "https://news.google.com/rss/articles/zzz", "", "Google News", IsNews: true),
            new("Video", "https://www.youtube.com/watch?v=1", "", "youtube.com"),
        };
        var picked = AskAgent.PickPages(results, "Starship orbit engine failure", 6);
        Assert.Equal("https://www.reuters.com/x", picked[0].Url);
        Assert.Equal(2, picked.Count(p => p.Site == "blog.example"));
        Assert.DoesNotContain(picked, p => p.Url.Contains("google") || p.Url.Contains("youtube"));
    }
}

public class DocumentTests
{
    private static void Zip(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
    }

    [Fact]
    public void ReadsWordDocuments()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "report.docx");
        Zip(path, ("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
            <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Budget 2027</w:t></w:r></w:p>
            <w:p><w:r><w:t>Spending rises by </w:t></w:r><w:r><w:t>4%</w:t></w:r></w:p>
            <w:p><w:pPr><w:numPr/></w:pPr><w:r><w:t>Housing first</w:t></w:r></w:p>
            </w:body></w:document>
            """));
        var text = Documents.ReadText(path);
        Assert.Contains("## Budget 2027", text);
        Assert.Contains("Spending rises by 4%", text);
        Assert.Contains("• Housing first", text);
    }

    [Fact]
    public void ReadsSpreadsheetsWithSharedStrings()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sheet.xlsx");
        Zip(path,
            ("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Costs" sheetId="1" r:id="rId1"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>"""),
            ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Item</t></si><si><t>Rent</t></si></sst>"""),
            ("xl/worksheets/sheet1.xml", """<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row><c t="s"><v>0</v></c><c t="inlineStr"><is><t>Amount</t></is></c></row><row><c t="s"><v>1</v></c><c><v>1450</v></c></row></sheetData></worksheet>"""));
        var text = Documents.ReadText(path);
        Assert.Contains("## Costs", text);
        Assert.Contains("Item\tAmount", text);
        Assert.Contains("Rent\t1450", text);
    }

    [Fact]
    public void ReadsSlidesInOrder()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "deck.pptx");
        const string ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        Zip(path, ("ppt/slides/slide10.xml", $"<p:sld {ns} xmlns:p=\"p\"><a:p><a:r><a:t>Tenth</a:t></a:r></a:p></p:sld>"),
                  ("ppt/slides/slide2.xml", $"<p:sld {ns} xmlns:p=\"p\"><a:p><a:r><a:t>Second</a:t></a:r></a:p></p:sld>"));
        var text = Documents.ReadText(path);
        Assert.True(text.IndexOf("Second", StringComparison.Ordinal) < text.IndexOf("Tenth", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsRtfAndText()
    {
        Assert.Equal("Hello world\nSecond line", Documents.Rtf(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Hello \b world\b0\par Second line}").Trim());
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "notes.md");
        File.WriteAllText(path, "# Notes\nBuy milk");
        Assert.Contains("Buy milk", Documents.ReadText(path));
        Assert.Throws<NotSupportedException>(() => Documents.ReadText(WriteFile(dir.Path, "tool.exe", "MZ")));
    }

    private static string WriteFile(string dir, string name, string content)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, content);
        return p;
    }
}

public class LocalFileTests
{
    [Fact]
    public void FindsFilesByNameAndContentButNeverSecrets()
    {
        using var dir = new TempDir();
        var docs = Directory.CreateDirectory(Path.Combine(dir.Path, "Documents")).FullName;
        Directory.CreateDirectory(Path.Combine(docs, "Taxes"));
        File.WriteAllText(Path.Combine(docs, "Taxes", "tax return 2025.txt"), "Refund expected in October");
        File.WriteAllText(Path.Combine(docs, "notes.txt"), "Remember the tax return deadline is 31 October");
        File.WriteAllText(Path.Combine(docs, "passwords.txt"), "tax return login hunter2");
        File.WriteAllText(Path.Combine(docs, "vault.kdbx"), "tax return");
        var files = new LocalFiles(() => new[] { docs });

        var hits = files.Search("tax return");
        Assert.Equal("tax return 2025.txt", hits[0].Name);
        Assert.Contains(hits, h => h.Name == "notes.txt" && h.Snippet!.Contains("deadline"));
        Assert.DoesNotContain(hits, h => h.Name is "passwords.txt" or "vault.kdbx");
    }

    [Fact]
    public void ReadsOnlyInsideAllowedFoldersOrWhatYouAttached()
    {
        using var dir = new TempDir();
        var allowed = Directory.CreateDirectory(Path.Combine(dir.Path, "Allowed")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(dir.Path, "Other")).FullName;
        File.WriteAllText(Path.Combine(other, "private.txt"), "x");
        var files = new LocalFiles(() => new[] { allowed });

        Assert.False(files.CanRead(Path.Combine(other, "private.txt"), out _, out var why));
        Assert.Contains("outside", why);
        Assert.False(files.CanRead(Path.Combine(allowed, "..", "Other", "private.txt"), out _, out _));
        Assert.False(files.CanRead(Path.Combine(allowed + "Sneaky", "x.txt"), out _, out _));
        files.Grant(Path.Combine(other, "private.txt"));
        Assert.True(files.CanRead(Path.Combine(other, "private.txt"), out _, out _));
        Assert.False(files.CanRead(Path.Combine(allowed, ".ssh", "id_rsa"), out _, out var secret));
        Assert.Contains("never", secret);
    }

    [Fact]
    public void ExpandsFolderTokens()
    {
        var list = LocalFiles.ExpandFolders(new[] { "%DOCUMENTS%", "%NOPE%", "relative\\path" }, t => t == "DOCUMENTS" ? Path.GetTempPath() : null);
        Assert.Single(list);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), list[0]);
    }
}

public class AskApprovalTests
{
    private sealed class FakeTool(ToolAccess access, string name = "fake") : AskTool
    {
        public override string Name => name;
        public override string Description => "";
        public override JsonObject Parameters => Schema();
        public override ToolAccess Access => access;
        public override string Describe(JsonElement args) => "Do the thing";
        public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct) => Task.FromResult(new ToolResult("ok", "ok"));
    }

    private static AskRun Run(HubSettings? s = null, bool sawPrivate = false, params string[] allowed) => new()
    {
        State = new HubState(null), Settings = s ?? new HubSettings(), Book = new SourceBook(), Options = new AskOptions(),
        Host = new NullHost(), AllowedForChat = new HashSet<string>(allowed), SawPrivate = sawPrivate,
    };

    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public void ActionsAskFirstUnlessAllowedForTheChat()
    {
        var act = new FakeTool(ToolAccess.Act, "open_item");
        Assert.NotNull(AskAgent.ApprovalFor(act, NoArgs, "Open x", Run()));
        Assert.Null(AskAgent.ApprovalFor(act, NoArgs, "Open x", Run(allowed: "open_item")));
        var s = new HubSettings();
        s.Ask.ConfirmActions = false;
        Assert.Null(AskAgent.ApprovalFor(act, NoArgs, "Open x", Run(s)));
    }

    [Fact]
    public void WebNeedsAnOkOnceTheAnswerHasSeenPrivateData()
    {
        var web = new FakeTool(ToolAccess.Web, "web_search");
        Assert.Null(AskAgent.ApprovalFor(web, NoArgs, "Search", Run()));
        var approval = AskAgent.ApprovalFor(web, NoArgs, "Search", Run(sawPrivate: true));
        Assert.NotNull(approval);
        Assert.Equal("web-after-private", approval!.Tool);
        Assert.Null(AskAgent.ApprovalFor(web, NoArgs, "Search", Run(sawPrivate: true, allowed: "web-after-private")));
        // Reading your files needs no extra OK (you switched on Use my PC), but it marks the answer.
        Assert.Null(AskAgent.ApprovalFor(new FakeTool(ToolAccess.Private, "read_file"), NoArgs, "Read", Run()));
    }
}

internal sealed class NullHost : IAskHost
{
    public readonly List<string> Steps = new();
    public readonly StringBuilder Answer = new();
    public readonly StringBuilder Reasoning = new();
    public Func<ToolApproval, bool> Approve { get; set; } = _ => true;
    public readonly List<ToolApproval> Asked = new();
    public void Status(string text) { }
    public int StepStarted(string icon, string text) { lock (Steps) { Steps.Add(text); return Steps.Count; } }
    public void StepFinished(int id, string text, bool ok = true, string? url = null) { lock (Steps) Steps[id - 1] = text; }
    public void Thinking(string delta) => Reasoning.Append(delta);
    public void Text(string delta) => Answer.Append(delta);
    public void ResetText() => Answer.Clear();
    public Task<bool> ApproveAsync(ToolApproval request, CancellationToken ct) { Asked.Add(request); return Task.FromResult(Approve(request)); }
}

/// <summary>
/// A scripted stand-in for Ollama on a loopback port: model list, capabilities, and /api/chat answers streamed as
/// NDJSON — enough to drive Ask's tool loop end to end without a GPU.
/// </summary>
internal sealed class FakeOllama : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public List<JsonNode> Chats { get; } = new();
    public Func<JsonNode, int, IEnumerable<JsonObject>> Script { get; set; } = (_, _) => new[] { Chunk("Hello", done: true) };
    /// <summary>Structured (JSON-schema) requests — planning, looking at pictures — answered here, not by <see cref="Script"/>.</summary>
    public List<JsonNode> JsonChats { get; } = new();
    public Func<JsonNode, string> Json { get; set; } = _ => "{}";
    public string[] Capabilities { get; set; } = { "completion", "tools", "thinking", "vision" };
    /// <summary>Installed models (the first is what "auto" picks when none is preferred).</summary>
    public string[] Models { get; set; } = { "qwen3.5:9b" };
    /// <summary>Per-model capabilities; null uses <see cref="Capabilities"/> for every model.</summary>
    public Func<string, string[]>? CapabilitiesFor { get; set; }

    public FakeOllama()
    {
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string Endpoint => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public static JsonObject Chunk(string content, bool done = false, string? thinking = null, JsonArray? calls = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (thinking is not null) message["thinking"] = thinking;
        if (calls is not null) message["tool_calls"] = calls;
        var o = new JsonObject { ["message"] = message, ["done"] = done };
        if (done) o["eval_count"] = 12;
        return o;
    }

    public static JsonArray Call(string name, JsonObject args) => new() { new JsonObject { ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } } };

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            var requestLine = await reader.ReadLineAsync() ?? "";
            var length = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
            var body = new char[length];
            var read = 0;
            while (read < length) read += await reader.ReadAsync(body, read, length - read);
            var path = requestLine.Split(' ')[1];
            string payload;
            if (path.EndsWith("/api/tags"))
                payload = new JsonObject
                {
                    ["models"] = new JsonArray(Models.Select(m => (JsonNode)new JsonObject
                    {
                        ["name"] = m, ["size"] = 6594462816L,
                        ["details"] = new JsonObject { ["family"] = m.Split(':')[0], ["parameter_size"] = "9.7B", ["quantization_level"] = "Q4_K_M" },
                    }).ToArray()),
                }.ToJsonString();
            else if (path.EndsWith("/api/version")) payload = """{"version":"0.30.5"}""";
            else if (path.EndsWith("/api/show"))
            {
                var model = JsonNode.Parse(new string(body))?["model"]?.GetValue<string>() ?? "";
                var caps = CapabilitiesFor?.Invoke(model) ?? Capabilities;
                payload = new JsonObject { ["capabilities"] = new JsonArray(caps.Select(c => (JsonNode)c).ToArray()) }.ToJsonString();
            }
            else if (path.EndsWith("/api/chat") && JsonNode.Parse(new string(body)) is { } json && json["format"] is not null)
            {
                lock (JsonChats) JsonChats.Add(json);
                payload = new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Json(json) }, ["done"] = true, ["eval_count"] = 20 }.ToJsonString();
            }
            else if (path.EndsWith("/api/chat"))
            {
                var request = JsonNode.Parse(new string(body))!;
                int turn;
                lock (Chats) { Chats.Add(request); turn = Chats.Count; }
                payload = string.Join("\n", Script(request, turn).Select(c => c.ToJsonString())) + "\n";
            }
            else payload = "{}";
            var bytes = Encoding.UTF8.GetBytes(payload);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

public class AskAgentTests
{
    private static (AskAgent Agent, HubState State, HubSettings Settings, LlmClient Llm) Build(FakeOllama server, HubSettings? settings = null)
    {
        var s = settings ?? new HubSettings();
        s.Ai.Endpoint = server.Endpoint;
        s.Ask.ReadStoryArticles = false;
        var state = new HubState(null);
        state.SetStories(new List<StoryCluster> { AskFixtures.Starship() });
        var llm = new LlmClient(() => s.Ai, new InMemorySecretStore());
        var http = new HttpFetcher(null);
        var agent = new AskAgent(state, null, llm, () => s, new WebSearch(http, () => s, new InMemorySecretStore()), new WebReader());
        return (agent, state, s, llm);
    }

    [Fact]
    public async Task TellMeMoreSendsTheWholeStoryToTheModel()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("It reached orbit [1] "), FakeOllama.Chunk("despite an engine failure [2].", done: true) } };
        var (agent, _, _, _) = Build(server);
        var host = new NullHost();

        var result = await agent.RunAsync("Tell me more about: SpaceX Starship reaches orbit", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions { StoryId = "s-starship" }, host, null, new HashSet<string>(), CancellationToken.None);

        var system = server.Chats[0]["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("THE STORY THE USER IS ASKING ABOUT", system);
        Assert.Contains("One Raptor engine shut down during ascent", system);
        Assert.Contains("https://www.rte.ie/news/2026/0928/1593259-spacex-starship/", system);
        Assert.Contains("Today is", system);
        Assert.Equal("It reached orbit [1] despite an engine failure [2].", result.Text);
        Assert.Equal(2, result.Citations.Count);
        Assert.Equal("qwen3.5:9b", result.Model);
    }

    [Fact]
    public async Task RunsToolsTheModelCallsAndCitesTheirResults()
    {
        using var server = new FakeOllama
        {
            Script = (req, turn) => turn == 1
                ? new[] { FakeOllama.Chunk("Let me check…"), FakeOllama.Chunk("", done: true, calls: FakeOllama.Call("search_hub", new JsonObject { ["query"] = "Starship orbit" })) }
                : new[] { FakeOllama.Chunk("Starship made orbit [1].", done: true) },
        };
        var (agent, _, _, _) = Build(server);
        var host = new NullHost();

        var result = await agent.RunAsync("Did Starship make orbit?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(),
            new AskOptions(), host, null, new HashSet<string>(), CancellationToken.None);

        Assert.Equal(2, server.Chats.Count);
        var offered = server.Chats[0]["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("search_hub", offered);
        Assert.DoesNotContain("web_search", offered); // Web off
        Assert.DoesNotContain("read_file", offered);  // Use my PC off
        var second = server.Chats[1]["messages"]!.AsArray();
        var toolMessage = second.Last(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Equal("search_hub", toolMessage["tool_name"]!.GetValue<string>());
        Assert.Contains("Starship", toolMessage["content"]!.GetValue<string>());
        Assert.Contains(second, m => m!["tool_calls"] is not null);
        Assert.Equal("Starship made orbit [1].", result.Text);
        Assert.Equal("Starship made orbit [1].", host.Answer.ToString()); // the "Let me check…" preamble was discarded
        Assert.Contains(host.Steps, s => s.StartsWith("Searched your feeds for", StringComparison.Ordinal));
        Assert.Single(result.Citations);
    }

    [Fact]
    public async Task ThinkModeTurnsReasoningOnAndReportsIt()
    {
        using var server = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("", thinking: "Compare the outlets."), FakeOllama.Chunk("Answer.", done: true) } };
        var (agent, _, _, _) = Build(server);
        var host = new NullHost();
        var result = await agent.RunAsync("Why?", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions { Think = true }, host, null, new HashSet<string>(), CancellationToken.None);
        Assert.True(server.Chats[0]["think"]!.GetValue<bool>());
        Assert.Equal("Compare the outlets.", result.Thinking);
        Assert.Equal("Compare the outlets.", host.Reasoning.ToString());
    }

    [Fact]
    public async Task ImagesGoToVisionModelsAndOcrTextToOthers()
    {
        var image = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var attachment = new AskAttachment { Name = "shot.png", Kind = AttachmentKind.Image, Image = image, Text = "ERROR 0x80070005 Access is denied" };

        using (var vision = new FakeOllama { Script = (_, _) => new[] { FakeOllama.Chunk("I see an error.", done: true) } })
        {
            var (agent, _, _, _) = Build(vision);
            await agent.RunAsync("What's this?", Array.Empty<LlmMessage>(), new[] { attachment }, new AskOptions(), new NullHost(), null, new HashSet<string>(), CancellationToken.None);
            var user = vision.Chats[0]["messages"]!.AsArray().Last()!;
            Assert.Equal(Convert.ToBase64String(image), user["images"]![0]!.GetValue<string>());
        }
        using (var blind = new FakeOllama { Capabilities = new[] { "completion", "tools" }, Script = (_, _) => new[] { FakeOllama.Chunk("Access denied.", done: true) } })
        {
            var (agent, _, _, _) = Build(blind);
            await agent.RunAsync("What's this?", Array.Empty<LlmMessage>(), new[] { attachment }, new AskOptions(), new NullHost(), null, new HashSet<string>(), CancellationToken.None);
            var messages = blind.Chats[0]["messages"]!.AsArray();
            Assert.Null(messages.Last()!["images"]);
            Assert.Contains("ERROR 0x80070005", messages[0]!["content"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ADeclinedActionIsReportedToTheModel()
    {
        using var server = new FakeOllama
        {
            Script = (req, turn) => turn == 1
                ? new[] { FakeOllama.Chunk("", done: true, calls: FakeOllama.Call("act_now", new JsonObject())) }
                : new[] { FakeOllama.Chunk("OK, I won't.", done: true) },
        };
        var (agent, _, _, _) = Build(server);
        var host = new NullHost { Approve = _ => false };
        var platform = new ToolPlatform(new ActTool());
        await agent.RunAsync("Open it", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions { Computer = true }, host, platform, new HashSet<string>(), CancellationToken.None);
        Assert.Single(host.Asked);
        Assert.False(ActTool.Ran);
        var toolReply = server.Chats[1]["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "tool")!["content"]!.GetValue<string>();
        Assert.Contains("declined", toolReply);
    }

    private sealed class ActTool : AskTool
    {
        public static bool Ran;
        public override string Name => "act_now";
        public override string Description => "Acts";
        public override JsonObject Parameters => Schema();
        public override ToolAccess Access => ToolAccess.Act;
        public override string Describe(JsonElement args) => "Act now";
        public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct) { Ran = true; return Task.FromResult(new ToolResult("done", "done")); }
    }

    private sealed class ToolPlatform(params AskTool[] tools) : IAskPlatform
    {
        public IEnumerable<AskTool> ComputerTools() => tools;
        public Task<(string Text, int Pages, int Total)> ReadPdfAsync(byte[] pdf, int maxPages, CancellationToken ct) => Task.FromResult(("", 0, 0));
        public Task<string> ReadImageTextAsync(byte[] image, CancellationToken ct) => Task.FromResult("");
        public byte[] PrepareImage(byte[] image, int maxEdge = 1600) => image;
        public string? KnownFolder(string token) => null;
        public Task<IReadOnlyList<IndexedFile>> SearchIndexAsync(IReadOnlyList<string> terms, IReadOnlyList<string> scopes, bool content, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IndexedFile>>(Array.Empty<IndexedFile>());
    }

    [Fact]
    public void RaisesTheContextWindowOnlyWhenNeeded()
    {
        var s = new HubSettings();
        Assert.Null(AskAgent.ContextFor(new string('x', 8000), new List<LlmMessage> { new("user", "hi") }, s));
        Assert.Equal(16384, AskAgent.ContextFor(new string('x', 36000), new List<LlmMessage> { new("user", "hi") }, s));
    }
}

public class YouTubeChannelTests
{
    [Theory]
    [InlineData("https://www.youtube.com/@GamersNexus", null, "https://www.youtube.com/@GamersNexus")]
    [InlineData("www.youtube.com/@GamersNexus/videos", null, "https://www.youtube.com/@GamersNexus")]
    [InlineData("@GamersNexus", null, "https://www.youtube.com/@GamersNexus")]
    [InlineData("https://www.youtube.com/channel/UChIs72whgZI9w6d6FhwGGHA", "UChIs72whgZI9w6d6FhwGGHA", null)]
    [InlineData("UChIs72whgZI9w6d6FhwGGHA", "UChIs72whgZI9w6d6FhwGGHA", null)]
    [InlineData("https://www.youtube.com/c/LinusTechTips", null, "https://www.youtube.com/c/LinusTechTips")]
    [InlineData("https://youtu.be/Smwo493IAfw", null, "https://www.youtube.com/watch?v=Smwo493IAfw")]
    [InlineData("https://www.youtube.com/watch?v=Smwo493IAfw&t=10s", null, "https://www.youtube.com/watch?v=Smwo493IAfw")]
    [InlineData("https://example.com/@someone", null, null)]
    public void UnderstandsEveryWayToPasteAChannel(string input, string? id, string? page)
    {
        var (gotId, gotPage) = YouTubeChannels.Parse(input);
        Assert.Equal(id, gotId);
        Assert.Equal(page, gotPage);
    }

    [Fact]
    public void ReadsNameHandleAndAvatarFromTheChannelPage()
    {
        var html = """
            <html><head><link rel="canonical" href="https://www.youtube.com/channel/UChIs72whgZI9w6d6FhwGGHA">
            <meta property="og:title" content="Gamers Nexus"><meta property="og:type" content="profile">
            <meta property="og:image" content="https://yt3.googleusercontent.com/ytc/abc=s900-c-k-c0x00ffffff-no-rj">
            <meta itemprop="identifier" content="UChIs72whgZI9w6d6FhwGGHA"></head>
            <script>var d={"vanityChannelUrl":"http://www.youtube.com/@GamersNexus"};</script></html>
            """;
        var info = YouTubeChannels.ParsePage(html)!;
        Assert.Equal("UChIs72whgZI9w6d6FhwGGHA", info.ChannelId);
        Assert.Equal("Gamers Nexus", info.Name);
        Assert.Equal("@GamersNexus", info.Handle);
        Assert.Equal("https://yt3.googleusercontent.com/ytc/abc=s176-c-k-c0x00ffffff-no-rj", info.AvatarUrl);
    }

    [Fact]
    public void AVideoPageNamesItsChannel()
    {
        var html = """<meta property="og:type" content="video.other"><script>{"channelId":"UChIs72whgZI9w6d6FhwGGHA","ownerChannelName":"Gamers Nexus"}</script>""";
        var info = YouTubeChannels.ParsePage(html)!;
        Assert.Equal("UChIs72whgZI9w6d6FhwGGHA", info.ChannelId);
        Assert.Equal("Gamers Nexus", info.Name);
        Assert.Null(info.AvatarUrl);
    }
}

public class NewSourceAndSettingsTests
{
    [Fact]
    public void OlderSettingsGainGamersNexusOnce()
    {
        var s = new HubSettings { Version = 1 };
        s.News.Sources.RemoveAll(x => x.Id == "gamersnexus");
        s.Social.YouTubeChannels.Clear();
        Assert.True(SettingsStore.Migrate(s));
        Assert.Contains(s.News.Sources, x => x.Id == "gamersnexus" && x.Url == "https://gamersnexus.net/rss.xml" && x.Category == "tech");
        Assert.Contains(s.Social.YouTubeChannels, c => c.ChannelId == "UChIs72whgZI9w6d6FhwGGHA");
        // Removed later: stays removed.
        s.Social.YouTubeChannels.Clear();
        Assert.False(SettingsStore.Migrate(s));
        Assert.Empty(s.Social.YouTubeChannels);
    }

    [Fact]
    public void ArsTechnicaAndGamersNexusAreDefaultSources()
    {
        var sources = NewsSettings.DefaultSources();
        Assert.Contains(sources, x => x.Name == "Ars Technica" && x.Enabled);
        Assert.Contains(sources, x => x.Name == "Gamers Nexus" && x.Enabled);
        Assert.Contains(new SocialSettings().YouTubeChannels, c => c.Handle == "@GamersNexus");
    }

    [Fact]
    public void AskSettingsAreClamped()
    {
        var s = new HubSettings();
        s.Ask.ResearchPages = 99;
        s.Ask.SearchEngine = "bing";
        s.Ask.SearxngUrl = "javascript:alert(1)";
        SettingsStore.Validate(s);
        Assert.Equal(12, s.Ask.ResearchPages);
        Assert.Equal("duckduckgo", s.Ask.SearchEngine);
        Assert.Equal("", s.Ask.SearxngUrl);
    }

    [Theory]
    [InlineData(234.5, "EUR", "€234.50")]
    [InlineData(6512.3, "USD", "$6,512.30")]
    [InlineData(0.12345, "USD", "$0.1235")]
    [InlineData(1234.5, "GBp", "1,234.50p")]
    [InlineData(99.9, "CHF", "99.90 CHF")]
    [InlineData(5123.456, "", "5,123.46")]
    public void HoverPricesShowCents(double value, string currency, string expected) =>
        Assert.Equal(expected, Money.Exact(value, currency, System.Globalization.CultureInfo.GetCultureInfo("en-IE")));

    [Theory]
    [InlineData("research quantum batteries", "@research:quantum batteries")]
    [InlineData("search the web for Irish housing figures", "@web:Irish housing figures")]
    [InlineData("what's on my screen", "@screen")]
    [InlineData("find my tax documents", "@pc:find my tax documents")]
    public void ModeShortcutsOpenAsk(string input, string target)
    {
        var s = new HubSettings();
        var commands = new CommandInterpreter(null, () => s);
        var cmd = commands.TryFastPath(input);
        Assert.NotNull(cmd);
        Assert.Equal("ask", cmd!.Action);
        Assert.Equal(target, cmd.Target);
    }
}

public class CalendarLeakTests
{
    [Fact]
    public void ASearchCarryingCalendarDetailsNeedsAnOk()
    {
        var state = new HubState(null);
        state.SetEvents(new List<HubEvent>
        {
            new() { Id = "e1", Title = "Oncology appointment with Dr Keane", Location = "Beaumont Hospital", Start = DateTimeOffset.Now.AddDays(2), Kind = EventKind.Calendar },
            new() { Id = "e2", Title = "Budget 2027 announced", Start = DateTimeOffset.Now.AddDays(1), Kind = EventKind.Economic },
        });
        var run = new AskRun
        {
            State = state, Settings = new HubSettings(), Book = new SourceBook(), Options = new AskOptions { Web = true }, Host = new NullHost(),
            AllowedForChat = new HashSet<string>(), Question = "What's new with Starship?",
            PrivateTerms = AskAgent.CalendarTerms(state, DateTimeOffset.Now),
        };
        var web = new WebSearchTool();
        JsonElement Args(string q) => JsonDocument.Parse(JsonSerializer.Serialize(new { query = q })).RootElement;

        Assert.Null(AskAgent.ApprovalFor(web, Args("Starship flight 14 results"), "Search", run));
        var leak = AskAgent.ApprovalFor(web, Args("oncology Keane Beaumont appointment"), "Search", run);
        Assert.Equal("web-with-calendar", leak?.Tool);
        // Economic releases aren't personal, and words the user asked about themselves are fine.
        Assert.Null(AskAgent.ApprovalFor(web, Args("budget 2027 announced"), "Search", run));
        var asked = new AskRun
        {
            State = state, Settings = run.Settings, Book = run.Book, Options = run.Options, Host = run.Host, AllowedForChat = new HashSet<string>(),
            Question = "Where is Beaumont Hospital's oncology unit?", PrivateTerms = run.PrivateTerms,
        };
        Assert.Null(AskAgent.ApprovalFor(web, Args("Beaumont Hospital oncology unit"), "Search", asked));
    }
}
