using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AquaHub.UI.Shell;

/// <summary>A window with one picture from a chat — a screenshot, or a thumbnail when the file has gone — up to its own size. Esc closes it.</summary>
public static class PictureViewer
{
    public static void Show(string title, BitmapSource picture)
    {
        var owner = Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
        var image = new Image { Source = picture, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var area = SystemParameters.WorkArea;
        var window = new Window
        {
            Title = title,
            Content = new Border { Padding = new Thickness(10), Child = image },
            Width = Math.Clamp(picture.Width + 40, 320, area.Width * 0.85),
            Height = Math.Clamp(picture.Height + 70, 240, area.Height * 0.85),
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
        };
        window.SetResourceReference(Control.BackgroundProperty, "B.WindowOpaque");
        window.SetResourceReference(Control.ForegroundProperty, "B.Text");
        AutomationProperties.SetAutomationId(window, "picture-viewer");
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            window.Close();
        };
        window.Show();
    }
}
