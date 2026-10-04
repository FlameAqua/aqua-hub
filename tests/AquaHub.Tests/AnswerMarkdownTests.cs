using System.Reflection;
using AquaHub.Core.Ai.Assistant;
using XamlMath;
using XamlMath.Colors;
using XamlMath.Rendering;

namespace AquaHub.Tests;

/// <summary>Phase 3.1: the Markdown answers are drawn from — tables, maths, headings, quotes and nested lists.</summary>
public class AnswerMarkdownTests
{
    private static T Single<T>(string markdown) where T : MdBlock => Assert.IsType<T>(Assert.Single(AnswerMarkdown.Parse(markdown)));

    [Fact]
    public void ATableHasItsHeaderAlignmentAndRows()
    {
        var table = Single<MdTable>("| Plan | Price | Notes |\n|:-----|------:|:-----:|\n| Basic | €5 | `a|b` |\n| Pro | €12 |");
        Assert.Equal(new[] { "Plan", "Price", "Notes" }, table.Header);
        Assert.Equal(new[] { MdAlign.Left, MdAlign.Right, MdAlign.Center }, table.Align);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(new[] { "Basic", "€5", "`a|b`" }, table.Rows[0]);   // a pipe inside code doesn't split the cell
        Assert.Equal(new[] { "Pro", "€12", "" }, table.Rows[1]);         // a short row is padded
        Assert.StartsWith("| Plan | Price | Notes |", table.Source);       // copying gives the Markdown back
    }

    [Fact]
    public void PricesSplitCellsButMathsAndEscapedPipesDont()
    {
        var table = Single<MdTable>("| a | b | c |\n|---|---|---|\n| $5 | $|x|$ | 1 \\| 2 |");
        Assert.Equal(new[] { "$5", "$|x|$", "1 | 2" }, table.Rows[0]);
        var prices = Single<MdTable>("| Item | Cost |\n|---|---|\n| Tea | $5 | \n| Cake | $10 |");
        Assert.Equal(new[] { "Tea", "$5" }, prices.Rows[0]);
    }

    [Fact]
    public void ATableCanFollowAParagraphAndBreaksInCellsAreKept()
    {
        var blocks = AnswerMarkdown.Parse("Here's how they compare:\n| A | B |\n|---|---|\n| one<br>two | x |\n\nThat's all.");
        Assert.Equal(3, blocks.Count);
        Assert.IsType<MdParagraph>(blocks[0]);
        Assert.Equal("one\ntwo", Assert.IsType<MdTable>(blocks[1]).Rows[0][0]);
        Assert.IsType<MdParagraph>(blocks[2]);
    }

    [Fact]
    public void AHalfStreamedTableIsTextUntilItsDividerArrives()
    {
        Assert.IsType<MdParagraph>(Assert.Single(AnswerMarkdown.Parse("| A | B |")));
        Assert.IsType<MdParagraph>(Assert.Single(AnswerMarkdown.Parse("| A | B |\n|---")));
        Assert.IsType<MdTable>(Assert.Single(AnswerMarkdown.Parse("| A | B |\n|---|---|")));
        // A divider with the wrong number of cells isn't a table.
        Assert.DoesNotContain(AnswerMarkdown.Parse("| A | B |\n|---|"), b => b is MdTable);
    }

    [Fact]
    public void DisplayFormulasInEveryForm()
    {
        Assert.Equal(@"\frac{a}{b}", Single<MdMath>("$$\\frac{a}{b}$$").Latex);
        Assert.Equal("x^2 + y^2\n= z^2", Single<MdMath>("$$\nx^2 + y^2\n= z^2\n$$").Latex);
        Assert.Equal(@"E = mc^2", Single<MdMath>(@"\[ E = mc^2 \]").Latex);
        var env = Single<MdMath>("\\begin{aligned}\na &= 1 \\\\\nb &= 2\n\\end{aligned}");
        Assert.StartsWith(@"\begin{aligned}", env.Latex);
        Assert.EndsWith(@"\end{aligned}", env.Latex);
        Assert.True(env.Closed);
    }

    [Fact]
    public void AFormulaStillStreamingInIsMarkedOpenAndABlankLineEndsIt()
    {
        var open = Single<MdMath>("$$\n\\frac{a}{");
        Assert.False(open.Closed);
        var blocks = AnswerMarkdown.Parse("$$\nx = 1\n\nThe rest of the answer.");
        Assert.False(Assert.IsType<MdMath>(blocks[0]).Closed);
        Assert.IsType<MdParagraph>(blocks[1]);
    }

    [Fact]
    public void ADisplayPairInsideASentenceIsInlineMaths()
    {
        var p = Single<MdParagraph>("$$x^2$$ is the area of the square.");
        var pieces = AnswerMarkdown.Inlines(p.Lines[0]);
        Assert.Equal(MdSpan.Math, pieces[0].Kind);
        Assert.Equal("x^2", pieces[0].Text);
    }

