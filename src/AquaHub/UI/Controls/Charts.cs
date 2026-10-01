using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace AquaHub.UI.Controls;

/// <summary>
/// Gives drawn charts a screen-reader description ("CPU: 34%", "Trend: up 1.2%"): the element's own
/// AutomationProperties.Name, if any, followed by a summary of its current values.
/// </summary>
internal sealed class ChartAutomationPeer : FrameworkElementAutomationPeer
{
    private readonly Func<string> _describe;
    private readonly AutomationControlType _type;

    public ChartAutomationPeer(FrameworkElement owner, AutomationControlType type, Func<string> describe) : base(owner)
    {
        _type = type;
        _describe = describe;
    }

    protected override string GetNameCore()
    {
        var label = base.GetNameCore();
        var value = _describe();
        return string.IsNullOrEmpty(label) ? value : string.IsNullOrEmpty(value) ? label : label + ": " + value;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => _type;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    // The base keeps hidden (collapsed) charts out of the control view; visible ones are content.
    protected override bool IsContentElementCore() => base.IsControlElementCore();

    internal static string Trend(IReadOnlyList<double>? values)
    {
        var v = values?.Where(x => !double.IsNaN(x)).ToList();
        if (v is null || v.Count < 2 || v[0] == 0) return "";
        var change = (v[^1] - v[0]) / Math.Abs(v[0]) * 100;
        return Math.Abs(change) < 0.05 ? "flat" : (change > 0 ? "up " : "down ") + Math.Abs(change).ToString("0.0", CultureInfo.CurrentCulture) + "%";
    }
}

/// <summary>Minimal line + soft area sparkline with an optional dashed baseline (e.g. previous close).</summary>
public sealed class Sparkline : FrameworkElement
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ChartAutomationPeer(this, AutomationControlType.Image, () => ChartAutomationPeer.Trend(Values) is { Length: > 0 } t ? "trend " + t : "");

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(nameof(Baseline), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillAreaProperty = DependencyProperty.Register(nameof(FillArea), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Baseline { get => (double)GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }
    public bool FillArea { get => (bool)GetValue(FillAreaProperty); set => SetValue(FillAreaProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    static Sparkline() => IsHitTestVisibleProperty.OverrideMetadata(typeof(Sparkline), new UIPropertyMetadata(false));

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values?.Where(v => !double.IsNaN(v)).ToArray();
        if (values is null || values.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var min = values.Min();
        var max = values.Max();
        if (!double.IsNaN(Baseline) && Baseline > 0) { min = Math.Min(min, Baseline); max = Math.Max(max, Baseline); }
        var range = Math.Max(max - min, Math.Abs(max) * 1e-6 + 1e-9);
        var w = ActualWidth;
        var h = ActualHeight;
        var pad = Thickness;
        double X(int i) => i * (w / (values.Length - 1));
        double Y(double v) => pad + (h - 2 * pad) * (1 - (v - min) / range);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(0), Y(values[0])), false, false);
            for (var i = 1; i < values.Length; i++) ctx.LineTo(new Point(X(i), Y(values[i])), true, true);
        }
        line.Freeze();

        if (FillArea && Stroke is SolidColorBrush sc)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(X(0), h), true, true);
                for (var i = 0; i < values.Length; i++) ctx.LineTo(new Point(X(i), Y(values[i])), true, true);
                ctx.LineTo(new Point(X(values.Length - 1), h), true, true);
            }
            area.Freeze();
            var c = sc.Color;
            var fill = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            dc.DrawGeometry(fill, null, area);
        }

        if (!double.IsNaN(Baseline) && Baseline > 0)
        {
            var pen = new Pen(Stroke, 1) { DashStyle = new DashStyle(new[] { 2.0, 3.0 }, 0) };
            pen.Brush = pen.Brush.Clone();
            pen.Brush.Opacity = 0.45;
            var y = Y(Baseline);
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
        }
        dc.DrawGeometry(null, new Pen(Stroke, Thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        // End dot
        var lastPoint = new Point(X(values.Length - 1), Y(values[^1]));
        dc.DrawEllipse(Stroke, null, lastPoint, Thickness + 0.9, Thickness + 0.9);
    }
}

