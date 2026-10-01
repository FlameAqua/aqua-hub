using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AquaHub.Core.Ai;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Controls;

/// <summary>
/// A selectable, copyable rendering of an answer: the Markdown subset local models write (paragraphs, bullets, numbered
/// lists, headings, **bold**, `code`) with [n] citations as links. Read-only rich text, so you can select any part,
/// copy it, or right-click to ask about it or search the web for it. Re-renders at most every 80 ms while streaming.
/// </summary>
public sealed partial class MarkdownView : RichTextBox
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

    // ───────────────────────────── Markdown → FlowDocument ─────────────────────────────

    [GeneratedRegex(@"(\*\*[^*]+\*\*|\[[^\]\n]{1,400}\]\((?:https?://[^\s)]+|file:/{2,3}[^)\n]+|[A-Za-z]:\\[^)\n]+)\)|`[^`]+`|\[\d{1,2}\]|<https?://[^\s<>]+>|https?://[^\s<>()\[\]""']+[^\s<>()\[\]""'.,;:!?]|(?<![\w/\\])[A-Za-z]:\\[^\s<>""|?*()\[\]]*[^\s<>""|?*()\[\].,;:!])")]
    private static partial Regex Tokens();

    [GeneratedRegex(@"^\[(?<text>[^\]\n]{1,400})\]\((?<url>https?://[^\s)]+|file:/{2,3}[^)\n]+|[A-Za-z]:\\[^)\n]+)\)$")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DrivePath();

    /// <summary>A clickable web link in an answer (opened in the browser; only http(s)).</summary>
    private static Hyperlink WebLink(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { Cursor = Cursors.Hand, ToolTip = url, Focusable = false };
        link.SetResourceReference(TextElement.ForegroundProperty, "B.AccentText");
        link.Click += (_, _) => AppLauncher.OpenUrl(url);
        return link;
    }

    private static string? LocalPath(string target) => Core.Ai.Assistant.AnswerText.LocalPath(target);

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

    [GeneratedRegex(@"^(\d+)[.)]\s+")]
    private static partial Regex Numbered();

    internal static void Build(FlowDocument doc, string text, IReadOnlyList<Citation>? citations)
    {
        List? list = null;
        var listNumbered = false;
        Paragraph? para = null;
        Paragraph? code = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            // ``` fenced blocks: kept as written, in a monospace box (paths in them can still be opened).
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (code is null)
                {
                    code = new Paragraph { Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(10, 6, 10, 6), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = doc.FontSize - 1 };
                    code.SetResourceReference(Block.BackgroundProperty, "B.Subtle");
                    doc.Blocks.Add(code);
                }
                else code = null;
                para = null;
                list = null;
                continue;
            }
            if (code is not null)
            {
                if (code.Inlines.Count > 0) code.Inlines.Add(new LineBreak());
                var path = Core.Ai.Assistant.AnswerText.LocalPath(line.Trim());
                code.Inlines.Add(path is not null && (File.Exists(path) || Directory.Exists(path)) ? FileLink(new Run(line), path) : new Run(line));
                continue;
            }
            if (trimmed.Length == 0) { para = null; list = null; continue; }

            var bullet = trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• ");
            var number = Numbered().Match(trimmed);
            if (bullet || number.Success)
            {
                var numbered = number.Success;
                if (list is null || listNumbered != numbered)
                {
                    list = new List
                    {
                        MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 2, 0, 6),
                        Padding = new Thickness(22, 0, 0, 0),
                    };
                    if (numbered && int.TryParse(number.Groups[1].Value, out var start) && start > 1) list.StartIndex = start;
                    listNumbered = numbered;
                    doc.Blocks.Add(list);
                }
                var content = numbered ? trimmed[number.Length..] : trimmed[2..];
                var item = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                Inlines(item.Inlines, content, citations);
                list.ListItems.Add(new ListItem(item));
                para = null;
                continue;
            }
            list = null;
            if (trimmed.StartsWith('#'))
            {
                var heading = new Paragraph { Margin = new Thickness(0, 10, 0, 4), FontWeight = FontWeights.SemiBold, FontSize = doc.FontSize + 1.5 };
                Inlines(heading.Inlines, trimmed.TrimStart('#', ' '), citations);
                doc.Blocks.Add(heading);
                para = null;
                continue;
            }
            if (para is null)
            {
                para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                doc.Blocks.Add(para);
            }
            else para.Inlines.Add(new LineBreak());
            Inlines(para.Inlines, trimmed, citations);
        }
        // No trailing gap under the last block.
        if (doc.Blocks.LastBlock is Paragraph last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    private static void Inlines(InlineCollection target, string line, IReadOnlyList<Citation>? citations)
    {
        foreach (var part in Tokens().Split(line))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                target.Add(new Bold(new Run(part[2..^2])));
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
            {
                var code = new Run(part[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                code.SetResourceReference(TextElement.BackgroundProperty, "B.Subtle");
                // A path in backticks is usually a file Aqua found: make it openable.
                target.Add(LocalPath(part[1..^1]) is { } codePath ? FileLink(code, codePath) : code);
            }
            else if (part.StartsWith('[') && part.EndsWith(']') && int.TryParse(part[1..^1], out var n) &&
                     citations?.FirstOrDefault(c => c.Number == n) is { } cite)
            {
                var link = new Hyperlink(new Run($"[{n}]"))
                {
                    TextDecorations = null, Cursor = Cursors.Hand, ToolTip = $"{cite.Source}: {cite.Title}", Focusable = false,
                };
                link.SetResourceReference(TextElement.ForegroundProperty, "B.AccentText");
                link.Click += (_, _) => OpenCitation(cite);
                target.Add(link);
            }
            else if (MarkdownLink().Match(part) is { Success: true } md)
            {
                var url = md.Groups["url"].Value;
                var label = md.Groups["text"].Value.Trim('`', ' ');
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) target.Add(WebLink(label, url));
                else if (LocalPath(url) is { } linked) target.Add(FileLink(new Run(label), linked));
                else target.Add(new Run(label));
            }
            else if (part.Length > 9 && part[0] == '<' && part[^1] == '>' && part[1..].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                target.Add(WebLink(part[1..^1], part[1..^1])); // <https://…>
            else if (part.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || part.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                target.Add(WebLink(part, part));
            // A bare path ends at the first space, so it's a link only when that is a real file or folder.
            else if (DrivePath().IsMatch(part) && LocalPath(part) is { } bare && (File.Exists(bare) || Directory.Exists(bare)))
                target.Add(FileLink(new Run(part), bare));
            else target.Add(new Run(part));
        }
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
        _ask.Click += (_, _) => { if (SelectedText() is { Length: > 0 } t) AskAboutSelection?.Invoke(t); };
        _search = new MenuItem { Header = "Search the web for this" };
        _search.Click += (_, _) => { if (SelectedText() is { Length: > 0 } t) SearchSelection?.Invoke(t); };
        menu.Items.Add(_ask);
        menu.Items.Add(_search);
        return menu;
    }

    private void UpdateMenu()
    {
        var has = SelectedText().Length > 0;
        if (_ask is not null) _ask.IsEnabled = has;
        if (_search is not null) _search.IsEnabled = has;
    }

    public string SelectedText() => Selection.IsEmpty ? "" : Selection.Text.Trim();
}
