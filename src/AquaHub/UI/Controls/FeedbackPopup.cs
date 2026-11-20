using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AquaHub.UI.Controls;

/// <summary>A single live notice, positioned over a workspace without participating in its measurement.</summary>
public sealed class FeedbackPopup : Canvas
{
    private readonly GroupBorder _card;
    private readonly TextBlock _message;
    private readonly Icon _icon;
    private readonly ProgressBar _progress;
    private readonly Button _actionButton;
    private Action? _action;
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromSeconds(5) };
    private HorizontalAlignment _noticeAlignment = HorizontalAlignment.Right;
    private bool _alignBottom;
    private UIElement? _previousFocus;

    public HorizontalAlignment NoticeAlignment
    {
        get => _noticeAlignment;
        set { _noticeAlignment = value; Place(); }
    }

    public bool AlignBottom
    {
        get => _alignBottom;
        set { _alignBottom = value; Place(); }
    }

    public FeedbackPopup()
    {
        IsHitTestVisible = false;
        _card = new GroupBorder
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 8, 10),
            BorderThickness = new Thickness(1), Width = 350, Visibility = Visibility.Collapsed,
        };
        _card.SetResourceReference(Border.BackgroundProperty, "B.Popup");
        _card.SetResourceReference(Border.BorderBrushProperty, "B.PopupStroke");
        _card.PreviewGotKeyboardFocus += (_, e) =>
        {
            if (!_card.IsKeyboardFocusWithin) _previousFocus = e.OldFocus as UIElement;
        };
        _card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Hide();
            e.Handled = true;
        };
        var body = new DockPanel();
        var dismiss = new Button
        {
            Content = new Icon { Kind = "close", Width = 13, Height = 13 },
            Width = 26, Height = 26, ToolTip = "Dismiss notice", VerticalAlignment = VerticalAlignment.Top,
        };
        dismiss.SetResourceReference(StyleProperty, "Btn.Icon");
        AutomationProperties.SetName(dismiss, "Dismiss notice");
        AutomationProperties.SetAutomationId(dismiss, "feedback-dismiss");
        dismiss.Click += (_, _) => Hide();
        DockPanel.SetDock(dismiss, Dock.Right);
        body.Children.Add(dismiss);
        _icon = new Icon { Width = 18, Height = 18, Margin = new Thickness(0, 3, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(_icon, Dock.Left);
        body.Children.Add(_icon);
        var detail = new StackPanel { Margin = new Thickness(0, 3, 6, 2) };
        _message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        detail.Children.Add(new ScrollViewer { Content = _message, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _progress = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 9, 0, 0) };
        detail.Children.Add(_progress);
        _actionButton = new Button { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        _actionButton.SetResourceReference(StyleProperty, "Btn.Link");
        AutomationProperties.SetAutomationId(_actionButton, "feedback-action");
        _actionButton.Click += (_, _) => { var action = _action; Hide(); action?.Invoke(); };
        detail.Children.Add(_actionButton);
        body.Children.Add(detail);
        _card.Child = body;
        Children.Add(_card);
        SizeChanged += (_, _) => Place();
        _expiry.Tick += (_, _) =>
        {
            if (!_card.IsMouseOver && !_card.IsKeyboardFocusWithin) Hide();
        };
        Unloaded += (_, _) => Hide();
    }

    public void Show(string message, string id = "feedback-message", bool error = false, bool busy = false, bool persistent = false,
        string? actionLabel = null, Action? action = null)
    {
        if (string.IsNullOrWhiteSpace(message)) { Hide(id); return; }
        _expiry.Stop();
        _icon.Kind = error ? "warning" : busy ? "refresh" : "info";
        _icon.SetResourceReference(Icon.ForegroundProperty, error ? "B.Down" : "B.AccentText");
        AutomationProperties.SetAutomationId(_message, id);
        AutomationProperties.SetLiveSetting(_message, error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        var announce = _card.Visibility != Visibility.Visible || !string.Equals(_message.Text, message, StringComparison.Ordinal);
        _message.Text = message;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _action = action;
        _actionButton.Content = actionLabel;
        _actionButton.Visibility = action is not null && !string.IsNullOrWhiteSpace(actionLabel) ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = true;
        _card.Visibility = Visibility.Visible;
        Place();
        if (!error && !busy && !persistent) _expiry.Start();
        if (announce && IsLoaded)
            UIElementAutomationPeer.CreatePeerForElement(_message)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void Hide(string? id = null)
    {
        if (id is not null && AutomationProperties.GetAutomationId(_message) != id) return;
        _expiry.Stop();
        var restore = _card.IsKeyboardFocusWithin ? _previousFocus : null;
        _previousFocus = null;
        IsHitTestVisible = false;
        _card.Visibility = Visibility.Collapsed;
        _message.Text = "";
        _action = null;
        _actionButton.Content = null;
        _actionButton.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_message, "");
        if (restore is { IsVisible: true, IsEnabled: true }) restore.Focus();
    }

    private void Place()
    {
        const double inset = 16;
        _card.Width = Math.Min(350, Math.Max(0, ActualWidth - inset * 2));
        var available = ActualWidth - _card.Width;
        var left = _noticeAlignment switch
        {
            HorizontalAlignment.Left => inset,
            HorizontalAlignment.Center => available / 2,
            _ => Math.Max(0, available - inset),
        };
        SetLeft(_card, left);
        SetTop(_card, _alignBottom ? double.NaN : inset);
        SetBottom(_card, _alignBottom ? inset : double.NaN);
    }
}
