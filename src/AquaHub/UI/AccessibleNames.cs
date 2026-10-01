using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AquaHub.Core.Util;

namespace AquaHub.UI;

/// <summary>
/// A button whose content is an icon plus a label (a panel, not a string) has no accessible name of its own, so
/// screen readers announce it as just "button". When such a button loads, it is given the label it shows. Buttons
/// named in markup or code keep their name; one whose label changes (Read aloud ↔ Stop) binds its name explicitly.
/// </summary>
public static class AccessibleNames
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button || button.Content is string || !string.IsNullOrEmpty(AutomationProperties.GetName(button))) return;
        var label = HtmlText.OneLine(string.Join(" ", Labels(button)), 120);
        if (label.Length > 0) AutomationProperties.SetName(button, label);
    }

    /// <summary>The visible text inside a button, in order (a nested button names itself).</summary>
    private static IEnumerable<string> Labels(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase || child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (child is TextBlock text)
            {
                if (text.Text.Trim() is { Length: > 0 } t) yield return t;
                continue;
            }
            foreach (var t in Labels(child)) yield return t;
        }
    }
}