    [Fact]
    public void InlineMathsButNotPrices()
    {
        var pieces = AnswerMarkdown.Inlines("Einstein's $E = mc^2$ and \\(a^2\\) cost $5 and $10, or US$5 and US$6 — \\$3 too.");
        var maths = pieces.Where(p => p.Kind == MdSpan.Math).ToList();
        Assert.Equal(new[] { "E = mc^2", "a^2" }, maths.Select(m => m.Text));
        Assert.Equal("$E = mc^2$", maths[0].Target); // what was written, for copying
        var text = string.Concat(pieces.Where(p => p.Kind == MdSpan.Text).Select(p => p.Text));
        Assert.Contains("cost $5 and $10, or US$5 and US$6 — $3 too.", text);
        Assert.DoesNotContain(AnswerMarkdown.Inlines("between $5-$10 or $1.50 to $2.00"), p => p.Kind == MdSpan.Math);
    }

    [Fact]
    public void HeadingsByLevelButNotHashtags()
    {
        Assert.Equal((1, "Title"), (Single<MdHeading>("# Title").Level, Single<MdHeading>("# Title").Text));
        Assert.Equal(3, Single<MdHeading>("### Key findings ###").Level);
        Assert.IsType<MdParagraph>(Assert.Single(AnswerMarkdown.Parse("#hashtag isn't a heading")));
    }

    [Fact]
    public void NestedListsByIndentation()
    {
        var list = Single<MdList>("- Fruit\n  - Apples\n  - Pears\n    1. Conference\n    2. Comice\n- Veg");
        Assert.False(list.Ordered);
        Assert.Equal(2, list.Items.Count);
        var fruit = list.Items[0].Blocks;
        Assert.Equal("Fruit", Assert.IsType<MdParagraph>(fruit[0]).Lines[0]);
        var inner = Assert.IsType<MdList>(fruit[1]);
        Assert.Equal(2, inner.Items.Count);
        var pears = Assert.IsType<MdList>(inner.Items[1].Blocks[1]);
        Assert.True(pears.Ordered);
        Assert.Equal(2, pears.Items.Count);
    }

    [Fact]
    public void AnOrderedItemKeepsItsIndentedLinesAndNumberingCarriesOn()
    {
        var list = Single<MdList>("1. **Step one**\n   Do this first.\n\n2. **Step two**\n   - a detail\n\n3. Done");
        Assert.True(list.Ordered);
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(new[] { "**Step one**", "Do this first." }, Assert.IsType<MdParagraph>(list.Items[0].Blocks[0]).Lines);
        Assert.IsType<MdList>(list.Items[1].Blocks[1]);
        // A list that starts later keeps its number.
        var blocks = AnswerMarkdown.Parse("1. a\n\nA paragraph.\n\n2. b");
        Assert.Equal(2, Assert.IsType<MdList>(blocks[2]).Start);
    }

    [Fact]
    public void YearsAndNegativeNumbersAreNotListItems()
    {
        Assert.IsType<MdParagraph>(Assert.Single(AnswerMarkdown.Parse("2026. That was the year.")));
        Assert.IsType<MdParagraph>(Assert.Single(AnswerMarkdown.Parse("-5 degrees tonight.")));
    }

    [Fact]
    public void TaskItems()
    {
        var list = Single<MdList>("- [ ] Book the plumber\n- [x] Pay the deposit");
        Assert.Equal(new bool?[] { false, true }, list.Items.Select(i => i.Checked));
        Assert.Equal("Book the plumber", Assert.IsType<MdParagraph>(list.Items[0].Blocks[0]).Lines[0]);
    }

    [Fact]
    public void QuotesHoldBlocksAndNest()
    {
        var quote = Single<MdQuote>("> Said the minister:\n> - one\n> - two\n>> and more");
        Assert.IsType<MdParagraph>(quote.Blocks[0]);
        Assert.IsType<MdList>(quote.Blocks[1]);
        Assert.IsType<MdQuote>(quote.Blocks[2]);
    }

    [Fact]
    public void RulesAndCodeFences()
    {
        Assert.IsType<MdRule>(Assert.Single(AnswerMarkdown.Parse("---")));
        Assert.IsType<MdRule>(Assert.Single(AnswerMarkdown.Parse("* * *")));
        var code = Single<MdCode>("```python\nprint('```python')\n```");
        Assert.Equal("python", code.Language);
        Assert.Equal(new[] { "print('```python')" }, code.Lines);
        Assert.True(code.Closed);
        Assert.False(Single<MdCode>("```\nstill typing").Closed);
    }

