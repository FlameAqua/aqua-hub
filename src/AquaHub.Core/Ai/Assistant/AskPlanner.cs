using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AquaHub.Core.Analysis;
using AquaHub.Core.Net;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// What Ask gathers for one question, decided before it answers: the searches a person would type (never the question
/// pasted whole), links to open, a site to search within, words a file's name probably contains, the kind of file and
/// what a picture shows. The model plans in one quick JSON call; rules fill the gaps and stand in when it can't.
/// </summary>
public sealed record AskPlan
{
    /// <summary>chat | feeds | web | page | site | files | screen | act</summary>
    public string Intent { get; init; } = "chat";
    public List<string> WebQueries { get; init; } = new();
    /// <summary>Pages to open: the links the user gave first.</summary>
    public List<string> Urls { get; init; } = new();
    /// <summary>A site to search within (x.com, reddit.com, the site of a link the user gave).</summary>
    public string Site { get; init; } = "";
    /// <summary>Words the file's name probably contains ("logo", "flame").</summary>
    public List<string> FileTerms { get; init; } = new();
    /// <summary>any | image | document | spreadsheet | presentation | pdf | video | audio | code | archive</summary>
    public string FileKind { get; init; } = "any";
    /// <summary>Folders the user named: DESKTOP, DOCUMENTS, PICTURES, DOWNLOADS, MUSIC, VIDEOS.</summary>
    public List<string> FileFolders { get; init; } = new();
    /// <summary>What a picture they're after shows ("a blue flame"), so Ask can look at candidates.</summary>
    public string Looks { get; init; } = "";
    public bool Screen { get; init; }
    public bool Recent { get; init; }
    /// <summary>direct | list | steps | report</summary>
    public string Format { get; init; } = "direct";
    /// <summary>A skill the user taught that fits this request (by name), with the values it needs.</summary>
    public string Skill { get; init; } = "";
    public Dictionary<string, string> SkillArgs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The model made this plan (false: rules only).</summary>
    public bool ByModel { get; init; }
    /// <summary>The message is about the files the user attached ("summarise the attached notes"): nothing to search for on the PC.</summary>
    public bool AboutAttachments { get; init; }

    /// <summary>One line for the activity list and logs.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (WebQueries.Count > 0) parts.Add("search " + string.Join(", ", WebQueries.Select(q => "“" + q + "”")));
        if (Urls.Count > 0) parts.Add("open " + string.Join(", ", Urls.Select(WebSearch.Host)));
        if (Site.Length > 0 && Urls.Count == 0) parts.Add("look on " + Site);
        if (FileTerms.Count > 0 || Intent == "files")
            parts.Add("look for " + (FileKind is "any" ? "files" : AskPlanner.KindLabel(FileKind)) +
                      (FileTerms.Count > 0 ? " named like " + string.Join(", ", FileTerms.Select(t => "“" + t + "”")) : "") +
                      (Looks.Length > 0 ? " showing " + Looks : ""));
        if (Screen) parts.Add("look at the screen");
        if (Skill.Length > 0) parts.Add("use the skill “" + Skill + "”");
        return parts.Count == 0 ? "answer from what Aqua already knows" : string.Join("; ", parts);
    }
}

/// <summary>What the planner is told about the situation besides the question.</summary>
public sealed record PlanContext
{
    public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Name, string Description)> Skills { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<string> Memories { get; init; } = Array.Empty<string>();
    /// <summary>Titles of the best matches in the user's feeds (so the planner knows what they already cover).</summary>
    public IReadOnlyList<string> FeedMatches { get; init; } = Array.Empty<string>();
    /// <summary>Names of the files the user attached to this message.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = Array.Empty<string>();
    public bool Research { get; init; }
    /// <summary>What Aqua's agents just refreshed for this message ("the news").</summary>
    public IReadOnlyList<string> Refreshed { get; init; } = Array.Empty<string>();
}

