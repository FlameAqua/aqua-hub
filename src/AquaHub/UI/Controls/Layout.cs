using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AquaHub.UI.Controls;

/// <summary>
/// Responsive masonry grid: picks 1…MaxColumns columns from the available width and places each child
/// (which may span several columns) where it leaves the smallest gap. Used for the dashboard.
/// </summary>
public sealed class FlowGrid : Panel
{
    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached("Span", typeof(int), typeof(FlowGrid),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(FlowGrid),
        new FrameworkPropertyMetadata(330.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(FlowGrid),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(FlowGrid),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static int GetSpan(DependencyObject d) => (int)d.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject d, int v) => d.SetValue(SpanProperty, v);
    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private readonly List<Rect> _slots = new();

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * MaxColumns + Gap * (MaxColumns - 1) : available.Width;
        return Layout(width, measure: true);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, measure: false);
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (i < _slots.Count) child.Arrange(_slots[i]);
            i++;
        }
        return finalSize;
    }

    private Size Layout(double width, bool measure)
    {
        _slots.Clear();
        var gap = Gap;
        var cols = (int)Math.Clamp(Math.Floor((width + gap) / (MinColumnWidth + gap)), 1, Math.Max(1, MaxColumns));
        var colW = Math.Max(0, (width - gap * (cols - 1)) / cols);
        var heights = new double[cols];
        var children = InternalChildren.Cast<UIElement>().ToList();
        var slots = new Rect[children.Count];
        var placed = new bool[children.Count];

        void Place(int index)
        {
            var child = children[index];
            placed[index] = true;
            if (child.Visibility == Visibility.Collapsed) { slots[index] = new Rect(); return; }
            var span = Math.Clamp(GetSpan(child), 1, cols);
            var (col, top) = BestSlot(heights, span, cols);
            var w = colW * span + gap * (span - 1);
            if (measure) child.Measure(new Size(w, double.PositiveInfinity));
            var h = child.DesiredSize.Height;
            slots[index] = new Rect(col * (colW + gap), top, w, h);
            for (var k = col; k < col + span; k++) heights[k] = top + h + gap;
        }

        for (var i = 0; i < children.Count; i++)
        {
            if (placed[i]) continue;
            var span = Math.Clamp(GetSpan(children[i]), 1, cols);
            if (span > 1)
            {
                // Dense packing: before a wide card, fill any hole it would leave with later single-column cards.
                for (var guard = 0; guard < children.Count; guard++)
                {
                    var (_, top) = BestSlot(heights, span, cols);
                    var shortest = Array.IndexOf(heights, heights.Min());
                    if (top - heights[shortest] < 120) break;
                    var filler = Enumerable.Range(i + 1, children.Count - i - 1)
                        .FirstOrDefault(j => !placed[j] && children[j].Visibility != Visibility.Collapsed && GetSpan(children[j]) <= 1, -1);
                    if (filler < 0) break;
                    Place(filler);
                }
            }
            Place(i);
        }
        _slots.AddRange(slots);
        var total = heights.Length == 0 ? 0 : Math.Max(0, heights.Max() - gap);
        return new Size(width, total);
    }

    private static (int Col, double Top) BestSlot(double[] heights, int span, int cols)
    {
        var bestCol = 0;
        var bestTop = double.MaxValue;
        for (var c = 0; c + span <= cols; c++)
        {
            var top = 0.0;
            for (var k = c; k < c + span; k++) top = Math.Max(top, heights[k]);
            if (top < bestTop - 0.5) { bestTop = top; bestCol = c; }
        }
        return (bestCol, bestTop);
    }
}

/// <summary>Loading placeholder with a gentle shimmer sweep.</summary>
public sealed class Shimmer : Border
{
    public Shimmer()
    {
        CornerRadius = new CornerRadius(6);
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Background = null;
    }

    private void Start()
    {
        var baseBrush = TryFindResource("B.Skeleton") as SolidColorBrush ?? new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
        var c = baseBrush.Color;
        var highlight = Color.FromArgb((byte)Math.Min(255, c.A * 2.2), c.R, c.G, c.B);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops = { new GradientStop(c, 0.3), new GradientStop(highlight, 0.5), new GradientStop(c, 0.7) },
        };
        var shift = new TranslateTransform();
        brush.RelativeTransform = shift;
        Background = brush;
        if (SystemParameters.ClientAreaAnimation)
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}

/// <summary>
/// Borders and panels have no automation peer, so their AutomationProperties.Name/AutomationId are dropped and
/// their contents look like loose controls. <see cref="GroupBorder"/> and <see cref="GroupGrid"/> are seen as a
/// named group (a popup, a panel, a banner) whose children are announced as part of it.
/// </summary>
internal sealed class GroupPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    // The base keeps hidden (collapsed) groups out of the control view; named, visible groups are content too.
    protected override bool IsContentElementCore() => base.IsControlElementCore() && !string.IsNullOrEmpty(GetNameCore());
}

/// <summary>A Border that screen readers and UI Automation see as a group (see <see cref="GroupPeer"/>).</summary>
public sealed class GroupBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
}

/// <summary>A Grid that screen readers and UI Automation see as a group (see <see cref="GroupPeer"/>).</summary>
public sealed class GroupGrid : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
}

