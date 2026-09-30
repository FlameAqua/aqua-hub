using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Analysis;
using AquaHub.Core.Data;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// What a tool touches, which decides when the user is asked first: Hub (what your agents collected) and Web (public
/// pages) run freely; Private (your files, screen, clipboard) runs only with "Use my PC" and marks the answer as having
/// seen private data; Act (opening, launching, media) asks first unless you allowed it for this chat.
/// </summary>
public enum ToolAccess { Hub, Web, Private, Act }

public sealed record ToolResult(string Text, string Summary, bool Ok = true, byte[]? Image = null, string? Url = null)
{
    public static ToolResult Fail(string why) => new("Error: " + why, why, Ok: false);
}

/// <summary>A capability the model can call while answering.</summary>
public abstract class AskTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract JsonObject Parameters { get; }
    public virtual ToolAccess Access => ToolAccess.Hub;
    public virtual string Icon => "search";
    /// <summary>Private tools that still ask first (screenshots, clipboard) when the matching setting says so.</summary>
    public virtual bool NeedsApproval(AskSettings s) => Access == ToolAccess.Act && s.ConfirmActions;
    /// <summary>
    /// Actions that type, click or press keys in other apps: once the answer has read anything that isn't the user's own
    /// words (web pages, feeds, files, the screen, app windows, the clipboard, attachments), each one asks, every time —
    /// whatever the settings or earlier OKs say — so nothing Aqua read can steer them.
    /// </summary>
    public virtual bool ConfirmAfterUntrusted => false;

    /// <summary>Each use needs its own OK: no "Allow for this chat" (typing that presses Enter, key presses).</summary>
    public virtual bool OneAtATime(JsonElement args) => false;

    /// <summary>This call opens a web link, so the rules for reaching the internet apply to it too.</summary>
    public virtual bool OpensLink(JsonElement args) => false;

    /// <summary>What the step list and approval card show, e.g. "Search the web for “x”".</summary>
    public abstract string Describe(JsonElement args);
    public abstract Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct);

    public LlmTool Spec => new(Name, Description, Parameters);

    protected static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, req) in props)
        {
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (req) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    protected static string Arg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : ""
            : "";

    protected static bool Flag(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() is "true" or "yes"));

    protected static string Quote(string s) => "“" + HtmlText.Truncate(s, 80) + "”";
}

