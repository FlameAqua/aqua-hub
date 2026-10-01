namespace AquaHub.Core.Ai.Assistant;

/// <summary>How Ask works on one question — the switches under the composer.</summary>
public sealed record AskOptions
{
    /// <summary>May search the web and read public pages.</summary>
    public bool Web { get; init; }
    /// <summary>Reason step by step before answering (slower).</summary>
    public bool Think { get; init; }
    /// <summary>Plan searches, read several sources and write a cited report (implies Web).</summary>
    public bool Research { get; init; }
    /// <summary>May look through your files and screen and act on the PC (actions need your OK).</summary>
    public bool Computer { get; init; }
    /// <summary>The story a "Tell me more" question is about.</summary>
    public string? StoryId { get; init; }
    /// <summary>Use this taught skill (Workbench › Try it), whatever the planner thinks.</summary>
    public string? SkillId { get; init; }
    /// <summary>Answer the message even if it reads like "remember …" / "forget …" (the chat's "Answer it instead").</summary>
    public bool AsQuestion { get; init; }
    /// <summary>The model picked in Ask (null: the one set in Settings › AI). Planning, reading and writing all use it.</summary>
    public string? Model { get; init; }

    public bool UsesWeb => Web || Research;
}

public enum AttachmentKind { Image, Document }

/// <summary>A file, screenshot or pasted image the user gave Ask.</summary>
public sealed record AskAttachment
{
    public required string Name { get; init; }
    public AttachmentKind Kind { get; init; }
    public string? Path { get; init; }
    /// <summary>Extracted text (documents), or OCR text for images when the model can't see.</summary>
    public string Text { get; init; } = "";
    /// <summary>PNG or JPEG bytes for images (already scaled down).</summary>
    public byte[]? Image { get; init; }
    /// <summary>E.g. "first 15 of 40 pages".</summary>
    public string Note { get; init; } = "";
}

/// <summary>
/// An action Ask wants to take that needs the user's OK. <paramref name="AllowForChat"/> is false for actions that must be
/// approved every single time (typing that presses Enter, key presses, anything after reading text that isn't yours).
/// </summary>
public sealed record ToolApproval(string Tool, string Title, string Detail, string Icon = "shield", bool AllowForChat = true);

/// <summary>The chat UI, as Ask sees it while answering.</summary>
public interface IAskHost
{
    /// <summary>A short status for the footer ("Searching the web…"); empty clears it.</summary>
    void Status(string text);
    /// <summary>Adds a step to the answer's activity list; returns its id.</summary>
    int StepStarted(string icon, string text);
    void StepFinished(int id, string text, bool ok = true, string? url = null);
    void Thinking(string delta);
    void Text(string delta);
    /// <summary>Discards streamed text (the model wrote a preamble, then decided to use a tool).</summary>
    void ResetText();
    /// <summary>Shows Allow / Deny in the chat and waits for the user.</summary>
    Task<bool> ApproveAsync(ToolApproval request, CancellationToken ct);
}

/// <summary>What the Windows app adds: OCR for PDFs and images, known folders and the PC tools (screen, open, media…).</summary>
public interface IAskPlatform
{
    /// <summary>Tools that act on or look at this PC (offered only with "Use my PC").</summary>
    IEnumerable<AskTool> ComputerTools();
    /// <summary>Text of a PDF's first <paramref name="maxPages"/> pages (OCR on this PC). Returns (text, pages read, total pages).</summary>
    Task<(string Text, int Pages, int Total)> ReadPdfAsync(byte[] pdf, int maxPages, CancellationToken ct);
    /// <summary>Text in an image (OCR on this PC); empty when there is none.</summary>
    Task<string> ReadImageTextAsync(byte[] image, CancellationToken ct);
    /// <summary>Scales an image down for the model (long edge ≤ <paramref name="maxEdge"/>), re-encoded as PNG or JPEG.</summary>
    byte[] PrepareImage(byte[] image, int maxEdge = 1600);
    /// <summary>Resolves %DOCUMENTS%-style tokens to real folders (null = use the defaults).</summary>
    string? KnownFolder(string token);
    /// <summary>
    /// Files the Windows search index knows whose name contains one of <paramref name="terms"/> (and, with
    /// <paramref name="content"/>, documents whose text does), inside <paramref name="scopes"/>. Empty when the index is off.
    /// </summary>
    Task<IReadOnlyList<IndexedFile>> SearchIndexAsync(IReadOnlyList<string> terms, IReadOnlyList<string> scopes, bool content, CancellationToken ct);
}

