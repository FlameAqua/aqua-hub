using System.Text;
using System.Text.RegularExpressions;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// LaTeX in answers: where code and formulas are (so tidying the text never rewrites them), and formulas rewritten
/// into what XAML-Math draws — the commands, environments and Unicode symbols local models use that it doesn't know,
/// as their nearest equivalents (\dfrac → \frac, \mathbf → \mathrm, \begin{cases} → \cases, × → \times…). Checked
/// against XAML-Math's own parser in the unit tests. Pure.
/// </summary>
public static partial class MathText
{
    // ───────────────────────────── Code and formulas in an answer ─────────────────────────────

    [GeneratedRegex(@"^\s{0,3}(?<fence>`{3,}|~{3,})")]
    private static partial Regex FenceRx();

    [GeneratedRegex(@"^\\begin\{(?<env>[A-Za-z]+\*?)\}")]
    private static partial Regex BeginRx();

    [GeneratedRegex(@"``.+?``|`[^`\n]+`")]
    private static partial Regex CodeSpanRx();

    [GeneratedRegex(@"``.+?``|`[^`\n]+`|\$\$[^$\n]+?\$\$|(?<![\\$\w])\$(?![\s$])(?:[^$\\\n]|\\.)+?(?<![\s\\])\$(?![\d$])|\\\(.+?\\\)|\\\[.+?\\\]")]
    private static partial Regex CodeOrMathSpanRx();

    /// <summary>
    /// The character ranges [start, end) of code — fenced blocks and `spans` — and, with <paramref name="maths"/>, of
    /// formulas: display blocks ($$…$$, \[…\], a bare \begin{…}…\end{…}) and inline ones read the way the chat reads them.
    /// </summary>
    public static List<(int Start, int End)> Protected(string text, bool maths)
    {
        var ranges = new List<(int, int)>();
        var starts = new List<int> { 0 };
        for (var k = 0; k < text.Length; k++) if (text[k] == '\n') starts.Add(k + 1);
        int End(int line) => line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
        string Line(int line) => text[starts[line]..End(line)];

        var i = 0;
        while (i < starts.Count)
        {
            var line = Line(i);
            var t = line.Trim();
            int? closes = null;
            if (FenceRx().Match(line) is { Success: true } f)
            {
                var fence = f.Groups["fence"].Value;
                var j = i + 1;
                while (j < starts.Count && !(Line(j).Trim() is var c && c.Length >= fence.Length && c.All(ch => ch == fence[0]))) j++;
                closes = Math.Min(j, starts.Count - 1);
            }
            else if (maths && (t.StartsWith("$$", StringComparison.Ordinal) || t.StartsWith(@"\[", StringComparison.Ordinal)))
            {
                var close = t.StartsWith("$$", StringComparison.Ordinal) ? "$$" : @"\]";
                var rest = t[2..];
                if (rest.Length > close.Length && rest.EndsWith(close, StringComparison.Ordinal) && !rest[..^close.Length].Contains(close, StringComparison.Ordinal)) closes = i;
                else if (!rest.Contains(close, StringComparison.Ordinal))
                {
                    var j = i + 1;
                    while (j < starts.Count && Line(j).Trim() is var c && c.Length > 0 && !c.EndsWith(close, StringComparison.Ordinal)) j++;
                    closes = Math.Min(j, starts.Count - 1);
                }
            }
            else if (maths && BeginRx().Match(t) is { Success: true } env)
            {
                var end = @"\end{" + env.Groups["env"].Value + "}";
                var j = i;
                while (j < starts.Count && !Line(j).Contains(end, StringComparison.Ordinal)) j++;
                if (j < starts.Count) closes = j;
            }
            if (closes is { } last)
            {
                ranges.Add((starts[i], End(last)));
                i = last + 1;
                continue;
            }
            foreach (Match m in (maths ? CodeOrMathSpanRx() : CodeSpanRx()).Matches(line))
                ranges.Add((starts[i] + m.Index, starts[i] + m.Index + m.Length));
            i++;
        }
        return ranges;
    }