/// <summary>Everything a tool may use while answering one question.</summary>
public sealed class AskRun
{
    public required HubState State { get; init; }
    public HubDatabase? Db { get; init; }
    public required HubSettings Settings { get; init; }
    public required SourceBook Book { get; init; }
    public required AskOptions Options { get; init; }
    public required IAskHost Host { get; init; }
    public IAskPlatform? Platform { get; init; }
    public WebSearch? Web { get; init; }
    public WebReader? Reader { get; init; }
    public LocalFiles? Files { get; init; }
    public bool Vision { get; init; }
    public string Question { get; init; } = "";
    /// <summary>Set once the answer has looked at files, the screen, the clipboard or attachments.</summary>
    public bool SawPrivate { get; set; }
    /// <summary>Tools (and "web-after-private") the user allowed for the rest of this chat.</summary>
    public required HashSet<string> AllowedForChat { get; init; }
    public int PagesRead;
    /// <summary>Distinctive words from the user's calendar: a web query or link carrying them needs an OK.</summary>
    public HashSet<string> PrivateTerms { get; init; } = new(StringComparer.Ordinal);
    /// <summary>How well the user's feeds match the question (best hub-search score; ≥ 1.4 means they cover it).</summary>
    public double HubCoverage { get; set; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
    /// <summary>What the planner decided to gather.</summary>
    public AskPlan Plan { get; set; } = new();
    /// <summary>Titles of the best matches in the user's feeds (the planner sees them).</summary>
    public List<string> FeedMatches { get; set; } = new();
    /// <summary>Set once web pages or search results were read.</summary>
    public bool SawWeb { get; set; }
    /// <summary>
    /// Set once the model has seen text that isn't the user's own — web pages and results, feed items, articles, files,
    /// the screen, app windows, the clipboard or attachments: typing, clicking and key presses then always ask.
    /// </summary>
    public bool SawUntrusted { get; set; }
    /// <summary>Skills the user taught (enabled ones) and the one this answer follows.</summary>
    public IReadOnlyList<AskSkill> Skills { get; init; } = Array.Empty<AskSkill>();
    public AskSkill? Skill { get; set; }
    /// <summary>Things the user asked Aqua to remember.</summary>
    public IReadOnlyList<string> Memories { get; init; } = Array.Empty<string>();
    /// <summary>Links Aqua saw while answering (on pages it read, in results), address → title: answers may only link these on those sites.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> SeenLinks { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Scratch space for tools within one answer (e.g. the controls read from a window).</summary>
    public Dictionary<string, object> Bag { get; } = new(StringComparer.Ordinal);
}

// ───────────────────────────── Hub ─────────────────────────────

public sealed class SearchHubTool : AskTool
{
    public override string Name => "search_hub";
    public override string Description =>
        "Search what the user's agents already collected today and recently: news stories (with summaries and every outlet), social posts, " +
        "the local social pulse, prediction markets, the user's calendar and market quotes. Fast and private — try this first for anything in the news.";
    public override JsonObject Parameters => Schema(
        ("query", "string", "Keywords to look for, e.g. \"Starship orbit engine\"", true),
        ("kind", "string", "Optional: any, news, social, markets, predictions or agenda", false));
    public override string Icon => "layers";
    public override string Describe(JsonElement args) => "Searched your feeds for " + Quote(Arg(args, "query"));

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var query = Arg(args, "query");
        if (query.Length == 0) return Task.FromResult(ToolResult.Fail("query is required"));
        var kind = Arg(args, "kind").ToLowerInvariant();
        var hits = HubSearch.Search(run.State, run.Db, query, kind.Length > 0 ? kind : "any", 8, run.Now);
        if (hits.Count == 0)
            return Task.FromResult(new ToolResult($"Nothing in the user's feeds matches \"{query}\".", "nothing found"));
        return Task.FromResult(new ToolResult(HubSearch.Format(hits, run.Book, run.Now), Plural.Of(hits.Count, "match", "matches")));
    }
}

public sealed class GetStoryTool : AskTool
{
    public override string Name => "get_story";
    public override string Description =>
        "Full coverage of one news story by its story_id (from search_hub or the context): the summary, key points and every outlet's headline, snippet and link.";
    public override JsonObject Parameters => Schema(("story_id", "string", "The story_id, e.g. s1a2b3c", true));
    public override string Icon => "news";
    public override string Describe(JsonElement args) => "Opened the story’s coverage";

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var story = HubSearch.FindStory(run.State, Arg(args, "story_id"));
        if (story is null) return Task.FromResult(ToolResult.Fail("no story with that id — use search_hub to find one"));
        return Task.FromResult(new ToolResult(HubSearch.Story(story, run.Book, run.Now), Plural.Of(story.SourceCount, "outlet")));
    }
}

// ───────────────────────────── Web ─────────────────────────────

