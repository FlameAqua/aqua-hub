using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AquaHub.Core.Agents;
using AquaHub.Core.Models;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.Pages;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Shell;

public interface IPage
{
    void OnNavigatedTo(string? arg);
    void OnNavigatedFrom();
}

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly UiThrottle _status;
    private readonly DispatcherTimer _clock;
    private string _current = "";
    private bool _syncingNav;

    public string CurrentPage => _current;

    public MainWindow()
    {
        InitializeComponent();
        _status = new UiThrottle(UpdateStatus, 250);
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => UpdateStatus();

        foreach (var rb in NavRadios())
            rb.Checked += (s, _) => { if (!_syncingNav) Navigate((string)((FrameworkElement)s).Tag); };

        SourceInitialized += (_, _) => ApplyBackdrop();
        if (!Hub.SnapshotMode) TaskbarIntegration.Attach(this);
        Loaded += (_, _) =>
        {
            Hub.State.Changed += OnStateChanged;
            Hub.Platform.AlertRaised += OnAlert;
            Hub.Theme.Changed += ApplyBackdrop;
            Hub.Core.Settings.Changed += OnSettingsChanged;
            Hub.Ollama.Changed += OnOllamaChanged;
            _clock.Start();
            UpdateStatus();
            UpdateNavMode();
            if (_current.Length == 0) Navigate("today");
            if (!Hub.S.OnboardingComplete && !Hub.SnapshotMode) ShowOverlay(new Onboarding());
        };
        Closed += (_, _) =>
        {
            Hub.State.Changed -= OnStateChanged;
            Hub.Platform.AlertRaised -= OnAlert;
            Hub.Theme.Changed -= ApplyBackdrop;
            Hub.Core.Settings.Changed -= OnSettingsChanged;
            Hub.Ollama.Changed -= OnOllamaChanged;
            _clock.Stop();
            (PageHost.Content as IPage)?.OnNavigatedFrom();
            PageHost.Content = null;
            _pages.Clear();
        };
        SizeChanged += (_, _) => UpdateNavMode();
        StateChanged += (_, _) =>
        {
            // WindowChrome windows overhang the screen by the resize border when maximised.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7, 7, 7, 7) : new Thickness(0);
            Hub.Windows.UpdateVisibility();
        };
        IsVisibleChanged += (_, _) =>
        {
            // Hidden (closed to the tray): the page stops listening and the clock stops; shown again: catch up.
            if (IsVisible)
            {
                if (_current.Length > 0) (PageHost.Content as IPage)?.OnNavigatedTo(null);
                if (IsLoaded) { _clock.Start(); UpdateStatus(); }
                Dispatcher.BeginInvoke(() => MaybeShowOllamaBanner(justStopped: false), DispatcherPriority.ApplicationIdle);
            }
            else
            {
                (PageHost.Content as IPage)?.OnNavigatedFrom();
                _clock.Stop();
                _status.Stop();
            }
            Hub.Windows.UpdateVisibility();
        };
        PreviewKeyDown += OnKey;
    }

    private IEnumerable<RadioButton> NavRadios() => NavItems.Children.OfType<RadioButton>().Append(SettingsNav);

    private void ApplyBackdrop()
    {
        var ok = WindowEffects.Apply(this, Backdrop.Mica, Hub.Theme.IsDark);
        Fallback.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateNavMode()
    {
        var compact = ActualWidth > 0 && ActualWidth < 1080;
        Nav.Width = compact ? 64 : 228;
        BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CommandBox.Width = Math.Clamp(ActualWidth - (compact ? 64 : 228) - 330, 240, 520);
    }

    // ───────────────────────── Navigation ─────────────────────────

    public void Navigate(string page, string? arg = null)
    {
        if (!_pages.TryGetValue(page, out var view))
        {
            view = CreatePage(page);
            System.Windows.Automation.AutomationProperties.SetAutomationId(view, "page-" + page);
            _pages[page] = view;
        }
        _syncingNav = true;
        foreach (var rb in NavRadios()) rb.IsChecked = (string)rb.Tag == page;
        _syncingNav = false;

        if (_current != page)
        {
            (PageHost.Content as IPage)?.OnNavigatedFrom();
            // Visited pages stay warm while the window is open (scroll position, filters, chosen symbol…);
            // they only listen for updates while shown, and all of them are released when the window closes.
            PageHost.Content = view;
            _current = page;
            Animate();
        }
        (view as IPage)?.OnNavigatedTo(arg);
        Hub.Windows.UpdateVisibility();
    }

    private void Animate()
    {
        if (Hub.S.General.ReduceMotion || !SystemParameters.ClientAreaAnimation || Hub.SnapshotMode) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        PageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private static FrameworkElement CreatePage(string page) => page switch
    {
        "news" => new NewsPage(),
        "social" => new SocialPage(),
        "markets" => new MarketsPage(),
        "upcoming" => new UpcomingPage(),
        "system" => new SystemPage(),
        "launchpad" => new LaunchpadPage(),
        "ask" => new AskPage(),
        "workbench" => new WorkbenchPage(),
        "agents" => new AgentsPage(),
        "settings" => new SettingsPage(),
        _ => new TodayPage(),
    };

    public void ShowOverlay(FrameworkElement content)
    {
        OverlayHost.Content = content;
        Overlay.Visibility = Visibility.Visible;
    }

    public void HideOverlay()
    {
        Overlay.Visibility = Visibility.Collapsed;
        OverlayHost.Content = null;
    }

    // ───────────────────────── Status & alerts ─────────────────────────

    private void OnStateChanged(string topic)
    {
        if (topic is Topics.Ai or Topics.Alerts or Topics.Freshness or Topics.Agents && IsVisible) _status.Request();
        // The list under the bell follows alerts that arrive, or are read, while it's open.
        if (topic is Topics.Alerts) Hub.OnUi(() => { if (AlertsPopup.IsOpen) FillAlerts(); });
    }

    private void OnSettingsChanged(Core.Settings.HubSettings s)
    {
        if (IsVisible) _status.Request();
    }

    private void UpdateStatus()
    {
        var ai = Hub.State.Ai;
        if (ai is null || !ai.Enabled)
        {
            AiModel.Text = "Local AI";
            AiState.Text = ai is null ? "Checking…" : "Turned off";
            AiDot.Fill = Fmt.Res("B.Text3");
        }
        else if (!ai.Available)
        {
            AiModel.Text = "Local AI offline";
            AiState.Text = "Using smart fallbacks";
            AiDot.Fill = Fmt.Res("B.Warn");
        }
        else
        {
            AiModel.Text = ai.ActiveModel ?? "Local AI";
            var working = Hub.Core.Agents.Statuses.Any(s => s.UsesAi && s.State == AgentState.Running);
            AiState.Text = ai.Paused ? "Paused · " + ai.PauseReason : working ? "Thinking…" : "Ready · on-device";
            AiDot.Fill = Fmt.Res(ai.Paused ? "B.Warn" : working ? "B.Accent" : "B.Up");
        }
        AiStatus.ToolTip = ai?.Error ?? (ai?.Available == true ? $"{ai.ActiveModel} via {ai.Provider} · {Hub.Core.Llm.Stats.LastTokensPerSecond:0} tok/s" : "Local AI");

        var unread = Hub.State.UnreadAlerts;
        BellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellCount.Text = unread > 9 ? "9+" : unread.ToString();

        var dnd = Hub.S.Notifications.DoNotDisturb;
        DndButton.Background = dnd ? Fmt.Res("B.AccentSoft") : System.Windows.Media.Brushes.Transparent;
        DndIcon.Foreground = dnd ? Fmt.Res("B.AccentText") : Fmt.Res("B.Text3");
        DndButton.ToolTip = dnd ? "Do not disturb is on — click to turn off" : "Turn on do not disturb";
        System.Windows.Automation.AutomationProperties.SetName(DndButton, dnd ? "Do not disturb: on" : "Do not disturb: off");

        UpdateFreshness();

        var errors = Hub.Core.Agents.Statuses.Any(s => s.State == AgentState.Error);
        foreach (var rb in NavRadios()) if ((string)rb.Tag == "agents") UiProps.SetBadge(rb, errors ? "dot" : null);
    }

    private static readonly (string Area, string Label)[] FreshAreas =
    {
        ("news", "News"), ("social", "Social"), ("markets", "Markets"), ("predictions", "Predictions"), ("events", "Agenda"), ("weather", "Weather"),
    };

    /// <summary>"News updated 4m ago" from the collectors' last successful fetch — not from UI or telemetry updates.</summary>
    private void UpdateFreshness()
    {
        var state = Hub.State;
        var news = state.FreshAt("news");
        var lines = FreshAreas.Select(a => $"{a.Label}: {(state.FreshAt(a.Area) is { } t ? TimeText.AgoPhrase(t) : "not fetched yet")}");
        SyncText.ToolTip = "Last successful update\n" + string.Join("\n", lines);
        RefreshButton.ToolTip = "Refresh everything (F5)\n" + string.Join("\n", lines);
        if (!state.Online)
        {
            SyncText.Text = "Offline";
            SyncText.Foreground = Fmt.Res("B.Warn");
            OfflineText.Text = news is { } n
                ? $"You're offline — showing what your agents collected up to {n.ToLocalTime():HH:mm}. Aqua catches up by itself when you're back."
                : "You're offline — Aqua will collect news, markets and posts as soon as you're back.";
            OfflineBar.Visibility = Visibility.Visible;
            return;
        }
        OfflineBar.Visibility = Visibility.Collapsed;
        var stale = news is { } s && DateTimeOffset.Now - s > TimeSpan.FromMinutes(Math.Max(45, Hub.S.News.RefreshMinutes * 3));
        SyncText.Foreground = Fmt.Res(stale ? "B.Warn" : "B.Text3");
        SyncText.Text = news is { } t2 ? "News updated " + TimeText.AgoPhrase(t2) : "Gathering news…";
    }

    private void OnAlert(HubAlert alert) => Hub.OnUi(() =>
    {
        _status.Request();
        if (!IsActive || Hub.S.Notifications.DoNotDisturb) return;
        var banner = new Border
        {
            Style = (Style)FindResource("Card"),
            Background = Fmt.Res("B.Popup"),
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(14, 12, 14, 12),
            Cursor = Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.3 },
        };
        var vm = new AlertVM(alert);
        var content = new ContentPresenter { Content = vm, ContentTemplate = (DataTemplate)FindResource("Tpl.AlertRow") };
        banner.Child = content;
        // Clicking it opens the alert (which marks it read); the banner has done its job.
        banner.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => Banners.Children.Remove(banner)));
        Banners.Children.Add(banner);
        banner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        timer.Tick += (_, _) => { timer.Stop(); Banners.Children.Remove(banner); };
        timer.Start();
        while (Banners.Children.Count > 3) Banners.Children.RemoveAt(0);
    });

    private void OnBellClick(object sender, RoutedEventArgs e) => ShowAlerts();

    /// <summary>Opens the alerts list under the bell (also used by the tray menu's "Show N new alerts").</summary>
    public void ShowAlerts()
    {
        FillAlerts();
        AlertsPopup.IsOpen = true;
    }

    public void CloseAlerts() => AlertsPopup.IsOpen = false;

    private void FillAlerts()
    {
        var alerts = Hub.State.Alerts.Take(40).Select(a => new AlertVM(a)).ToList();
        AlertList.ItemsSource = alerts;
        NoAlerts.Visibility = alerts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMarkRead(object sender, RoutedEventArgs e)
    {
        Hub.State.MarkAlertsRead();
        AlertsPopup.IsOpen = false;
    }

    private void OnClearAlerts(object sender, RoutedEventArgs e)
    {
        Hub.State.ClearAlerts();
        AlertsPopup.IsOpen = false;
    }

    private void OnDndClick(object sender, RoutedEventArgs e) =>
        Hub.Core.Settings.Update(s => s.Notifications.DoNotDisturb = !s.Notifications.DoNotDisturb);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = Hub.Actions.ExecuteAsync(new Core.Ai.HubCommand("refresh"));

    private void OnCommandBoxClick(object sender, RoutedEventArgs e) => Hub.Windows.ShowPalette(this);

    /// <summary>On a narrow window the status text makes way (it's in the refresh button's tooltip), then the key hint.</summary>
    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SyncText.Visibility = e.NewSize.Width < 720 ? Visibility.Collapsed : Visibility.Visible;
        CommandKeyHint.Visibility = e.NewSize.Width < 420 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAiStatusClick(object sender, RoutedEventArgs e)
    {
        UpdateAiPopup();
        AiPopup.IsOpen = true;
    }

    private void UpdateAiPopup()
    {
        var ai = Hub.State.Ai;
        var ollama = Hub.Ollama;
        var local = OllamaManager.IsLocalOllama;
        AiPopupStatus.Text = !Hub.S.Ai.Enabled ? "AI is turned off in Settings."
            : ai?.Available == true ? (ai.Paused ? $"Ready, paused — {ai.PauseReason}." : $"Ready · {ai.ActiveModel} on this PC.")
            : local ? $"Ollama: {ollama.StatusText}." : "The model server isn't answering.";
        AiPopupDetail.Text = local
            ? (Hub.S.Ai.StopOllamaIdleMinutes > 0 ? $"Aqua stops it after {Plural.Of(Hub.S.Ai.StopOllamaIdleMinutes, "minute")} unused" : "Aqua keeps it running")
              + (Hub.S.Ai.StopOllamaForGpu ? " and while a game needs the GPU." : ".")
            : Hub.S.Ai.Endpoint;
        AiPopupToggle.Visibility = local && OllamaManager.Installed ? Visibility.Visible : Visibility.Collapsed;
        AiPopupToggle.Content = ollama.Running == true ? "Turn off" : "Turn on";
        AiPopupToggle.IsEnabled = !ollama.Transitioning;
    }

    private bool? _ollamaWasRunning;
    private DateTime _lastOllamaBanner = DateTime.MinValue;
    private Border? _ollamaBanner;

    private void OnOllamaChanged() => Hub.OnUi(() =>
    {
        _status.Request();
        if (AiPopup.IsOpen) UpdateAiPopup();
        var running = Hub.Ollama.Running;
        if (_ollamaWasRunning == true && running == false && !Hub.Ollama.Transitioning) MaybeShowOllamaBanner(justStopped: true);
        if (running == true && _ollamaBanner is not null) { Banners.Children.Remove(_ollamaBanner); _ollamaBanner = null; }
        if (!Hub.Ollama.Transitioning) _ollamaWasRunning = running;
    });

    /// <summary>
    /// "Local AI is off" with a Turn on button, which disappears by itself — when you open the dashboard with the
    /// server stopped (at most every 20 minutes) or when it stops while you're looking.
    /// </summary>
    private void MaybeShowOllamaBanner(bool justStopped)
    {
        if (!IsVisible || Hub.SnapshotMode || !OllamaManager.IsLocalOllama || !OllamaManager.Installed || !Hub.S.Ai.Enabled) return;
        // Not when you turned it off yourself — you know, and nagging would undo the point.
        if (Hub.Ollama.Running != false || Hub.Ollama.Transitioning || Hub.Ollama.UserTurnedOff || _ollamaBanner is not null) return;
        if (!justStopped && DateTime.UtcNow - _lastOllamaBanner < TimeSpan.FromMinutes(20)) return;
        _lastOllamaBanner = DateTime.UtcNow;
        _ollamaBanner = ShowActionBanner("Local AI is off",
            Hub.Ollama.StoppedBecause is { } why ? $"Ollama stopped {why}. Summaries use quick fallbacks meanwhile." : "Ollama isn't running, so summaries use quick fallbacks.",
            "Turn on", () => Hub.Ollama.TurnOnAsync(), icon: "cube", iconBrush: "B.Warn", automationId: "ollama-banner",
            onClosed: b => { if (ReferenceEquals(_ollamaBanner, b)) _ollamaBanner = null; });
    }

    /// <summary>
    /// A small notice in the corner with one action and "Not now". It disappears by itself after
    /// <paramref name="seconds"/> (hovering keeps it up), e.g. "Local AI is off · Turn on" or "Removed Spotify · Undo".
    /// </summary>
    public Border ShowActionBanner(string title, string body, string actionLabel, Func<Task> action, string icon = "info",
        string iconBrush = "B.AccentText", string? automationId = null, int seconds = 12, Action<Border>? onClosed = null)
    {
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Fmt.Res("B.Text"), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = body, Style = (Style)FindResource("T.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var act = new Button { Content = actionLabel, Style = (Style)FindResource("Btn.Accent") };
        var dismiss = new Button { Content = "Not now", Style = (Style)FindResource("Btn.Standard"), Margin = new Thickness(8, 0, 0, 0) };
        if (automationId is not null) System.Windows.Automation.AutomationProperties.SetAutomationId(act, automationId + "-action");
        buttons.Children.Add(act);
        buttons.Children.Add(dismiss);
        text.Children.Add(buttons);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new AquaHub.UI.Controls.Icon { Kind = icon, Width = 18, Height = 18, Foreground = Fmt.Res(iconBrush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var banner = new Border
        {
            Style = (Style)FindResource("Card"),
            Background = Fmt.Res("B.Popup"),
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(14, 12, 14, 12),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.3 },
        };
        if (automationId is not null) System.Windows.Automation.AutomationProperties.SetAutomationId(banner, automationId);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        void Close()
        {
            timer.Stop();
            if (!Banners.Children.Contains(banner)) return;
            Banners.Children.Remove(banner);
            onClosed?.Invoke(banner);
        }
        timer.Tick += (_, _) => Close();
        banner.MouseEnter += (_, _) => timer.Stop();
        banner.MouseLeave += (_, _) => timer.Start();
        act.Click += async (_, _) =>
        {
            act.IsEnabled = false;
            timer.Stop();
            try { await action(); }
            finally { Close(); }
        };
        dismiss.Click += (_, _) => Close();
        Banners.Children.Add(banner);
        banner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        timer.Start();
        while (Banners.Children.Count > 3) Banners.Children.RemoveAt(0);
        return banner;
    }

    private async void OnAiServerToggle(object sender, RoutedEventArgs e)
    {
        if (Hub.Ollama.Running == true) await Hub.Ollama.TurnOffAsync();
        else await Hub.Ollama.TurnOnAsync();
        UpdateAiPopup();
    }

    private void OnAiPopupAgents(object sender, RoutedEventArgs e) { AiPopup.IsOpen = false; Navigate("agents"); }
    private void OnAiPopupSettings(object sender, RoutedEventArgs e) { AiPopup.IsOpen = false; Navigate("settings", "ai"); }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.K) { Hub.Windows.ShowPalette(this); e.Handled = true; return; }
        if (e.Key == Key.F5) { OnRefreshClick(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible && OverlayHost.Content is not Onboarding) { HideOverlay(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.OemComma) { Navigate("settings"); e.Handled = true; return; }
        if (ctrl && e.Key is >= Key.D1 and <= Key.D9)
        {
            var pages = new[] { "today", "news", "social", "markets", "upcoming", "system", "launchpad", "ask", "agents" };
            Navigate(pages[e.Key - Key.D1]);
            e.Handled = true;
        }
    }
}