    [Fact]
    public void EmphasisNestsAndWordsWithUnderscoresStayWhole()
    {
        var pieces = AnswerMarkdown.Inlines("**Area $\\pi r^2$ with `r`** and *slant* _lean_ ~~gone~~ ***both*** my_logo5.png snake_case_name");
        var bold = pieces[0];
        Assert.Equal(MdSpan.Bold, bold.Kind);
        Assert.Contains(bold.Children!, c => c.Kind == MdSpan.Math && c.Text == @"\pi r^2");
        Assert.Contains(bold.Children!, c => c.Kind == MdSpan.Code && c.Text == "r");
        Assert.Contains(pieces, p => p.Kind == MdSpan.Italic && p.Text == "slant");
        Assert.Contains(pieces, p => p.Kind == MdSpan.Italic && p.Text == "lean");
        Assert.Contains(pieces, p => p.Kind == MdSpan.Strike && p.Text == "gone");
        Assert.Contains(pieces, p => p.Kind == MdSpan.BoldItalic && p.Text == "both");
        Assert.EndsWith("my_logo5.png snake_case_name", pieces[^1].Text);
    }

    [Fact]
    public void LinksCitationsAddressesAndPaths()
    {
        var pieces = AnswerMarkdown.Inlines(@"See [the report](https://example.org/a_b_c) [3], https://example.org/x_y_z. and C:\Users\me\notes.txt.");
        Assert.Contains(pieces, p => p.Kind == MdSpan.Link && p.Text == "the report" && p.Target == "https://example.org/a_b_c");
        Assert.Contains(pieces, p => p.Kind == MdSpan.Citation && p.Number == 3);
        Assert.Contains(pieces, p => p.Kind == MdSpan.Url && p.Target == "https://example.org/x_y_z");
        Assert.Contains(pieces, p => p.Kind == MdSpan.Path && p.Target == @"C:\Users\me\notes.txt");
        Assert.DoesNotContain(pieces, p => p.Kind == MdSpan.Italic);
    }

    [Fact]
    public void HostileNestingCantExhaustTheStack()
    {
        var quotes = string.Concat(Enumerable.Repeat(">", 20000)) + " deep";
        Assert.NotEmpty(AnswerMarkdown.Parse(quotes));
        var lists = string.Join("\n", Enumerable.Range(0, 3000).Select(i => new string(' ', i * 2) + "- item"));
        Assert.NotEmpty(AnswerMarkdown.Parse(lists));
        var stars = string.Concat(Enumerable.Repeat("**a *b ~~c ", 2000));
        Assert.NotEmpty(AnswerMarkdown.Inlines(stars));
    }

    // Emphasis nests four deep at most — each kind's text excludes its own marker, so _ ⊃ ~~ ⊃ ** ⊃ * is the longest
    // chain — and the depth guard stands behind that: whatever the input, the spans stay a few levels deep.
    [Fact]
    public void EmphasisNestsAFewLevelsAtMost()
    {
        static int Depth(IReadOnlyList<MdInline> spans) => spans.Count == 0 ? 0 : spans.Max(s => 1 + (s.Children is { } c ? Depth(c) : 0));
        var chain = AnswerMarkdown.Inlines("_a ~~b **c *d* c** b~~ a_");
        Assert.Equal(5, Depth(chain)); // four kinds of emphasis and the text inside
        var italic = chain.Single().Children!.Single(s => s.Kind == MdSpan.Strike).Children!
            .Single(s => s.Kind == MdSpan.Bold).Children!.Single(s => s.Kind == MdSpan.Italic);
        Assert.Equal("d", italic.Text);

        var hostile = string.Concat(Enumerable.Repeat("_~~***", 400)) + "x" + string.Concat(Enumerable.Repeat("***~~_", 400));
        Assert.InRange(Depth(AnswerMarkdown.Inlines(hostile)), 1, 6);
        Assert.Contains("x", AnswerMarkdown.Plain(hostile));
    }

    [Fact]
    public void PlainTextOfALine() => Assert.Equal("Speed is d/t (see 1)", AnswerMarkdown.Plain("**Speed** is $d/t$ (see `1`)"));

    [Fact]
    public void ALinkMayHoldBracketsAndACitationCanLink()
    {
        var wiki = AnswerMarkdown.Inlines("See [Mercury](https://en.wikipedia.org/wiki/Mercury_(planet)) now.");
        Assert.Contains(wiki, p => p.Kind == MdSpan.Link && p.Target == "https://en.wikipedia.org/wiki/Mercury_(planet)");
        Assert.Equal(" now.", wiki[^1].Text);
        var cited = AnswerMarkdown.Inlines("As reported [[1]](https://example.org/a).");
        Assert.Contains(cited, p => p.Kind == MdSpan.Link && p.Text == "[1]" && p.Target == "https://example.org/a");
        Assert.DoesNotContain(cited, p => p.Kind == MdSpan.Url);
        var file = AnswerMarkdown.Inlines(@"[setup](C:\Program Files (x86)\App\setup.log)");
        Assert.Contains(file, p => p.Kind == MdSpan.Link && p.Target == @"C:\Program Files (x86)\App\setup.log");
    }

    [Fact]
    public void ANetworkPathKeepsItsBackslashesButEscapesStillWork()
    {
        Assert.Equal(@"Copy it to \\server\share\f.txt now", AnswerMarkdown.Plain(@"Copy it to \\server\share\f.txt now"));
        Assert.Equal("*not italic* and _this_ and #1", AnswerMarkdown.Plain(@"\*not italic\* and \_this\_ and \#1"));
    }