public sealed class WebSearchTool : AskTool
{
    public override string Name => "web_search";
    public override string Description =>
        "Search the internet for current or general information that isn't in the user's feeds. Use a short keyword query (names, handles, " +
        "products, the year), not the user's sentence. Returns titles, links and snippets; read the most relevant pages with read_webpage before relying on details.";
    public override JsonObject Parameters => Schema(
        ("query", "string", "Short keyword query, e.g. \"Re:Zero leaker Ice twitter\"", true),
        ("site", "string", "Optional: only search this site, e.g. x.com, reddit.com or arstechnica.com", false),
        ("recent", "boolean", "true for news and anything that happened recently", false));
    public override ToolAccess Access => ToolAccess.Web;
    public override string Icon => "globe";
    // In full: the approval card must show exactly what would be sent.
    public override string Describe(JsonElement args) => "Search the web for “" + HtmlText.Truncate(Arg(args, "query"), 400) + "”" + (Arg(args, "site") is { Length: > 0 } site ? " on " + site : "");

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var query = Arg(args, "query");
        if (query.Length == 0) return ToolResult.Fail("query is required");
        if (run.Web is null) return ToolResult.Fail("web search is off");
        var site = AskPlanner.SiteName(Arg(args, "site"));
        if (site.Length > 3 && site.Contains('.') && !query.Contains("site:", StringComparison.OrdinalIgnoreCase)) query += " site:" + site;
        List<WebResult> results;
        try { results = await run.Web.SearchAsync(query, 8, Flag(args, "recent"), ct).ConfigureAwait(false); }
        catch (WebSearchException ex) { return ToolResult.Fail(ex.Message); }
        if (results.Count == 0) return new ToolResult($"No web results for \"{query}\".", "no results");
        return new ToolResult(FormatResults(results, run.Book), Plural.Of(results.Count, "result"));
    }

    public static string FormatResults(IEnumerable<WebResult> results, SourceBook book)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            // Only the title and snippet are seen here ("result"); reading the page makes it a full source ("web").
            var n = book.Add(r.Title, r.Site, r.Url, "result", r.Snippet);
            sb.Append('[').Append(n).Append("] ").Append(r.Title).Append(" — ").Append(r.Site);
            if (r.Published is { } p) sb.Append(", ").Append(TimeText.Dated(p));
            sb.Append('\n');
            if (r.Snippet.Length > 0) sb.Append("    ").Append(r.Snippet).Append('\n');
            sb.Append("    url: ").Append(r.Url).Append('\n');
        }
        return sb.ToString();
    }
}

