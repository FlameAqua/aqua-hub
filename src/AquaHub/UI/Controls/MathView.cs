using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using AquaHub.Core.Ai.Assistant;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;
using XamlMath.Boxes;
using XamlMath.Rendering;
using XamlMath.Rendering.Transformations;
using Size = System.Windows.Size;
using TexPoint = XamlMath.Rendering.Point;
using TexRectangle = XamlMath.Rendering.Rectangle;

namespace AquaHub.UI.Controls;

/// <summary>
/// A LaTeX formula drawn by XAML-Math as shapes in the theme's text colour (so it follows light and dark). Inline
/// formulas sit on the line's baseline; display ones stand on their own. Drawings are cached by formula and size, so an
/// answer drawn again as it streams in doesn't parse its formulas again. <see cref="Create"/> returns null for a formula
/// XAML-Math can't draw (the chat then shows the LaTeX as written).
/// </summary>
public sealed class MathView : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(MathView), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>The formula's shapes with their top-left at (0, 0), its size and where its baseline is (from the top).</summary>
    private sealed record Drawn(Geometry Geometry, double Width, double Height, double Baseline);

    private static readonly Dictionary<(string Latex, bool Display, double Scale), Drawn?> Cache = new();
    private static readonly Dictionary<(bool Display, double Scale), TexEnvironment> Environments = new();
    private readonly Drawn _drawn;

    private MathView(string latex, bool display, Drawn drawn)
    {
        Latex = latex;
        Display = display;
        _drawn = drawn;
        Focusable = false;
        SnapsToDevicePixels = true;
        SetResourceReference(ForegroundProperty, "B.Text");
    }

    /// <summary>The LaTeX as the model wrote it.</summary>
    public string Latex { get; }
    public bool Display { get; }
    /// <summary>How far the formula reaches below its baseline (an inline one is moved down by this to sit on the line).</summary>
    public double Depth => Math.Max(0, _drawn.Height - _drawn.Baseline);

    /// <summary>The formula drawn to go with text of <paramref name="fontSize"/>, or null when it can't be drawn.</summary>
    public static MathView? Create(string latex, bool display, double fontSize)
    {
        // Computer Modern looks smaller than Segoe UI at the same size.
        var scale = Math.Round(fontSize * (display ? 1.3 : 1.15), 1);
        var key = (latex, display, scale);
        Drawn? drawn;
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out drawn))
            {
                if (Cache.Count > 400) Cache.Clear();
                drawn = Cache[key] = Draw(latex, display, scale);
            }
        }
        return drawn is null ? null : new MathView(latex, display, drawn);
    }

    private static Drawn? Draw(string latex, bool display, double scale) =>
        MathText.ForDrawing(latex) is { } tex ? FormulaThread.Run(() => Layout(tex, display, scale)) : null;

    private static Drawn? Layout(string tex, bool display, double scale)
    {
        try
        {
            var formula = WpfTeXFormulaParser.Instance.Parse(tex, null);
            var group = new GeometryGroup();
            var probe = new RootProbe(new GeometryElementRenderer(group, scale));
            formula.RenderTo(probe, EnvironmentFor(display, scale), 0, 0);
            if (probe.Root is not { } root) return null;
            var bounds = group.Bounds;
            var ink = !bounds.IsEmpty;
            var left = ink ? Math.Min(0, bounds.Left) : 0;
            var top = ink ? Math.Min(0, bounds.Top) : 0;
            var width = Math.Max(root.TotalWidth * scale, ink ? bounds.Right : 0) - left;
            var height = Math.Max(root.TotalHeight * scale, ink ? bounds.Bottom : 0) - top;
            if (width <= 0 || height <= 0) return null;
            group.Transform = new TranslateTransform(-left, -top);
            // Made on the formula thread: only a frozen drawing can be used on the UI thread.
            if (!group.CanFreeze) return null;
            group.Freeze();
            return new Drawn(group, Math.Ceiling(width + 1), Math.Ceiling(height + 1), root.Height * scale - top);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // XAML-Math reports unknown commands as TexParseException, and a few malformed inputs crash it differently.
            Core.Util.Log.Debug("ask", "A formula couldn't be drawn: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The one thread formulas are parsed and laid out on, with a stack 20 times the UI thread's: XAML-Math recurses,
    /// and a stack overflow ends the process whatever tries to catch it. <see cref="MathText.Drawable"/> keeps formulas
    /// inside a quarter of the UI thread's stack; this is room for anything it doesn't know about. The UI thread waits
    /// for each formula, as it did when it drew them itself (a few milliseconds, once: drawings are cached).
    /// </summary>
    private static class FormulaThread
    {
        private static readonly BlockingCollection<Action> Work = new();

        static FormulaThread()
        {
            var thread = new Thread(() => { foreach (var work in Work.GetConsumingEnumerable()) work(); }, 32 * 1024 * 1024)
            {
                IsBackground = true,
                Name = "Formulas",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public static T Run<T>(Func<T> work)
        {
            T result = default!;
            Exception? error = null;
            using var done = new ManualResetEventSlim();
            Work.Add(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (error is not null) ExceptionDispatchInfo.Throw(error);
            return result;
        }
    }

    /// <summary>One environment per style and size (looking the text font up walks every installed font).</summary>
    private static TexEnvironment EnvironmentFor(bool display, double scale)
    {
        lock (Environments)
        {
            if (Environments.TryGetValue((display, scale), out var environment)) return environment;
            if (Environments.Count > 16) Environments.Clear();
            return Environments[(display, scale)] = WpfTeXEnvironment.Create(display ? TexStyle.Display : TexStyle.Text, scale, "Segoe UI");
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(_drawn.Width, _drawn.Height);

    protected override void OnRender(DrawingContext dc) => dc.DrawGeometry(Foreground, null, _drawn.Geometry);

    protected override AutomationPeer OnCreateAutomationPeer() => new MathPeer(this);

    /// <summary>Screen readers hear the formula's LaTeX.</summary>
    private sealed class MathPeer(MathView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(MathView);
        protected override string GetNameCore() => "Formula: " + owner.Latex;
    }

    /// <summary>Passes everything on, keeping the first (root) box: its height above the baseline places the formula on the line.</summary>
    private sealed class RootProbe(IElementRenderer inner) : IElementRenderer
    {
        public Box? Root { get; private set; }

        public void RenderElement(Box box, double x, double y)
        {
            Root ??= box;
            inner.RenderElement(box, x, y);
        }

        public void RenderCharacter(CharInfo info, double x, double y, IBrush? foreground) => inner.RenderCharacter(info, x, y, foreground);
        public void RenderRectangle(TexRectangle rectangle, IBrush? foreground) => inner.RenderRectangle(rectangle, foreground);
        public void RenderLine(TexPoint point0, TexPoint point1, IBrush? foreground) => inner.RenderLine(point0, point1, foreground);
        public void RenderTransformed(Box box, IEnumerable<Transformation> transforms, double x, double y) => inner.RenderTransformed(box, transforms, x, y);
        public void FinishRendering() => inner.FinishRendering();
    }
}
