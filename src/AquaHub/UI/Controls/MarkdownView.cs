using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Platform;
using AquaHub.Services;

namespace AquaHub.UI.Controls;

/// <summary>
/// A selectable, copyable rendering of an answer: paragraphs, headings, quotes, nested and numbered lists, code, tables
/// (native WPF tables) and LaTeX formulae (drawn by XAML-Math), with **bold**, *italic*, `code`, links, file paths and
/// [n] citations as links. Read-only rich text, so you can select any part, copy it — tables, formulae and whole blocks
/// copy as the Markdown they came from — or right-click to ask about it or search the web for it. Re-renders at most
/// every 80 ms while streaming.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).Schedule()));
    public static readonly DependencyProperty CitationsProperty = DependencyProperty.Register(nameof(Citations), typeof(IReadOnlyList<Citation>), typeof(MarkdownView),
        new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Schedule()));

    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public IReadOnlyList<Citation>? Citations { get => (IReadOnlyList<Citation>?)GetValue(CitationsProperty); set => SetValue(CitationsProperty, value); }

    /// <summary>Raised with the selected text when "Ask about this" is picked.</summary>
    public static event Action<string>? AskAboutSelection;
    /// <summary>Raised with the selected text when "Search the web for this" is picked.</summary>
    public static event Action<string>? SearchSelection;

    private readonly DispatcherTimer _timer;
    private string _rendered = "\u0001";
    private IReadOnlyList<Citation>? _renderedCitations;

    public MarkdownView()
    {
        IsReadOnly = true;
        // Each render rewrites the document; with undo on, every streamed re-render would pile up in the undo stack.
        IsUndoEnabled = false;
        IsDocumentEnabled = true;
        IsReadOnlyCaretVisible = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        FocusVisualStyle = null;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        AcceptsReturn = false;
        Cursor = Cursors.IBeam;
        SetResourceReference(ForegroundProperty, "B.Text");
        SetResourceReference(SelectionBrushProperty, "B.Accent");
        Document = new FlowDocument { PagePadding = new Thickness(0) };
        Document.SetResourceReference(FlowDocument.ForegroundProperty, "B.Text");
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => { _timer.Stop(); Render(); };
        Loaded += (_, _) => Render();
        ContextMenu = BuildMenu();
        ContextMenuOpening += (_, _) => UpdateMenu();
        // Copying keeps the Markdown: tables and formulae come out as written, not as tab-separated text or nothing.
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewCommand);
        // Let the chat's scroll viewer handle the wheel (this box never scrolls itself).
        PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled || VisualTreeHelper.GetParent(this) is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
        };
    }

    private void Schedule()
    {
        if (!IsLoaded) return;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void Render()
    {
        var text = Markdown ?? "";
        if (text == _rendered && ReferenceEquals(Citations, _renderedCitations)) return;
        _rendered = text;
        _renderedCitations = Citations;
        // Re-render into the same document: a screen reader's text pattern stays bound to the document it first saw,
        // so swapping in a new one would leave it reading an empty answer.
        var doc = Document;
        doc.PagePadding = new Thickness(0);
        doc.FontFamily = FontFamily;
        doc.FontSize = FontSize;
        doc.LineHeight = FontSize * 1.55;
        doc.TextAlignment = TextAlignment.Left;
        doc.Blocks.Clear();
        Build(doc, text, Citations);
    }

    // ───────────────────────────── Links ─────────────────────────────

    /// <summary>A clickable web link in an answer (opened in the browser; only http(s)).</summary>
    private static Hyperlink WebLink(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { Cursor = Cursors.Hand, ToolTip = url, Focusable = false };
        link.SetResourceReference(TextElement.ForegroundProperty, "B.AccentText");
        link.Click += (_, _) => AppLauncher.OpenUrl(url);
        return link;
    }

    private static string? LocalPath(string target) => AnswerText.LocalPath(target);

    /// <summary>
    /// A file on the PC named in an answer: click opens documents, pictures and media (anything else is shown in its
    /// folder, never run); right-click also offers Show in folder.
    /// </summary>
    private static Hyperlink FileLink(Inline content, string path)
    {
        var link = new Hyperlink(content) { Cursor = Cursors.Hand, ToolTip = path, Focusable = false };
        link.SetResourceReference(TextElement.ForegroundProperty, "B.AccentText");
        link.Click += (_, _) => OpenFile(path);
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => OpenFile(path);
        var reveal = new MenuItem { Header = "Show in folder" };
        reveal.Click += (_, _) => OpenFile(path, reveal: true);
        var copy = new MenuItem { Header = "Copy the path" };
        copy.Click += (_, _) => { try { Clipboard.SetText(path); } catch (System.Runtime.InteropServices.COMException) { } };
        menu.Items.Add(open);
        menu.Items.Add(reveal);
        menu.Items.Add(copy);
        link.ContextMenu = menu;
        return link;
    }

    /// <summary>What a click on a file opens with its app; everything else is only shown in File Explorer.</summary>
    private static readonly HashSet<string> Viewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".ico", ".pdf", ".txt", ".md", ".rtf", ".csv", ".log",
        ".doc", ".docx", ".odt", ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp", ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".mp4", ".mkv",
        ".mov", ".avi", ".webm",
    };

    /// <summary>Opens a file from an answer: documents, pictures and media with their app; programs, scripts and folders are shown in File Explorer.</summary>
    public static void OpenFile(string path, bool reveal = false)
    {
        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path)) return;
        var open = !reveal && isFile && Viewable.Contains(Path.GetExtension(path));
        if (Sandbox.Intercept(open ? "open-file" : "reveal-file", path)) return;
        try
        {
            if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else if (isFile) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Core.Util.Log.Warn("ask", "Couldn't open the file", ex); }
    }

    /// <summary>Opens a cited source: web links in the browser; files open with their app when they're documents, pictures or media, else they're shown in their folder (never run).</summary>
    public static void OpenCitation(Citation cite)
    {
        if (cite.Url is not { Length: > 0 } url) return;
        if (cite.Kind == "file")
        {
            OpenFile(url);
            return;
        }
        AppLauncher.OpenUrl(url);
    }

    // ───────────────────────────── Markdown → FlowDocument ─────────────────────────────

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    /// <summary>What drawing an answer needs: its text size and its sources.</summary>
    private sealed record Ctx(double FontSize, IReadOnlyList<Citation>? Citations);

    internal static void Build(FlowDocument doc, string text, IReadOnlyList<Citation>? citations)
    {
        var ctx = new Ctx(doc.FontSize, citations);
        foreach (var block in AnswerMarkdown.Parse(text)) doc.Blocks.Add(BlockOf(block, ctx, 0));
        // No trailing gap under the last block.
        if (doc.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    /// <summary>A block, tagged with the Markdown it came from (what copying a whole block gives back).</summary>
    private static Block BlockOf(MdBlock md, Ctx ctx, int level)
    {
        Block block = md switch
        {
            MdHeading h => HeadingOf(h, ctx),
            MdCode c => CodeOf(c.Lines, ctx),
            MdMath m => DisplayMath(m, ctx),
            MdTable t => TableOf(t, ctx),
            MdList l => ListOf(l, ctx, level),
            MdQuote q => QuoteOf(q, ctx, level),
            MdRule => RuleOf(),
            MdParagraph p => ParagraphOf(p.Lines, ctx),
            _ => new Paragraph(),
        };
        block.Tag = md.Source;
        return block;
    }

    private static Paragraph ParagraphOf(IReadOnlyList<string> lines, Ctx ctx)
    {
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) para.Inlines.Add(new LineBreak());
            Inlines(para.Inlines, lines[i], ctx);
        }
        return para;
    }

    private static Paragraph HeadingOf(MdHeading h, Ctx ctx)
    {
        var heading = new Paragraph
        {
            Margin = new Thickness(0, h.Level <= 2 ? 14 : 10, 0, 4),
            FontWeight = FontWeights.SemiBold,
            FontSize = ctx.FontSize + h.Level switch { 1 => 5, 2 => 3, 3 => 1.5, _ => 0.5 },
        };
        Inlines(heading.Inlines, h.Text, ctx);
        return heading;
    }

    /// <summary>A code block kept as written, in a monospace box; a line that is a file or folder on the PC can be opened.</summary>
    private static Paragraph CodeOf(IReadOnlyList<string> lines, Ctx ctx)
    {
        var code = new Paragraph { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(10, 6, 10, 6), FontFamily = Mono, FontSize = ctx.FontSize - 1 };
        code.SetResourceReference(TextElement.BackgroundProperty, "B.Subtle");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) code.Inlines.Add(new LineBreak());
            var line = lines[i].TrimEnd();
            var path = LocalPath(line.Trim());
            code.Inlines.Add(path is not null && (File.Exists(path) || Directory.Exists(path)) ? FileLink(new Run(line), path) : new Run(line));
        }
        return code;
    }

    /// <summary>A display formula on its own line, centred and shrunk to fit; while it streams in, or if it can't be drawn, its LaTeX.</summary>
    private static Block DisplayMath(MdMath m, Ctx ctx)
    {
        if (m.Closed && MathView.Create(m.Latex, true, ctx.FontSize) is { } view)
            return new BlockUIContainer(new Viewbox
            {
                Child = view, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            }) { Margin = new Thickness(0, 4, 0, 10) };
        var raw = CodeOf(m.Source.Split('\n'), ctx);
        if (m.Closed) raw.ToolTip = "This formula couldn't be drawn, so here is its LaTeX.";
        return raw;
    }

    private static Block RuleOf()
    {
        var line = new Border { Height = 1, SnapsToDevicePixels = true };
        line.SetResourceReference(Border.BackgroundProperty, "B.Divider");
        return new BlockUIContainer(line) { Margin = new Thickness(0, 6, 0, 12) };
    }

    private static Section QuoteOf(MdQuote q, Ctx ctx, int level)
    {
        var section = new Section { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(12, 0, 0, 0), BorderThickness = new Thickness(3, 0, 0, 0) };
        section.SetResourceReference(Block.BorderBrushProperty, "B.Divider");
        section.SetResourceReference(TextElement.ForegroundProperty, "B.Text2");
        foreach (var child in q.Blocks) section.Blocks.Add(BlockOf(child, ctx, level));
        if (section.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        return section;
    }

    private static List ListOf(MdList l, Ctx ctx, int level)
    {
        var list = new List
        {
            // Nested lists change marker, as printed lists do: • ◦ ▪ and 1. a. i.
            MarkerStyle = l.Ordered
                ? (level % 3) switch { 0 => TextMarkerStyle.Decimal, 1 => TextMarkerStyle.LowerLatin, _ => TextMarkerStyle.LowerRoman }
                : (level % 3) switch { 0 => TextMarkerStyle.Disc, 1 => TextMarkerStyle.Circle, _ => TextMarkerStyle.Square },
            Margin = level == 0 ? new Thickness(0, 2, 0, 6) : new Thickness(0, 1, 0, 1),
            Padding = new Thickness(level == 0 ? 22 : 20, 0, 0, 0),
        };
        if (l.Ordered && l.Start > 1) list.StartIndex = l.Start;
        foreach (var item in l.Items)
        {
            var li = new ListItem();
            foreach (var child in item.Blocks)
            {
                var block = BlockOf(child, ctx, level + 1);
                if (block is Paragraph p && child is MdParagraph) p.Margin = new Thickness(0, 1, 0, 1);
                li.Blocks.Add(block);
            }
            if (li.Blocks.FirstBlock is not Paragraph first)
            {
                first = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                if (li.Blocks.FirstBlock is { } head) li.Blocks.InsertBefore(head, first);
                else li.Blocks.Add(first);
            }
            if (item.Checked is { } done)
            {
                var box = new Run(done ? "☑ " : "☐ ");
                if (first.Inlines.FirstInline is { } at) first.Inlines.InsertBefore(at, box);
                else first.Inlines.Add(box);
            }
            list.ListItems.Add(li);
        }
        return list;
    }

    /// <summary>A table with a shaded header row; columns share the width by how much they hold (numbers stay narrow).</summary>
    private static Table TableOf(MdTable t, Ctx ctx)
    {
        var columns = t.Header.Count;
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10), BorderThickness = new Thickness(1) };
        table.SetResourceReference(Table.BorderBrushProperty, "B.Divider");
        for (var c = 0; c < columns; c++)
        {
            var column = c;
            var longest = t.Rows.Select(r => r[column]).Prepend(t.Header[column])
                .Max(cell => cell.Split('\n').Max(line => AnswerMarkdown.Plain(line).Length));
            table.Columns.Add(new TableColumn { Width = new GridLength(Math.Sqrt(Math.Clamp(longest, 3, 60)), GridUnitType.Star) });
        }
        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);
        rows.Rows.Add(Row(t.Header, header: true, last: t.Rows.Count == 0));
        for (var r = 0; r < t.Rows.Count; r++) rows.Rows.Add(Row(t.Rows[r], header: false, last: r == t.Rows.Count - 1));
        return table;

        TableRow Row(IReadOnlyList<string> cells, bool header, bool last)
        {
            var row = new TableRow();
            if (header)
            {
                row.SetResourceReference(TextElement.BackgroundProperty, "B.Subtle");
                row.FontWeight = FontWeights.SemiBold;
            }
            for (var c = 0; c < columns; c++)
            {
                var para = new Paragraph
                {
                    Margin = new Thickness(0),
                    LineHeight = ctx.FontSize * 1.4,
                    TextAlignment = t.Align[c] switch { MdAlign.Center => TextAlignment.Center, MdAlign.Right => TextAlignment.Right, _ => TextAlignment.Left },
                };
                var lines = cells[c].Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) para.Inlines.Add(new LineBreak());
                    Inlines(para.Inlines, lines[i], ctx);
                }
                var cell = new TableCell(para) { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, c < columns - 1 ? 1 : 0, last ? 0 : 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "B.Divider");
                row.Cells.Add(cell);
            }
            return row;
        }
    }

    private static void Inlines(InlineCollection target, string line, Ctx ctx)
    {
        foreach (var piece in AnswerMarkdown.Inlines(line)) target.Add(InlineOf(piece, ctx));
    }

    private static Span Spans(IReadOnlyList<MdInline> children, Ctx ctx, Span span)
    {
        foreach (var child in children) span.Inlines.Add(InlineOf(child, ctx));
        return span;
    }

    private static Inline InlineOf(MdInline piece, Ctx ctx)
    {
        switch (piece.Kind)
        {
            case MdSpan.Bold:
                return Spans(piece.Children ?? Array.Empty<MdInline>(), ctx, new Bold());
            case MdSpan.Italic:
                return Spans(piece.Children ?? Array.Empty<MdInline>(), ctx, new Italic());
            case MdSpan.BoldItalic:
                return new Bold(Spans(piece.Children ?? Array.Empty<MdInline>(), ctx, new Italic()));
            case MdSpan.Strike:
                return Spans(piece.Children ?? Array.Empty<MdInline>(), ctx, new Span { TextDecorations = TextDecorations.Strikethrough });
            case MdSpan.Code:
            {
                var code = new Run(piece.Text) { FontFamily = Mono };
                code.SetResourceReference(TextElement.BackgroundProperty, "B.Subtle");
                // A path in backticks is usually a file Aqua found: make it openable.
                return LocalPath(piece.Text) is { } codePath ? FileLink(code, codePath) : code;
            }
            case MdSpan.Math:
                return InlineMath(piece, ctx);
            case MdSpan.Citation when ctx.Citations?.FirstOrDefault(c => c.Number == piece.Number) is { } cite:
            {
                var link = new Hyperlink(new Run($"[{piece.Number}]"))
                {
                    TextDecorations = null, Cursor = Cursors.Hand, ToolTip = $"{cite.Source}: {cite.Title}", Focusable = false,
                };
                link.SetResourceReference(TextElement.ForegroundProperty, "B.AccentText");
                link.Click += (_, _) => OpenCitation(cite);
                return link;
            }
            case MdSpan.Link when piece.Target is { } target:
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return WebLink(piece.Text, target);
                return LocalPath(target) is { } linked ? FileLink(new Run(piece.Text), linked) : new Run(piece.Text);
            case MdSpan.Url when piece.Target is { } url:
                return WebLink(piece.Text, url);
            // A bare path ends at the first space, so it's a link only when that is a real file or folder.
            case MdSpan.Path when LocalPath(piece.Text) is { } bare && (File.Exists(bare) || Directory.Exists(bare)):
                return FileLink(new Run(piece.Text), bare);
            default:
                return new Run(piece.Text);
        }
    }

    /// <summary>A formula in the line, sitting on its baseline; one that can't be drawn shows its LaTeX in code type.</summary>
    private static Inline InlineMath(MdInline piece, Ctx ctx)
    {
        var source = piece.Target ?? "$" + piece.Text + "$";
        if (MathView.Create(piece.Text, false, ctx.FontSize) is not { } view)
        {
            var raw = new Run(source) { FontFamily = Mono, ToolTip = "This formula couldn't be drawn, so here is its LaTeX." };
            raw.SetResourceReference(TextElement.BackgroundProperty, "B.Subtle");
            return raw;
        }
        // The container stands on the baseline: moving the drawing down by its depth puts the formula's own baseline there.
        view.RenderTransform = new TranslateTransform(0, Math.Round(view.Depth));
        return new InlineUIContainer(view) { BaselineAlignment = BaselineAlignment.Baseline, Tag = source };
    }

    // ───────────────────────────── Copying ─────────────────────────────

    private void OnPreviewCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Copy || Selection.IsEmpty) return;
        e.Handled = true;
        AskSession.Copy(SelectionMarkdown());
    }

    /// <summary>
    /// The selection as Markdown: all of it gives the whole answer as written; a table, a formula or any block selected
    /// whole gives its Markdown; part of a block gives its text, with formulae as their LaTeX.
    /// </summary>
    public string SelectionMarkdown()
    {
        var selection = Selection;
        if (selection.IsEmpty) return "";
        var doc = Document;
        if (!HasContent(doc.ContentStart, selection.Start) && !HasContent(selection.End, doc.ContentEnd)) return (Markdown ?? "").Trim();
        var parts = new List<string>();
        foreach (var block in doc.Blocks)
        {
            if (block.ContentEnd.CompareTo(selection.Start) <= 0 || block.ContentStart.CompareTo(selection.End) >= 0) continue;
            var from = selection.Start.CompareTo(block.ContentStart) > 0 ? selection.Start : block.ContentStart;
            var to = selection.End.CompareTo(block.ContentEnd) < 0 ? selection.End : block.ContentEnd;
            var whole = !HasContent(block.ContentStart, from) && !HasContent(to, block.ContentEnd);
            if (block.Tag is string source && source.Length > 0 && (whole || block is Table or BlockUIContainer)) parts.Add(source);
            else if (TextOf(from, to) is { Length: > 0 } text) parts.Add(text);
        }
        return string.Join("\n\n", parts).Trim();
    }

    /// <summary>Whether there is any text or formula between two positions.</summary>
    private static bool HasContent(TextPointer from, TextPointer to)
    {
        for (var p = from; p is not null && p.CompareTo(to) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (p.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.EmbeddedElement:
                    return true;
                case TextPointerContext.Text:
                    var run = p.GetTextInRun(LogicalDirection.Forward);
                    var max = p.GetOffsetToPosition(to);
                    if ((max < run.Length ? run[..max] : run).Trim().Length > 0) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>The text between two positions: line breaks between paragraphs, list items and rows; formulae as their LaTeX.</summary>
    private static string TextOf(TextPointer from, TextPointer to)
    {
        var sb = new StringBuilder();
        void NewLine() { if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n'); }
        for (var p = from; p is not null && p.CompareTo(to) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (p.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    var run = p.GetTextInRun(LogicalDirection.Forward);
                    var max = p.GetOffsetToPosition(to);
                    sb.Append(max < run.Length ? run[..max] : run);
                    break;
                case TextPointerContext.ElementStart:
                    switch (p.GetAdjacentElement(LogicalDirection.Forward))
                    {
                        case LineBreak: sb.Append('\n'); break;
                        case InlineUIContainer { Tag: string math }: sb.Append(math); break;
                        case BlockUIContainer { Tag: string block }: NewLine(); sb.Append(block).Append('\n'); break;
                        case TableCell when sb.Length > 0 && sb[^1] != '\n': sb.Append(" | "); break;
                        case System.Windows.Documents.Paragraph or ListItem or TableRow: NewLine(); break;
                    }
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    // ───────────────────────────── Context menu ─────────────────────────────

    private MenuItem? _ask, _search;

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = this, InputGestureText = "Ctrl+C" });
        menu.Items.Add(new MenuItem { Header = "Select all", Command = ApplicationCommands.SelectAll, CommandTarget = this, InputGestureText = "Ctrl+A" });
        var copyAll = new MenuItem { Header = "Copy the whole answer" };
        copyAll.Click += (_, _) => AskSession.Copy(Markdown ?? "");
        menu.Items.Add(copyAll);
        menu.Items.Add(new Separator());
        _ask = new MenuItem { Header = "Ask about this" };
        _ask.Click += (_, _) => { if (SelectionMarkdown() is { Length: > 0 } t) AskAboutSelection?.Invoke(t); };
        _search = new MenuItem { Header = "Search the web for this" };
        _search.Click += (_, _) => { if (SelectedText() is { Length: > 0 } t) SearchSelection?.Invoke(t); };
        menu.Items.Add(_ask);
        menu.Items.Add(_search);
        return menu;
    }

    private void UpdateMenu()
    {
        if (_ask is not null) _ask.IsEnabled = !Selection.IsEmpty;
        if (_search is not null) _search.IsEnabled = SelectedText().Length > 0;
    }

    public string SelectedText() => Selection.IsEmpty ? "" : Selection.Text.Trim();
}