public sealed class ReadWebpageTool : AskTool
{
    public const int ExcerptChars = 5000;
    /// <summary>A long page's parts (read_webpage part=1, 2, …).</summary>
    public const int PartChars = 7000;
    public override string Name => "read_webpage";
    public override string Description =>
        "Open any public web page or link and read it: articles, documentation, Wikipedia, a site or profile the user mentions, or a link they gave. " +
        "Returns the main text and the page's most relevant links, which you can open in turn to search through a site.";
    public override JsonObject Parameters => Schema(
        ("url", "string", "The page's full http(s) address", true),
        ("focus", "string", "Optional: what you're looking for, to pick the relevant parts of a long page", false),
        ("part", "integer", "Optional: read a long page in order, one part at a time (1, 2, …); the result says how many parts there are", false));
    public override ToolAccess Access => ToolAccess.Web;
    public override string Icon => "book";
    // The whole address (not just the site): the approval card must show exactly what would be requested.
    public override string Describe(JsonElement args) => "Read " + HtmlText.Truncate(Arg(args, "url"), 400);

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var url = Arg(args, "url");
        if (run.Reader is null) return ToolResult.Fail("reading web pages is off");
        if (!WebReader.IsReadableUrl(url, out _)) return ToolResult.Fail("only public http(s) pages can be read");
        if (url.Contains("news.google.com/rss/articles", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail("Google News links can't be read directly — search the web for the headline to find the outlet's own page");
        var page = await ReadAsync(run, url, ct).ConfigureAwait(false);
        if (page is null) return ToolResult.Fail("the page couldn't be read");
        // A long page read in order, part by part — or the parts most relevant to the question.
        var all = page.Value.Page.Text;
        var parts = all.Length > PartChars ? TextChunks.Split(all, PartChars) : new List<string> { all };
        var part = int.TryParse(Arg(args, "part"), out var p0) ? Math.Clamp(p0, 1, parts.Count) : 0;
        var excerpt = part > 0 ? parts[part - 1] : page.Value.Page.Excerpt(Arg(args, "focus") is { Length: > 0 } f ? f : run.Question, ExcerptChars);
        if (excerpt.Length < 80) return ToolResult.Fail("the page has no readable text (it may need a browser, a login or a subscription)");
        var more = parts.Count <= 1 ? ""
            : part > 0 ? $"\n(Part {part} of {parts.Count}.{(part < parts.Count ? $" Call read_webpage with part={part + 1} for the next." : " That's the end of the page.")})"
            : $"\n(The page is {all.Length:N0} characters; that was the part most relevant to the question. To read all of it in order, call read_webpage with part=1 … {parts.Count}.)";
        var n = run.Book.Add(page.Value.Page.Title.Length > 0 ? page.Value.Page.Title : url, page.Value.Page.Site, page.Value.Page.Url, "web", excerpt);
        Interlocked.Increment(ref run.PagesRead);
        var head = $"[{n}] {page.Value.Page.Title} — {page.Value.Page.Site}{(page.Value.Page.Published is { } p ? ", published " + TimeText.Dated(p, run.Now) : "")}{page.Value.Note}\n";
        var links = page.Value.Page.RelevantLinks(Arg(args, "focus") is { Length: > 0 } lf ? lf : run.Question, 10);
        foreach (var l in links) run.SeenLinks.TryAdd(l.Url, l.Text);
        var linkText = links.Count == 0 ? "" : "\nLinks on this page (most relevant first):\n" + string.Join("\n", links.Select(l => "- " + HtmlText.Truncate(l.Text, 90) + " — " + l.Url));
        return new ToolResult(head + excerpt + more + linkText, $"{Plural.Of(WordCount(excerpt), "word")}" + (part > 0 ? $" · part {part} of {parts.Count}" : ""), Url: page.Value.Page.Url);
    }

    /// <summary>
    /// Reads a page (PDFs through the platform's OCR) within <paramref name="timeout"/>. Returns null when it can't be
    /// read or takes too long — one slow page never stops the answer; only <paramref name="ct"/> (the user's Stop) does.
    /// </summary>
    public static async Task<(WebPage Page, string Note)?> ReadAsync(AskRun run, string url, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            var page = await run.Reader!.ReadAsync(url, limit.Token).ConfigureAwait(false);
            if (page.Pdf is { } pdf)
            {
                if (run.Platform is null) return null;
                var (text, pages, total) = await run.Platform.ReadPdfAsync(pdf, run.Settings.Ask.MaxPdfPages, limit.Token).ConfigureAwait(false);
                return (page with { Text = text, Pdf = null }, pages < total ? $" (PDF, first {pages} of {total} pages)" : " (PDF)");
            }
            return (page, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.Debug("ask", $"read {HttpFetcher.Redact(url)} failed: {ex.Message}");
            return null;
        }
    }

    internal static int WordCount(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

// ───────────────────────────── Files ─────────────────────────────

public sealed class SearchFilesTool : AskTool
{
    public override string Name => "search_files";
    public override string Description =>
        "Find files on the user's PC (in the folders they allowed) by words in the file NAME — any of the words, files matching more of them first — " +
        "or text inside documents. Use short keywords (e.g. \"logo flame\"), not a sentence. If nothing matches, try other words, a kind or a folder.";
    public override JsonObject Parameters => Schema(
        ("query", "string", "Keywords from the file's name or content, e.g. \"boiler service\"", true),
        ("kind", "string", "Optional: any, image, document, spreadsheet, presentation, pdf, video, audio, code or archive", false),
        ("folder", "string", "Optional: Desktop, Documents, Pictures, Downloads, Music, Videos or a full folder path to look in first", false));
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "folder";
    public override string Describe(JsonElement args) => "Searched your files for " + Quote(Arg(args, "query")) + (Arg(args, "folder") is { Length: > 0 } f ? " in " + f : "");

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        if (run.Files is null) return ToolResult.Fail("file access is off");
        var query = Arg(args, "query");
        var kind = Arg(args, "kind").ToLowerInvariant();
        if (!AskPlanner.Kinds.Contains(kind)) kind = "any";
        var folder = Arg(args, "folder");
        var prefer = folder.Length == 0 ? new List<string>()
            : ListFolderTool.Resolve(folder, run) is { } resolved ? new List<string> { resolved } : new List<string>();
        var terms = System.Text.RegularExpressions.Regex.Split(query, @"[^\p{L}\p{N}_]+").Where(t => t.Length > 1 && !TextTools.IsStopword(t.ToLowerInvariant())).ToList();
        IReadOnlyList<IndexedFile> indexed = Array.Empty<IndexedFile>();
        if (run.Platform is not null && terms.Count > 0)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                indexed = await run.Platform.SearchIndexAsync(LocalFiles.Normalise(terms), run.Files.Roots, kind is "any" or "document" or "pdf", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { Log.Debug("ask", "Windows search index: " + ex.Message); }
        }
        var hits = await Task.Run(() => run.Files.Find(new FileQuery { Terms = terms, Kind = kind, Prefer = prefer, Max = 15 }, indexed, ct), ct).ConfigureAwait(false);
        run.SawPrivate = true;
        if (hits.Count == 0)
            return new ToolResult($"No files matching \"{query}\"{(kind != "any" ? " (" + kind + ")" : "")} in: {string.Join(", ", run.Files.Roots)}. " +
                                  "Try fewer or different words, another kind, or list_folder on a likely folder.", "nothing found");
        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            var n = run.Book.Add(h.Name, "Your files", h.Path, "file");
            sb.Append('[').Append(n).Append("] ").Append(h.Path).Append(" · ").Append(LocalFiles.Size(h.Size)).Append(" · modified ")
              .Append(h.Modified.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)).Append('\n');
            if (h.Snippet is { Length: > 0 } snip) sb.Append("    …").Append(snip).Append("…\n");
        }
        return new ToolResult(sb.ToString(), Plural.Of(hits.Count, "file"));
    }
}

