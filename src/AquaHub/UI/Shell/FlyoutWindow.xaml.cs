using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AquaHub.Core.Agents;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Shell;

/// <summary>The taskbar widget: an acrylic quick panel anchored above the notification area.</summary>
public partial class FlyoutWindow : Window
{
    private readonly TodayVM _vm = new();
    private readonly DispatcherTimer _clock;
    private DateTime _hiddenAt = DateTime.MinValue;
    private bool _attached;

    public FlyoutWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        SourceInitialized += (_, _) =>
        {
            WindowEffects.MakeToolWindow(this);
            var ok = WindowEffects.Apply(this, Backdrop.Acrylic, Hub.Theme.IsDark);
            Fallback.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        };
        Deactivated += (_, _) => { if (!Hub.SnapshotMode) HideAnimated(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideAnimated(); };
        Loaded += (_, _) => Attach();
        Closed += (_, _) => Detach();
        // Content can change size while open (command result, media appearing): stay anchored above the taskbar.
        SizeChanged += (_, _) => { if (IsVisible && !_positioning && !Hub.SnapshotMode) PositionNearTray(); };
        Hub.Theme.Changed += OnTheme;
    }

    private void OnTheme()
    {
        if (!IsLoaded) return;
        var ok = WindowEffects.Apply(this, Backdrop.Acrylic, Hub.Theme.IsDark);
        Fallback.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        _vm.Attach();
        Hub.State.Changed += OnState;
        Refresh();
        UpdateClock();
        _clock.Start();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _vm.Detach();
        Hub.State.Changed -= OnState;
        Hub.Theme.Changed -= OnTheme;
        _clock.Stop();
    }

    private void OnState(string topic)
    {
        if (topic is Topics.News or Topics.Events) Hub.OnUi(Refresh);
    }

    private readonly StoryVMCache _headlines = new();
    private bool _positioning;

    private void Refresh()
    {
        Headlines.ItemsSource = _headlines.Map(Hub.State.Stories.Take(3));
        var next = Hub.State.Events.FirstOrDefault(e => e.Start > DateTimeOffset.Now.AddMinutes(-30));
        NextEvent.Content = next is null ? null : new EventVM(next, Hub.S.General.Use24Hour);
        NextEventButton.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString(Hub.S.General.Use24Hour ? "HH:mm" : "h:mm", CultureInfo.CurrentCulture);
        DateText.Text = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
    }

    /// <param name="fromTrayClick">A click on the tray icon while we're open first deactivates (hides) us; that same
    /// click mustn't reopen us. The hotkey and the --flyout command always show.</param>
    public void ShowAnimated(bool fromTrayClick = false)
    {
        if (fromTrayClick && DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(350)) return;
        Opacity = 0;
        Show();
        Attach();
        _vm.UpdateLaunchpad();
        _vm.RefreshVolume();
        Hub.System.Sample();
        FitToScreen();
        UpdateLayout();
        PositionNearTray();
        BodyScroll.ScrollToTop();
        Activate();
        WindowEffects.ForceForeground(new WindowInteropHelper(this).Handle);
        Command.Text = "";
        CommandResult.Visibility = Visibility.Collapsed;
        var reduce = Hub.S.General.ReduceMotion || !SystemParameters.ClientAreaAnimation;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(reduce ? 1 : 160)));
        if (!reduce)
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Hub.Windows.UpdateVisibility();
    }

    public void HideAnimated()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
        _clock.Stop();
        Detach();
        Hub.Windows.UpdateVisibility();
    }

    private Native.POINT Anchor()
    {
        Native.POINT anchor;
        if (Hub.Tray?.GetIconRect() is { } r) anchor = new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        else Native.GetCursorPos(out anchor);
        return anchor;
    }

    /// <summary>Never taller than the monitor's work area (e.g. 1080p at 125% or a 768p laptop): the body scrolls instead.</summary>
    private void FitToScreen()
    {
        var (work, scale) = WindowEffects.MonitorAt(Anchor());
        MaxHeight = Math.Max(320, (work.Bottom - work.Top) / scale - 24);
    }

    private void PositionNearTray()
    {
        _positioning = true;
        try { PlaceNearTray(); }
        finally { _positioning = false; }
    }

    private void PlaceNearTray()
    {
        var (work, scale) = WindowEffects.MonitorAt(Anchor());
        var w = (int)Math.Round(ActualWidth * scale);
        var h = (int)Math.Round(ActualHeight * scale);
        var margin = (int)Math.Round(12 * scale);
        var edge = WindowEffects.TaskbarEdge();
        var x = work.Right - w - margin;
        var y = work.Bottom - h - margin;
        if (edge == 0) x = work.Left + margin;
        if (edge == 1) y = work.Top + margin;
        WindowEffects.Place(this, x, y, w, h, topmost: true);
    }

    private async void OnCommandKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(Command.Text)) return;
        var text = Command.Text.Trim();
        CommandResult.Text = "Thinking…";
        CommandResult.Visibility = Visibility.Visible;
        var cmd = await Hub.Core.Commands.InterpretAsync(text);
        if (cmd.Action is "ask" or "open_page" or "open_ticker" or "show_brief" or "summarize_clipboard") HideAnimated();
        var result = await Hub.Actions.ExecuteAsync(cmd);
        CommandResult.Text = string.IsNullOrEmpty(result) ? (cmd.Reply.Length > 0 ? cmd.Reply : "Done") : result;
        Command.Text = "";
    }

    private void OnOpenBrief(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("today");
    private void OnOpenHub(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain();
    private void OnOpenPc(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("system");
    private void OnOpenUpcoming(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("upcoming");
    private void OnSettings(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings");
}
