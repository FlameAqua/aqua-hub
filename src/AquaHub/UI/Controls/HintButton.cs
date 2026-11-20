using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AquaHub.UI.Controls;

/// <summary>Short contextual help with ordinary tooltips and a dismissible click or keyboard disclosure.</summary>
public sealed class HintButton : Button
{
    public static readonly DependencyProperty HelpTextProperty = DependencyProperty.Register(nameof(HelpText), typeof(string),
        typeof(HintButton), new PropertyMetadata("", (sender, _) => ((HintButton)sender).UpdateHelp()));

    private Popup? _open;
    private Window? _owner;

    public string HelpText { get => (string)GetValue(HelpTextProperty); set => SetValue(HelpTextProperty, value); }

    public HintButton()
    {
        Unloaded += (_, _) => Dismiss();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Dismiss(); };
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Key.Escape && _open is not null) { Dismiss(); e.Handled = true; }
        }), handledEventsToo: true);
    }

    private TextBlock HelpContent()
    {
        var text = new TextBlock { Text = HelpText, MaxWidth = 300, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        return text;
    }

    private void UpdateHelp()
    {
        Dismiss();
        SetCurrentValue(ToolTipProperty, string.IsNullOrWhiteSpace(HelpText) ? null : new ToolTip { Content = HelpContent() });
        AutomationProperties.SetHelpText(this, HelpText);
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (_open is not null) { Dismiss(); return; }
        if (!IsVisible || string.IsNullOrWhiteSpace(HelpText)) return;
        if (ToolTip is ToolTip tip) tip.IsOpen = false;

        var card = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8),
            BorderThickness = new Thickness(1), Child = HelpContent(),
        };
        card.SetResourceReference(Border.BackgroundProperty, "B.Popup");
        card.SetResourceReference(Border.BorderBrushProperty, "B.PopupStroke");
        card.PreviewMouseDown += (_, _) => Dismiss();
        var popup = _open = new Popup
        {
            PlacementTarget = this, Placement = PlacementMode.Bottom, VerticalOffset = 4,
            AllowsTransparency = true, StaysOpen = false, Child = card,
        };
        ToolTipService.SetIsEnabled(this, false);
        _owner = Window.GetWindow(this);
        if (_owner is not null) _owner.Deactivated += OnWindowDeactivated;
        popup.Closed += (_, _) =>
        {
            if (_owner is not null) _owner.Deactivated -= OnWindowDeactivated;
            _owner = null;
            _open = null;
            ToolTipService.SetIsEnabled(this, true);
        };
        popup.IsOpen = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Dismiss();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => Dismiss();
    private void Dismiss() { if (_open is { } popup) popup.IsOpen = false; }
}