    [Fact]
    public void AnEnvironmentClosedUnderAnotherNameOrNeverClosedDoesntSwallowTheAnswer()
    {
        var blocks = AnswerMarkdown.Parse("\\begin{align*}\nx &= 1 \\\\\ny &= 2\n\\end{align}\nSo x is 1.\n\n- and a list");
        var math = Assert.IsType<MdMath>(blocks[0]);
        Assert.True(math.Closed);
        Assert.EndsWith(@"\end{align}", math.Latex);
        Assert.IsType<MdParagraph>(blocks[1]);
        Assert.IsType<MdList>(blocks[2]);

        var nested = Assert.IsType<MdMath>(AnswerMarkdown.Parse("\\begin{equation}\n\\begin{cases} 1 & x \\end{cases}\n\\end{equation}")[0]);
        Assert.True(nested.Closed);
        Assert.EndsWith(@"\end{equation}", nested.Latex);

        var open = AnswerMarkdown.Parse("\\begin{align}\nx &= 1\n\nThe rest of the answer.\n\nMore.");
        Assert.False(Assert.IsType<MdMath>(open[0]).Closed);
        Assert.Equal(3, open.Count);
    }

    [Fact]
    public void WordsAfterAnEnvironmentAreAParagraphOfTheirOwn()
    {
        var blocks = AnswerMarkdown.Parse(@"\begin{equation} E = mc^2 \end{equation} where c is the speed of light.");
        var math = Assert.IsType<MdMath>(blocks[0]);
        Assert.Equal(@"\begin{equation} E = mc^2 \end{equation}", math.Latex);
        Assert.Equal(math.Latex, math.Source);
        Assert.Equal("where c is the speed of light.", Assert.Single(Assert.IsType<MdParagraph>(blocks[1]).Lines));
        // A full stop after the \end stays with the formula.
        Assert.Single(AnswerMarkdown.Parse(@"\begin{equation} E = mc^2 \end{equation}."));
    }

    [Fact]
    public void TripleBackticksInALineAreCodeAndABreakTagBreaksTheLine()
    {
        var code = AnswerMarkdown.Inlines("Run ```npm install``` first.");
        Assert.Contains(code, p => p.Kind == MdSpan.Code && p.Text == "npm install");
        Assert.DoesNotContain(code, p => p.Text.Contains('`'));
        var lines = AnswerMarkdown.Inlines("line one<br>line two<BR/>three");
        Assert.Equal(2, lines.Count(p => p.Kind == MdSpan.Break));
        Assert.Equal("line one line two three", AnswerMarkdown.Plain("line one<br>line two<BR/>three"));
    }

    [Fact]
    public void AnEscapedPipeIsAPipeInCodeInATableButNotInMaths()
    {
        var table = Single<MdTable>("| op | norm |\n|---|---|\n| `a\\|b` | $\\|v\\|$ |");
        Assert.Equal(new[] { "`a|b`", @"$\|v\|$" }, table.Rows[0]);
    }

    /// <summary>
    /// The chat parses every line on the UI thread, again for each streamed chunk: a line of thousands of openers that
    /// never close must be read once, not once from each. Each case also checks the line was really parsed (the
    /// regex's timeout would return it whole, as plain text, and hide a slow pattern).
    /// </summary>
    [Fact]
    public void LinesOfUnclosedOpenersAreReadInLinearTime()
    {
        const int n = 20000;
        static string Repeat(string s, int count) => string.Concat(Enumerable.Repeat(s, count));
        void Fast(string what, Action read)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            read();
            Assert.True(clock.ElapsedMilliseconds < 1000, $"{what}: {clock.ElapsedMilliseconds} ms");
        }
        Fast(@"\(", () => Assert.Equal(new string('(', n), Assert.Single(AnswerMarkdown.Inlines(Repeat(@"\(", n))).Text));
        Fast(@"\[", () => Assert.Equal(new string('[', n), Assert.Single(AnswerMarkdown.Inlines(Repeat(@"\[", n))).Text));
        Fast("[a](https://", () => Assert.Equal(n, AnswerMarkdown.Inlines(Repeat("[a](https://x.y/", n)).Count(p => p.Kind == MdSpan.Url)));
        Fast("[a](C:\\", () => Assert.Equal(n, AnswerMarkdown.Inlines(Repeat(@"[a](C:\x ", n)).Count(p => p.Kind == MdSpan.Path)));
        Fast("a divider that isn't", () => Assert.IsType<MdParagraph>(Assert.Single(AnswerMarkdown.Parse("a | b\n" + new string(' ', n) + "|x"))));
        Fast("tidying", () => Assert.NotEmpty(AnswerText.Tidy(Repeat(@"\(", n))));
        Fast("checking links", () => Assert.NotEmpty(AnswerText.RepairLinks(Repeat("[a](https://x.y/", n), new Dictionary<string, string> { ["https://x.y/b"] = "B" }, out _)));
    }
}