    /// <summary>Applies <paramref name="change"/> to the text outside code (and, with <paramref name="maths"/>, outside formulas).</summary>
    public static string Outside(string text, bool maths, Func<string, string> change)
    {
        var ranges = Protected(text, maths);
        if (ranges.Count == 0) return change(text);
        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var (start, end) in ranges)
        {
            if (start > at) sb.Append(change(text[at..start]));
            sb.Append(text, start, end - start);
            at = end;
        }
        if (at < text.Length) sb.Append(change(text[at..]));
        return sb.ToString();
    }

    // ───────────────────────────── Formulas for XAML-Math ─────────────────────────────

    /// <summary>Commands XAML-Math doesn't have, and what stands in for them ("" drops the command, keeping what follows).</summary>
    private static readonly Dictionary<string, string> Commands = new(StringComparer.Ordinal)
    {
        ["dfrac"] = @"\frac", ["tfrac"] = @"\frac", ["cfrac"] = @"\frac",
        ["mathbf"] = @"\mathrm", ["boldsymbol"] = @"\mathrm", ["bm"] = @"\mathrm", ["mathbb"] = @"\mathrm", ["mathsf"] = @"\mathrm",
        ["mathtt"] = @"\mathrm", ["mathfrak"] = @"\mathrm", ["operatorname"] = @"\mathrm", ["textup"] = @"\mathrm", ["mathscr"] = @"\mathcal",
        ["textbf"] = @"\text", ["textit"] = @"\text", ["textrm"] = @"\text", ["textsf"] = @"\text", ["texttt"] = @"\text", ["textnormal"] = @"\text",
        ["mbox"] = @"\text", ["hbox"] = @"\text", ["emph"] = @"\text",
        ["dots"] = @"\ldots", ["dotsc"] = @"\ldots", ["dotsb"] = @"\ldots", ["dotso"] = @"\ldots",
        ["dotsm"] = @"\cdots", ["dotsi"] = @"\cdots", ["vdots"] = @"\cdots", ["ddots"] = @"\cdots",
        ["implies"] = @"\Rightarrow", ["Longrightarrow"] = @"\Rightarrow", ["impliedby"] = @"\Leftarrow", ["Longleftarrow"] = @"\Leftarrow",
        ["iff"] = @"\Leftrightarrow", ["Longleftrightarrow"] = @"\Leftrightarrow", ["longrightarrow"] = @"\rightarrow", ["mapsto"] = @"\rightarrow",
        ["longmapsto"] = @"\rightarrow", ["longleftarrow"] = @"\leftarrow", ["longleftrightarrow"] = @"\leftrightarrow",
        ["lvert"] = "|", ["rvert"] = "|", ["lVert"] = @"\|", ["rVert"] = @"\|",
        ["overbrace"] = @"\overline", ["underbrace"] = @"\underline", ["overrightarrow"] = @"\vec", ["notin"] = @"\not\in",
        ["quad"] = @"\;\;\;\;", ["qquad"] = @"\;\;\;\;\;\;\;\;", ["degree"] = @"^\circ", ["bmod"] = @"\;\mathrm{mod}\;", ["middle"] = "",
        ["displaystyle"] = "", ["textstyle"] = "", ["scriptstyle"] = "", ["scriptscriptstyle"] = "", ["limits"] = "", ["nolimits"] = "",
        ["big"] = "", ["Big"] = "", ["bigg"] = "", ["Bigg"] = "", ["bigl"] = "", ["bigr"] = "", ["Bigl"] = "", ["Bigr"] = "",
        ["biggl"] = "", ["biggr"] = "", ["Biggl"] = "", ["Biggr"] = "", ["bigm"] = "", ["Bigm"] = "",
        ["nonumber"] = "", ["notag"] = "", ["hline"] = "", ["centering"] = "", ["boxed"] = "", ["cancel"] = "", ["bcancel"] = "", ["xcancel"] = "",
    };

    /// <summary>A backslash and what it escapes: a command name, or one other character ("\\", "\,", "\$").</summary>
    [GeneratedRegex(@"\\(?:(?<name>[A-Za-z]+)|(?<other>[^A-Za-z]))")]
    private static partial Regex EscapeRx();

    [GeneratedRegex(@"\\(?:tag|label|hspace|vspace|phantom|hphantom|vphantom)\*?\s*\{[^{}]*\}")]
    private static partial Regex DropWithArgumentRx();

    [GeneratedRegex(@"\\pmod\s*\{([^{}]*)\}")]
    private static partial Regex PmodRx();

    [GeneratedRegex(@"\\(begin|end)\{(?:aligned|align\*|alignat\*?|eqnarray\*?|split|gathered|gather\*?|multline\*?)\}(?:\{\d+\})?")]
    private static partial Regex AlignRx();

    [GeneratedRegex(@"\\(?:begin|end)\{(?:equation\*?|displaymath|math)\}")]
    private static partial Regex WrapperRx();

    [GeneratedRegex(@"\\begin\{cases\}(?<body>.*?)\\end\{cases\}", RegexOptions.Singleline)]
    private static partial Regex CasesRx();

    [GeneratedRegex(@"\\begin\{(?<env>matrix|bmatrix|Bmatrix|vmatrix|Vmatrix|smallmatrix|array)\}(?:\{[^{}]*\})?(?<body>.*?)\\end\{\k<env>\}", RegexOptions.Singleline)]
    private static partial Regex MatrixRx();

    [GeneratedRegex(@"(?:\\\\\s*)+$")]
    private static partial Regex TrailingBreakRx();

    /// <summary>Symbols models type as Unicode inside a formula (XAML-Math's fonts only have its own commands).</summary>
    private static readonly Dictionary<char, string> Unicode = new()
    {
        ['×'] = @"\times", ['÷'] = @"\div", ['±'] = @"\pm", ['∓'] = @"\mp", ['≤'] = @"\le", ['≥'] = @"\ge", ['≠'] = @"\neq", ['≈'] = @"\approx",
        ['≡'] = @"\equiv", ['∼'] = @"\sim", ['∝'] = @"\propto", ['−'] = "-", ['–'] = "-", ['·'] = @"\cdot", ['⋅'] = @"\cdot", ['…'] = @"\ldots",
        ['→'] = @"\rightarrow", ['←'] = @"\leftarrow", ['↔'] = @"\leftrightarrow", ['⇒'] = @"\Rightarrow", ['⇐'] = @"\Leftarrow", ['⇔'] = @"\Leftrightarrow",
        ['∞'] = @"\infty", ['∑'] = @"\sum", ['∏'] = @"\prod", ['∫'] = @"\int", ['√'] = @"\surd", ['∂'] = @"\partial", ['∇'] = @"\nabla",
        ['∈'] = @"\in", ['∉'] = @"\not\in", ['⊂'] = @"\subset", ['⊆'] = @"\subseteq", ['⊃'] = @"\supset", ['⊇'] = @"\supseteq", ['∪'] = @"\cup",
        ['∩'] = @"\cap", ['∅'] = @"\emptyset", ['∀'] = @"\forall", ['∃'] = @"\exists", ['¬'] = @"\neg", ['∧'] = @"\wedge", ['∨'] = @"\vee",
        ['⊥'] = @"\perp", ['∠'] = @"\angle", ['°'] = @"^\circ", ['′'] = "'", ['″'] = "''", ['µ'] = @"\mu",
        ['α'] = @"\alpha", ['β'] = @"\beta", ['γ'] = @"\gamma", ['δ'] = @"\delta", ['ε'] = @"\epsilon", ['ζ'] = @"\zeta", ['η'] = @"\eta",
        ['θ'] = @"\theta", ['ι'] = @"\iota", ['κ'] = @"\kappa", ['λ'] = @"\lambda", ['μ'] = @"\mu", ['ν'] = @"\nu", ['ξ'] = @"\xi", ['ο'] = "o",
        ['π'] = @"\pi", ['ρ'] = @"\rho", ['σ'] = @"\sigma", ['ς'] = @"\varsigma", ['τ'] = @"\tau", ['υ'] = @"\upsilon", ['φ'] = @"\phi",
        ['χ'] = @"\chi", ['ψ'] = @"\psi", ['ω'] = @"\omega", ['Γ'] = @"\Gamma", ['Δ'] = @"\Delta", ['Θ'] = @"\Theta", ['Λ'] = @"\Lambda",
        ['Ξ'] = @"\Xi", ['Π'] = @"\Pi", ['Σ'] = @"\Sigma", ['Υ'] = @"\Upsilon", ['Φ'] = @"\Phi", ['Ψ'] = @"\Psi", ['Ω'] = @"\Omega",
        ['Α'] = "A", ['Β'] = "B", ['Ε'] = "E", ['Ζ'] = "Z", ['Η'] = "H", ['Ι'] = "I", ['Κ'] = "K", ['Μ'] = "M", ['Ν'] = "N", ['Ο'] = "O",
        ['Ρ'] = "P", ['Τ'] = "T", ['Χ'] = "X",
        ['½'] = @"\frac{1}{2}", ['⅓'] = @"\frac{1}{3}", ['¼'] = @"\frac{1}{4}", ['¾'] = @"\frac{3}{4}",
    };

    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻ⁿ";
    private const string Subscripts = "₀₁₂₃₄₅₆₇₈₉₊₋";
    private const string ScriptPlain = "0123456789+-n";

    /// <summary>
    /// Whether a formula is small and shallow enough to hand to XAML-Math, whose parser recurses for every group: a
    /// hostile answer with thousands of nested braces would otherwise overflow the stack and end the app. Too big or too
    /// deep, and the chat shows the LaTeX as written.
    /// </summary>
    public static bool Drawable(string latex)
    {
        if (latex.Length is 0 or > 1500) return false;
        var depth = 0;
        for (var k = 0; k < latex.Length; k++)
        {
            switch (latex[k])
            {
                case '\\': k++; break;
                case '{':
                    if (++depth > 24) return false;
                    break;
                case '}': depth = Math.Max(0, depth - 1); break;
            }
        }
        // Commands that read the next element as their own argument nest without braces ("\frac\frac\frac…").
        return NestingRx().Count(latex) <= 48;
    }

    [GeneratedRegex(@"\\(?:frac|dfrac|tfrac|cfrac|sqrt|left|overline|underline|vec|hat|bar|binom|text|mathrm|mathbf)(?![A-Za-z])")]
    private static partial Regex NestingRx();

    /// <summary>A formula the way XAML-Math reads it (what the model wrote is kept for copying; this is only for drawing).</summary>
    public static string Normalize(string latex)
    {
        var t = (latex ?? "").Trim();
        if (t.Length == 0) return t;
        // A line break at the very end ("x = 1 \\") would add an empty row.
        t = TrailingBreakRx().Replace(t, "").Trim();
        t = AlignRx().Replace(t, m => m.Groups[1].Value == "begin" ? @"\begin{align}" : @"\end{align}");
        t = WrapperRx().Replace(t, "");
        t = CasesRx().Replace(t, m => @"\cases{" + m.Groups["body"].Value.Trim() + "}");
        t = MatrixRx().Replace(t, m =>
        {
            var body = @"\matrix{" + m.Groups["body"].Value.Trim() + "}";
            return m.Groups["env"].Value switch
            {
                "bmatrix" => @"\left[" + body + @"\right]",
                "Bmatrix" => @"\left\{" + body + @"\right\}",
                "vmatrix" => @"\left|" + body + @"\right|",
                "Vmatrix" => @"\left\|" + body + @"\right\|",
                _ => body,
            };
        });
        t = DropWithArgumentRx().Replace(t, "");
        t = PmodRx().Replace(t, m => @"\;(\mathrm{mod}\;" + m.Groups[1].Value + ")");
        t = EscapeRx().Replace(t, m =>
        {
            if (m.Groups["name"].Success)
                return Commands.TryGetValue(m.Groups["name"].Value, out var instead)
                    // A replacement ending in a letter must not run into the next one ("\mapsto x" → "\rightarrow x").
                    ? instead.Length > 0 && char.IsLetter(instead[^1]) && m.Index + m.Length < t.Length && char.IsLetter(t[m.Index + m.Length]) ? instead + " " : instead
                    : m.Value;
            return m.Groups["other"].Value switch
            {
                "$" or "&" or "#" or "_" => @"\text{" + m.Groups["other"].Value + "}",
                " " => @"\;",
                _ => m.Value,
            };
        });
        return OutsideText(t, Symbols).Trim();
    }

    /// <summary>
    /// Unicode symbols as commands (a space after each, so "πr" stays two things), and runs of superscript or subscript
    /// characters as one group ("10⁻³" → "10^{-3}").
    /// </summary>
    private static string Symbols(string s)
    {
        if (!s.Any(c => c > 127)) return s;
        var sb = new StringBuilder(s.Length + 16);
        var k = 0;
        while (k < s.Length)
        {
            var c = s[k];
            var scripts = Superscripts.Contains(c) ? Superscripts : Subscripts.Contains(c) ? Subscripts : null;
            if (scripts is not null)
            {
                var run = new StringBuilder();
                for (; k < s.Length && scripts.IndexOf(s[k]) is var at && at >= 0; k++) run.Append(ScriptPlain[at]);
                sb.Append(scripts == Superscripts ? '^' : '_').Append(run.Length == 1 ? run.ToString() : "{" + run + "}");
                continue;
            }
            if (Unicode.TryGetValue(c, out var command))
            {
                sb.Append(command);
                if (command.Length > 0 && char.IsLetter(command[^1]) && k + 1 < s.Length && char.IsLetter(s[k + 1])) sb.Append(' ');
            }
            else sb.Append(c);
            k++;
        }
        return sb.ToString();
    }

    /// <summary>Applies <paramref name="change"/> outside \text{…} (drawn in the text font, where Unicode is fine).</summary>
    private static string OutsideText(string latex, Func<string, string> change)
    {
        var sb = new StringBuilder(latex.Length);
        var at = 0;
        var search = 0;
        while (true)
        {
            var open = latex.IndexOf(@"\text{", search, StringComparison.Ordinal);
            if (open < 0) break;
            var depth = 0;
            var close = -1;
            for (var k = open + 5; k < latex.Length; k++)
            {
                if (latex[k] == '\\') { k++; continue; }
                if (latex[k] == '{') depth++;
                else if (latex[k] == '}' && --depth == 0) { close = k; break; }
            }
            if (close < 0) break;
            sb.Append(change(latex[at..open])).Append(latex, open, close + 1 - open);
            at = search = close + 1;
        }
        return sb.Append(change(latex[at..])).ToString();
    }
}
