using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AquaHub.Core.Reminders;
using AquaHub.Core.Settings;
using AquaHub.Services;
using AquaHub.UI.Pages;

namespace AquaHub.UI.Shell;

public partial class MainWindow
{
    private const double DeckInset = 12;
    private const double GripSize = 56;
    private const double ActionSize = 44;
    private DeckSettings _deck = new();
    private (Button Button, double X, double Y)[] _deckBubbles = [];
    private readonly ScaleTransform _deckScale = new(1, 1);
    private readonly DispatcherTimer _deckClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private Point _deckStart;
    private Point _deckStartPosition;
    private bool _deckDragged;
    private bool _timerSaving;
    private Reminder? _quickReminder;

    private bool AnimateDeck => !Hub.S.General.ReduceMotion && SystemParameters.ClientAreaAnimation && !Hub.SnapshotMode;

    private void InitializeDeck()
    {
        _deck = Hub.S.Deck;
        _deckBubbles = [(DeckAsk, 66, 0), (DeckLaunchpad, 112, 32), (DeckToday, 93, 77),
            (DeckAdd, 47, 98), (DeckTimer, 0, 66), (DeckHide, 0, 115), (DeckApp1, 161, 32), (DeckApp2, 142, 79)];
        DeckHandle.RenderTransform = _deckScale;
        DeckHandle.RenderTransformOrigin = new Point(.5, .5);
        DeckHandle.ToggleRequested = ToggleDeck;
        DeckHandle.MoveRequested = screen =>
        {
            if (!DeckHandle.IsVisible) return;
            SetDeckPosition(DeckSurface.PointFromScreen(screen));
            Hub.Windows.SaveDeck(_deck);
        };
        DeckHandle.Visibility = _deck.Hidden || Hub.SnapshotMode ? Visibility.Collapsed : Visibility.Visible;
        _deckClock.Tick += (_, _) => RefreshDeckClock();
        Loaded += (_, _) => TrackDeckClock(IsVisible);
        IsVisibleChanged += (_, _) => TrackDeckClock(IsVisible);
        StateChanged += (_, _) => TrackDeckClock(IsVisible);
        Closed += (_, _) => TrackDeckClock(false);
        PreviewMouseDown += (_, _) => { if (!DeckSurface.IsMouseOver) CloseDeckSurfaces(); };
    }

    private void TrackDeckClock(bool visible)
    {
        Hub.Core.Reminders.Changed -= OnDeckRemindersChanged;
        if (visible && IsLoaded && WindowState != WindowState.Minimized && !Hub.SnapshotMode)
        {
            Hub.Core.Reminders.Changed += OnDeckRemindersChanged;
            RefreshDeckClock();
        }
        else _deckClock.Stop();
    }

    private void OnDeckRemindersChanged() => Hub.OnUi(() => { if (IsVisible) RefreshDeckClock(); });

    public void ReopenDeck()
    {
        if (Hub.SnapshotMode) return;
        DeckHandle.Visibility = Visibility.Visible;
        _deck = _deck with { Hidden = false };
        Hub.Windows.SaveDeck(_deck);
        RefreshDeckClock();
        DeckHandle.Focus();
        OpenDeckActions();
    }

    private void ToggleDeck()
    {
        if (!DeckHandle.IsVisible) return;
        if (CloseDeckSurfaces()) return;
        OpenDeckActions();
    }

    private void OpenDeckActions()
    {
        RefreshDeckPins();
        DeckActions.Visibility = Visibility.Visible;
        PlaceDeck();
        if (AnimateDeck)
        {
            DeckActions.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
            foreach (var (button, x, y) in _deckBubbles)
            {
                var slide = new TranslateTransform();
                button.RenderTransform = slide;
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-DeckDirectionX * x * .24, 0, TimeSpan.FromMilliseconds(215))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-DeckDirectionY * y * .24, 0, TimeSpan.FromMilliseconds(215))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        DeckAsk.Focus();
    }

    private bool CloseDeckSurfaces()
    {
        if (DeckActions.Visibility != Visibility.Visible && DeckTimerPanel.Visibility != Visibility.Visible) return false;
        DeckActions.Visibility = Visibility.Collapsed;
        DeckTimerPanel.Visibility = Visibility.Collapsed;
        return true;
    }

    private void OnHideDeck(object sender, RoutedEventArgs e)
    {
        CloseDeckSurfaces();
        DeckHandle.Visibility = Visibility.Collapsed;
        _deck = _deck with { Hidden = true };
        Hub.Windows.SaveDeck(_deck);
        RefreshDeckClock();
        if (PageHost.Content is TodayPage today) today.FocusDeckButton();
        else NavRadios().FirstOrDefault(item => item.IsChecked == true)?.Focus();
    }