/// <summary>Illustrated weather glyph for WMO weather codes (day/night aware).</summary>
public sealed class WeatherIcon : FrameworkElement
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(nameof(Code), typeof(int), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsDayProperty = DependencyProperty.Register(nameof(IsDay), typeof(bool), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Cloud colours come from the theme (B.WeatherCloud / B.WeatherCloudEdge) so light clouds stay visible on a light page.</summary>
    public static readonly DependencyProperty CloudBrushProperty = DependencyProperty.Register(nameof(CloudBrush), typeof(Brush), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CloudEdgeBrushProperty = DependencyProperty.Register(nameof(CloudEdgeBrush), typeof(Brush), typeof(WeatherIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Code { get => (int)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public bool IsDay { get => (bool)GetValue(IsDayProperty); set => SetValue(IsDayProperty, value); }
    public Brush? CloudBrush { get => (Brush?)GetValue(CloudBrushProperty); set => SetValue(CloudBrushProperty, value); }
    public Brush? CloudEdgeBrush { get => (Brush?)GetValue(CloudEdgeBrushProperty); set => SetValue(CloudEdgeBrushProperty, value); }

    private static readonly Brush SunFill = Freeze(new RadialGradientBrush(Color.FromRgb(0xFF, 0xD8, 0x5E), Color.FromRgb(0xFF, 0xA6, 0x2B)));
    private static readonly Brush MoonFill = Freeze(new LinearGradientBrush(Color.FromRgb(0xF1, 0xF4, 0xFA), Color.FromRgb(0xB9, 0xC5, 0xD8), 45));
    private static readonly Brush CloudFill = Freeze(new LinearGradientBrush(Color.FromRgb(0xF7, 0xF9, 0xFC), Color.FromRgb(0xC9, 0xD3, 0xE0), 90));
    private static readonly Brush DarkCloudFill = Freeze(new LinearGradientBrush(Color.FromRgb(0xB7, 0xC1, 0xCF), Color.FromRgb(0x84, 0x90, 0xA2), 90));
    private static readonly Pen CloudEdge = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 0.8));
    private static readonly Pen RainPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0x4D, 0xA8, 0xFF)), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen FogPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0xA9, 0xB4, 0xC2)), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen RayPen = FreezePen(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xBE, 0x3B)), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Brush SnowFill = Freeze(new SolidColorBrush(Color.FromRgb(0xE3, 0xF1, 0xFF)));
    private static readonly Brush BoltFill = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 32 : Width, double.IsNaN(Height) ? 32 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 64, size / 64));

        var code = Code;
        var cloudFill = CloudBrush ?? CloudFill;
        var edge = CloudEdgeBrush is { } eb ? new Pen(eb, 0.8) : CloudEdge;
        if (!ReferenceEquals(edge, CloudEdge)) edge.Freeze();
        var clear = code is 0 or 1;
        var partly = code == 2;
        var fog = code is 45 or 48;
        var drizzle = code is >= 51 and <= 57;
        var rain = code is (>= 61 and <= 67) or (>= 80 and <= 82);
        var snow = code is (>= 71 and <= 77) or 85 or 86;
        var thunder = code >= 95;

        if (clear) { DrawCelestial(dc, new Point(32, 32), 13, IsDay); }
        else
        {
            if (partly) DrawCelestial(dc, new Point(24, 22), 10, IsDay);
            var dark = rain || thunder || code == 3 && !IsDay;
            DrawCloud(dc, partly ? new Point(36, 38) : new Point(32, 30), partly ? 0.85 : 1.0, dark ? DarkCloudFill : cloudFill, edge);
            if (code == 3) DrawCloud(dc, new Point(22, 36), 0.6, cloudFill, edge);
            if (fog) for (var i = 0; i < 3; i++) dc.DrawLine(FogPen, new Point(14 + i * 3, 47 + i * 6), new Point(50 - i * 3, 47 + i * 6));
            if (drizzle) for (var i = 0; i < 3; i++) dc.DrawLine(RainPen, new Point(23 + i * 9, 48), new Point(21 + i * 9, 53));
            if (rain) for (var i = 0; i < 4; i++) dc.DrawLine(RainPen, new Point(19 + i * 9, 47), new Point(15 + i * 9, 57));
            if (snow) for (var i = 0; i < 4; i++) dc.DrawEllipse(SnowFill, edge, new Point(19 + i * 9, 51 + (i % 2) * 5), 2.8, 2.8);
            if (thunder)
            {
                var bolt = Geometry.Parse("M 33 42 L 25 54 L 31 54 L 28 63 L 39 49 L 33 49 L 36 42 Z");
                dc.DrawGeometry(BoltFill, null, bolt);
            }
        }
        dc.Pop();
        dc.Pop();
    }

    private static void DrawCelestial(DrawingContext dc, Point c, double r, bool day)
    {
        if (day)
        {
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                dc.DrawLine(RayPen, new Point(c.X + Math.Cos(a) * (r + 4), c.Y + Math.Sin(a) * (r + 4)),
                    new Point(c.X + Math.Cos(a) * (r + 8.5), c.Y + Math.Sin(a) * (r + 8.5)));
            }
            dc.DrawEllipse(SunFill, null, c, r, r);
        }
        else
        {
            var moon = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(c, r, r), new EllipseGeometry(new Point(c.X + r * 0.55, c.Y - r * 0.45), r * 0.85, r * 0.85));
            dc.DrawGeometry(MoonFill, null, moon);
        }
    }

    private static void DrawCloud(DrawingContext dc, Point c, double s, Brush fill, Pen edge)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(c.X - 10 * s, c.Y + 3 * s), 9 * s, 9 * s));
        g.Children.Add(new EllipseGeometry(new Point(c.X + 2 * s, c.Y - 3 * s), 12.5 * s, 12.5 * s));
        g.Children.Add(new EllipseGeometry(new Point(c.X + 13 * s, c.Y + 4 * s), 8.5 * s, 8.5 * s));
        g.Children.Add(new RectangleGeometry(new Rect(c.X - 19 * s, c.Y + 3 * s, 40 * s, 9.5 * s), 4.5 * s, 4.5 * s));
        var outline = g.GetOutlinedPathGeometry();
        dc.DrawGeometry(fill, edge, outline);
    }
}
