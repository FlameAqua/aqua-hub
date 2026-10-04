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

    // A \( or \[ formula can't hold another opener, so a line of unclosed ones is read once, not once from each.
    [GeneratedRegex(@"``.+?``|`[^`\n]+`|\$\$[^$\n]+?\$\$|(?<![\\$\w])\$(?![\s$])(?:[^$\\\n]|\\.)+?(?<![\s\\])\$(?![\d$])|\\\((?:[^\\\n]|\\[^()\n])+?\\\)|\\\[(?:[^\\\n]|\\[^\[\]\n])+?\\\]")]
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
    internal static readonly Dictionary<string, string> Commands = new(StringComparer.Ordinal)
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
        ["iint"] = @"\int\int", ["iiint"] = @"\int\int\int", ["textcolor"] = @"\color",
    };

    /// <summary>A backslash and what it escapes: a command name, or one other character ("\\", "\,", "\$").</summary>
    [GeneratedRegex(@"\\(?:(?<name>[A-Za-z]+)|(?<other>[^A-Za-z]))")]
    private static partial Regex EscapeRx();

    [GeneratedRegex(@"\\(?:tag|label|hspace|vspace|phantom|hphantom|vphantom)\*?\s*\{[^{}]*\}")]
    private static partial Regex DropWithArgumentRx();

    [GeneratedRegex(@"\\pmod\s*\{([^{}]*)\}")]
    private static partial Regex PmodRx();

    // XAML-Math can't stack one thing over another: what goes above or below is set as a script instead.
    [GeneratedRegex(@"\\(?<cmd>overset|stackrel|underset)\s*\{(?<a>(?:[^{}]|\{[^{}]*\})*)\}\s*\{(?<b>(?:[^{}]|\{[^{}]*\})*)\}")]
    private static partial Regex StackRx();

    [GeneratedRegex(@"\\x(?<dir>right|left)arrow\s*(?:\[[^\]]*\])?\s*\{(?<a>(?:[^{}]|\{[^{}]*\})*)\}")]
    private static partial Regex LabelledArrowRx();

    // In LaTeX "%" starts a comment; in a model's formula it's a percentage ("$50%$").
    [GeneratedRegex(@"(?<!\\)%")]
    private static partial Regex PercentRx();

    [GeneratedRegex(@"\\(begin|end)\{(?:aligned|align\*|alignat\*?|eqnarray\*?|split|gathered|gather\*?|multline\*?)\}(?:\{\d+\})?")]
    private static partial Regex AlignRx();

    [GeneratedRegex(@"\\(?:begin|end)\{(?:equation\*?|displaymath|math)\}")]
    private static partial Regex WrapperRx();

    [GeneratedRegex(@"\\begin\{cases\}(?<body>.*?)\\end\{cases\}", RegexOptions.Singleline)]
    private static partial Regex CasesRx();

    // A column break (not an escaped "\&").
    [GeneratedRegex(@"(?<!\\)&\s*")]
    private static partial Regex ColumnRx();

    [GeneratedRegex(@"\\begin\{(?<env>matrix|pmatrix|bmatrix|Bmatrix|vmatrix|Vmatrix|smallmatrix|array)\}(?:\{[^{}]*\})?(?<body>.*?)\\end\{\k<env>\}", RegexOptions.Singleline)]
    private static partial Regex MatrixRx();

    [GeneratedRegex(@"\\text\{([^{}]*)\}")]
    private static partial Regex TextGroupRx();

    /// <summary>Words that read the same set upright in maths (no apostrophe: in maths it's a prime).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9 ,.()/+\-]+$")]
    private static partial Regex PlainWordsRx();

    /// <summary>Symbols models type as Unicode inside a formula (XAML-Math's fonts only have its own commands).</summary>
    internal static readonly Dictionary<char, string> Unicode = new()
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

    /// <summary>The longest formula XAML-Math is given.</summary>
    public const int MaxLength = 1500;
    /// <summary>How deep its braces may nest.</summary>
    public const int MaxDepth = 24;
    /// <summary>How many commands that read the next element as their own argument it may have ("\frac\frac\frac…").</summary>
    public const int MaxNesting = 48;
    /// <summary>How many line breaks (\\) it may have: outside an environment XAML-Math parses the rest of the formula again for each one, a level deeper.</summary>
    public const int MaxBreaks = 100;

    /// <summary>
    /// Whether a formula is small and shallow enough to hand to XAML-Math, whose parser recurses for every group and
    /// every line break: a hostile answer with hundreds of either would otherwise overflow the stack, which ends the app
    /// (nothing can catch it). Every limit at once still parses in a quarter of the UI thread's stack (the unit tests
    /// check it). Too big or too deep, and the chat shows the LaTeX as written.
    /// </summary>
    public static bool Drawable(string latex)
    {
        if (latex.Length is 0 or > MaxLength) return false;
        var depth = 0;
        var breaks = 0;
        for (var k = 0; k < latex.Length; k++)
        {
            switch (latex[k])
            {
                // "\\" is a line break; any other backslash escapes the character after it ("\{" isn't a brace).
                case '\\':
                    if (++k < latex.Length && latex[k] == '\\' && ++breaks > MaxBreaks) return false;
                    break;
                case '{':
                    if (++depth > MaxDepth) return false;
                    break;
                case '}': depth = Math.Max(0, depth - 1); break;
            }
        }
        return NestingRx().Count(latex) <= MaxNesting;
    }

    /// <summary>
    /// The formula as XAML-Math should get it (<see cref="Normalize"/>), or null when it shouldn't try
    /// (<see cref="Drawable"/>). Anything far over the limit is turned away before it's rewritten.
    /// </summary>
    public static string? ForDrawing(string latex)
    {
        if (string.IsNullOrEmpty(latex) || latex.Length > 2 * MaxLength) return null;
        var tex = Normalize(latex);
        return Drawable(tex) ? tex : null;
    }

    [GeneratedRegex(@"\\(?:frac|dfrac|tfrac|cfrac|sqrt|left|overline|underline|vec|hat|bar|binom|text|mathrm|mathbf)(?![A-Za-z])")]
    private static partial Regex NestingRx();

    // ───────────────────────────── Formulas in words ─────────────────────────────

    private static readonly Dictionary<string, string> SpokenCommands = new(StringComparer.Ordinal)
    {
        ["times"] = "times", ["cdot"] = "times", ["div"] = "divided by", ["pm"] = "plus or minus", ["mp"] = "minus or plus",
        ["le"] = "is at most", ["leq"] = "is at most", ["ge"] = "is at least", ["geq"] = "is at least", ["neq"] = "is not", ["ne"] = "is not",
        ["approx"] = "is about", ["sim"] = "is about", ["equiv"] = "is equivalent to", ["propto"] = "is proportional to", ["infty"] = "infinity",
        ["rightarrow"] = "to", ["to"] = "to", ["leftarrow"] = "from", ["Rightarrow"] = "implies", ["Leftrightarrow"] = "if and only if",
        ["in"] = "in", ["subset"] = "is a subset of", ["cup"] = "union", ["cap"] = "intersection", ["forall"] = "for all", ["exists"] = "there is",
        ["partial"] = "partial", ["nabla"] = "del", ["circ"] = "degrees", ["%"] = "percent", ["ldots"] = "and so on", ["cdots"] = "and so on",
        ["sin"] = "sine", ["cos"] = "cosine", ["tan"] = "tangent", ["log"] = "log", ["ln"] = "natural log", ["exp"] = "e to the",
        ["max"] = "the maximum of", ["min"] = "the minimum of", ["Delta"] = "delta", ["mu"] = "mu", ["pi"] = "pi",
    };

    // Commands that take what follows as limits: "\sum_{i=1}^{n}" is "the sum from i equals 1 to n".
    private static readonly Dictionary<string, string> SpokenLimits = new(StringComparer.Ordinal)
    {
        ["sum"] = "the sum", ["prod"] = "the product", ["int"] = "the integral", ["lim"] = "the limit",
    };

    [GeneratedRegex(@"\\(?:[A-Za-z]+|.)")]
    private static partial Regex SpokenNameRx();

    /// <summary>
    /// A formula in words, for reading an answer aloud: "\frac{a}{b}" is "a over b", "x^2" "x squared", "\sqrt{x}" "the
    /// square root of x", "\sum_{i=1}^{n}" "the sum from i equals 1 to n". A matrix, cases or anything long is "a formula".
    /// </summary>
    public static string Spoken(string latex)
    {
        var tex = Normalize(latex);
        if (tex.Length == 0) return "";
        if (tex.Length > 160 || tex.Contains(@"\matrix", StringComparison.Ordinal) || tex.Contains(@"\cases", StringComparison.Ordinal) || tex.Contains(@"\begin", StringComparison.Ordinal))
            return "a formula";
        var words = Regex.Replace(Speak(tex, 0), @"\s+", " ").Trim();
        return words.Length == 0 ? "a formula" : words;
    }

    private static string Speak(string t, int depth)
    {
        if (depth > 8) return " ";
        var sb = new StringBuilder();
        var k = 0;
        while (k < t.Length)
        {
            var c = t[k];
            if (c == '\\')
            {
                var m = SpokenNameRx().Match(t, k);
                var name = m.Value[1..];
                k += m.Length;
                switch (name)
                {
                    case "frac":
                    {
                        var (a, k1) = Group(t, k);
                        var (b, k2) = Group(t, k1);
                        sb.Append(' ').Append(Speak(a, depth + 1)).Append(" over ").Append(Speak(b, depth + 1)).Append(' ');
                        k = k2;
                        continue;
                    }
                    case "sqrt":
                    {
                        var root = "";
                        if (k < t.Length && t[k] == '[' && t.IndexOf(']', k) is var close and > 0) { root = t[(k + 1)..close]; k = close + 1; }
                        var (a, k1) = Group(t, k);
                        var which = Speak(root, depth + 1).Trim() switch { "" or "2" => "square", "3" => "cube", var n => n + "th" };
                        sb.Append($" the {which} root of ").Append(Speak(a, depth + 1)).Append(' ');
                        k = k1;
                        continue;
                    }
                    case "text" or "mathrm" or "mathcal" or "operatorname":
                    {
                        var (a, k1) = Group(t, k);
                        sb.Append(' ').Append(a).Append(' ');
                        k = k1;
                        continue;
                    }
                }
                if (SpokenLimits.TryGetValue(name, out var limit))
                {
                    sb.Append(' ').Append(limit);
                    for (var n = 0; n < 2 && k < t.Length && t[k] is '_' or '^'; n++)
                    {
                        var below = t[k] == '_';
                        var (a, k1) = Group(t, k + 1);
                        sb.Append(below ? (name == "lim" ? " as " : " from ") : " to ").Append(Speak(a, depth + 1));
                        k = k1;
                    }
                    sb.Append(" of ");
                    continue;
                }
                sb.Append(' ').Append(SpokenCommands.TryGetValue(name, out var word) ? word : name.Length > 1 && char.IsLetter(name[0]) ? name : "").Append(' ');
                continue;
            }
            if (c is '^' or '_')
            {
                var (a, k1) = Group(t, k + 1);
                var said = Speak(a, depth + 1).Trim();
                sb.Append(c == '_' ? " sub " + said + " " : said switch { "2" => " squared ", "3" => " cubed ", "degrees" => " degrees ", _ => " to the power of " + said + " " });
                k = k1;
                continue;
            }
            sb.Append(c switch
            {
                '=' => " equals ", '+' => " plus ", '-' => " minus ", '<' => " is less than ", '>' => " is greater than ", '/' => " over ",
                '{' or '}' or '(' or ')' or '[' or ']' or '|' or '&' or '~' => " ",
                _ => c.ToString(),
            });
            k++;
        }
        return sb.ToString();
    }

    /// <summary>The argument at <paramref name="k"/> — a {…} group's inside, a command or one character — and where it ends.</summary>
    private static (string Inside, int End) Group(string t, int k)
    {
        while (k < t.Length && t[k] == ' ') k++;
        if (k >= t.Length) return ("", k);
        if (t[k] == '{' && Close(t, k) is var end and > 0) return (t[(k + 1)..end], end + 1);
        if (t[k] == '\\') { var m = SpokenNameRx().Match(t, k); return (m.Value, k + m.Length); }
        return (t[k].ToString(), k + 1);
    }

    /// <summary>A formula the way XAML-Math reads it (what the model wrote is kept for copying; this is only for drawing).</summary>
    public static string Normalize(string latex)
    {
        var t = (latex ?? "").Trim();
        if (t.Length == 0) return t;
        // A line break at the very end ("x = 1 \\") would add an empty row.
        t = WithoutTrailingBreaks(t);
        t = AlignRx().Replace(t, m => m.Groups[1].Value == "begin" ? @"\begin{align}" : @"\end{align}");
        t = WrapperRx().Replace(t, "");
        // amsmath sets a quad between a value and its condition; XAML-Math's \cases puts them side by side.
        t = CasesRx().Replace(t, m => @"\cases{" + ColumnRx().Replace(m.Groups["body"].Value.Trim(), @"&\quad ") + "}");
        t = MatrixRx().Replace(t, m =>
        {
            var body = @"\matrix{" + m.Groups["body"].Value.Trim() + "}";
            return m.Groups["env"].Value switch
            {
                // XAML-Math draws its own pmatrix with square brackets.
                "pmatrix" => @"\left(" + body + @"\right)",
                "bmatrix" => @"\left[" + body + @"\right]",
                "Bmatrix" => @"\left\{" + body + @"\right\}",
                "vmatrix" => @"\left|" + body + @"\right|",
                "Vmatrix" => @"\left\|" + body + @"\right\|",
                _ => body,
            };
        });
        t = DropWithArgumentRx().Replace(t, "");
        t = PmodRx().Replace(t, m => @"\;(\mathrm{mod}\;" + m.Groups[1].Value + ")");
        t = StackRx().Replace(t, m => "{" + m.Groups["b"].Value + "}" + (m.Groups["cmd"].Value == "underset" ? "_{" : "^{") + m.Groups["a"].Value + "}");
        t = LabelledArrowRx().Replace(t, m => @"\" + m.Groups["dir"].Value + "arrow^{" + m.Groups["a"].Value + "}");
        t = PercentRx().Replace(t, @"\%");
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
        t = ScriptText(t);
        return OutsideText(t, Symbols).Trim();
    }

    /// <summary>
    /// \text in a subscript or superscript ("Q_{\text{out}}", "x^\text{2nd}") as \mathrm, which XAML-Math sizes as a
    /// script: it draws \text there at full size and below the line, and can't read an unbraced one at all. Only plain
    /// words are rewritten; other text gets the braces it needs and stays \text.
    /// </summary>
    private static string ScriptText(string t)
    {
        if (!t.Contains(@"\text{", StringComparison.Ordinal)) return t;
        var sb = new StringBuilder(t.Length + 16);
        var k = 0;
        while (k < t.Length)
        {
            var c = t[k];
            if (c == '\\' && k + 1 < t.Length)
            {
                sb.Append(c).Append(t[k + 1]);
                k += 2;
                continue;
            }
            if (c is '_' or '^')
            {
                var j = k + 1;
                while (j < t.Length && t[j] == ' ') j++;
                // x_\text{out}: the whole \text{…} is the script.
                if (t.AsSpan(j).StartsWith(@"\text{", StringComparison.Ordinal) && Close(t, j + 5) is var end and > 0)
                {
                    sb.Append(c).Append('{').Append(Upright(t[(j + 6)..end]) ?? t[j..(end + 1)]).Append('}');
                    k = end + 1;
                    continue;
                }
                // x_{\text{out},1}: each \text{…} in the group.
                if (j < t.Length && t[j] == '{' && Close(t, j) is var close and > 0)
                {
                    sb.Append(c).Append(TextGroupRx().Replace(t[j..(close + 1)], m => Upright(m.Groups[1].Value) ?? m.Value));
                    k = close + 1;
                    continue;
                }
            }
            sb.Append(c);
            k++;
        }
        return sb.ToString();
    }

    /// <summary>Plain words as \mathrm, their spaces kept as thin spaces (null for anything else).</summary>
    private static string? Upright(string words) =>
        words.Length > 0 && PlainWordsRx().IsMatch(words) ? @"\mathrm{" + words.Replace(" ", @"\,") + "}" : null;

    /// <summary>The formula without the line breaks and spaces at its end (a loop: a pattern would read a long run once from every break in it).</summary>
    private static string WithoutTrailingBreaks(string t)
    {
        var end = t.Length;
        while (true)
        {
            while (end > 0 && char.IsWhiteSpace(t[end - 1])) end--;
            if (end < 2 || t[end - 1] != '\\' || t[end - 2] != '\\') return t[..end];
            end -= 2;
        }
    }

    /// <summary>The index of the brace that closes the one at <paramref name="open"/> (-1 if it never closes).</summary>
    private static int Close(string t, int open)
    {
        var depth = 0;
        for (var k = open; k < t.Length; k++)
        {
            switch (t[k])
            {
                case '\\': k++; break;
                case '{': depth++; break;
                case '}' when --depth == 0: return k;
            }
        }
        return -1;
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
