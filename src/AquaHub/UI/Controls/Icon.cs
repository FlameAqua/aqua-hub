using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AquaHub.UI.Controls;

/// <summary>
/// Crisp vector icons (24×24 design grid, 1.75 stroke, round caps) — an original, consistent set
/// drawn with WPF geometry so it scales perfectly at any DPI and inherits the text colour.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Kind { get => (string?)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }

    static Icon()
    {
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Icon), new UIPropertyMetadata(false));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(true));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsNaN(Width) ? 16 : Width;
        var h = double.IsNaN(Height) ? 16 : Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var icon = IconData.Get(Kind);
        if (icon is null) return;
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var scale = size / 24.0;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (icon.Value.Filled)
        {
            dc.DrawGeometry(Foreground, null, icon.Value.Geometry);
        }
        else
        {
            var pen = new Pen(Foreground, StrokeThickness)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, icon.Value.Geometry);
        }
        dc.Pop();
        dc.Pop();
    }
}

public static class IconData
{
    private static readonly Dictionary<string, (Geometry Geometry, bool Filled)> Cache = new();

    // "F:" prefix = filled shape; otherwise stroked outline.
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["home"] = "M3.5 10.5 L12 3.5 L20.5 10.5 M5.5 9 V20 H18.5 V9 M10 20 V14.5 H14 V20",
        ["news"] = "M4 5 H16.5 V18 A2 2 0 0 0 18.5 20 H6 A2 2 0 0 1 4 18 Z M16.5 9 H20 V18 A2 2 0 0 1 18.5 20 M7.5 8.5 H13 M7.5 12 H13 M7.5 15.5 H11",
        ["social"] = "M9 11 A3.5 3.5 0 1 0 9 4 A3.5 3.5 0 1 0 9 11 Z M3 20 C3 16.4 5.7 14 9 14 C12.3 14 15 16.4 15 20 M16 4.3 A3.5 3.5 0 0 1 16 10.7 M18 14.3 C20 15 21 17 21 20",
        ["markets"] = "M3.5 17 L9 11.5 L13 15.5 L20.5 8 M15 8 H20.5 V13.5",
        ["trend-down"] = "M3.5 7 L9 12.5 L13 8.5 L20.5 16 M15 16 H20.5 V10.5",
        ["upcoming"] = "M4 6.5 A1.5 1.5 0 0 1 5.5 5 H18.5 A1.5 1.5 0 0 1 20 6.5 V18.5 A1.5 1.5 0 0 1 18.5 20 H5.5 A1.5 1.5 0 0 1 4 18.5 Z M4 10 H20 M8 3 V7 M16 3 V7 M8 14 H8.01 M12 14 H12.01 M16 14 H16.01",
        ["system"] = "M7 7 H17 V17 H7 Z M10 10 H14 V14 H10 Z M9.5 3 V7 M14.5 3 V7 M9.5 17 V21 M14.5 17 V21 M3 9.5 H7 M3 14.5 H7 M17 9.5 H21 M17 14.5 H21",
        ["launchpad"] = "M4.5 4.5 H10 V10 H4.5 Z M14 4.5 H19.5 V10 H14 Z M4.5 14 H10 V19.5 H4.5 Z M14 14 H19.5 V19.5 H14 Z",
        ["ask"] = "M11 3 L12.9 8.1 L18 10 L12.9 11.9 L11 17 L9.1 11.9 L4 10 L9.1 8.1 Z M18.5 15 L19.2 17.3 L21.5 18 L19.2 18.7 L18.5 21 L17.8 18.7 L15.5 18 L17.8 17.3 Z",
        ["agents"] = "M6.5 9 H17.5 A2.5 2.5 0 0 1 20 11.5 V17.5 A2.5 2.5 0 0 1 17.5 20 H6.5 A2.5 2.5 0 0 1 4 17.5 V11.5 A2.5 2.5 0 0 1 6.5 9 Z M12 5.5 V9 M12 3.5 V3.51 M9.5 14 V15 M14.5 14 V15 M2 13.5 V16 M22 13.5 V16",
        ["settings"] = "M4 6 H13.5 M18 6 H20 M15.75 3.75 V8.25 M4 12 H7.5 M12 12 H20 M9.75 9.75 V14.25 M4 18 H13.5 M18 18 H20 M15.75 15.75 V20.25",
        ["search"] = "M17.5 10.5 A7 7 0 1 1 3.5 10.5 A7 7 0 1 1 17.5 10.5 Z M15.5 15.5 L20.5 20.5",
        ["bell"] = "M6 16 V11 A6 6 0 0 1 18 11 V16 L19.5 17.5 H4.5 Z M10 20.5 H14",
        ["refresh"] = "M20 11 A8 8 0 0 0 5.6 6.4 L4 8 M4 3.5 V8 H8.5 M4 13 A8 8 0 0 0 18.4 17.6 L20 16 M20 20.5 V16 H15.5",
        ["play"] = "F:M8 5.2 C8 4.4 8.9 3.9 9.6 4.4 L19 10.9 C19.6 11.3 19.6 12.3 19 12.7 L9.6 19.2 C8.9 19.7 8 19.2 8 18.4 Z",
        ["pause"] = "F:M6.5 5 A1 1 0 0 1 7.5 4 H9.5 A1 1 0 0 1 10.5 5 V19 A1 1 0 0 1 9.5 20 H7.5 A1 1 0 0 1 6.5 19 Z M13.5 5 A1 1 0 0 1 14.5 4 H16.5 A1 1 0 0 1 17.5 5 V19 A1 1 0 0 1 16.5 20 H14.5 A1 1 0 0 1 13.5 19 Z",
        ["next"] = "F:M5 6.2 C5 5.4 5.9 4.9 6.6 5.4 L14.5 11 C15.1 11.4 15.1 12.4 14.5 12.8 L6.6 18.4 C5.9 18.9 5 18.4 5 17.6 Z M17 5.5 A1 1 0 0 1 18 4.5 H18.5 A1 1 0 0 1 19.5 5.5 V18.5 A1 1 0 0 1 18.5 19.5 H18 A1 1 0 0 1 17 18.5 Z",
        ["previous"] = "F:M19 6.2 C19 5.4 18.1 4.9 17.4 5.4 L9.5 11 C8.9 11.4 8.9 12.4 9.5 12.8 L17.4 18.4 C18.1 18.9 19 18.4 19 17.6 Z M7 5.5 A1 1 0 0 0 6 4.5 H5.5 A1 1 0 0 0 4.5 5.5 V18.5 A1 1 0 0 0 5.5 19.5 H6 A1 1 0 0 0 7 18.5 Z",
        ["volume"] = "M4 9.5 H7.5 L12.5 5.5 V18.5 L7.5 14.5 H4 Z M16 9 A4.2 4.2 0 0 1 16 15 M18.8 6.3 A8 8 0 0 1 18.8 17.7",
        ["mute"] = "M4 9.5 H7.5 L12.5 5.5 V18.5 L7.5 14.5 H4 Z M16.5 9.5 L21 14 M21 9.5 L16.5 14",
        ["pin"] = "M9 3.5 H15 L14 10 L17 13 V14.5 H7 V13 L10 10 Z M12 14.5 V20.5",
        ["star"] = "M12 3.5 L14.6 8.9 L20.5 9.7 L16.2 13.8 L17.3 19.6 L12 16.8 L6.7 19.6 L7.8 13.8 L3.5 9.7 L9.4 8.9 Z",
        ["star-filled"] = "F:M12 3.5 L14.6 8.9 L20.5 9.7 L16.2 13.8 L17.3 19.6 L12 16.8 L6.7 19.6 L7.8 13.8 L3.5 9.7 L9.4 8.9 Z",
        ["external"] = "M14 4 H20 V10 M20 4 L11 13 M18 14 V18.5 A1.5 1.5 0 0 1 16.5 20 H5.5 A1.5 1.5 0 0 1 4 18.5 V7.5 A1.5 1.5 0 0 1 5.5 6 H10",
        ["close"] = "M6 6 L18 18 M18 6 L6 18",
        ["chevron-right"] = "M9.5 5.5 L16 12 L9.5 18.5",
        ["chevron-left"] = "M14.5 5.5 L8 12 L14.5 18.5",
        ["chevron-down"] = "M5.5 9.5 L12 16 L18.5 9.5",
        ["chevron-up"] = "M5.5 14.5 L12 8 L18.5 14.5",
        ["arrow-up"] = "M12 19 V5.5 M6 11 L12 5 L18 11",
        ["arrow-down"] = "M12 5 V18.5 M6 13 L12 19 L18 13",
        ["arrow-right"] = "M5 12 H18.5 M13 6 L19 12 L13 18",
        ["plus"] = "M12 5 V19 M5 12 H19",
        ["minus"] = "M5 12 H19",
        ["trash"] = "M4 7 H20 M9.5 7 V4.5 H14.5 V7 M6 7 L7 20 H17 L18 7 M10 11 V16 M14 11 V16",
        ["sun"] = "M16 12 A4 4 0 1 1 8 12 A4 4 0 1 1 16 12 Z M12 2.5 V4.5 M12 19.5 V21.5 M2.5 12 H4.5 M19.5 12 H21.5 M5.3 5.3 L6.7 6.7 M17.3 17.3 L18.7 18.7 M5.3 18.7 L6.7 17.3 M17.3 6.7 L18.7 5.3",
        ["bell-off"] = "M8.5 5.2 A6 6 0 0 1 18 11 V14.5 M6 9.8 V16 L4.5 17.5 H17.5 M10 20.5 H14 M4 4 L20 20",
        ["wifi-off"] = "M3 3 L21 21 M8.5 16.5 A5 5 0 0 1 15.5 16.5 M5 12.8 A10 10 0 0 1 10.2 10.1 M13.8 10 A10 10 0 0 1 19 12.8 M2 9.3 A15 15 0 0 1 6.4 6.6 M11 5.5 A15 15 0 0 1 22 9.3 M12 20 V20.01",
        ["moon"] = "M20 14.5 A8 8 0 1 1 9.5 4 A6.5 6.5 0 0 0 20 14.5 Z",
        ["clock"] = "M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M12 7 V12 L15.5 14",
        ["location"] = "M12 21 C12 21 5 14.8 5 9.8 A7 7 0 0 1 19 9.8 C19 14.8 12 21 12 21 Z M14.5 9.8 A2.5 2.5 0 1 1 9.5 9.8 A2.5 2.5 0 1 1 14.5 9.8 Z",
        ["globe"] = "M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M3.5 9 H20.5 M3.5 15 H20.5 M12 3 C15 6 15 18 12 21 C9 18 9 6 12 3 Z",
        ["bolt"] = "M13 3 L5 13.5 H11.5 L10.5 21 L19 10.5 H12.5 Z",
        ["game"] = "M7.5 8 H16.5 A4.5 4.5 0 0 1 21 12.5 V14 A3 3 0 0 1 15.8 16 L14.8 14.5 H9.2 L8.2 16 A3 3 0 0 1 3 14 V12.5 A4.5 4.5 0 0 1 7.5 8 Z M8 10.5 V13.5 M6.5 12 H9.5 M15.5 11.5 H15.51 M17.5 13 H17.51",
        ["mic"] = "M9 6 A3 3 0 0 1 15 6 V11 A3 3 0 0 1 9 11 Z M5.5 11 A6.5 6.5 0 0 0 18.5 11 M12 17.5 V21",
        ["link"] = "M10 14 L14 10 M8.5 11.5 L6.5 13.5 A3.5 3.5 0 0 0 11.5 18.5 L13.5 16.5 M15.5 12.5 L17.5 10.5 A3.5 3.5 0 0 0 12.5 5.5 L10.5 7.5",
        ["send"] = "M4.5 11.5 L20 4 L13.5 19.5 L11 13 Z M11 13 L20 4",
        ["check"] = "M5 12.5 L10 17 L19 7",
        ["alert"] = "M12 4 L21 19.5 H3 Z M12 10 V14 M12 17 V17.01",
        ["info"] = "M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M12 11 V16 M12 8 V8.01",
        ["cloud"] = "M7 18.5 H17 A4 4 0 0 0 17 10.5 A5.5 5.5 0 0 0 6.6 11.4 A3.6 3.6 0 0 0 7 18.5 Z",
        ["memory"] = "M3 8 H21 V16 H3 Z M7 11 V13 M11 11 V13 M15 11 V13 M6 16 V19 M18 16 V19",
        ["disk"] = "M3 14 L6 5 H18 L21 14 V19 H3 Z M3 14 H21 M16.5 16.5 H16.51 M13.5 16.5 H13.51",
        ["network"] = "M8 20 V5 M4 9 L8 5 L12 9 M16 4 V19 M12 15 L16 19 L20 15",
        ["gpu"] = "M2.5 7 H21.5 V17 H2.5 Z M6 17 V20 M10 17 V20 M14 17 V20 M18 17 V20 M11.5 12 A2.5 2.5 0 1 1 6.5 12 A2.5 2.5 0 1 1 11.5 12 Z M17.5 12 A2.5 2.5 0 1 1 12.5 12 A2.5 2.5 0 1 1 17.5 12 Z",
        ["wand"] = "M4.5 19.5 L14.5 9.5 M13 7.5 L16.5 11 M17.5 3.5 V6.5 M16 5 H19 M20 9 V11 M19 10 H21 M11 3.5 V5 M10.25 4.25 H11.75",
        ["focus"] = "M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M16 12 A4 4 0 1 1 8 12 A4 4 0 1 1 16 12 Z M12 12 H12.01",
        ["coffee"] = "M5 8.5 H16.5 V14 A5 5 0 0 1 11.5 19 H10 A5 5 0 0 1 5 14 Z M16.5 10 H18.5 A2 2 0 0 1 18.5 14 H16.5 M8.5 3.5 V5.5 M12 3.5 V5.5",
        ["music"] = "M9 18 V5.5 L20 3.5 V16 M9 18 A2.8 2.8 0 1 1 3.4 18 A2.8 2.8 0 1 1 9 18 Z M20 16 A2.8 2.8 0 1 1 14.4 16 A2.8 2.8 0 1 1 20 16 Z",
        ["briefcase"] = "M3.5 8 H20.5 V19 H3.5 Z M8.5 8 V5 H15.5 V8 M3.5 13 H20.5",
        ["book"] = "M5 5 A2 2 0 0 1 7 3.5 H19 V17.5 H7 A2 2 0 0 0 5 19.5 Z M5 19.5 A2 2 0 0 0 7 21 H19 V17.5",
        ["bookmark"] = "M6.5 3.5 H17.5 V20.5 L12 16.5 L6.5 20.5 Z",
        ["bookmark-filled"] = "F:M6.5 3.5 H17.5 V20.5 L12 16.5 L6.5 20.5 Z",
        ["dots"] = "M6 12 H6.01 M12 12 H12.01 M18 12 H18.01",
        ["fire"] = "M12 21 A6 6 0 0 0 18 15 C18 10 13 8.5 13 3 C10 6 8.5 8 8.5 10.5 C7.5 9.8 7 9 7 9 C5.8 11 6 13 6 15 A6 6 0 0 0 12 21 Z",
        ["eye"] = "M2.5 12 C5 7.2 8.3 5 12 5 C15.7 5 19 7.2 21.5 12 C19 16.8 15.7 19 12 19 C8.3 19 5 16.8 2.5 12 Z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12 Z",
        ["power"] = "M12 3 V11.5 M6.4 6.5 A8 8 0 1 0 17.6 6.5",
        ["copy"] = "M9 9 H20 V20 H9 Z M5 15 H4 V4 H15 V5",
        ["filter"] = "M3.5 5 H20.5 L14 12.5 V18.5 L10 20.5 V12.5 Z",
        ["layers"] = "M12 3.5 L21 8 L12 12.5 L3 8 Z M3 12.2 L12 16.7 L21 12.2 M3 16.4 L12 20.9 L21 16.4",
        ["wind"] = "M3 8.5 H13.5 A3 3 0 1 0 10.5 5.5 M3 12.5 H18 A3 3 0 1 1 15 15.5 M3 16.5 H9.5",
        ["droplet"] = "M12 3.5 C12 3.5 5.5 10.5 5.5 15 A6.5 6.5 0 0 0 18.5 15 C18.5 10.5 12 3.5 12 3.5 Z",
        ["shield"] = "M12 3 L19.5 6 V11.5 C19.5 16 16.3 19.3 12 21 C7.7 19.3 4.5 16 4.5 11.5 V6 Z M9 12 L11.2 14.2 L15.5 9.8",
        ["download"] = "M12 4 V15 M7 10.5 L12 15.5 L17 10.5 M5 20 H19",
        ["user"] = "M16 8 A4 4 0 1 1 8 8 A4 4 0 1 1 16 8 Z M4.5 20.5 C4.5 16.8 7.9 14.5 12 14.5 C16.1 14.5 19.5 16.8 19.5 20.5",
        ["keyboard"] = "M3 6.5 H21 V17.5 H3 Z M7 10 H7.01 M11 10 H11.01 M15 10 H15.01 M17 10 H17.01 M7 14 H17",
        ["database"] = "M4 6 C4 4.3 7.6 3 12 3 C16.4 3 20 4.3 20 6 V18 C20 19.7 16.4 21 12 21 C7.6 21 4 19.7 4 18 Z M4 6 C4 7.7 7.6 9 12 9 C16.4 9 20 7.7 20 6 M4 12 C4 13.7 7.6 15 12 15 C16.4 15 20 13.7 20 12",
        ["cube"] = "M12 3 L20 7.5 V16.5 L12 21 L4 16.5 V7.5 Z M4 7.5 L12 12 L20 7.5 M12 12 V21",
        ["hash"] = "M9.5 4 L7.5 20 M16.5 4 L14.5 20 M4.5 9 H20 M4 15 H19.5",
        ["headphones"] = "M4 15.5 V12 A8 8 0 0 1 20 12 V15.5 M4 15.5 A2 2 0 0 1 6 13.5 H7.5 V20 H6 A2 2 0 0 1 4 18 Z M20 15.5 A2 2 0 0 0 18 13.5 H16.5 V20 H18 A2 2 0 0 0 20 18 Z",
        ["thermometer"] = "M14 14.6 V5.5 A2 2 0 0 0 10 5.5 V14.6 A4 4 0 1 0 14 14.6 Z M12 17 V11",
        ["umbrella"] = "M12 3.5 A9 9 0 0 1 21 12.5 H3 A9 9 0 0 1 12 3.5 Z M12 12.5 V18.5 A2 2 0 0 1 8 18.5",
        ["sunrise"] = "M12 3 V7 M5.6 9.6 L7 11 M18.4 9.6 L17 11 M3 17 H21 M7.5 17 A4.5 4.5 0 0 1 16.5 17 M6 20.5 H18",
        ["sparkle"] = "F:M12 2.5 L14.1 9.9 L21.5 12 L14.1 14.1 L12 21.5 L9.9 14.1 L2.5 12 L9.9 9.9 Z",
        ["target"] = "M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 Z M17 12 A5 5 0 1 1 7 12 A5 5 0 1 1 17 12 Z M13 12 A1 1 0 1 1 11 12 A1 1 0 1 1 13 12 Z",
        ["grip"] = "M9 6 H9.01 M15 6 H15.01 M9 12 H9.01 M15 12 H15.01 M9 18 H9.01 M15 18 H15.01",
        ["expand"] = "M14 4 H20 V10 M10 20 H4 V14 M20 4 L13.5 10.5 M4 20 L10.5 13.5",
        ["message"] = "M4.5 5 H19.5 A1.5 1.5 0 0 1 21 6.5 V15.5 A1.5 1.5 0 0 1 19.5 17 H9 L5 20.5 V17 H4.5 A1.5 1.5 0 0 1 3 15.5 V6.5 A1.5 1.5 0 0 1 4.5 5 Z",
        ["dot"] = "F:M16 12 A4 4 0 1 1 8 12 A4 4 0 1 1 16 12 Z",
        ["folder"] = "M3 6.5 A1.5 1.5 0 0 1 4.5 5 H9.5 L11.5 7 H19.5 A1.5 1.5 0 0 1 21 8.5 V17.5 A1.5 1.5 0 0 1 19.5 19 H4.5 A1.5 1.5 0 0 1 3 17.5 Z",
        ["file"] = "M6 3.5 H14 L19 8.5 V20.5 H6 Z M14 3.5 V8.5 H19 M9 13 H16 M9 16.5 H14",
        ["image"] = "M3.5 5 H20.5 V19 H3.5 Z M3.5 16 L8.5 11 L13 15.5 L15.5 13 L20.5 18 M17 9 A1.5 1.5 0 1 1 14 9 A1.5 1.5 0 1 1 17 9 Z",
        ["paperclip"] = "M19.5 11.5 L12.2 18.8 A4.6 4.6 0 0 1 5.7 12.3 L13 5 A3 3 0 0 1 17.3 9.3 L10 16.6 A1.5 1.5 0 0 1 7.9 14.5 L14.5 7.9",
        ["screenshot"] = "M3.5 8 V5 H6.5 M17.5 5 H20.5 V8 M20.5 16 V19 H17.5 M6.5 19 H3.5 V16 M8 9 H16 V15 H8 Z",
        ["scissors"] = "M9 7 A2.5 2.5 0 1 1 4 7 A2.5 2.5 0 1 1 9 7 Z M9 17 A2.5 2.5 0 1 1 4 17 A2.5 2.5 0 1 1 9 17 Z M8.5 8.5 L20 18 M8.5 15.5 L20 6",
        ["brain"] = "M12 5 A3 3 0 0 0 6.2 6.1 A3 3 0 0 0 4.5 11 A3 3 0 0 0 6 16.3 A3 3 0 0 0 12 18 Z M12 5 A3 3 0 0 1 17.8 6.1 A3 3 0 0 1 19.5 11 A3 3 0 0 1 18 16.3 A3 3 0 0 1 12 18 Z M12 5 V20",
        ["edit"] = "M4 20 H8 L19 9 A2.1 2.1 0 0 0 16 6 L5 17 Z M14.5 7.5 L17.5 10.5",
        ["warning"] = "M12 4 L21 19.5 H3 Z M12 10 V14 M12 17 V17.01",
        ["research"] = "M11 18 A7 7 0 1 1 11 4 A7 7 0 1 1 11 18 Z M16 16 L20.5 20.5 M8 11 H14 M11 8 V14",
        ["computer"] = "M3.5 5 H20.5 V16 H3.5 Z M9 20 H15 M12 16 V20",
        // A bug (the Debug settings) and a spanner (maintenance tools) and a pulse (health check).
        ["bug"] = "M8 9 H16 V15 A4 4 0 0 1 8 15 Z M9.5 9 A2.5 2.5 0 0 1 14.5 9 M8 12 H4.5 M16 12 H19.5 M8.5 15.5 L5.5 18 M15.5 15.5 L18.5 18 M9 7.5 L7 5.5 M15 7.5 L17 5.5 M12 11 V19",
        ["wrench"] = "M14.5 6.5 A4 4 0 0 1 19.5 11.2 L17 10.5 L15.5 12 L16.2 14.5 A4 4 0 0 1 11.2 9.5 L4.5 16.2 A1.8 1.8 0 0 0 7.8 19.5 L14.5 12.8",
        ["pulse"] = "M3 12 H7 L9.5 6 L13.5 18 L16 12 H21",
        // A note with its corner folded (a chat's notes), and lines pressed together (compressing a chat).
        ["note"] = "M5 4.5 H19 V14.5 L14 19.5 H5 Z M14 19.5 V14.5 H19 M8.5 8.5 H15.5 M8.5 11.5 H13",
        ["compress"] = "M12 3 V9 M8.5 5.5 L12 9 L15.5 5.5 M12 21 V15 M8.5 18.5 L12 15 L15.5 18.5 M4.5 12 H19.5",
    };

    public static (Geometry Geometry, bool Filled)? Get(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            if (!Paths.TryGetValue(name, out var data)) return null;
            var filled = data.StartsWith("F:", StringComparison.Ordinal);
            var geo = Geometry.Parse(filled ? data[2..] : data);
            geo.Freeze();
            Cache[name] = (geo, filled);
            return (geo, filled);
        }
    }

    public static IEnumerable<string> Names => Paths.Keys;
}