public static partial class AskPlanner
{
    public static readonly string[] Intents = { "chat", "feeds", "web", "page", "site", "files", "screen", "act" };
    public static readonly string[] Kinds = { "any", "image", "document", "spreadsheet", "presentation", "pdf", "video", "audio", "code", "archive" };
    public static readonly string[] Formats = { "direct", "list", "steps", "report" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string KindLabel(string kind) => kind switch
    {
        "image" => "pictures", "document" => "documents", "spreadsheet" => "spreadsheets", "presentation" => "presentations", "pdf" => "PDFs",
        "video" => "videos", "audio" => "audio files", "code" => "code files", "archive" => "archives", _ => "files",
    };

    internal static JsonObject Schema()
    {
        JsonObject Str() => new() { ["type"] = "string" };
        JsonObject Arr() => new() { ["type"] = "array", ["items"] = Str() };
        JsonObject Enum(string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["intent"] = Enum(Intents),
                ["web_queries"] = Arr(),
                ["urls"] = Arr(),
                ["site"] = Str(),
                ["file_terms"] = Arr(),
                ["file_kind"] = Enum(Kinds),
                ["file_folders"] = Arr(),
                ["looks"] = Str(),
                ["screen"] = new JsonObject { ["type"] = "boolean" },
                ["recent"] = new JsonObject { ["type"] = "boolean" },
                ["format"] = Enum(Formats),
                ["skill"] = Str(),
                ["skill_args"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["name"] = Str(), ["value"] = Str() },
                        ["required"] = new JsonArray("name", "value"),
                    },
                },
            },
            ["required"] = new JsonArray("intent", "web_queries", "file_terms", "format"),
        };
    }

    /// <summary>The planning request: fast (no reasoning), JSON only.</summary>
    public static LlmRequest Request(string question, IReadOnlyList<LlmMessage> history, AskOptions o, PlanContext ctx, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("Today is ").Append(now.ToString("dddd d MMMM yyyy", Inv)).Append(".\n");
        var turns = history.Where(m => m.Role is "user" or "assistant").TakeLast(6).ToList();
        if (turns.Count > 0)
        {
            sb.Append("Earlier in this chat (oldest first):\n");
            foreach (var m in turns) sb.Append(m.Role == "user" ? "User: " : "Aqua: ").Append(HtmlText.Truncate(m.Content.ReplaceLineEndings(" "), 280)).Append('\n');
        }
        sb.Append("Web: ").Append(o.UsesWeb ? "on" : "off").Append(". Use my PC: ").Append(o.Computer ? "on" : "off");
        if ((o.Computer || o.Folders.Count > 0) && ctx.Folders.Count > 0) sb.Append(" (folders Aqua may search: ").Append(string.Join(", ", ctx.Folders.Take(8))).Append(')');
        if (o.Folders.Count > 0) sb.Append(". The user attached a folder to this chat; questions are usually about its files");
        sb.Append(".\n");
        if (ctx.Memories.Count > 0) sb.Append("The user asked Aqua to remember: ").Append(string.Join(" | ", ctx.Memories.Take(12).Select(m => HtmlText.Truncate(m, 140)))).Append('\n');
        if (ctx.Skills.Count > 0)
            sb.Append("Skills the user taught Aqua (use one only if it clearly fits): ")
              .Append(string.Join(" | ", ctx.Skills.Take(20).Select(s => s.Name + ": " + HtmlText.Truncate(s.Description, 120)))).Append('\n');
        if (ctx.Refreshed.Count > 0)
            sb.Append("Aqua's agents just refreshed ").Append(string.Join(" and ", ctx.Refreshed)).Append(" in the user's feeds for this message: ")
              .Append("asking for that needs no web search (intent feeds), unless the message also asks about something else.\n");
        if (ctx.FeedMatches.Count > 0) sb.Append("The user's own news feeds already have: ").Append(string.Join(" | ", ctx.FeedMatches.Take(3).Select(t => HtmlText.Truncate(t, 90)))).Append('\n');
        if (ctx.Attachments.Count > 0)
            sb.Append("Attached to this message: ").Append(string.Join(", ", ctx.Attachments.Take(6).Select(a => HtmlText.Truncate(a, 80))))
              .Append(". Aqua already has their contents: a message about them (\"the attached…\", \"this file\", \"this screenshot\") needs no file search — intent chat, no file_terms — unless it asks to find other files on the PC.\n");
        sb.Append("\nThe user's latest message: \"").Append(HtmlText.Truncate(question, 1200)).Append("\"\n\n");
        sb.Append("""
            Plan how Aqua should handle the latest message. Fill in:
            - intent: chat (conversation, writing or rewording, maths, opinions, or something already in this chat — no lookup), feeds (the user's news, weather, agenda, markets), web (look something up online: with Web on, any factual question about people, things, specs, prices, places or events — the user switched Web on to have facts checked, so search even if you think you know), page (read links the user gave), site (search within a website or platform), files (find or read the user's files — with Use my PC on, also questions about their own records that only their files would answer: services, bills, warranties, bookings, policies, contracts), screen (what is on their screen), act (open, start, type into or control something on the PC).
            - web_queries: 1 to
            """);
        sb.Append(ctx.Research ? "4" : "3");
        sb.Append("""
             short keyword queries a person would type into a search engine — never the whole message. Keep names, handles, products, places and the year when it matters; resolve "it", "he", "that" from the chat. A question with several parts gets a query for each part; a comparison, one for each side. Empty if nothing needs looking up.
            - urls: links the user wrote, in this message or earlier in the chat (add https:// if missing). Never invent addresses: web_queries find pages.
            - site: the domain to search within when the user names a site or platform (Twitter or X = x.com, Reddit = reddit.com, YouTube = youtube.com, or the site of their link).
            - file_terms: 1 to 4 words the file's NAME probably contains (e.g. logo, invoice, boiler). Not verbs, not 'file', 'photo' or 'picture'. Include words the user remembers from the name.
            - file_kind: image, document, spreadsheet, presentation, pdf, video, audio, code, archive or any.
            - file_folders: folders the user mentioned (Desktop, Documents, Pictures, Downloads, Music, Videos).
            - looks: for a picture, what it shows in a few words (e.g. "a blue flame"); else empty.
            - screen: true only when the question is about what is on their screen.
            - recent: true for news or anything that changes (prices, scores, releases).
            - format: direct (a fact, a link, a file or a short answer), list, steps or report.
            - skill and skill_args: the fitting skill's name and the values it needs, else empty.

            Examples:
            "Find me my logo photo of a blue flame" -> {"intent":"files","web_queries":[],"file_terms":["logo","flame"],"file_kind":"image","looks":"a blue flame","format":"direct"}
            "when is my boiler service due?" (Use my PC on) -> {"intent":"files","web_queries":[],"file_terms":["boiler","service"],"file_kind":"any","format":"direct"}
            "find the Re:Zero leaker Ice on twitter and link me" -> {"intent":"web","web_queries":["Re:Zero leaker Ice twitter","rezero Ice leaks x.com"],"site":"x.com","file_terms":[],"format":"direct"}
            "look through https://example.com/blog for anything about pricing" -> {"intent":"site","urls":["https://example.com/blog"],"site":"example.com","web_queries":["site:example.com pricing"],"file_terms":[],"format":"list"}
            "is it going to rain later?" -> {"intent":"feeds","web_queries":[],"file_terms":[],"format":"direct"}
            """);
        return new LlmRequest
        {
            Purpose = "ask-plan",
            Priority = LlmPriority.Interactive,
            System = "You plan the steps for Aqua, a private assistant on the user's PC. Reply with JSON only. " + Prompts.UntrustedNotice,
            Messages = { new LlmMessage("user", sb.ToString()) },
            Schema = Schema(),
            Temperature = 0.1,
            MaxTokens = 360,
            Think = false,
        };
    }

    // ───────────────────────────── Rules ─────────────────────────────

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"(?<![@\w.-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?:com|org|net|io|ie|co\.uk|uk|dev|app|ai|gov|edu|eu|de|fr|tv|me|info|news|gg|xyz|ly|to|so|sh|fm|us|ca|au|nz|jp|kr|cn|in|es|it|nl|se|no|fi|dk|pl|pt|ch|at|be)(?:/[^\s<>""'\)\]\}]*)?(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex BareDomainRx();

    [GeneratedRegex(@"\b(?:on|from|in|at|via|search|through)\s+(twitter|x|reddit|youtube|github|wikipedia|instagram|tiktok|linkedin|bluesky|facebook|twitch|hacker ?news|stack ?overflow|imdb|steam|spotify|amazon|ebay|mastodon|threads)\b|\b(twitter|reddit|youtube|github|instagram|tiktok|linkedin|bluesky|facebook|twitch|steam)\s+(?:account|profile|handle|page|channel|thread|post|user|repo|link)", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformRx();

    [GeneratedRegex(@"\b((?:my|the|this|both|all)(?: of)?(?: my)?(?: [\w-]+)? (?:screens?|monitors?|displays?)|on screen|this window|this page|this error|this dialog|this message|what i'?m (looking at|seeing)|what'?s on (my|the) screen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenRx();

    [GeneratedRegex(@"\b(files?|documents?|docs?|pdfs?|notes?|invoices?|receipts?|bills?|contracts?|letters?|reports?|spreadsheets?|photos?|pictures?|pics?|images?|screenshots?|logos?|icons?|wallpapers?|videos?|recordings?|songs?|music|downloads?|folders?|cv|resume|statements?|polic(?:y|ies)|warrant(?:y|ies)|manuals?|tickets?|bookings?|payslips?|tax|budget|presentations?|slides?|decks?|spreadsheet|zip|archive)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileWordsRx();

    [GeneratedRegex(@"^\s*(?:(?:find|search for|look for|locate|where(?:'s| is| are| did i (?:put|save))|open|show me|get me|pull up|dig up)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FindVerbRx();

    /// <summary>Questions about the user's own records ("when is my boiler service due?"): with Use my PC on, their files may say.</summary>
    [GeneratedRegex(@"\bmy\s+(?:[\p{L}-]+\s+){0,2}(?:service|servicing|warranty|guarantee|policy|insurance|lease|tenancy|contract|mortgage|loan|renewal|licen[cs]e|passport|registration|nct|mot|appointment|booking|reservation|subscription|bill|invoice|payslip|pension|receipt|order|delivery|serial number|model number|plan|quote|estimate)s?\b", RegexOptions.IgnoreCase)]
    private static partial Regex PersonalRecordRx();

    [GeneratedRegex(@"\b(my|mine|our|i|i've|i'd|we)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PossessiveRx();

    [GeneratedRegex(@"\b(open|launch|start|run|close|quit|type|click|press|play|pause|skip|mute|turn (?:up|down)|switch to|focus)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActRx();

    [GeneratedRegex(@"\b(weather|rain|temperature|forecast|umbrella|agenda|calendar|schedule|meetings?|watchlist|portfolio|my stocks|markets? today|brief|pulse|headlines|news today|today'?s news)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FeedsRx();

    [GeneratedRegex(@"\b(?:photo|picture|image|pic|logo|screenshot|drawing|icon|wallpaper|render|artwork)s?\s+(?:of|showing|with)\s+(?<what>.+?)(?=\s+(?:in|on|from|that|which|i)\b|[.?!,]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LooksRx();

    [GeneratedRegex(@"^\s*(?:(?:hey|hi|ok|okay)\s+)?(?:aqua[,:]?\s+)?(?:(?:please|pls|can you|could you|would you|will you|i want you to|i need you to|help me|go and|go)\s+)*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadFillerRx();

    /// <summary>"the newest articles about X" → "X": what a page lists, not what the search is about.</summary>
    [GeneratedRegex(@"^(?:the\s+|any\s+|some\s+)?(?:(?:newest|latest|most recent|recent|new|top|best|last)\s+)?(?:articles?|news|posts?|stories|story|updates?|information|info|details|coverage|reports?)\s+(?:about|on|regarding|for|of)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadNounRx();

    [GeneratedRegex(@"^(?:and\s+|then\s+)?(?:find(?: me)?|search(?: the web| online| the internet| google)?(?: for)?|search through|look up|look for|look through|go through|check|google|tell me(?: more)?(?: about:?)?|give me|show me|get me|i'?m looking for|i want to know)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadVerbRx();

    [GeneratedRegex(@"\s*(?:,|\band\b)?\s*(?:(?:and\s+)?(?:send|give|share|post|paste)?\s*(?:me\s+)?(?:the\s+|a\s+)?link(?:\s+(?:me|to))?(?:\s+(?:it|them|that|this|him|her|there))?|and link (?:me )?(?:to )?(?:it|them|that|him|her)?|link me(?: to)?(?: it| them| that)?|for me|please|thanks|thank you|in (?:one|a|two|three|a few) (?:sentence|sentences|words|lines|paragraphs?)|briefly|in detail|keep it short|in simple terms|and summari[sz]e(?: it)?|and explain(?: it)?|,?\s*with (?:the |their |some )?links?|on the (?:internet|web)|online)\s*[.!?]*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailFillerRx();

    private static readonly HashSet<string> NotNameWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "find", "search", "look", "locate", "show", "open", "get", "give", "where", "wheres", "file", "files", "folder", "folders", "document", "documents",
        "doc", "docs", "photo", "photos", "picture", "pictures", "pic", "pics", "image", "images", "called", "named", "name", "something", "like", "similar",
        "some", "somewhere", "computer", "pc", "laptop", "drive", "disk", "saved", "save", "stored", "kept", "put", "desktop", "downloads", "download",
        "music", "videos", "please", "thanks", "think", "maybe", "probably", "exactly", "kind", "sort", "type", "copy", "one", "ones", "need", "want",
        "help", "mine", "the", "a", "an", "of", "my", "me", "i", "it", "is", "in", "on", "from", "for", "with", "that", "this", "which", "and", "or",
        "can", "you", "could", "would", "should", "do", "did", "have", "has", "had", "be", "been", "was", "were", "am", "are", "there", "here", "aqua",
        "hey", "pull", "dig", "up", "pdf", "pdfs", "png", "jpg", "jpeg", "gif", "mp4", "mp3", "docx", "xlsx", "pptx", "txt", "csv", "somewhere", "around",
        "any", "all", "latest", "newest", "recent", "old", "older", "new", "last", "file's", "whats", "what's", "just", "into", "onto", "about",
        "attached", "attachment", "attachments", "summarise", "summarize", "summary", "bullets", "bullet", "explain", "describe", "translate",
        "rewrite", "tell", "read",
    };

    private static readonly (string Word, string Kind)[] KindWords =
    {
        ("photo", "image"), ("picture", "image"), ("pic", "image"), ("image", "image"), ("screenshot", "image"), ("wallpaper", "image"), ("png", "image"),
        ("jpg", "image"), ("jpeg", "image"), ("gif", "image"), ("drawing", "image"), ("artwork", "image"), ("icon", "image"),
        ("pdf", "pdf"), ("spreadsheet", "spreadsheet"), ("excel", "spreadsheet"), ("xlsx", "spreadsheet"), ("csv", "spreadsheet"),
        ("presentation", "presentation"), ("slides", "presentation"), ("slide", "presentation"), ("powerpoint", "presentation"), ("deck", "presentation"),
        ("word document", "document"), ("docx", "document"), ("letter", "document"), ("cv", "document"), ("resume", "document"),
        ("video", "video"), ("recording", "video"), ("mp4", "video"), ("clip", "video"), ("song", "audio"), ("mp3", "audio"), ("audio", "audio"),
        ("zip", "archive"), ("archive", "archive"), ("script", "code"), ("code", "code"),
    };

    private static readonly (string Word, string Token)[] FolderWords =
    {
        ("desktop", "DESKTOP"), ("documents", "DOCUMENTS"), ("my documents", "DOCUMENTS"), ("pictures", "PICTURES"), ("photos folder", "PICTURES"),
        ("downloads", "DOWNLOADS"), ("download folder", "DOWNLOADS"), ("music", "MUSIC"), ("videos", "VIDEOS"),
    };

    private static readonly Dictionary<string, string> PlatformSites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twitter"] = "x.com", ["x"] = "x.com", ["reddit"] = "reddit.com", ["youtube"] = "youtube.com", ["github"] = "github.com",
        ["wikipedia"] = "wikipedia.org", ["instagram"] = "instagram.com", ["tiktok"] = "tiktok.com", ["linkedin"] = "linkedin.com",
        ["bluesky"] = "bsky.app", ["facebook"] = "facebook.com", ["twitch"] = "twitch.tv", ["hackernews"] = "news.ycombinator.com",
        ["hacker news"] = "news.ycombinator.com", ["stackoverflow"] = "stackoverflow.com", ["stack overflow"] = "stackoverflow.com",
        ["imdb"] = "imdb.com", ["steam"] = "store.steampowered.com", ["spotify"] = "open.spotify.com", ["amazon"] = "amazon.com",
        ["ebay"] = "ebay.com", ["threads"] = "threads.net",
    };

    /// <summary>Links in a message (with or without https://), trailing punctuation removed.</summary>
    public static List<string> Links(string text)
    {
        var list = new List<string>();
        void Add(string raw)
        {
            var u = raw.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', '’', '”');
            if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.') &&
                !list.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
                list.Add(uri.AbsoluteUri);
        }
        var rest = text;
        foreach (Match m in UrlRx().Matches(text)) { Add(m.Value); rest = rest.Replace(m.Value, " "); }
        foreach (Match m in BareDomainRx().Matches(rest))
            if (!m.Value.Contains('@')) Add(m.Value);
        return list.Take(5).ToList();
    }

    /// <summary>
    /// Web addresses in text read off the screen, in reading order — top of the screen first, where a browser's address
    /// bar is. (<see cref="Links"/> puts addresses written with "https://" first, but browsers hide it in the address bar,
    /// so a full link in the page below would win.)
    /// </summary>
    public static List<string> ScreenLinks(string screenText)
    {
        var found = new List<(int At, string Raw)>();
        foreach (Match m in UrlRx().Matches(screenText)) found.Add((m.Index, m.Value));
        foreach (Match m in BareDomainRx().Matches(screenText))
            if (!m.Value.Contains('@') && !found.Any(f => m.Index >= f.At && m.Index < f.At + f.Raw.Length)) found.Add((m.Index, m.Value));
        var list = new List<string>();
        foreach (var (_, raw) in found.OrderBy(f => f.At))
        {
            var u = raw.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', '’', '”');
            if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.') &&
                !list.Contains(uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
                list.Add(uri.AbsoluteUri);
        }
        return list.Take(5).ToList();
    }

    /// <summary>"https://www.Example.com/blog" → "example.com" (a bare domain for site: searches).</summary>
    public static string SiteName(string raw)
    {
        var site = raw.Trim().ToLowerInvariant();
        site = Regex.Replace(site, @"^[a-z]+://", "");
        site = Regex.Replace(site, @"^www\.", "");
        return Regex.Replace(site, @"[/?#:].*$", "");
    }

    /// <summary>A search query from a message: the request and its instructions stripped, the subject kept.</summary>
    public static string WebQuery(string text)
    {
        var q = UrlRx().Replace(text, " ").Trim();
        var mark = q.IndexOf('?');
        if (mark > 10 && mark < q.Length - 1) q = q[..(mark + 1)];
        for (var i = 0; i < 3; i++)
        {
            var before = q;
            q = LeadFillerRx().Replace(q, "");
            q = LeadVerbRx().Replace(q, "");
            q = LeadNounRx().Replace(q, "");
            q = TrailFillerRx().Replace(q, "");
            q = q.Trim().TrimEnd('?', '.', '!').Trim();
            if (q == before) break;
        }
        q = Regex.Replace(q, @"\s+", " ");
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 14) q = string.Join(' ', words.Take(14));
        return q.Length >= 2 ? q : text.Trim();
    }

    /// <summary>Words a file's name probably contains: the message minus requests, kinds, places and filler.</summary>
    public static List<string> NameTerms(string text)
    {
        var cleaned = UrlRx().Replace(text, " ");
        var terms = new List<string>();
        foreach (var raw in Regex.Split(cleaned, @"[^\p{L}\p{N}_]+"))
        {
            foreach (var part in raw.Split('_', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.ToLowerInvariant();
                if (t.Length < 2 || NotNameWords.Contains(t) || TextTools.IsStopword(t) && t is not ("video" or "image")) continue;
                if (t.All(char.IsDigit) && t.Length < 4) continue;
                if (!terms.Contains(t)) terms.Add(t);
            }
        }
        return terms.Take(5).ToList();
    }

    /// <summary>The kind of file a message asks for ("photo" → image).</summary>
    public static string KindOf(string text)
    {
        var lower = " " + text.ToLowerInvariant() + " ";
        foreach (var (word, kind) in KindWords)
            if (Regex.IsMatch(lower, @"[^\p{L}]" + Regex.Escape(word) + @"s?[^\p{L}]")) return kind;
        return "any";
    }

    public static List<string> FoldersIn(string text)
    {
        var lower = text.ToLowerInvariant();
        var list = new List<string>();
        foreach (var (word, token) in FolderWords)
            if (Regex.IsMatch(lower, @"\b" + Regex.Escape(word) + @"\b") && !list.Contains(token)) list.Add(token);
        return list;
    }

    /// <summary>The site a message is about ("on twitter" → x.com, a link → its host).</summary>
    public static string SiteOf(string text, IReadOnlyList<string> links)
    {
        if (PlatformRx().Match(text) is { Success: true } m)
        {
            var name = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToLowerInvariant().Replace(" ", "");
            if (PlatformSites.TryGetValue(name, out var site) && !(name == "x" && !Regex.IsMatch(text, @"\bon x\b", RegexOptions.IgnoreCase))) return site;
        }
        return links.Count > 0 && Uri.TryCreate(links[0], UriKind.Absolute, out var u) ? u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host : "";
    }

    /// <summary>
    /// With files attached, whether the message is about them ("summarise the attached notes", "what's wrong in this
    /// screenshot?") rather than asking to find other files on the PC ("find my other invoices from them").
    /// </summary>
    public static bool AboutTheAttachments(string question, int attachments) =>
        attachments > 0 && (AttachRefRx().IsMatch(question) || !ElsewhereRx().IsMatch(question) && !FindVerbRx().IsMatch(question));

    /// <summary>The plan the rules make on their own (used when the model can't plan, and merged into its plan).</summary>
    public static AskPlan Heuristic(string question, IReadOnlyList<LlmMessage> history, AskOptions o, int attachments = 0)
    {
        var links = Links(question);
        var site = SiteOf(question, links);
        var screen = ScreenRx().IsMatch(question) && attachments == 0;
        var aboutAttachments = AboutTheAttachments(question, attachments);
        // With a folder attached to the chat, questions are about its files unless they plainly look something up.
        var fileish = !aboutAttachments && (FileWordsRx().IsMatch(question) || FindVerbRx().IsMatch(question) && PossessiveRx().IsMatch(question) ||
                                            o.Computer && PersonalRecordRx().IsMatch(question)) ||
                      o.Folders.Count > 0 && !OnlineRx().IsMatch(question);
        var feeds = FeedsRx().IsMatch(question);
        var siteSearch = links.Count > 0 && Regex.IsMatch(question, @"\b(search|look|find|anything|check|go) (through|on|in|for)\b|\bsearch\b", RegexOptions.IgnoreCase);
        var intent = screen ? "screen"
            : links.Count > 0 ? siteSearch ? "site" : "page"
            : aboutAttachments ? LookUpRx().IsMatch(question) && o.UsesWeb ? "web" : "chat"
            : fileish && (o.Computer || o.Folders.Count > 0) && !feeds && site.Length == 0 ? "files"
            : feeds ? "feeds"
            : ActRx().IsMatch(question) && o.Computer && Regex.IsMatch(question, @"^\s*(?:please\s+)?(open|launch|start|close|quit|type|click|press|play|pause|mute|switch)", RegexOptions.IgnoreCase) ? "act"
            : o.UsesWeb ? "web" : "chat";
        var looks = LooksRx().Match(question) is { Success: true } lm ? Regex.Replace(lm.Groups["what"].Value.Trim(), @"\s+", " ") : "";
        var query = WebQuery(question);
        var queries = new List<string>();
        if (intent is "web" or "site" && query.Length > 1)
        {
            queries.Add(HtmlText.Truncate(query, 120));
            if (site.Length > 0 && links.Count == 0 && !query.Contains(site, StringComparison.OrdinalIgnoreCase)) queries.Add(HtmlText.Truncate(query + " site:" + site, 140));
        }
        return new AskPlan
        {
            Intent = intent,
            WebQueries = queries,
            Urls = links,
            Site = site,
            FileTerms = intent == "files" || fileish ? NameTerms(question) : new(),
            FileKind = KindOf(question),
            FileFolders = FoldersIn(question),
            Looks = HtmlText.Truncate(looks, 120),
            Screen = screen,
            Recent = WebSearch.LooksTimely(question),
            // A summary of a page you link (a chapter, a report) follows the whole page: a report, not a sentence or two.
            Format = links.Count > 0 && SummaryRx().IsMatch(question) ? "report"
                   : ListRx().IsMatch(question) ? "list"
                   : Regex.IsMatch(question, @"\bhow (do|can|to|should) i\b|\bsteps?\b", RegexOptions.IgnoreCase) ? "steps" : "direct",
            AboutAttachments = aboutAttachments,
        };
    }

    [GeneratedRegex(@"\b(summari[sz]e|summary|recap|tl;?dr|overview|what happens|what happened|walk me through)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryRx();

    /// <summary>Asks for several things ("the newest articles … with links", "some options"): answered as a list.</summary>
    [GeneratedRegex(@"\b(list|options|examples|ideas|compare|pros and cons|articles|links|stories|posts|videos|results|headlines|papers|products|apps|tools|books|games|episodes|chapters|alternatives|recommendations)\b|\bwith (?:the |their )?links?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ListRx();

    [GeneratedRegex(@"\b(attach(?:ed|ment|ments)?|enclosed|(?:this|these|the above|that) (?:files?|documents?|docs?|pdfs?|images?|pictures?|photos?|screenshots?|notes?|sheets?|spreadsheets?|texts?|code|logs?|reports?|slides?|decks?|letters?|cvs?|resumes?|invoices?|receipts?))\b", RegexOptions.IgnoreCase)]
    private static partial Regex AttachRefRx();

    [GeneratedRegex(@"\b(?:on my (?:pc|computer|laptop|drive|desktop)|in my (?:documents|downloads|pictures|desktop|files|folders?)|my other|other (?:files|documents|pictures|photos|invoices|versions?)|similar (?:files|ones)|search my|look through my)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ElsewhereRx();

    [GeneratedRegex(@"\b(search|look up|google|online|on the web|internet|latest|news|is (?:it|this|that) true|fact.?check)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LookUpRx();

    /// <summary>A question that plainly wants the internet rather than the files in front of it.</summary>
    [GeneratedRegex(@"\b(look (?:it |this |that )?up|google|online|on the web|internet|news|fact.?check)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnlineRx();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’.+-]*")]
    private static partial Regex WordRx();

    /// <summary>
    /// The names in a text, as search terms: capitalised words that don't start a sentence ("Dublin", "Starship"), words
    /// with capitals inside or digits ("NIRSpec", "JWST", "RTX4090"). Used to tell a related item from a coincidence, and
    /// to spot private details (from what Aqua remembers) in a web query.
    /// </summary>
    public static HashSet<string> NamesIn(string text)
    {
        var (strong, weak) = NamesByKind(text);
        strong.UnionWith(weak);
        return strong;
    }

    /// <summary>
    /// <see cref="NamesIn"/>, split: strong names have capitals inside or digits (NIRSpec, JWST, SpaceX, RTX4090); weak
    /// ones are ordinary capitalised words (Dublin, Space, Telescope).
    /// </summary>
    public static (HashSet<string> Strong, HashSet<string> Weak) NamesByKind(string text)
    {
        var strong = new HashSet<string>(StringComparer.Ordinal);
        var weak = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in WordRx().Matches(text))
        {
            var w = Regex.Replace(m.Value, @"(?:['’]s|['’.+-])+$", "");
            if (w.Length < 2) continue;
            var before = text[..m.Index].TrimEnd();
            var starts = before.Length == 0 || before[^1] is '.' or '!' or '?' or ':' or '\n' or '"' or '“' or '(' or '-' or '•' or '*';
            var distinctive = w.Skip(1).Any(char.IsUpper) || w.Any(char.IsDigit) && w.Any(char.IsLetter);
            if (!distinctive && !(char.IsUpper(w[0]) && !starts)) continue;
            foreach (var t in TextTools.Signature(w)) (distinctive ? strong : weak).Add(t);
        }
        strong.RemoveWhere(HubSearch.AskingWords.Contains);
        weak.RemoveWhere(t => HubSearch.AskingWords.Contains(t) || strong.Contains(t));
        return (strong, weak);
    }

    /// <summary>
    /// The model's plan, keeping only sensible values, merged with the rules' plan: links the user typed are always
    /// opened, a query that is just the question pasted whole is replaced, and unknown kinds, folders or skills are dropped.
    /// </summary>
    public static AskPlan Merge(JsonElement model, AskPlan rules, string question, AskOptions o, IReadOnlyCollection<string>? skillNames = null,
        IReadOnlyList<LlmMessage>? history = null)
    {
        if (model.ValueKind != JsonValueKind.Object) return rules;
        string Str(string name) => model.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
        List<string> List(string name) => model.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToList()
            : new();
        bool Bool(string name) => model.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        var intent = Str("intent").ToLowerInvariant();
        if (!Intents.Contains(intent)) intent = rules.Intent;
        // A message about the attached files needs no file search, whatever the model thought.
        if (rules.AboutAttachments && intent is "files" or "screen") intent = rules.Intent;
        // Links the user typed win over the model's idea of the intent.
        if (rules.Urls.Count > 0 && intent is "chat" or "feeds" or "web") intent = rules.Intent;
        if (rules.Screen) intent = "screen";

        var questionTerms = TextTools.Signature(question);
        var queries = new List<string>();
        foreach (var raw in List("web_queries"))
        {
            // The model sometimes leaves the request in ("look through … and tell me …"): keep only the subject.
            var q = WebQuery(Regex.Replace(raw.Trim('"', '\''), @"\s+", " "));
            if (q.Length < 2 || q.Length > 160) continue;
            // A "query" that is the whole message again: search for its subject instead.
            var terms = TextTools.Signature(q);
            if (questionTerms.Count > 8 && terms.Count >= questionTerms.Count * 0.9 && q.Length > 70) q = WebQuery(question);
            if (!queries.Contains(q, StringComparer.OrdinalIgnoreCase)) queries.Add(HtmlText.Truncate(q, 140));
        }
        if (queries.Count == 0 && intent is "web" or "site") queries.AddRange(rules.WebQueries);

        // Links are opened only when the user gave them (now or earlier in the chat): an address the model made up — or
        // one from an earlier answer, which may have come from a web page — is never fetched on its own; searches find pages.
        var urls = new List<string>(rules.Urls);
        var known = new HashSet<string>(rules.Urls.Select(u => u.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
        foreach (var m in history ?? Array.Empty<LlmMessage>())
            if (m.Role == "user") known.UnionWith(Links(m.Content).Select(u => u.TrimEnd('/')));
        foreach (var raw in List("urls"))
            foreach (var u in Links(raw))
                if (known.Contains(u.TrimEnd('/')) && !urls.Any(x => x.TrimEnd('/').Equals(u.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) urls.Add(u);

        // A link you gave names its site better than the model's guess ("ars-technica.com").
        var site = rules.Urls.Count > 0 && rules.Site.Length > 0 ? rules.Site : SiteName(Str("site"));
        if (!Regex.IsMatch(site, @"^[a-z0-9-]+(\.[a-z0-9-]+)+$")) site = rules.Site;

        var fileTerms = new List<string>();
        foreach (var raw in rules.AboutAttachments ? new List<string>() : List("file_terms"))
            foreach (var t in Regex.Split(raw.ToLowerInvariant(), @"[^\p{L}\p{N}_]+"))
                if (t.Length >= 2 && !NotNameWords.Contains(t) && !fileTerms.Contains(t)) fileTerms.Add(t);
        foreach (var t in rules.FileTerms)
            if (fileTerms.Count < 5 && !fileTerms.Contains(t)) fileTerms.Add(t);

        var kind = Str("file_kind").ToLowerInvariant();
        if (!Kinds.Contains(kind) || kind == "any" && rules.FileKind != "any") kind = rules.FileKind;

        var folders = new List<string>(rules.FileFolders);
        foreach (var f in List("file_folders"))
            foreach (var token in FoldersIn(f).Append(f.Trim('%').ToUpperInvariant()).Where(t => FolderWords.Any(w => w.Token == t)))
                if (!folders.Contains(token)) folders.Add(token);

        var format = Str("format").ToLowerInvariant();
        if (!Formats.Contains(format)) format = rules.Format;
        // "The newest articles … with links" is a list, even when the model calls it a direct answer.
        if (rules.Format is "list" or "report" && format == "direct") format = rules.Format;

        var skill = Str("skill");
        if (skillNames is null || !skillNames.Contains(skill, StringComparer.OrdinalIgnoreCase)) skill = "";
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (skill.Length > 0 && model.TryGetProperty("skill_args", out var sa) && sa.ValueKind == JsonValueKind.Array)
            foreach (var a in sa.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String &&
                    a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } key)
                    args[key.Trim()] = HtmlText.Truncate(v.GetString() ?? "", 300);

        // Searching a site: one of the searches is restricted to it.
        if (intent == "site" && site.Length > 0 && !queries.Any(q => q.Contains("site:", StringComparison.OrdinalIgnoreCase)))
            queries.Insert(0, HtmlText.Truncate((queries.FirstOrDefault() ?? WebQuery(question)) + " site:" + site, 140));

        var looks = Str("looks");
        return new AskPlan
        {
            Intent = intent,
            WebQueries = queries.Take(o.Research ? 4 : 3).ToList(),
            Urls = urls.Take(4).ToList(),
            Site = site,
            FileTerms = fileTerms.Take(5).ToList(),
            FileKind = kind,
            FileFolders = folders,
            Looks = HtmlText.Truncate(looks.Length > 0 ? looks : rules.Looks, 120),
            Screen = rules.Screen || Bool("screen") && intent == "screen",
            Recent = rules.Recent || Bool("recent"),
            Format = format,
            Skill = skill,
            SkillArgs = args,
            ByModel = true,
            AboutAttachments = rules.AboutAttachments,
        };
    }
}