    private void OnDeckAsk(object sender, RoutedEventArgs e) => OpenFromDeck("ask");
    private void OnDeckLaunchpad(object sender, RoutedEventArgs e) => OpenFromDeck("launchpad");
    private void OnDeckAddReminder(object sender, RoutedEventArgs e) => OpenFromDeck("upcoming", "new-reminder");
    private void OnDeckToday(object sender, RoutedEventArgs e) => OpenFromDeck("upcoming", "agenda-today");

    private void OpenFromDeck(string page, string? argument = null)
    {
        CloseDeckSurfaces();
        Navigate(page, argument);
        if (page != "upcoming") NavRadios().FirstOrDefault(item => item.IsChecked == true)?.Focus();
    }

    private void RefreshDeckPins()
    {
        var pins = Hub.S.Apps.Where(app => app.Pinned).Take(2).ToArray();
        UpdateDeckPin(DeckApp1, pins.ElementAtOrDefault(0));
        UpdateDeckPin(DeckApp2, pins.ElementAtOrDefault(1));
    }

    private static void UpdateDeckPin(Button button, AppEntry? app)
    {
        button.Visibility = app is null ? Visibility.Collapsed : Visibility.Visible;
        button.Tag = app?.Id;
        button.ToolTip = app?.Name;
        var initials = app?.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)
            .Select(part => StringInfo.GetNextTextElement(part));
        button.Content = initials is null ? null : string.Concat(initials).ToUpper(CultureInfo.CurrentCulture);
        AutomationProperties.SetName(button, app is null ? "" : $"Open {app.Name}");
    }

    private async void OnDeckPinnedApp(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        CloseDeckSurfaces();
        DeckHandle.Focus();
        if (Hub.S.Apps.FirstOrDefault(app => app.Id == id && app.Pinned) is not { } app) return;
        if (!await Task.Run(() => Hub.Launcher.Launch(app)))
            WorkspaceFeedback.Show($"Couldn’t open {app.Name}.", error: true);
    }

    private void OnDeckTimer(object sender, RoutedEventArgs e)
    {
        DeckActions.Visibility = Visibility.Collapsed;
        DeckTimerPanel.Visibility = Visibility.Visible;
        PlaceDeck();
        RefreshDeckClock();
        if (DeckDial.IsEnabled) DeckDial.Focus();
        else DeckTimerCommand.Focus();
    }

    private void OnCloseDeckTimer(object sender, RoutedEventArgs e)
    {
        CloseDeckSurfaces();
        DeckHandle.Focus();
    }

    private void OnDeckDurationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DeckTimerCommand is not null && DeckHandle is not null && _quickReminder is null) PaintDeckTimer();
    }

    private async void OnDeckTimerCommand(object sender, RoutedEventArgs e)
    {
        if (_timerSaving) return;
        _timerSaving = true;
        PaintDeckTimer();
        try
        {
            if (_quickReminder is { } existing)
            {
                await Hub.Core.Reminders.DeleteAsync(existing);
                WorkspaceFeedback.Show("Timer stopped.", "deck-status");
            }
            else
            {
                var created = await Hub.Core.Reminders.SaveQuickAsync((int)Math.Round(DeckDial.Value));
                var time = created.FirstInstant.ToLocalTime().ToString(Hub.S.General.Use24Hour ? "HH:mm" : "h:mm tt");
                WorkspaceFeedback.Show($"Reminder set for {time}.", "deck-status");
            }
        }
        catch (ReminderException ex) { WorkspaceFeedback.Show(ex.Message, "deck-status", error: true); }
        catch (OperationCanceledException) { }
        finally
        {
            _timerSaving = false;
            RefreshDeckClock();
            if (DeckTimerPanel.IsVisible && Keyboard.FocusedElement is null) DeckTimerCommand.Focus();
        }
    }

    private void RefreshDeckClock()
    {
        var now = DateTimeOffset.UtcNow;
        _quickReminder = Hub.Core.Reminders.Snapshot.Items
            .Where(item => item.Draft.QuickMinutes is not null && item.NextLocal is not null && item.FirstInstant > now)
            .MinBy(item => item.FirstInstant);
        PaintDeckTimer();
        if (_quickReminder is not null && IsVisible && WindowState != WindowState.Minimized && !_deck.Hidden) _deckClock.Start();
        else _deckClock.Stop();
    }

    private void PaintDeckTimer()
    {
        var minutes = (int)Math.Round(DeckDial.Value);
        var active = _quickReminder is not null;
        var label = minutes + " min";
        var fraction = minutes / 60.0;
        if (_quickReminder is { } timer)
        {
            var left = TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((timer.FirstInstant - DateTimeOffset.UtcNow).TotalSeconds)));
            label = $"{(int)left.TotalMinutes}:{left.Seconds:00}";
            fraction = left.TotalMinutes / timer.Draft.QuickMinutes!.Value;
        }
        DeckTimerRing.Label = label;
        DeckTimerRing.Value = fraction * 100;
        DeckHandle.ToolTip = active ? label : "Open Deck · drag to move";
        DeckHandle.Tag = active ? "timer" : null;
        DeckDial.IsEnabled = !active && !_timerSaving;
        DeckTimerCommand.IsEnabled = !_timerSaving;
        DeckTimerCommand.Content = active ? "Stop timer" : "Start";
        AutomationProperties.SetName(DeckHandle, active ? $"Open Deck, timer {label} remaining" : "Open Deck");
    }

    private double DeckRangeX => Math.Max(0, DeckSurface.ActualWidth - GripSize - 2 * DeckInset);
    private double DeckRangeY => Math.Max(0, DeckSurface.ActualHeight - GripSize - 2 * DeckInset);
    private int DeckDirectionX => _deck.X < .5 ? 1 : -1;
    private int DeckDirectionY => _deck.Y < .5 ? 1 : -1;

    private void OnDeckSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DeckHandle is not null) PlaceDeck();
    }

    private void PlaceDeck()
    {
        var x = DeckInset + _deck.X * DeckRangeX;
        var y = DeckInset + _deck.Y * DeckRangeY;
        Canvas.SetLeft(DeckHandle, x);
        Canvas.SetTop(DeckHandle, y);
        if (DeckActions.Visibility == Visibility.Visible)
        {
            foreach (var (button, dx, dy) in _deckBubbles)
            {
                Canvas.SetLeft(button, Math.Clamp(x + (GripSize - ActionSize) / 2 + DeckDirectionX * dx, 0, Math.Max(0, DeckSurface.ActualWidth - ActionSize)));
                Canvas.SetTop(button, Math.Clamp(y + (GripSize - ActionSize) / 2 + DeckDirectionY * dy, 0, Math.Max(0, DeckSurface.ActualHeight - ActionSize)));
            }
        }
        if (DeckTimerPanel.Visibility != Visibility.Visible) return;
        var timerX = DeckDirectionX > 0 ? x + GripSize + 10 : x - DeckTimerPanel.Width - 10;
        var timerY = DeckDirectionY > 0 ? y : y + GripSize - DeckTimerPanel.Height;
        Canvas.SetLeft(DeckTimerPanel, Math.Clamp(timerX, 0, Math.Max(0, DeckSurface.ActualWidth - DeckTimerPanel.Width)));
        Canvas.SetTop(DeckTimerPanel, Math.Clamp(timerY, 0, Math.Max(0, DeckSurface.ActualHeight - DeckTimerPanel.Height)));
    }

    private void SetDeckPosition(Point point)
    {
        _deck = _deck with
        {
            X = DeckRangeX > 0 ? Math.Clamp((point.X - DeckInset) / DeckRangeX, 0, 1) : _deck.X,
            Y = DeckRangeY > 0 ? Math.Clamp((point.Y - DeckInset) / DeckRangeY, 0, 1) : _deck.Y,
        };
        PlaceDeck();
    }

    private Point DeckPosition => new(Canvas.GetLeft(DeckHandle), Canvas.GetTop(DeckHandle));

    private void OnDeckDragStarted(object sender, DragStartedEventArgs e)
    {
        _deckStart = Mouse.GetPosition(DeckSurface);
        _deckStartPosition = DeckPosition;
        _deckDragged = false;
        AnimateGrip(.93);
    }

    private void OnDeckDrag(object sender, DragDeltaEventArgs e)
    {
        var delta = Mouse.GetPosition(DeckSurface) - _deckStart;
        if (!_deckDragged && delta.Length < SystemParameters.MinimumHorizontalDragDistance) return;
        _deckDragged = true;
        CloseDeckSurfaces();
        SetDeckPosition(_deckStartPosition + delta);
    }

    private void OnDeckDragCompleted(object sender, DragCompletedEventArgs e)
    {
        AnimateGrip(1);
        if (_deckDragged) Hub.Windows.SaveDeck(_deck);
        else if (!e.Canceled) ToggleDeck();
    }

    private void AnimateGrip(double scale)
    {
        if (!AnimateDeck) return;
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            _deckScale.BeginAnimation(property, new DoubleAnimation(scale, TimeSpan.FromMilliseconds(135))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void OnDeckMoveKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            ToggleDeck();
            e.Handled = true;
            return;
        }
        var move = e.Key switch
        {
            Key.Left => new Vector(-16, 0), Key.Right => new Vector(16, 0),
            Key.Up => new Vector(0, -16), Key.Down => new Vector(0, 16), _ => new Vector(),
        };
        if (move.LengthSquared == 0) return;
        SetDeckPosition(DeckPosition + move);
        Hub.Windows.SaveDeck(_deck);
        e.Handled = true;
    }
}
