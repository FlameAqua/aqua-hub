using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using AquaHub.Core.Ai;
using AquaHub.Platform;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI;

/// <summary>
/// Renders the small Markdown subset local models produce (paragraphs, bullets, **bold**, `code`,
/// headings) into WPF inlines, turning [n] citations into links. Never renders HTML.
/// </summary>
public static partial class MarkdownLite
{
    /// <summary>Attached: renders bound Markdown text into a TextBlock (re-renders on change).</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MarkdownLite), new PropertyMetadata(null, (d, e) => Rerender(d)));

    public static readonly DependencyProperty CitationsProperty = DependencyProperty.RegisterAttached(
        "Citations", typeof(IReadOnlyList<Citation>), typeof(MarkdownLite), new PropertyMetadata(null, (d, e) => Rerender(d)));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? v) => d.SetValue(TextProperty, v);
    public static IReadOnlyList<Citation>? GetCitations(DependencyObject d) => (IReadOnlyList<Citation>?)d.GetValue(CitationsProperty);
    public static void SetCitations(DependencyObject d, IReadOnlyList<Citation>? v) => d.SetValue(CitationsProperty, v);

    private static void Rerender(DependencyObject d)
    {
        if (d is TextBlock tb) Render(tb, GetText(tb) ?? "", GetCitations(tb));
    }

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`|\[\d{1,2}\])")]
    private static partial Regex Tokens();

    public static void Render(TextBlock target, string text, IReadOnlyList<Citation>? citations = null)
    {
        target.Inlines.Clear();
        var lines = text.Replace("\r", "").Split('\n');
        var first = true;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                if (!first) target.Inlines.Add(new LineBreak());
                continue;
            }
            if (!first) target.Inlines.Add(new LineBreak());
            first = false;

            var bullet = false;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• "))
            {
                bullet = true;
                line = trimmed[2..];
            }
            else if (Regex.IsMatch(trimmed, @"^\d+\.\s"))
            {
                target.Inlines.Add(new Run(trimmed[..(trimmed.IndexOf('.') + 1)] + " ") { Foreground = Fmt.Res("B.AccentText"), FontWeight = FontWeights.SemiBold });
                line = trimmed[(trimmed.IndexOf('.') + 1)..].TrimStart();
            }
            else if (trimmed.StartsWith('#'))
            {
                target.Inlines.Add(new Run(trimmed.TrimStart('#', ' ')) { FontWeight = FontWeights.SemiBold, FontSize = target.FontSize + 1 });
                continue;
            }
            if (bullet) target.Inlines.Add(new Run("  •  ") { Foreground = Fmt.Res("B.AccentText") });

            foreach (var part in Tokens().Split(line))
            {
                if (part.Length == 0) continue;
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                    target.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.SemiBold });
                else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                    target.Inlines.Add(new Run(part[1..^1]) { FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), Background = Fmt.Res("B.Subtle") });
                else if (part.StartsWith('[') && part.EndsWith(']') && int.TryParse(part[1..^1], out var n) &&
                         citations?.FirstOrDefault(c => c.Number == n) is { } cite)
                {
                    var link = new Hyperlink(new Run($"[{n}]")) { Foreground = Fmt.Res("B.AccentText"), TextDecorations = null, Cursor = Cursors.Hand, ToolTip = $"{cite.Source}: {cite.Title}" };
                    link.Click += (_, _) => AppLauncher.OpenUrl(cite.Url);
                    target.Inlines.Add(link);
                }
                else target.Inlines.Add(new Run(part));
            }
        }
    }
}
