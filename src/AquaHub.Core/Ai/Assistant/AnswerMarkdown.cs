using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>A block of an answer, with the Markdown it came from (copying a table or a formula gives that back).</summary>
public abstract record MdBlock
{
    public string Source { get; init; } = "";
}

/// <summary>Lines of text; each line break the model wrote is kept.</summary>
public sealed record MdParagraph(IReadOnlyList<string> Lines) : MdBlock;

public sealed record MdHeading(int Level, string Text) : MdBlock;

/// <summary>A fenced code block; <paramref name="Closed"/> is false while it's still streaming in.</summary>
public sealed record MdCode(string Language, IReadOnlyList<string> Lines, bool Closed) : MdBlock;

/// <summary>A display formula ($$ … $$, \[ … \] or a bare \begin{…}…\end{…}).</summary>
public sealed record MdMath(string Latex, bool Closed) : MdBlock;

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

public sealed record MdRule : MdBlock;

public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;

/// <summary>A list item's blocks (nested lists among them); <paramref name="Checked"/> for "- [ ]" / "- [x]" task items.</summary>
public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Checked);

public enum MdAlign { None, Left, Center, Right }

/// <summary>A GitHub-style table: every row has as many cells as the header.</summary>
public sealed record MdTable(IReadOnlyList<string> Header, IReadOnlyList<MdAlign> Align, IReadOnlyList<IReadOnlyList<string>> Rows) : MdBlock;

public enum MdSpan { Text, Bold, Italic, BoldItalic, Strike, Code, Math, Link, Citation, Url, Path }

/// <summary>
/// A piece of a line. Bold, italic and struck-through text have <see cref="Children"/>; a link's or path's
/// <see cref="Target"/> is where it goes; maths keeps its LaTeX in <see cref="Text"/> and what was written (with the
/// dollar signs) in <see cref="Target"/>; a citation's number is <see cref="Number"/>.
/// </summary>
public sealed record MdInline(MdSpan Kind, string Text, string? Target = null, IReadOnlyList<MdInline>? Children = null, int Number = 0);

