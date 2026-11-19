using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AquaHub.Core.Reminders;
using AquaHub.Core.Settings;
using AquaHub.Services;
using AquaHub.UI.Pages;

namespace AquaHub.UI.Shell;

public partial class MainWindow
{
    private const double DeckInset = 12;
    private DeckSettings _deck = new();

    private void InitializeDeck()
    {
        _deck = Hub.S.Deck;
        DeckHandle.MoveRequested = screen =>
        {
            if (!DailyDeck.IsVisible) return;
            var delta = DeckHandle.PointFromScreen(screen);
            MoveDeck(delta.X, delta.Y);
            Hub.Windows.SaveDeck(_deck);
        };
        DailyDeck.Visibility = _deck.Hidden || Hub.SnapshotMode ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ReopenDeck()
    {
        if (Hub.SnapshotMode) return;
        DailyDeck.Visibility = Visibility.Visible;
        _deck = _deck with { Hidden = false };
        Hub.Windows.SaveDeck(_deck);
        DeckHandle.Focus();
    }

    private void OnHideDeck(object sender, RoutedEventArgs e)
    {
        DailyDeck.Visibility = Visibility.Collapsed;
        _deck = _deck with { Hidden = true };
        Hub.Windows.SaveDeck(_deck);
        if (PageHost.Content is TodayPage today) today.FocusDeckButton();
        else NavRadios().FirstOrDefault(item => item.IsChecked == true)?.Focus();
    }

    private void OnDeckAddReminder(object sender, RoutedEventArgs e) => Navigate("upcoming", "new-reminder");

    private void OnDeckToday(object sender, RoutedEventArgs e) => Navigate("upcoming", "agenda-today");

    private async void OnDeckQuickReminder(object sender, RoutedEventArgs e)
    {
        if (!DeckQuickTimes.IsEnabled || sender is not Button { Tag: string tag }
            || !int.TryParse(tag, CultureInfo.InvariantCulture, out var minutes)) return;

        DeckQuickTimes.IsEnabled = false;
        SetDeckStatus("Saving reminder…");
        try
        {
            var reminder = await Hub.Core.Reminders.SaveQuickAsync(minutes);
            var time = reminder.FirstInstant.ToLocalTime().ToString(Hub.S.General.Use24Hour ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);
            SetDeckStatus($"Reminder set for {time}.");
        }
        catch (ReminderException ex) { SetDeckStatus(ex.Message); }
        catch (OperationCanceledException) { DeckStatus.Visibility = Visibility.Collapsed; }
        finally
        {
            DeckQuickTimes.IsEnabled = true;
            if (DailyDeck.IsVisible && Keyboard.FocusedElement is null && sender is Button button) button.Focus();
        }
    }

    private void SetDeckStatus(string message)
    {
        DeckStatus.Text = message;
        DeckStatus.Visibility = Visibility.Visible;
        UIElementAutomationPeer.CreatePeerForElement(DeckStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private double DeckRangeX => Math.Max(0, DeckSurface.ActualWidth - DailyDeck.ActualWidth - 2 * DeckInset);
    private double DeckRangeY => Math.Max(0, DeckSurface.ActualHeight - DailyDeck.ActualHeight - 2 * DeckInset);

    private void OnDeckSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DailyDeck is not null) PlaceDeck();
    }

    private void PlaceDeck()
    {
        Canvas.SetLeft(DailyDeck, DeckInset + _deck.X * DeckRangeX);
        Canvas.SetTop(DailyDeck, DeckInset + _deck.Y * DeckRangeY);
    }

    private void MoveDeck(double dx, double dy)
    {
        if (DeckRangeX > 0) _deck = _deck with { X = Math.Clamp(_deck.X + dx / DeckRangeX, 0, 1) };
        if (DeckRangeY > 0) _deck = _deck with { Y = Math.Clamp(_deck.Y + dy / DeckRangeY, 0, 1) };
        PlaceDeck();
    }

    private void OnDeckDrag(object sender, DragDeltaEventArgs e) => MoveDeck(e.HorizontalChange, e.VerticalChange);
    private void OnDeckDragCompleted(object sender, DragCompletedEventArgs e) => Hub.Windows.SaveDeck(_deck);

    private void OnDeckMoveKey(object sender, KeyEventArgs e)
    {
        var move = e.Key switch
        {
            Key.Left => (-16, 0),
            Key.Right => (16, 0),
            Key.Up => (0, -16),
            Key.Down => (0, 16),
            _ => (0, 0),
        };
        if (move == (0, 0)) return;
        MoveDeck(move.Item1, move.Item2);
        Hub.Windows.SaveDeck(_deck);
        e.Handled = true;
    }
}
