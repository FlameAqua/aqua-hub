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

    [Fact]
    public void PlainTextOfALine() => Assert.Equal("Speed is d/t (see 1)", AnswerMarkdown.Plain("**Speed** is $d/t$ (see `1`)"));
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
    [InlineData(@"\begin{cases} 1 & x > 0 \\ 0 & \text{otherwise} \end{cases}", @"\cases{1 & x > 0 \\ 0 & \text{otherwise}}")]
    [InlineData(@"\begin{aligned} x &= 1 \\ y &= 2 \end{aligned}", @"\begin{align} x &= 1 \\ y &= 2 \end{align}")]
    [InlineData(@"\begin{bmatrix} a & b \\ c & d \end{bmatrix}", @"\left[\matrix{a & b \\ c & d}\right]")]
    [InlineData("πr² ≈ 3 × 4", @"\pi r^2 \approx 3 \times 4")]
    [InlineData("x₁ + ½", @"x_1 + \frac{1}{2}")]
    [InlineData("10⁻³ m and x²³", "10^{-3} m and x^{23}")]
    [InlineData(@"x = 1 \\", "x = 1")]
    [InlineData(@"\text{café ≥ π}", @"\text{café ≥ π}")]
    [InlineData(@"\$5 \quad \text{each}", @"\text{$}5 \;\;\;\; \text{each}")]
    [InlineData(@"\mapsto x \implies y", @"\rightarrow x \Rightarrow y")]
    public void RewritesWhatXamlMathDoesntKnow(string latex, string expected) => Assert.Equal(expected, MathText.Normalize(latex));

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
    };

    [Theory]
    [MemberData(nameof(ModelFormulas))]
    public void EveryRewrittenFormulaParses(string latex) => XamlMathParser.Parse(MathText.Normalize(latex));
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