/// <summary>
/// The Markdown local models write, as blocks and inline pieces for the chat to draw: paragraphs, headings, quotes,
/// nested and numbered lists (task boxes too), fenced code, GitHub tables, rules and LaTeX maths ($…$, $$…$$, \(…\),
/// \[…\]). Forgiving by design: an answer is drawn again as it streams in, so a half-written table is a paragraph
/// until its divider row arrives, and an unclosed formula or code block shows what has come so far. Pure; unit-tested.
/// </summary>
public static partial class AnswerMarkdown
{
    /// <summary>Quotes and lists inside each other go no deeper than this (a hostile answer can't exhaust the stack).</summary>
    private const int MaxDepth = 10;

    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return Blocks(lines, 0);
    }

    private static List<MdBlock> Blocks(IReadOnlyList<string> lines, int depth)
    {
        var blocks = new List<MdBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            if (IsBlank(lines[i])) { i++; continue; }
            var start = i;
            var block = Fence(lines, ref i) ?? DisplayMath(lines, ref i) ?? Table(lines, ref i) ?? Heading(lines, ref i) ?? Rule(lines, ref i)
                        ?? (depth < MaxDepth ? Quote(lines, ref i, depth) ?? List(lines, ref i, depth) : null)
                        ?? Paragraph(lines, ref i, depth);
            if (i == start) i++; // never stall on a line nothing claimed
            blocks.Add(block with { Source = string.Join("\n", lines.Skip(start).Take(i - start)).TrimEnd() });
        }
        return blocks;
    }

    private static bool IsBlank(string line) => line.Trim().Length == 0;

    /// <summary>Columns of leading white space (a tab counts as four).</summary>
    internal static int Indent(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4 - n % 4;
            else break;
        }
        return n;
    }

    /// <summary>The line without its first <paramref name="columns"/> columns of indentation.</summary>
    private static string Dedent(string line, int columns)
    {
        var n = 0;
        var at = 0;
        while (at < line.Length && n < columns && line[at] is ' ' or '\t')
        {
            n += line[at] == '\t' ? 4 - n % 4 : 1;
            at++;
        }
        return line[at..];
    }

    // ───────────────────────────── Blocks ─────────────────────────────

    [GeneratedRegex(@"^\s{0,3}(?<fence>`{3,}|~{3,})\s*(?<lang>[^\s`]*)")]
    private static partial Regex FenceRx();

    private static MdBlock? Fence(IReadOnlyList<string> lines, ref int i)
    {
        var m = FenceRx().Match(lines[i]);
        if (!m.Success) return null;
        var fence = m.Groups["fence"].Value;
        var body = new List<string>();
        var j = i + 1;
        var closed = false;
        for (; j < lines.Count; j++)
        {
            var t = lines[j].Trim();
            // Only a run of the same fence character, at least as long, closes it ("```python" inside doesn't).
            if (t.Length >= fence.Length && t.All(c => c == fence[0]))
            {
                closed = true;
                j++;
                break;
            }
            body.Add(lines[j]);
        }
        var indent = Indent(lines[i]);
        i = j;
        return new MdCode(m.Groups["lang"].Value, body.Select(l => Dedent(l, indent)).ToList(), closed);
    }

    /// <summary>LaTeX environments a model writes on their own, without dollar signs around them.</summary>
    private static readonly HashSet<string> MathEnvironments = new(StringComparer.Ordinal)
    {
        "equation", "equation*", "align", "align*", "aligned", "alignat", "alignat*", "gather", "gather*", "gathered", "multline", "multline*",
        "eqnarray", "eqnarray*", "displaymath", "cases", "matrix", "pmatrix", "bmatrix", "Bmatrix", "vmatrix", "Vmatrix", "split",
    };

    [GeneratedRegex(@"^\\begin\{(?<env>[A-Za-z]+\*?)\}")]
    private static partial Regex BeginRx();

    private static MdBlock? DisplayMath(IReadOnlyList<string> lines, ref int i)
    {
        var t = lines[i].Trim();
        string open, close;
        if (t.StartsWith("$$", StringComparison.Ordinal)) (open, close) = ("$$", "$$");
        else if (t.StartsWith(@"\[", StringComparison.Ordinal)) (open, close) = (@"\[", @"\]");
        else if (BeginRx().Match(t) is { Success: true } env && MathEnvironments.Contains(env.Groups["env"].Value))
        {
            // A bare environment: everything up to its \end, which stays part of the formula.
            var end = @"\end{" + env.Groups["env"].Value + "}";
            var body = new List<string>();
            var k = i;
            for (; k < lines.Count; k++)
            {
                body.Add(lines[k].Trim());
                if (lines[k].Contains(end, StringComparison.Ordinal)) { k++; i = k; return new MdMath(string.Join("\n", body), true); }
            }
            i = k;
            return new MdMath(string.Join("\n", body), false);
        }
        else return null;

        var rest = t[open.Length..];
        // "$$ x^2 $$" on one line. Text after the closing pair ("$$x$$ is the area") makes it an ordinary line instead.
        if (rest.Length > close.Length && rest.EndsWith(close, StringComparison.Ordinal))
        {
            var inner = rest[..^close.Length];
            if (open == "$$" && inner.Contains("$$", StringComparison.Ordinal)) return null;
            i++;
            return new MdMath(inner.Trim(), true);
        }
        if (rest.Contains(close, StringComparison.Ordinal)) return null;
        var parts = new List<string>();
        if (rest.Trim().Length > 0) parts.Add(rest.Trim());
        var j = i + 1;
        for (; j < lines.Count; j++)
        {
            var l = lines[j].Trim();
            if (l.EndsWith(close, StringComparison.Ordinal))
            {
                var last = l[..^close.Length].Trim();
                if (last.Length > 0) parts.Add(last);
                i = j + 1;
                return new MdMath(string.Join("\n", parts), true);
            }
            // A blank line ends a formula that never closed: the rest of the answer isn't swallowed into it.
            if (l.Length == 0) break;
            parts.Add(l);
        }
        i = j;
        return new MdMath(string.Join("\n", parts), false);
    }

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex DelimiterRx();

    internal static bool IsTableStart(IReadOnlyList<string> lines, int i)
    {
        if (i + 1 >= lines.Count || !lines[i].Contains('|') || !lines[i + 1].Contains('|') || !DelimiterRx().IsMatch(lines[i + 1])) return false;
        var header = Cells(lines[i]);
        return header.Count > 0 && header.Count == Cells(lines[i + 1]).Count;
    }

    private static MdBlock? Table(IReadOnlyList<string> lines, ref int i)
    {
        if (!IsTableStart(lines, i)) return null;
        var header = Cells(lines[i]);
        var align = Cells(lines[i + 1]).Select(AlignOf).ToList();
        var rows = new List<IReadOnlyList<string>>();
        var j = i + 2;
        for (; j < lines.Count; j++)
        {
            var l = lines[j];
            if (IsBlank(l) || !l.Contains('|')) break;
            var cells = Cells(l);
            rows.Add(Enumerable.Range(0, header.Count).Select(k => k < cells.Count ? cells[k] : "").ToList());
        }
        i = j;
        return new MdTable(header, align, rows);
    }

    private static MdAlign AlignOf(string delimiter)
    {
        var d = delimiter.Trim();
        var left = d.StartsWith(':');
        var right = d.EndsWith(':');
        return left && right ? MdAlign.Center : right ? MdAlign.Right : left ? MdAlign.Left : MdAlign.None;
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    /// <summary>
    /// A table row's cells: split at "|", but not at an escaped "\|" or one inside `code` or $maths$ (so "$|x|$" stays
    /// whole while "$5 | $6" — prices — splits). "&lt;br&gt;" in a cell is a line break.
    /// </summary>
    internal static List<string> Cells(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith(@"\|", StringComparison.Ordinal)) t = t[..^1];
        var shielded = new bool[t.Length];
        foreach (Match m in ShieldRx().Matches(t))
            for (var k = m.Index; k < m.Index + m.Length; k++) shielded[k] = true;
        var cells = new List<string>();
        var sb = new StringBuilder();
        for (var k = 0; k < t.Length; k++)
        {
            var c = t[k];
            if (c == '\\' && k + 1 < t.Length && t[k + 1] == '|' && !shielded[k]) { sb.Append('|'); k++; continue; }
            if (c == '|' && !shielded[k]) { cells.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells.Select(x => BreakTag().Replace(x.Trim(), "\n")).ToList();
    }

    [GeneratedRegex(@"`[^`\n]+`|(?<![\\$\w])\$(?![\s$])(?:[^$\\\n]|\\.)+?(?<![\s\\])\$(?![\d$])")]
    private static partial Regex ShieldRx();

    [GeneratedRegex(@"^\s{0,3}(?<level>#{1,6})(?:[ \t]+(?<text>.*?))?[ \t]*#*[ \t]*$")]
    private static partial Regex HeadingRx();

    private static MdBlock? Heading(IReadOnlyList<string> lines, ref int i)
    {
        var m = HeadingRx().Match(lines[i]);
        if (!m.Success || m.Groups["text"].Value.Trim().Length == 0) return null;
        i++;
        return new MdHeading(m.Groups["level"].Length, m.Groups["text"].Value.Trim());
    }

    [GeneratedRegex(@"^\s{0,3}(?<c>[-*_])(?:[ \t]*\k<c>){2,}[ \t]*$")]
    private static partial Regex RuleRx();

    private static MdBlock? Rule(IReadOnlyList<string> lines, ref int i)
    {
        if (!RuleRx().IsMatch(lines[i])) return null;
        i++;
        return new MdRule();
    }

    [GeneratedRegex(@"^\s{0,3}>")]
    private static partial Regex QuoteRx();

    private static MdBlock? Quote(IReadOnlyList<string> lines, ref int i, int depth)
    {
        if (!QuoteRx().IsMatch(lines[i])) return null;
        var inner = new List<string>();
        var j = i;
        for (; j < lines.Count && QuoteRx().IsMatch(lines[j]); j++)
        {
            var l = lines[j].TrimStart();
            l = l[1..];
            inner.Add(l.StartsWith(' ') ? l[1..] : l);
        }
        i = j;
        return new MdQuote(Blocks(inner, depth + 1));
    }

    /// <summary>"- ", "* ", "+ ", "• ", "1. " or "1) " (numbers of up to three digits: "2026. That year…" is a sentence).</summary>
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>[-*+•]|(?<num>\d{1,3})[.)])(?:[ \t]+(?<rest>.*)|$)")]
    private static partial Regex ItemRx();

    private static MdBlock? List(IReadOnlyList<string> lines, ref int i, int depth)
    {
        var first = ItemRx().Match(lines[i]);
        if (!first.Success) return null;
        var baseIndent = Indent(first.Groups["indent"].Value);
        var ordered = first.Groups["num"].Success;
        var start = ordered && int.TryParse(first.Groups["num"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s0) ? s0 : 1;
        var items = new List<MdListItem>();
        var j = i;
        while (j < lines.Count && Sibling(lines[j], baseIndent, ordered) is { } m)
        {
            var contentIndent = baseIndent + m.Groups["marker"].Length + 1;
            var body = new List<string> { m.Groups["rest"].Value };
            var k = j + 1;
            while (k < lines.Count)
            {
                if (IsBlank(lines[k]))
                {
                    // A blank line: the item carries on when indented text follows (a second paragraph, a nested list).
                    var next = k;
                    while (next < lines.Count && IsBlank(lines[next])) next++;
                    if (next < lines.Count && Indent(lines[next]) > baseIndent)
                    {
                        for (; k < next; k++) body.Add("");
                        continue;
                    }
                    break;
                }
                var indent = Indent(lines[k]);
                if (indent <= baseIndent) break;
                body.Add(Dedent(lines[k], Math.Min(indent, contentIndent)));
                k++;
            }
            bool? check = null;
            if (TaskRx().Match(body[0]) is { Success: true } task)
            {
                check = task.Groups["mark"].Value != " ";
                body[0] = body[0][task.Length..];
            }
            items.Add(new MdListItem(Blocks(body, depth + 1), check));
            j = k;
            // Blank lines between items keep one list (numbering carries on).
            var after = j;
            while (after < lines.Count && IsBlank(lines[after])) after++;
            if (after > j && after < lines.Count && Sibling(lines[after], baseIndent, ordered) is not null) j = after;
        }
        i = j;
        return new MdList(ordered, start, items);
    }

    private static Match? Sibling(string line, int indent, bool ordered)
    {
        var m = ItemRx().Match(line);
        return m.Success && Indent(m.Groups["indent"].Value) == indent && m.Groups["num"].Success == ordered ? m : null;
    }

    [GeneratedRegex(@"^\[(?<mark>[ xX])\][ \t]+")]
    private static partial Regex TaskRx();

    private static MdBlock Paragraph(IReadOnlyList<string> lines, ref int i, int depth)
    {
        var body = new List<string> { lines[i].Trim() };
        var j = i + 1;
        for (; j < lines.Count; j++)
        {
            if (IsBlank(lines[j]) || StartsBlock(lines, j, depth)) break;
            body.Add(lines[j].Trim());
        }
        i = j;
        return new MdParagraph(body);
    }

    /// <summary>Lines that end a paragraph without a blank line before them (models often skip it before a list or table).</summary>
    private static bool StartsBlock(IReadOnlyList<string> lines, int j, int depth)
    {
        var l = lines[j];
        if (FenceRx().IsMatch(l) || RuleRx().IsMatch(l) || IsTableStart(lines, j)) return true;
        if (HeadingRx().Match(l) is { Success: true } h && h.Groups["text"].Value.Trim().Length > 0) return true;
        if (depth < MaxDepth && (QuoteRx().IsMatch(l) || ItemRx().IsMatch(l))) return true;
        var t = l.Trim();
        if (BeginRx().Match(t) is { Success: true } env && MathEnvironments.Contains(env.Groups["env"].Value)) return true;
        return t.StartsWith("$$", StringComparison.Ordinal) && !t[2..].Contains("$$", StringComparison.Ordinal)
               || t.Length > 4 && t.StartsWith("$$", StringComparison.Ordinal) && t.EndsWith("$$", StringComparison.Ordinal) && !t[2..^2].Contains("$$", StringComparison.Ordinal)
               || t.StartsWith(@"\[", StringComparison.Ordinal) && (!t.Contains(@"\]", StringComparison.Ordinal) || t.EndsWith(@"\]", StringComparison.Ordinal));
    }

    // ───────────────────────────── Inline ─────────────────────────────

    /// <summary>
    /// One line's pieces. Leftmost match wins; at the same place the order below decides: code, maths, emphasis, links,
    /// citations, addresses, Windows paths and backslash escapes. A "$" pair counts as maths only the way Pandoc reads it
    /// — "$" with no space after it, a closing "$" with no space before it and no digit after it — so prices don't.
    /// </summary>
    [GeneratedRegex(
        @"``(?<code2>.+?)``" +
        @"|`(?<code>[^`\n]+)`" +
        @"|\$\$(?<mathd>[^$\n]+?)\$\$" +
        @"|(?<![\\$\w])\$(?![\s$])(?<math>(?:[^$\\\n]|\\.)+?)(?<![\s\\])\$(?![\d$])" +
        @"|\\\((?<mathp>.+?)\\\)" +
        @"|\\\[(?<mathb>.+?)\\\]" +
        @"|\*\*\*(?<bi>[^*\n]+?)\*\*\*" +
        @"|\*\*(?<bold>(?:[^*\n]|\*(?!\*))+?)\*\*" +
        @"|~~(?<strike>[^~\n]+?)~~" +
        @"|(?<![*\w\\])\*(?![\s*])(?<it>[^*\n]+?)(?<![\s*\\])\*(?![*\w])" +
        @"|(?<![_\w\\])_(?![\s_])(?<it2>[^_\n]+?)(?<![\s_\\])_(?![_\w])" +
        @"|\[(?<ltext>[^\]\n]{1,400})\]\((?<lurl>https?://[^\s)]+|file:/{2,3}[^)\n]+|[A-Za-z]:\\[^)\n]+)\)" +
        @"|\[(?<cite>\d{1,2})\]" +
        @"|<(?<aurl>https?://[^\s<>]+)>" +
        @"|(?<url>https?://[^\s<>()\[\]""']+[^\s<>()\[\]""'.,;:!?])" +
        @"|(?<![\w/\\])(?<path>[A-Za-z]:\\[^\s<>""|?*()\[\]]*[^\s<>""|?*()\[\].,;:!])" +
        @"|\\(?<esc>[\\`*_{}\[\]()#+\-.!|$~<>])")]
    private static partial Regex InlineRx();

    /// <summary>Emphasis inside emphasis goes this deep; anything deeper is plain text.</summary>
    private const int MaxInlineDepth = 4;

    public static IReadOnlyList<MdInline> Inlines(string? line) => Inlines(line ?? "", 0);

    private static List<MdInline> Inlines(string line, int depth)
    {
        var list = new List<MdInline>();
        if (line.Length == 0) return list;
        if (depth > MaxInlineDepth)
        {
            list.Add(new MdInline(MdSpan.Text, line));
            return list;
        }
        var at = 0;
        foreach (Match m in InlineRx().Matches(line))
        {
            if (m.Index > at) AddText(list, line[at..m.Index]);
            list.Add(Token(m, depth));
            at = m.Index + m.Length;
        }
        if (at < line.Length) AddText(list, line[at..]);
        // Escapes come back as text: merge them with their neighbours.
        for (var k = list.Count - 1; k > 0; k--)
            if (list[k].Kind == MdSpan.Text && list[k - 1].Kind == MdSpan.Text)
            {
                list[k - 1] = list[k - 1] with { Text = list[k - 1].Text + list[k].Text };
                list.RemoveAt(k);
            }
        return list;
    }

    private static void AddText(List<MdInline> list, string text)
    {
        if (text.Length > 0) list.Add(new MdInline(MdSpan.Text, text));
    }

    private static MdInline Token(Match m, int depth)
    {
        string G(string name) => m.Groups[name].Value;
        bool Has(string name) => m.Groups[name].Success;
        if (Has("code2")) return new MdInline(MdSpan.Code, G("code2").Trim());
        if (Has("code")) return new MdInline(MdSpan.Code, G("code"));
        if (Has("mathd")) return new MdInline(MdSpan.Math, G("mathd").Trim(), m.Value);
        if (Has("math")) return new MdInline(MdSpan.Math, G("math").Trim(), m.Value);
        if (Has("mathp")) return new MdInline(MdSpan.Math, G("mathp").Trim(), m.Value);
        if (Has("mathb")) return new MdInline(MdSpan.Math, G("mathb").Trim(), m.Value);
        if (Has("bi")) return new MdInline(MdSpan.BoldItalic, G("bi"), Children: Inlines(G("bi"), depth + 1));
        if (Has("bold")) return new MdInline(MdSpan.Bold, G("bold"), Children: Inlines(G("bold"), depth + 1));
        if (Has("strike")) return new MdInline(MdSpan.Strike, G("strike"), Children: Inlines(G("strike"), depth + 1));
        if (Has("it")) return new MdInline(MdSpan.Italic, G("it"), Children: Inlines(G("it"), depth + 1));
        if (Has("it2")) return new MdInline(MdSpan.Italic, G("it2"), Children: Inlines(G("it2"), depth + 1));
        if (Has("ltext")) return new MdInline(MdSpan.Link, G("ltext").Trim('`', ' '), G("lurl"));
        if (Has("cite")) return new MdInline(MdSpan.Citation, m.Value, Number: int.Parse(G("cite"), CultureInfo.InvariantCulture));
        if (Has("aurl")) return new MdInline(MdSpan.Url, G("aurl"), G("aurl"));
        if (Has("url")) return new MdInline(MdSpan.Url, G("url"), G("url"));
        if (Has("path")) return new MdInline(MdSpan.Path, G("path"), G("path"));
        return new MdInline(MdSpan.Text, G("esc"));
    }

    /// <summary>A line's text without its Markdown (what a table column's width is judged by).</summary>
    public static string Plain(string line) => Plain(Inlines(line));

    private static string Plain(IReadOnlyList<MdInline> pieces) => string.Concat(pieces.Select(p => p.Children is { } children ? Plain(children) : p.Text));
}