/// <summary>
/// Something Aqua read while answering — a page, an article, a file — kept with the chat (an excerpt), so a later
/// question in the same chat can use it again without fetching it again.
/// </summary>
public sealed record ChatSource(string Title, string Source, string? Url, string Kind, string Text);

/// <summary>What happened while answering, for the footer and the conversation history.</summary>
public sealed record AskResult
{
    public string Text { get; init; } = "";
    public List<Citation> Citations { get; init; } = new();
    public string Model { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
    public int ToolCalls { get; init; }
    public bool UsedWeb { get; init; }
    public bool UsedPc { get; init; }
    public string Thinking { get; init; } = "";
    /// <summary>research | think | "" — how the answer was made.</summary>
    public string Mode { get; init; } = "";
    /// <summary>Tokens the last model call used (prompt and reply) and the context window it had.</summary>
    public int PromptTokens { get; init; }
    public int OutputTokens { get; init; }
    public int ContextTokens { get; init; }
    /// <summary>Older messages left out so the conversation fits the context window.</summary>
    public int TrimmedMessages { get; init; }
    /// <summary>The taught skill used, if any.</summary>
    public string Skill { get; init; } = "";
    /// <summary>The reasoning ran long or in circles and was cut short (the answer was then written directly).</summary>
    public bool ReasoningCut { get; init; }
    /// <summary>What this answer read (cited first), for the chat to keep.</summary>
    public List<ChatSource> Sources { get; init; } = new();
}

/// <summary>
/// Numbers every source Ask shows the model — hub items, story coverage, web pages, files — so answers cite them as
/// [n] and the chat can turn those into links. A source seen twice keeps its first number.
/// </summary>
public sealed partial class SourceBook
{
    private readonly List<Citation> _list = new();
    private readonly Dictionary<int, System.Text.StringBuilder> _evidence = new();
    private readonly object _gate = new();

    /// <summary>
    /// Numbers a source; <paramref name="text"/> is what it told the model (a snippet, summary or page text), kept to check
    /// citations against. <paramref name="kind"/>: news, social, hub, web (a page Aqua read), result (only a search
    /// result's title and snippet were seen) or file.
    /// </summary>
    public int Add(string title, string source, string? url, string kind = "news", string? text = null)
    {
        lock (_gate)
        {
            var i = url is { Length: > 0 }
                ? _list.FindIndex(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase))
                : _list.FindIndex(c => c.Url is null && c.Title == title && c.Source == source);
            int n;
            if (i >= 0)
            {
                n = _list[i].Number;
                // A search result whose page was then read is a full source.
                if (_list[i].Kind == "result" && kind == "web") _list[i] = _list[i] with { Kind = "web" };
            }
            else
            {
                n = _list.Count + 1;
                _list.Add(new Citation(n, title, source, url, kind));
                Note(n, title);
            }
            if (text is { Length: > 0 }) Note(n, text);
            return n;
        }
    }