public sealed class ReadFileTool : AskTool
{
    public override string Name => "read_file";
    public override string Description =>
        "Open a file on the user's PC by its full path: reads documents (text, Markdown, CSV, code, Word, Excel, PowerPoint, OpenDocument, PDF) " +
        "and shows you pictures — you can see images, so use this to check what a picture shows.";
    public override JsonObject Parameters => Schema(
        ("path", "string", "The file's full path, e.g. from search_files", true),
        ("focus", "string", "Optional: what to look for in a long document", false));
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "file";
    public override string Describe(JsonElement args) => "Read " + Path.GetFileName(Arg(args, "path"));

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        if (run.Files is null) return ToolResult.Fail("file access is off");
        if (!run.Files.CanRead(Arg(args, "path"), out var path, out var reason)) return ToolResult.Fail(reason);
        if (!File.Exists(path)) return ToolResult.Fail("that file doesn't exist");
        run.SawPrivate = true;
        var (text, note, image) = await FileText.ReadAsync(path, run, ct).ConfigureAwait(false);
        if (image is not null && run.Vision)
        {
            var n0 = run.Book.Add(Path.GetFileName(path), "Your files", path, "file");
            return new ToolResult($"[{n0}] {path} — the image is attached for you to look at." + (text.Length > 0 ? "\nText in it (OCR): " + text : ""),
                "image", Image: image);
        }
        if (text.Trim().Length == 0) return ToolResult.Fail("no readable text in that file");
        var page = new WebPage { Url = path, Text = text.Replace("\r", "") };
        var excerpt = page.Excerpt(Arg(args, "focus") is { Length: > 0 } f ? f : run.Question, 6000);
        var n = run.Book.Add(Path.GetFileName(path), "Your files", path, "file");
        return new ToolResult($"[{n}] {path}{note}\n{excerpt}", Plural.Of(ReadWebpageTool.WordCount(excerpt), "word"));
    }
}

