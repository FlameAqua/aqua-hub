using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Controls;

/// <summary>Windows 11 settings-style row: icon, title, description and a control on the right.</summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(string), typeof(SettingRow), new PropertyMetadata(null));
    public static readonly DependencyProperty BelowProperty = DependencyProperty.Register(nameof(Below), typeof(object), typeof(SettingRow), new PropertyMetadata(null));
    public static readonly DependencyProperty KeywordsProperty = DependencyProperty.Register(nameof(Keywords), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    /// <summary>Optional full-width content under the row (lists, editors).</summary>
    public object? Below { get => GetValue(BelowProperty); set => SetValue(BelowProperty, value); }
    /// <summary>Other words people might search Settings with ("dark mode" for Theme). Not shown.</summary>
    public string Keywords { get => (string)GetValue(KeywordsProperty); set => SetValue(KeywordsProperty, value); }

    public SettingRow()
    {
        Loaded += (_, _) => NameControls();
    }

    /// <summary>
    /// Gives the row's inputs an accessible name (screen readers, UI automation): the row title. A name set on the
    /// control itself wins, but not one from a style (a text box's placeholder, e.g. "Press a shortcut…"); a button
    /// keeps its visible label ("Open log") and gets the title as help text.
    /// </summary>
    private void NameControls()
    {
        if (string.IsNullOrEmpty(Title) || Content is not DependencyObject root) return;
        var n = 0;
        foreach (var control in Controls(root))
        {
            var nameSource = DependencyPropertyHelper.GetValueSource(control, System.Windows.Automation.AutomationProperties.NameProperty).BaseValueSource;
            if (nameSource == BaseValueSource.Local && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control))) continue;
            if (control is ContentControl { Content: string label } && label.Length > 0)
            {
                if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(control)))
                    System.Windows.Automation.AutomationProperties.SetHelpText(control, Title);
                continue;
            }
            System.Windows.Automation.AutomationProperties.SetName(control, n++ == 0 ? Title : $"{Title} ({n})");
        }
    }

    private static IEnumerable<Control> Controls(DependencyObject root)
    {
        if (root is Control c && root is not ContentControl { Content: Panel }) { yield return c; yield break; }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var found in Controls(child)) yield return found;
    }
}

/// <summary>Editable list of short strings shown as removable chips with an inline "add" box.</summary>
public sealed class ChipEditor : UserControl
{
    private readonly WrapPanel _panel = new();
    private readonly TextBox _input = new() { MinWidth = 180, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 5, 10, 5), FontSize = 13 };
    private List<string> _items = new();

    public event Action<List<string>>? Changed;

    public string Placeholder
    {
        get => UiProps.GetPlaceholder(_input);
        set => UiProps.SetPlaceholder(_input, value);
    }

    /// <summary>Optional normaliser (e.g. strip "#" or "r/"); return null to reject.</summary>
    public Func<string, string?> Normalize { get; set; } = s => s.Trim();

    public ChipEditor()
    {
        Content = _panel;
        _input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            var value = Normalize(_input.Text);
            if (!string.IsNullOrWhiteSpace(value) && !_items.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                _items.Add(value);
                Changed?.Invoke(_items);
                Render();
            }
            _input.Text = "";
            e.Handled = true;
        };
        UiProps.SetPlaceholder(_input, "Add…");
    }

    public void SetItems(IEnumerable<string> items)
    {
        _items = items.ToList();
        Render();
    }

    private void Render()
    {
        _panel.Children.Clear();
        foreach (var item in _items)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Fmt.Res("B.Control"),
                BorderBrush = Fmt.Res("B.ControlStroke"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 4, 6, 4),
                Margin = new Thickness(0, 0, 6, 6),
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = item, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = Fmt.Res("B.Text") });
            var remove = new Button { Style = (Style)FindResource("Btn.Icon"), Width = 20, Height = 20, Margin = new Thickness(4, 0, 0, 0), ToolTip = $"Remove {item}" };
            remove.Content = new Icon { Kind = "close", Width = 10, Height = 10 };
            var captured = item;
            remove.Click += (_, _) =>
            {
                _items.Remove(captured);
                Changed?.Invoke(_items);
                Render();
            };
            sp.Children.Add(remove);
            chip.Child = sp;
            _panel.Children.Add(chip);
        }
        _panel.Children.Add(_input);
    }
}