    /// <summary>Adds to what source <paramref name="n"/> told the model.</summary>
    public void Note(int n, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (!_evidence.TryGetValue(n, out var sb)) _evidence[n] = sb = new System.Text.StringBuilder();
            if (sb.Length < 24000) sb.Append(' ').Append(text.Length > 12000 ? text[..12000] : text);
        }
    }

    public IReadOnlyList<Citation> All { get { lock (_gate) return _list.ToList(); } }

    /// <summary>What source <paramref name="n"/> told the model (empty when nothing was recorded).</summary>
    public string Evidence(int n)
    {
        lock (_gate) return _evidence.TryGetValue(n, out var sb) ? sb.ToString().Trim() : "";
    }

    /// <summary>
    /// What to keep with the chat: the sources the answer cites, then pages, articles and files it read — up to five,
    /// each an excerpt of what the model saw. Search snippets and feed headlines alone aren't worth keeping, and the
    /// screen and the clipboard never are (a moment's view, and whatever happened to be copied).
    /// </summary>
    public List<ChatSource> Keep(string answer, int max = 5, int chars = 6000)
    {
        var cited = CitedIn(answer).Select(c => c.Number).ToHashSet();
        List<Citation> all;
        lock (_gate) all = _list.ToList();
        return all.Select(c => (Citation: c, Text: Evidence(c.Number)))
            .Where(x => x.Citation.Kind is not ("screen" or "clipboard"))
            .Where(x => cited.Contains(x.Citation.Number) ? x.Text.Length > 0 : x.Citation.Kind is "web" or "news" or "file" && x.Text.Length >= 600)
            .OrderByDescending(x => cited.Contains(x.Citation.Number)).ThenBy(x => x.Citation.Number)
            .Take(max)
            .Select(x => new ChatSource(x.Citation.Title, x.Citation.Source, x.Citation.Url, x.Citation.Kind, x.Text.Length > chars ? x.Text[..chars] : x.Text))
            .ToList();
    }
    public int Count { get { lock (_gate) return _list.Count; } }

    /// <summary>
    /// The sources an answer actually cites ([3], [3][5] or [3, 5]), links to by writing out their address, or names
    /// (a file found on the PC, so the chat can offer to open it).
    /// </summary>
    public List<Citation> CitedIn(string text)
    {
        var numbers = CitationText.Numbers(text);
        lock (_gate) return _list.Where(c => numbers.Contains(c.Number) || Mentions(text, c)).ToList();
    }

    private static bool Mentions(string text, Citation c) => c.Url switch
    {
        { Length: > 12 } u when u.StartsWith("http", StringComparison.OrdinalIgnoreCase) => text.Contains(u.TrimEnd('/'), StringComparison.OrdinalIgnoreCase),
        { Length: > 3 } path when c.Kind == "file" => Path.GetFileName(path) is { Length: >= 5 } name && name.Contains('.') && text.Contains(name, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>Only web and feed sources are checked: the user's files, screen and attachments may be pictures.</summary>
    private static readonly HashSet<string> Checked = new(StringComparer.Ordinal) { "news", "social", "hub", "web", "result" };

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9“""(*])")]
    private static partial System.Text.RegularExpressions.Regex SentenceBreak();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial System.Text.RegularExpressions.Regex Marker();

    [System.Text.RegularExpressions.GeneratedRegex(@"https?://\S+|\]\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex Links();

    /// <summary>
    /// Removes [n] markers whose sentence shares (almost) nothing with what source n said: small models sometimes cite
    /// the nearest number rather than the source of the claim. Sources with no recorded text are left alone.
    /// </summary>
    public string Verify(string text, out int removed)
    {
        removed = 0;
        if (!text.Contains('[')) return text;
        Dictionary<int, HashSet<string>> evidence;
        HashSet<int> numbers;
        lock (_gate)
        {
            evidence = _list.Where(c => Checked.Contains(c.Kind) && _evidence.ContainsKey(c.Number))
                .ToDictionary(c => c.Number, c => Analysis.TextTools.Signature(_evidence[c.Number].ToString()));
            numbers = _list.Select(c => c.Number).ToHashSet();
        }
        // A number that was never a source ("[4]" for the fourth chunk of a page) goes too.
        if (evidence.Count == 0 && CitationText.Numbers(text).All(numbers.Contains)) return text;

        var dropped = 0;
        var lines = text.Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            if (!lines[l].Contains('[')) continue;
            var sentences = SentenceBreak().Split(lines[l]);
            var changed = false;
            for (var s = 0; s < sentences.Length; s++)
            {
                var sentence = sentences[s];
                if (!Marker().IsMatch(sentence)) continue;
                var terms = Analysis.TextTools.Signature(Marker().Replace(Links().Replace(sentence, " "), " "));
                terms.RemoveWhere(t => t.Length < 2);
                var cleaned = Marker().Replace(sentence, m =>
                {
                    var number = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!numbers.Contains(number)) { dropped++; return ""; }
                    if (terms.Count == 0 || !evidence.TryGetValue(number, out var source)) return m.Value;
                    // Enough of the claim must come from the source: two or more of its words and a quarter of them (a long
                    // sentence sharing "space" and "telescope" with an unrelated article isn't supported by it).
                    var shared = terms.Count(source.Contains);
                    if (shared >= 5 || shared >= 2 && shared * 4 >= terms.Count || shared == 1 && terms.Count <= 3) return m.Value;
                    dropped++;
                    return "";
                });
                if (cleaned == sentence) continue;
                sentences[s] = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[ \t]+(?=[.,;:!?)]|$)", "");
                changed = true;
            }
            if (changed) lines[l] = string.Join(' ', sentences);
        }
        removed = dropped;
        return dropped == 0 ? text : string.Join('\n', lines);
    }
}