public class MathTextTests
{
    [Fact]
    public void CodeAndFormulasAreProtected()
    {
        const string text = "Use `a \\times b` here.\n```\nx \\to y\n```\n$$\n\\alpha \\times \\beta\n$$\nThen $a \\times b$ and 2 \\times 3.";
        var tidied = AnswerText.Tidy(text);
        Assert.Contains("`a \\times b`", tidied);          // code
        Assert.Contains("x \\to y", tidied);                // fenced code
        Assert.Contains("\\alpha \\times \\beta", tidied);  // display formula
        Assert.Contains("$a \\times b$", tidied);           // inline formula
        Assert.EndsWith("2 × 3.", tidied);                 // a stray command in the text still becomes the symbol
    }

    [Fact]
    public void ASingleSymbolStillBecomesTheSymbolButAFormulaKeepsItsDollars()
    {
        Assert.Equal("a → b", AnswerText.Tidy(@"a $\rightarrow$ b"));
        Assert.Equal(@"Let $\alpha$ be the angle", AnswerText.Tidy(@"Let $\alpha$ be the angle"));
        Assert.Equal(@"Area $= \pi r^2$", AnswerText.Tidy(@"Area $= \pi r^2$"));
    }

    [Fact]
    public void OutsideOnlyTouchesUnprotectedText()
    {
        var upper = MathText.Outside("abc `def` $g^2$ hij", maths: true, s => s.ToUpperInvariant());
        Assert.Equal("ABC `def` $g^2$ HIJ", upper);
        Assert.Equal("ABC `def` $G^2$ HIJ", MathText.Outside("abc `def` $g^2$ hij", maths: false, s => s.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(@"\dfrac{a}{b}", @"\frac{a}{b}")]
    [InlineData(@"\mathbf{F} = m\mathbf{a}", @"\mathrm{F} = m\mathrm{a}")]
    [InlineData(@"\begin{cases} 1 & x > 0 \\ 0 & \text{otherwise} \end{cases}", @"\cases{1 &\;\;\;\; x > 0 \\ 0 &\;\;\;\; \text{otherwise}}")] // \quad, as XAML-Math knows it
    [InlineData(@"\begin{aligned} x &= 1 \\ y &= 2 \end{aligned}", @"\begin{align} x &= 1 \\ y &= 2 \end{align}")]
    [InlineData(@"\begin{bmatrix} a & b \\ c & d \end{bmatrix}", @"\left[\matrix{a & b \\ c & d}\right]")]
    [InlineData("πr² ≈ 3 × 4", @"\pi r^2 \approx 3 \times 4")]
    [InlineData("x₁ + ½", @"x_1 + \frac{1}{2}")]
    [InlineData("10⁻³ m and x²³", "10^{-3} m and x^{23}")]
    [InlineData(@"x = 1 \\", "x = 1")]
    [InlineData(@"\text{café ≥ π}", @"\text{café ≥ π}")]
    [InlineData(@"\$5 \quad \text{each}", @"\text{$}5 \;\;\;\; \text{each}")]
    [InlineData(@"\mapsto x \implies y", @"\rightarrow x \Rightarrow y")]
    // XAML-Math draws \text in a script at full size, below the line (and can't read "Q_\text{out}"): plain words go upright.
    [InlineData(@"\frac{Q_{\text{out}}}{W_{\text{in}}}", @"\frac{Q_{\mathrm{out}}}{W_{\mathrm{in}}}")]
    [InlineData(@"Q_\text{out} = 5", @"Q_{\mathrm{out}} = 5")]
    [InlineData(@"T_{\text{flow, max}} + x^{\textbf{2nd}}", @"T_{\mathrm{flow,\,max}} + x^{\mathrm{2nd}}")]
    [InlineData(@"\text{speed}_{\text{avg}} = \frac{\text{distance}}{\text{time}}", @"\text{speed}_{\mathrm{avg}} = \frac{\text{distance}}{\text{time}}")]
    [InlineData(@"x_\text{über} \cdot y_{\text{driver's}}", @"x_{\text{über}} \cdot y_{\text{driver's}}")]
    [InlineData(@"y^2 \_ x", @"y^2 \text{_} x")]
    [InlineData(@"\begin{pmatrix} 1 & 0 \\ 0 & 1 \end{pmatrix}", @"\left(\matrix{1 & 0 \\ 0 & 1}\right)")]
    [InlineData(@"\begin{vmatrix} a \end{vmatrix}", @"\left|\matrix{a}\right|")]
    [InlineData(@"\begin{Vmatrix} a \end{Vmatrix}", @"\left\|\matrix{a}\right\|")]
    [InlineData(@"\begin{Bmatrix} a \end{Bmatrix}", @"\left\{\matrix{a}\right\}")]
    [InlineData(@"\begin{matrix} a \end{matrix}", @"\matrix{a}")]
    [InlineData(@"\tfrac{a}{b} + \cfrac{c}{d}", @"\frac{a}{b} + \frac{c}{d}")]
    [InlineData(@"a \equiv b \pmod{5}", @"a \equiv b \;(\mathrm{mod}\;5)")]
    [InlineData("50%", @"50\%")]
    [InlineData(@"5\% off", @"5\% off")]
    [InlineData(@"\overset{\text{def}}{=}", @"{=}^{\mathrm{def}}")]
    [InlineData(@"\underset{n}{\max} x_n", @"{\max}_{n} x_n")]
    [InlineData(@"A \xrightarrow{f} B \xleftarrow[g]{h} C", @"A \rightarrow^{f} B \leftarrow^{h} C")]
    [InlineData(@"\iint_D f\,dA", @"\int\int_D f\,dA")]
    [InlineData(@"x" + @" \\ \\", "x")]
    public void RewritesWhatXamlMathDoesntKnow(string latex, string expected) => Assert.Equal(expected, MathText.Normalize(latex));

    /// <summary>What each symbol must become, written out apart from the table (the table can't be its own check).</summary>
    [Theory]
    [InlineData("≤", @"\le")]
    [InlineData("≥", @"\ge")]
    [InlineData("≠", @"\neq")]
    [InlineData("≈", @"\approx")]
    [InlineData("∞", @"\infty")]
    [InlineData("∝", @"\propto")]
    [InlineData("±", @"\pm")]
    [InlineData("∓", @"\mp")]
    [InlineData("→", @"\rightarrow")]
    [InlineData("←", @"\leftarrow")]
    [InlineData("⇒", @"\Rightarrow")]
    [InlineData("⇔", @"\Leftrightarrow")]
    [InlineData("∈", @"\in")]
    [InlineData("∉", @"\not\in")]
    [InlineData("⊂", @"\subset")]
    [InlineData("⊆", @"\subseteq")]
    [InlineData("∪", @"\cup")]
    [InlineData("∩", @"\cap")]
    [InlineData("∀", @"\forall")]
    [InlineData("∃", @"\exists")]
    [InlineData("∑", @"\sum")]
    [InlineData("∫", @"\int")]
    [InlineData("∂", @"\partial")]
    [InlineData("√", @"\surd")]
    [InlineData("−", "-")]
    [InlineData("°", @"^\circ")]
    [InlineData("µ", @"\mu")]
    [InlineData("ε", @"\epsilon")]
    [InlineData("φ", @"\phi")]
    [InlineData("Ω", @"\Omega")]
    [InlineData("¼", @"\frac{1}{4}")]
    [InlineData("¾", @"\frac{3}{4}")]
    public void UnicodeSymbolsBecomeTheirCommands(string symbol, string command) => Assert.Equal(command, MathText.Normalize(symbol));

    /// <summary>Every entry of both tables is applied as written, so a new entry can't be dead (what it should be is checked above).</summary>
    [Fact]
    public void EveryRewriteInTheTablesIsApplied()
    {
        foreach (var (name, instead) in MathText.Commands) Assert.Equal(instead.Trim(), MathText.Normalize(@"\" + name));
        foreach (var (symbol, command) in MathText.Unicode) Assert.Equal(command, MathText.Normalize(symbol.ToString()));
    }

    [Fact]
    public void ALongRunOfTrailingLineBreaksIsRemovedInLinearTime()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("x", MathText.Normalize("x" + string.Concat(Enumerable.Repeat(@" \\", 13000))));
        Assert.EndsWith("x", MathText.Normalize(string.Concat(Enumerable.Repeat(@"\\ ", 13000)) + "x"));
        Assert.True(clock.ElapsedMilliseconds < 1000, clock.ElapsedMilliseconds + " ms");
    }

    /// <summary>Formulas local models write: after rewriting, XAML-Math's own parser must accept every one.</summary>
    public static TheoryData<string> ModelFormulas() => new()
    {
        @"E = mc^2", @"\dfrac{a}{b} + \tfrac{1}{2}", @"\sum_{i=1}^{n} i = \frac{n(n+1)}{2}", @"\int_0^1 x\,dx", @"\lim_{x \to 0} \frac{\sin x}{x} = 1",
        @"\mathbf{F} = m\mathbf{a}", @"\text{speed} = \frac{\text{distance}}{\text{time}}", @"\approx 3.14", @"\pm 5\%", @"\$5 \times 3",
        @"\begin{cases} 1 & x > 0 \\ 0 & \text{otherwise} \end{cases}", @"\begin{aligned} x &= 1 \\ y &= 2 \end{aligned}",
        @"\begin{bmatrix} a & b \\ c & d \end{bmatrix}", @"\begin{vmatrix} a & b \\ c & d \end{vmatrix}", @"\begin{matrix} 1 & 2 \end{matrix}",
        @"\begin{equation} y = mx + c \end{equation}", @"x \quad y \qquad z", @"\mathbb{R}^n", @"\operatorname{sgn}(x)", @"\boxed{42}",
        @"\cdots \ldots \dots", @"\displaystyle \sum_{k=0}^{\infty} \frac{x^k}{k!}", @"p \implies q \iff r", @"\lvert x \rvert + \lVert v \rVert",
        @"\textbf{Total}: 12", @"x \notin A", @"\overbrace{a+b}^{n} \underbrace{c+d}_{m}", @"\sum\limits_{i=1}^n i", @"\big( x \big)",
        @"a \equiv b \pmod{5}", @"20\degree", "π ≈ 3.14159 and α + β = γ", "2 × 3 ÷ 4 ≤ 5 ≥ 1 ≠ 0", @"\frac{\partial^2 u}{\partial t^2} = c^2 \nabla^2 u",
        @"e^{i\pi} + 1 = 0", @"\sqrt[3]{x}", @"\left( \frac{1}{2} \right)^2", @"\binom{n}{k}", @"f(x) = x^2 \tag{1}", @"\vec{v} \cdot \hat{n}",
        @"\mathcal{O}(n \log n)", @"x_{i,j} = \overline{y}", @"\Delta G = \Delta H - T\Delta S", @"1{,}000 \text{ km}", @"\& \# \_",
        @"\frac{Q_{\text{out}}}{W_{\text{in}}}", @"Q_\text{out}", @"T_{\text{flow, max}}", @"x_\text{über}", @"\begin{pmatrix} 1 & 0 \\ 0 & 1 \end{pmatrix}",
        "50%", @"\overset{\text{def}}{=}", @"\stackrel{?}{=}", @"\underset{n \to \infty}{\lim} a_n", @"A \xrightarrow{f} B", @"\iint_D f\,dA", @"\iiint_V \rho\,dV",
        @"\textcolor{red}{x} + y", @"\begin{Bmatrix} a \\ b \end{Bmatrix}", @"\begin{Vmatrix} a \end{Vmatrix}",
    };

    [Theory]
    [MemberData(nameof(ModelFormulas))]
    public void EveryRewrittenFormulaParses(string latex) => XamlMathParser.Parse(MathText.Normalize(latex));
}

/// <summary>Read aloud: an answer without its Markdown, its formulae in words (the review's L5: they were read as LaTeX).</summary>
public class SpokenAnswerTests
{
    [Theory]
    [InlineData(@"\frac{a}{b}", "a over b")]
    [InlineData("x^2 + y^3", "x squared plus y cubed")]
    [InlineData(@"\sqrt{x}", "the square root of x")]
    [InlineData(@"\sqrt[3]{8}", "the cube root of 8")]
    [InlineData("E = mc^2", "E equals mc squared")]
    [InlineData(@"\sum_{i=1}^{n} i", "the sum from i equals 1 to n of i")]
    [InlineData(@"\lim_{x \to 0} \frac{\sin x}{x}", "the limit as x to 0 of sine x over x")]
    [InlineData(@"20^\circ", "20 degrees")]
    [InlineData(@"\pi r^2", "pi r squared")]
    [InlineData(@"a \le b \ne c", "a is at most b is not c")]
    [InlineData(@"\text{speed} = \frac{d}{t}", "speed equals d over t")]
    [InlineData(@"\begin{bmatrix} a & b \\ c & d \end{bmatrix}", "a formula")]
    public void AFormulaIsReadAsWords(string latex, string spoken) => Assert.Equal(spoken, MathText.Spoken(latex));

    [Fact]
    public void AnAnswerIsReadWithoutItsMarkdown()
    {
        var spoken = AnswerText.Spoken("## Area\nThe area is $\\pi r^2$ [1], about \\(3.14 r^2\\).\n\n| Plan | Price |\n|---|---|\n| Basic | €5 |\n\n**Note:** see [the docs](https://example.org) and $$\\frac{1}{2}$$");
        Assert.Contains("The area is pi r squared , about 3.14 r squared", spoken);
        Assert.Contains("Plan, Price", spoken);
        Assert.Contains("Basic, €5", spoken);
        Assert.Contains("Note: see the docs and 1 over 2", spoken);
        foreach (var gone in new[] { "\\", "$", "[1]", "**", "|", "---", "##", "https" }) Assert.DoesNotContain(gone, spoken, StringComparison.Ordinal);
        // Prices aren't formulae.
        Assert.Equal("It costs $5 and $10.", AnswerText.Spoken("It costs $5 and $10."));
    }
}

/// <summary>
/// The guard in front of XAML-Math: its parser recurses for every group and every line break, and a stack overflow
/// ends the app (nothing can catch it). 720 line breaks in one formula did, on the UI thread's 1.5 MB.
/// </summary>
public class DrawableTests
{
    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    [Fact]
    public void HundredsOfLineBreaksArentGivenToXamlMath()
    {
        Assert.False(MathText.Drawable(MathText.Normalize("a" + Repeat(@"\\", 740) + "x")));
        Assert.Null(MathText.ForDrawing("a" + Repeat(@"\\", 740) + "x"));
        // Rows of an environment are line breaks too, but a table that size is not an answer's.
        Assert.False(MathText.Drawable(@"\matrix{" + Repeat(@"a \\ ", MathText.MaxBreaks + 1) + "}"));
    }

    public static TheoryData<string, bool> EitherSideOfEachLimit() => new()
    {
        { Repeat("{", MathText.MaxDepth) + "x" + Repeat("}", MathText.MaxDepth), true },
        { Repeat("{", MathText.MaxDepth + 1) + "x" + Repeat("}", MathText.MaxDepth + 1), false },
        { Repeat(@"\{", 3 * MathText.MaxDepth) + "x", true },                           // escaped braces aren't groups
        { Repeat("}", 5) + Repeat("{", MathText.MaxDepth + 1) + "x", false },          // stray closers give no extra room
        { Repeat(@"\sqrt ", MathText.MaxNesting) + "x", true },
        { Repeat(@"\sqrt ", MathText.MaxNesting + 1) + "x", false },
        { Repeat(@"\frac", MathText.MaxNesting + 1) + " x", false },
        { "a" + Repeat(@"\\", MathText.MaxBreaks) + "x", true },
        { "a" + Repeat(@"\\", MathText.MaxBreaks + 1) + "x", false },
        { Repeat(@"\{", MathText.MaxBreaks + 1), true },                                 // an escape isn't a line break
        { new string('x', MathText.MaxLength), true },
        { new string('x', MathText.MaxLength + 1), false },
        { "", false },
    };

    [Theory]
    [MemberData(nameof(EitherSideOfEachLimit))]
    public void EachLimitHolds(string latex, bool drawable) => Assert.Equal(drawable, MathText.Drawable(latex));

    [Fact]
    public void AFormulaFarOverTheLimitIsntEvenRewritten()
    {
        Assert.Null(MathText.ForDrawing(new string('x', 2 * MathText.MaxLength + 1)));
        Assert.Null(MathText.ForDrawing(""));
        Assert.Equal(@"\frac{a}{b}", MathText.ForDrawing(@"\dfrac{a}{b}"));
    }

    /// <summary>
    /// Every limit at once, through XAML-Math's own parser on a thread with a third of the UI thread's stack (the app
    /// draws on a thread with 32 MB): raising a limit without checking the parser fails here, by crashing the test run,
    /// instead of in the app.
    /// </summary>
    [Fact]
    public void TheWorstFormulasDrawableAcceptsParseInAThirdOfTheUiThreadsStack()
    {
        var worst = new[]
        {
            Repeat(@"\left(", MathText.MaxNesting) + Repeat("{", MathText.MaxDepth) + "a" + Repeat(@"\\", MathText.MaxBreaks) + "x"
                + Repeat("}", MathText.MaxDepth) + Repeat(@"\right)", MathText.MaxNesting),
            Repeat(@"\frac{1}{", MathText.MaxDepth) + "a" + Repeat(@"\\", MathText.MaxBreaks) + "x" + Repeat("}", MathText.MaxDepth),
            Repeat(@"\sqrt{", MathText.MaxDepth) + "x" + Repeat("}", MathText.MaxDepth),
            Repeat("x^{", MathText.MaxDepth) + "2" + Repeat("}", MathText.MaxDepth),
            Repeat("x_{", MathText.MaxDepth) + "2" + Repeat("}", MathText.MaxDepth),
            Repeat(@"\mathrm{", MathText.MaxDepth) + "x" + Repeat("}", MathText.MaxDepth),
            Repeat(@"\overline{", MathText.MaxDepth) + "x" + Repeat("}", MathText.MaxDepth),
        };
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var latex in worst)
                {
                    Assert.True(MathText.Drawable(latex), latex[..40]);
                    XamlMathParser.Parse(latex);
                }
            }
            catch (Exception ex) { error = ex; }
        }, 512 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}

/// <summary>XAML-Math's parser built the way WpfMath builds its own (predefined formulas included), with no WPF.</summary>
internal static class XamlMathParser
{
    private sealed class NoBrushes : IBrushFactory
    {
        public IBrush FromColor(RgbaColor color) => new NoBrush();
    }

    private sealed class NoBrush : IBrush;

    private static readonly Lazy<TexFormulaParser> Instance = new(() =>
    {
        var brushes = new NoBrushes();
        var type = typeof(TexFormulaParser).Assembly.GetType("XamlMath.TexPredefinedFormulaParser", throwOnError: true)!;
        var predefined = new Dictionary<string, Func<SourceSpan, TexFormula?>>();
        var loader = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { brushes }, null)!;
        type.GetMethod("Parse", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(loader, new object[] { predefined });
        return new TexFormulaParser(brushes, predefined);
    });

    public static TexFormula Parse(string latex) => Instance.Value.Parse(latex, null);
}
