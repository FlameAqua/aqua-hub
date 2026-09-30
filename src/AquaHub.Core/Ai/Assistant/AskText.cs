using System.Text;
using System.Text.RegularExpressions;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// Small local models sometimes think out loud in the answer itself ("The user is asking… Let me check…") or write a
/// "Thinking Process:" block without a separate reasoning channel. This splits that narration off (it goes to the
/// Reasoning panel) and keeps the answer. Conservative: only whole sentences that clearly talk about the task are moved.
/// </summary>
public static partial class AnswerText
{
    [GeneratedRegex(@"^\s*(?:\*\*)?\s*(?:thinking(?: process)?|reasoning|analysis|internal monologue)\s*:?\s*(?:\*\*)?\s*:?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ThinkingHeader();

    [GeneratedRegex(@"^\s*(?:#+\s*|\*\*)?\s*(?:final answer|answer|response|reply|draft(?:ed)? (?:answer|response))\s*(?:\*\*)?\s*:\s*(?:\*\*)?\s*", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AnswerMarker();

    [GeneratedRegex(@"^(?:the user(?:'s)? (?:is asking|asks|asked|wants|said|says|mentioned|is looking|would like|has|needs|request)|user (?:is asking|wants|asked)|let me (?:check|search|look|try|find|see|list|read|open|verify|think|start|re-?read|examine|use|call|analy[sz]e|go|first|now)|let's (?:check|search|look|try|find|see|start)|i (?:should|need to|will|'ll|must|can|am going to|'m going to) (?:check|search|look|try|find|list|read|open|call|use|verify|answer|respond|provide|tell|mention|cite|describe|inform|start|first|now|search)|(?:according to|based on|looking at|checking) the (?:context|tool results?|search results?|data|provided)|(?:okay|ok|alright|hmm|wait|actually|so)[,.!]\s|(?:first|now|next|then|so),? (?:i|let me)\b|my (?:previous|last) (?:answer|turn|response)|since i can'?t (?:see|view|actually))", RegexOptions.IgnoreCase)]
    private static partial Regex Narration();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z\*\[""“(])")]
    private static partial Regex SentenceBreak();

    private static readonly (string Latex, string Symbol)[] Symbols =
    {
        (@"\rightarrow", "→"), (@"\Rightarrow", "⇒"), (@"\leftarrow", "←"), (@"\to", "→"), (@"\approx", "≈"), (@"\times", "×"),
        (@"\pm", "±"), (@"\leq", "≤"), (@"\geq", "≥"), (@"\le", "≤"), (@"\ge", "≥"), (@"\neq", "≠"), (@"\sim", "~"), (@"\cdot", "·"),
        (@"^\circ", "°"), (@"^{\circ}", "°"), (@"\circ", "°"), (@"\%", "%"), (@"\$", "$"),
    };

    [GeneratedRegex(@"\$\s*((?:\\[A-Za-z]+|\^\{?\\circ\}?|\\%|\\\$)(?:\s*\\?[A-Za-z]*)?)\s*\$")]
    private static partial Regex InlineMath();

    /// <summary>A fenced block (or doubled backticks) around nothing but one path: shown as an inline path.</summary>
    [GeneratedRegex(@"```[\w-]*[ \t]*\r?\n[ \t]*([A-Za-z]:\\[^\r\n`]+?)[ \t]*\r?\n[ \t]*```|``[ \t]*([A-Za-z]:\\[^\r\n`]+?)[ \t]*``")]
    private static partial Regex FencedPath();

    /// <summary>
    /// Small models write LaTeX in plain answers ("$\rightarrow$", "20$^\circ$C"): shown as the symbols. A path they wrap in
    /// a code fence becomes an inline path (the chat links paths).
    /// </summary>
    public static string Tidy(string text)
    {
        // Non-breaking spaces ("14\u00A0October") read the same but break copying and searching; "[n]" is the prompt's
        // placeholder copied literally.
        text = text.Replace('\u00A0', ' ').Replace('\u202F', ' ').Replace('\u2007', ' ');
        if (text.Contains("[n]", StringComparison.Ordinal)) text = Regex.Replace(text, @"[ \t]?\[n\]", "");
        // HTML code tags ("<code>C:\…</code>") as Markdown, so the chat shows (and links) them.
        if (text.Contains("<code>", StringComparison.OrdinalIgnoreCase)) text = Regex.Replace(text, @"<code>([^<\n]*)</code>", "`$1`", RegexOptions.IgnoreCase);
        if (text.Contains("``", StringComparison.Ordinal))
            text = FencedPath().Replace(text, m => "`" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "`");
        if (!text.Contains('\\')) return text;
        var t = InlineMath().Replace(text, m =>
        {
            var inner = m.Groups[1].Value.Trim();
            return Symbols.FirstOrDefault(s => s.Latex == inner).Symbol ?? inner;
        });
        // Only where a command starts a word ("a \times b", "20^\circ"), so paths like C:\Users\me\tools are left alone.
        foreach (var (latex, symbol) in Symbols)
            t = Regex.Replace(t, @"(?<=^|[\s(\d])" + Regex.Escape(latex) + @"(?![A-Za-z])", symbol.Replace("$", "$$"));
        return t;
    }

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DrivePath();

    /// <summary>
    /// The local path in a file: link or a plain "C:\…" path from an answer, so the chat can offer to open it — never a
    /// network (\\server) path or one that climbs with ".."; null otherwise.
    /// </summary>
    public static string? LocalPath(string target)
    {
        var t = target.Trim().Trim('<', '>');
        if (t.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) t = Uri.UnescapeDataString(t[5..]).TrimStart('/').Replace('/', '\\');
        return DrivePath().IsMatch(t) && !t.Contains("..", StringComparison.Ordinal) ? t : null;
    }

    [GeneratedRegex(@"\[(?<text>[^\]\n]{1,300})\]\((?<url>https?://[^\s)]+)\)|(?<bare>https?://[^\s<>()\[\]""']+[^\s<>()\[\]""'.,;:!?])")]
    private static partial Regex AnswerUrl();

    [GeneratedRegex(@"[ \t]*\((?:[^()\n]{0,40}\b(?:inferred|assumed|guessed|approximate|not verified|unverified|likely|probably)\b[^()\n]{0,40})\)", RegexOptions.IgnoreCase)]
    private static partial Regex GuessNote();

    /// <summary>Words in an address's path ("/2026/09/spacex-releases-otters/" → "spacex releases otters").</summary>
    private static string SlugWords(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? Regex.Replace(Uri.UnescapeDataString(u.AbsolutePath), @"[/_\-.]+|\b\d+\b", " ") : "";

    private static string Key(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant().Replace("www.", "") : "";

    /// <summary>
    /// Links in an answer must be ones Aqua actually saw. On a site Aqua read, an address that wasn't among its links,
    /// results or pages — a model's made-up or mangled slug — is replaced by the seen link whose title best matches that
    /// line (and the slug's own words), or dropped when nothing matches. Links to sites Aqua didn't read are left alone.
    /// </summary>
    public static string RepairLinks(string text, IReadOnlyDictionary<string, string> seen, out int changed)
    {
        changed = 0;
        if (seen.Count == 0 || !text.Contains("http", StringComparison.OrdinalIgnoreCase)) return text;
        var known = seen.ToDictionary(k => Key(k.Key), k => (Url: k.Key, Title: k.Value));
        var hosts = new HashSet<string>(seen.Keys.Select(Host));
        var fixes = 0;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("http", StringComparison.OrdinalIgnoreCase)) continue;
            var line = lines[i];
            var context = AnswerUrl().Replace(line, m => m.Groups["text"].Success ? m.Groups["text"].Value : " ");
            // A line that's only an address belongs to the item above it.
            if (context.Trim(' ', '*', '-', '•', '\t').Length < 3 && i > 0) context = AnswerUrl().Replace(lines[i - 1], " ");
            var updated = AnswerUrl().Replace(line, m =>
            {
                var url = m.Groups["url"].Success ? m.Groups["url"].Value : m.Groups["bare"].Value;
                if (known.ContainsKey(Key(url)) || !hosts.Contains(Host(url))) return m.Value;
                var words = Analysis.TextTools.Signature(context + " " + SlugWords(url));
                var best = known.Values.Where(k => Host(k.Url) == Host(url))
                    .Select(k => (k.Url, Shared: Analysis.TextTools.Signature(k.Title + " " + SlugWords(k.Url)).Count(words.Contains), Size: Analysis.TextTools.Signature(k.Title).Count))
                    .OrderByDescending(x => x.Shared).FirstOrDefault();
                fixes++;
                var replacement = best.Url is not null && best.Shared >= 3 && best.Shared * 2 >= Math.Min(words.Count, Math.Max(1, best.Size)) ? best.Url : null;
                if (m.Groups["url"].Success) return replacement is null ? m.Groups["text"].Value : $"[{m.Groups["text"].Value}]({replacement})";
                return replacement ?? "";
            });
            if (updated == line) continue;
            updated = GuessNote().Replace(updated, "");
            lines[i] = updated.TrimEnd();
        }
        changed = fixes;
        if (fixes == 0) return text;
        // Bullets left empty by a dropped address go.
        return string.Join('\n', lines.Where(l => !Regex.IsMatch(l, @"^\s*(?:[-*•]|\d+[.)])\s*$")));
    }

    /// <summary>Splits an answer into what the user should read and the model's narration (both trimmed).</summary>
    public static (string Answer, string Notes) Split(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return ("", "");
        var notes = new StringBuilder();

        // A "Thinking Process:" block: keep what follows an answer marker, or treat it all as reasoning.
        var header = ThinkingHeader().Match(t);
        if (header.Success && header.Index < 40)
        {
            var marker = AnswerMarker().Matches(t).LastOrDefault(m => m.Index > header.Index);
            if (marker is null) return ("", t);
            notes.Append(t[..marker.Index].Trim());
            t = t[(marker.Index + marker.Length)..].Trim();
        }

        // Leading narration sentences ("The user is asking… Let me check…").
        var paragraphs = t.Split("\n\n");
        var first = paragraphs[0];
        var sentences = SentenceBreak().Split(first);
        var moved = 0;
        while (moved < sentences.Length && Narration().IsMatch(sentences[moved].TrimStart('*', ' ', '>')))
            moved++;
        if (moved > 0)
        {
            var rest = string.Join(' ', sentences.Skip(moved)).Trim();
            var remainder = (rest.Length > 0 ? rest + "\n\n" : "") + string.Join("\n\n", paragraphs.Skip(1));
            // Keep it only if a real answer is left.
            if (remainder.Trim().Length >= 20)
            {
                if (notes.Length > 0) notes.Append("\n\n");
                notes.Append(string.Join(' ', sentences.Take(moved)).Trim());
                t = remainder.Trim();
            }
        }
        return (t, notes.ToString());
    }
}

/// <summary>Long text in parts that fit the model: split at paragraph breaks, else sentence ends, else hard. Pure; unit-tested.</summary>
public static partial class TextChunks
{
    [GeneratedRegex(@"(?<=[.!?…""”’)])\s+")]
    private static partial Regex SentenceEnd();

    public static List<string> Split(string text, int max)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0) parts.Add(current.ToString().Trim());
            current.Clear();
        }
        foreach (var paragraph in Regex.Split(text.Replace("\r", ""), @"\n\s*\n|\n"))
        {
            var p = paragraph.Trim();
            if (p.Length == 0) continue;
            if (current.Length > 0 && current.Length + p.Length + 2 > max) Flush();
            if (p.Length <= max) { current.Append(current.Length > 0 ? "\n\n" : "").Append(p); continue; }
            // One very long paragraph: by sentences, and a sentence longer than a part is cut.
            foreach (var sentence in SentenceEnd().Split(p))
            {
                var s = sentence;
                while (s.Length > max) { Flush(); parts.Add(s[..max]); s = s[max..]; }
                if (current.Length > 0 && current.Length + s.Length + 1 > max) Flush();
                current.Append(current.Length > 0 ? " " : "").Append(s);
            }
        }
        Flush();
        return parts.Where(x => x.Length > 0).ToList();
    }
}

/// <summary>
/// Stops reasoning that has gone on too long or in circles (small models can "wait, actually…" until they run out of
/// room and never answer). Pure: unit-tested.
/// </summary>
public static partial class ThinkingGuard
{
    [GeneratedRegex(@"\b(?:wait|actually|hmm|hold on)\b[,.]?", RegexOptions.IgnoreCase)]
    private static partial Regex Dithering();

    /// <summary>Why to stop, or null to carry on. <paramref name="segment"/> is this step's reasoning so far.</summary>
    public static string? Check(string segment, int budget)
    {
        if (segment.Length > budget) return "long";
        if (segment.Length < 2500) return null;
        if (Dithering().Matches(segment).Count >= 14) return "circles";
        // The same sentence coming back again and again.
        var sentences = segment.Split(new[] { '.', '\n', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ")).Where(s => s.Length >= 40).ToList();
        if (sentences.GroupBy(s => s).Any(g => g.Count() >= 3)) return "circles";
        return null;
    }
}