public sealed class ListFolderTool : AskTool
{
    public override string Name => "list_folder";
    public override string Description =>
        "List what's in a folder the user allowed (newest first): Desktop, Documents, Pictures, Downloads, Music, Videos, or a full path. kind narrows it (e.g. image).";
    public override JsonObject Parameters => Schema(
        ("path", "string", "Desktop, Documents, Pictures, Downloads, Music, Videos or a full folder path", true),
        ("kind", "string", "Optional: any, image, document, spreadsheet, pdf, video, audio…", false));
    public override ToolAccess Access => ToolAccess.Private;
    public override string Icon => "folder";
    public override string Describe(JsonElement args) => "Looked in " + Arg(args, "path");

    /// <summary>A folder name ("Pictures", "my documents") or path, as a full path (null when it can't be resolved).</summary>
    public static string? Resolve(string raw, AskRun run)
    {
        var token = raw.Trim().Trim('%').ToLowerInvariant() switch
        {
            "downloads" or "download" => "%DOWNLOADS%", "documents" or "my documents" or "docs" => "%DOCUMENTS%", "desktop" => "%DESKTOP%",
            "pictures" or "photos" or "my pictures" or "images" => "%PICTURES%", "music" => "%MUSIC%", "videos" or "my videos" => "%VIDEOS%",
            _ => null,
        };
        if (token is null) return raw.Trim();
        var known = LocalFiles.ExpandFolders(new[] { token }, run.Platform is { } p ? p.KnownFolder : null).FirstOrDefault();
        var roots = run.Files?.Roots ?? Array.Empty<string>();
        if (known is not null && (roots.Count == 0 || roots.Any(r => LocalFiles.IsUnder(known, r)))) return known;
        // The Windows folder isn't allowed: a folder with that name inside the allowed ones ("D:\Stuff\Pictures").
        var name = AskAgent.FolderLabel(token);
        return roots.SelectMany(r => LocalFiles.FindFolders(r, name, 3, 1)).FirstOrDefault() ?? known;
    }

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        if (run.Files is null) return Task.FromResult(ToolResult.Fail("file access is off"));
        var raw = Arg(args, "path");
        var resolved = Resolve(raw, run) ?? raw;
        if (!run.Files.CanRead(resolved, out var full, out var reason)) return Task.FromResult(ToolResult.Fail(reason));
        if (!Directory.Exists(full)) return Task.FromResult(ToolResult.Fail("that folder doesn't exist"));
        run.SawPrivate = true;
        var kind = Arg(args, "kind").ToLowerInvariant();
        var entries = run.Files.List(full, 60, AskPlanner.Kinds.Contains(kind) ? kind : "any");
        var sb = new StringBuilder().Append(full).Append(":\n");
        foreach (var e in entries)
            sb.Append("- ").Append(e.Name).Append(" · ").Append(LocalFiles.Size(e.Size)).Append(" · ").Append(e.Modified.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        return Task.FromResult(new ToolResult(sb.ToString(), Plural.Of(entries.Count, "item")));
    }
}

/// <summary>Reads any supported file: documents here, PDFs and images through the platform (OCR), images kept for vision models.</summary>
public static class FileText
{
    public static async Task<(string Text, string Note, byte[]? Image)> ReadAsync(string path, AskRun run, CancellationToken ct)
    {
        if (Documents.IsPdf(path))
        {
            if (run.Platform is null) return ("", "", null);
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var (text, pages, total) = await run.Platform.ReadPdfAsync(bytes, run.Settings.Ask.MaxPdfPages, ct).ConfigureAwait(false);
            return (text, pages < total ? $" (first {pages} of {total} pages)" : $" ({Plural.Of(total, "page")})", null);
        }
        if (Documents.IsImage(path))
        {
            if (run.Platform is null) return ("", "", null);
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var prepared = run.Platform.PrepareImage(bytes);
            var ocr = await run.Platform.ReadImageTextAsync(prepared, ct).ConfigureAwait(false);
            return (ocr, " (image; text read with OCR)", prepared);
        }
        return (await Task.Run(() => Documents.ReadText(path), ct).ConfigureAwait(false), "", null);
    }
}