/// <summary>Interactive price chart: area line, baseline, min/max labels and a hover crosshair with value + time.</summary>
public sealed class LineChart : FrameworkElement
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this, AutomationControlType.Image, () =>
    {
        var v = Values?.Where(x => !double.IsNaN(x)).ToList();
        if (v is null || v.Count < 2) return "price chart, no data";
        var f = "N" + Decimals;
        return $"price chart, {ChartAutomationPeer.Trend(v)}, low {v.Min().ToString(f, CultureInfo.CurrentCulture)}, high {v.Max().ToString(f, CultureInfo.CurrentCulture)}, last {v[^1].ToString(f, CultureInfo.CurrentCulture)}";
    });

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TimesProperty = DependencyProperty.Register(nameof(Times), typeof(IReadOnlyList<long>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BaselineProperty = DependencyProperty.Register(nameof(Baseline), typeof(double), typeof(LineChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TimeFormatProperty = DependencyProperty.Register(nameof(TimeFormat), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("HH:mm", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(LineChart),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<long>? Times { get => (IReadOnlyList<long>?)GetValue(TimesProperty); set => SetValue(TimesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public double Baseline { get => (double)GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }
    public string TimeFormat { get => (string)GetValue(TimeFormatProperty); set => SetValue(TimeFormatProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

    /// <summary>Currency of the values (EUR, USD, GBp…); empty for index points. Used for the hover price.</summary>
    public static readonly DependencyProperty CurrencyProperty = DependencyProperty.Register(nameof(Currency), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Currency { get => (string)GetValue(CurrencyProperty); set => SetValue(CurrencyProperty, value); }

    private Point? _hover;
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public LineChart()
    {
        Cursor = Cursors.Cross;
    }

    protected override void OnMouseMove(MouseEventArgs e) { _hover = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }

    /// <summary>1, 2 or 5 × 10ⁿ step giving roughly <paramref name="ticks"/> intervals over <paramref name="range"/>.</summary>
    internal static double NiceStep(double range, int ticks)
    {
        if (range <= 0 || double.IsNaN(range)) return 1;
        var rough = range / ticks;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var n = rough / magnitude;
        return (n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10) * magnitude;
    }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable
        var raw = Values;
        if (raw is null || raw.Count < 2) return;
        var idx = Enumerable.Range(0, raw.Count).Where(i => !double.IsNaN(raw[i])).ToArray();
        if (idx.Length < 2) return;
        var values = idx.Select(i => raw[i]).ToArray();
        var times = Times is { } t && t.Count == raw.Count ? idx.Select(i => t[i]).ToArray() : null;

        const double right = 58, top = 8, bottom = 22;
        var w = Math.Max(10, ActualWidth - right);
        var h = Math.Max(10, ActualHeight - top - bottom);
        var min = values.Min();
        var max = values.Max();
        if (!double.IsNaN(Baseline) && Baseline > 0) { min = Math.Min(min, Baseline); max = Math.Max(max, Baseline); }
        var pad = (max - min) * 0.08 + 1e-9;
        min -= pad; max += pad;
        // Round axis values (e.g. 225 / 230 / 235, not 226.83): snap the range to a "nice" step.
        var step = NiceStep(max - min, 4);
        min = Math.Floor(min / step) * step;
        max = Math.Ceiling(max / step) * step;
        double X(int i) => i * (w / (values.Length - 1));
        double Y(double v) => top + h * (1 - (v - min) / (max - min));
        var fmt = "N" + (step >= 1 ? 0 : Math.Min(6, (int)Math.Ceiling(-Math.Log10(step) - 1e-9)));

        // Grid lines + price labels on the right
        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle(new[] { 1.0, 4.0 }, 0) };
        for (var g = 0; min + g * step <= max + step / 2 && g <= 8; g++)
        {
            var v = min + g * step;
            var y = Y(v);
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            var label = Text(v.ToString(fmt, CultureInfo.CurrentCulture), 11, LabelBrush);
            dc.DrawText(label, new Point(w + 8, y - label.Height / 2));
        }

        if (!double.IsNaN(Baseline) && Baseline > 0)
        {
            var bp = new Pen(LabelBrush, 1) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) };
            dc.DrawLine(bp, new Point(0, Y(Baseline)), new Point(w, Y(Baseline)));
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(new Point(X(0), Y(values[0])), false, false);
            a.BeginFigure(new Point(X(0), top + h), true, true);
            a.LineTo(new Point(X(0), Y(values[0])), true, true);
            for (var i = 1; i < values.Length; i++)
            {
                l.LineTo(new Point(X(i), Y(values[i])), true, true);
                a.LineTo(new Point(X(i), Y(values[i])), true, true);
            }
            a.LineTo(new Point(X(values.Length - 1), top + h), true, true);
        }
        line.Freeze();
        area.Freeze();
        if (Stroke is SolidColorBrush sc)
        {
            var c = sc.Color;
            dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(80, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90), null, area);
        }
        dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, line);

        // Time labels (start / middle / end)
        if (times is not null)
        {
            foreach (var i in new[] { 0, values.Length / 2, values.Length - 1 })
            {
                var label = Text(DateTimeOffset.FromUnixTimeSeconds(times[i]).ToLocalTime().ToString(TimeFormat, CultureInfo.CurrentCulture), 11, LabelBrush);
                var x = Math.Clamp(X(i) - label.Width / 2, 0, w - label.Width);
                dc.DrawText(label, new Point(x, top + h + 5));
            }
        }

        // Crosshair
        if (_hover is { } p && p.X >= 0 && p.X <= w)
        {
            var i = (int)Math.Round(p.X / (w / (values.Length - 1)));
            i = Math.Clamp(i, 0, values.Length - 1);
            var pt = new Point(X(i), Y(values[i]));
            dc.DrawLine(new Pen(LabelBrush, 1), new Point(pt.X, top), new Point(pt.X, top + h));
            dc.DrawEllipse(Stroke, new Pen(Brushes.White, 1.5), pt, 4.5, 4.5);
            var when = times is null ? "" : "  ·  " + DateTimeOffset.FromUnixTimeSeconds(times[i]).ToLocalTime().ToString(TimeFormat.Length <= 5 ? "ddd HH:mm" : TimeFormat, CultureInfo.CurrentCulture);
            // The axis rounds to its step; the hover shows the exact price, cents included.
            var tip = Text(ViewModels.Fmt.PriceExact(values[i], Currency) + when, 12, Brushes.White, bold: true);
            var box = new Rect(Math.Clamp(pt.X - tip.Width / 2 - 8, 0, Math.Max(0, w - tip.Width - 16)), top, tip.Width + 16, tip.Height + 8);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(230, 20, 24, 30)), null, box, 6, 6);
            dc.DrawText(tip, new Point(box.X + 8, box.Y + 4));
        }
    }
}

/// <summary>Circular gauge (e.g. CPU %) with centred value and caption.</summary>
public sealed class RingGauge : FrameworkElement
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ChartAutomationPeer(this, AutomationControlType.ProgressBar, () => Label is { Length: > 0 } l ? l : Value.ToString("0", CultureInfo.CurrentCulture) + "%");

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(nameof(ValueBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–100.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush ValueBrush { get => (Brush)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - Thickness / 2;
        const double start = 135, sweepTotal = 270;
        DrawArc(dc, c, r, start, sweepTotal, new Pen(TrackBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var v = Math.Clamp(Value, 0, 100);
        if (v > 0.5) DrawArc(dc, c, r, start, sweepTotal * v / 100, new Pen(ValueBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var text = new FormattedText(Label.Length > 0 ? Label : $"{v:0}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size * 0.22, TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        if (sweepDeg <= 0) return;
        double Rad(double d) => d * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(Rad(startDeg)), c.Y + r * Math.Sin(Rad(startDeg)));
        var p1 = new Point(c.X + r * Math.Cos(Rad(startDeg + sweepDeg)), c.Y + r * Math.Sin(Rad(startDeg + sweepDeg)));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Segment of a track (e.g. a day's min–max temperature within the week's range).</summary>
public sealed class RangeBar : FrameworkElement
{
    public static readonly DependencyProperty StartProperty = DependencyProperty.Register(nameof(Start), typeof(double), typeof(RangeBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LengthProperty = DependencyProperty.Register(nameof(Length), typeof(double), typeof(RangeBar),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(RangeBar),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(RangeBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Start { get => (double)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double Length { get => (double)GetValue(LengthProperty); set => SetValue(LengthProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 5 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight;
        var r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, ActualWidth, h), r, r);
        var x = ActualWidth * Math.Clamp(Start, 0, 1);
        var w = Math.Max(h, ActualWidth * Math.Clamp(Length, 0, 1));
        if (x + w > ActualWidth) x = Math.Max(0, ActualWidth - w);
        dc.DrawRoundedRectangle(Fill, null, new Rect(x, 0, w, h), r, r);
    }
}

/// <summary>Thin rounded progress/probability bar.</summary>
public sealed class Bar : FrameworkElement
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ChartAutomationPeer(this, AutomationControlType.ProgressBar, () => (Value * 100).ToString("0", CultureInfo.CurrentCulture) + "%");

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Bar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(Bar),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(Bar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0–1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 6 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var h = ActualHeight;
        var r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, ActualWidth, h), r, r);
        var w = ActualWidth * Math.Clamp(Value, 0, 1);
        if (w > 0.5) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(w, h), h), r, r);
    }
}
