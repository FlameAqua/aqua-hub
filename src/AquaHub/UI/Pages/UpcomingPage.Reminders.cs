using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AquaHub.Core.Models;
using AquaHub.Core.Reminders;
using AquaHub.Core.Settings;
using AquaHub.Services;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public partial class UpcomingPage
{
    private const int EventsPerPage = 60;
    private readonly ReminderService _reminders;
    private readonly UiThrottle _agendaRefresh;
    private readonly ObservableCollection<AgendaEntry> _agendaRows = new();
    private CancellationTokenSource? _visible;
    private Reminder? _editing;
    private FrameworkElement? _editorEntryPoint;
    private DateOnly? _selectedDay;
    private bool _allReminders;
    private bool _settingDay;
    private bool _busy;
    private int _eventOffset;
    private string? _storageError;

    private DateOnly SelectedDay => _selectedDay ?? _reminders.Snapshot.Today;

    private void OnRemindersChanged() => _agendaRefresh.Request();
    private void OnAgendaSettingsChanged(HubSettings settings) => _agendaRefresh.Request();

    private void NavigateAgenda(string? target)
    {
        if (target == "agenda-today")
        {
            SetDay(null);
            Dispatcher.BeginInvoke(() =>
            {
                if (_visible is null) return;
                AgendaDate.BringIntoView();
                if (ReminderEditor.Visibility != Visibility.Visible) AgendaDate.Focus();
            }, DispatcherPriority.Loaded);
            return;
        }
        if (target == "new-reminder")
        {
            if (!_busy && ReminderEditor.Visibility != Visibility.Visible) OpenEditor(null, AddReminder);
            return;
        }
        if (target?.StartsWith("reminder:", StringComparison.Ordinal) == true
            && Guid.TryParseExact(target.AsSpan("reminder:".Length), "N", out var id))
        {
            var item = _reminders.Snapshot.Items.FirstOrDefault(r => r.Id == id);
            if (item?.Pending is { } pending) SetDay(_reminders.Snapshot.LocalDay(pending.At));
            var row = _agendaRows.FirstOrDefault(r => r.Reminder?.Id == id);
            if (row is not null)
                Dispatcher.BeginInvoke(() => (AgendaList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement)?.BringIntoView(),
                    DispatcherPriority.Loaded);
        }
        RefreshAgenda();
    }

    private void RefreshAgenda()
    {
        if (_visible is null) return;
        var snapshot = _reminders.Snapshot;
        var day = SelectedDay;
        var settings = Hub.S;
        var use24 = settings.General.Use24Hour;
        CancelSceneRun.Visibility = Hub.Actions.ReminderSceneRunning ? Visibility.Visible : Visibility.Collapsed;
        _settingDay = true;
        AgendaDate.SelectedDate = day.ToDateTime(TimeOnly.MinValue);
        _settingDay = false;
        AllReminders.Content = _allReminders ? "Back to day" : "All reminders";
        AgendaHeading.Text = _allReminders ? "All reminders" : day.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
        var rows = new List<AgendaData>();
        var events = _allReminders ? [] : Hub.State.Events.Where(e => OnDay(e, day, snapshot)).Skip(_eventOffset).Take(EventsPerPage + 1).ToArray();
        if (!_allReminders && events.Length == 0 && _eventOffset > 0)
        {
            _eventOffset = 0;
            events = Hub.State.Events.Where(e => OnDay(e, day, snapshot)).Take(EventsPerPage + 1).ToArray();
        }
        foreach (var item in events.Take(EventsPerPage))
        {
            var at = TimeZoneInfo.ConvertTime(item.Start, snapshot.Zone);
            var when = item.AllDay ? "All day" : snapshot.LocalDay(item.Start) < day ? "Ongoing" : FormatTime(at, use24);
            var detail = string.Join(" · ", new[] { item.Source, item.Location, item.Detail }.Where(part => !string.IsNullOrWhiteSpace(part)));
            rows.Add(new($"event:{item.Id}", item.Start, item.AllDay ? 1 : 2, item.Title, when, detail, null));
        }

        var reminders = _allReminders ? snapshot.AllRows() : snapshot.RowsForDay(day);
        foreach (var reminder in reminders) rows.Add(ReminderData(reminder, snapshot, settings, showDate: _allReminders, priority: 2));
        if (!_allReminders && day == snapshot.Today)
            foreach (var item in snapshot.Items)
                if (item.Pending is { } pending && snapshot.LocalDay(pending.At) < day)
                    rows.Add(ReminderData(new(item, pending), snapshot, settings, showDate: true, priority: 0));

        SetAgendaRows(rows.OrderBy(row => row.Priority).ThenBy(row => row.At).ThenBy(row => row.Key, StringComparer.Ordinal));
        MoreAgendaItems.Visibility = events.Length > EventsPerPage ? Visibility.Visible : Visibility.Collapsed;
        PreviousAgendaItems.Visibility = !_allReminders && _eventOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
        AgendaRetry.Visibility = snapshot.Error is not null ? Visibility.Visible : Visibility.Collapsed;
        if (_storageError != snapshot.Error)
        {
            _storageError = snapshot.Error;
            if (_storageError is not null) AgendaFeedback.Show(_storageError, "agenda-storage-error", error: true);
            else AgendaFeedback.Hide("agenda-storage-error");
        }
        AgendaEmpty.Text = _allReminders ? "No reminders yet." : "Nothing scheduled for this day.";
        AgendaEmpty.Visibility = rows.Count == 0 && !snapshot.Loading && snapshot.Error is null ? Visibility.Visible : Visibility.Collapsed;
        if (_allReminders || !Hub.S.Events.Calendars.Any(c => c.Enabled)) return;
        if (Hub.State.FreshAt("events") is not { } refreshed)
            AgendaHeading.Text += " · Calendar hasn’t refreshed yet";
        else if (!Hub.State.Online)
            AgendaHeading.Text += " · Showing cached events";
        else if (day < snapshot.Today || (day.ToDateTime(TimeOnly.MinValue) - snapshot.Today.ToDateTime(TimeOnly.MinValue)).Days > Hub.S.Events.LookaheadDays)
            AgendaHeading.Text += " · Outside the calendar feed’s date range";
    }

    private static bool OnDay(HubEvent item, DateOnly day, ReminderSnapshot snapshot)
    {
        if (item.AllDay)
        {
            var date = DateOnly.FromDateTime(item.Start.Date);
            return date == day || (date < day && item.End is { } allDayEnd && DateOnly.FromDateTime(allDayEnd.Date) > day);
        }
        var start = snapshot.LocalDay(item.Start);
        if (start == day) return true;
        return start < day && item.End is { } end && end > item.Start && snapshot.LocalDay(end.AddTicks(-1)) >= day;
    }

    private static AgendaData ReminderData(ReminderRow row, ReminderSnapshot snapshot, HubSettings settings, bool showDate, int priority)
    {
        var at = TimeZoneInfo.ConvertTime(row.Occurrence.At, snapshot.Zone);
        var when = FormatTime(at, settings.General.Use24Hour);
        if (showDate) when = at.ToString("d MMM", CultureInfo.CurrentCulture) + Environment.NewLine + when;
        var repeat = row.Reminder.Draft.Repeat switch { ReminderRepeat.Daily => "Daily", ReminderRepeat.Weekly => "Weekly", _ => "Once" };
        var status = row.Occurrence.Status;
        var detail = status == ReminderStatus.Scheduled ? $"Reminder · {repeat}" : $"Reminder · {repeat} · {status}";
        var scene = ReminderSceneReview.FindScene(settings, row.Reminder.Draft.SceneId);
        if (row.Reminder.Draft.SceneId is not null)
        {
            detail += scene is null ? " · Scene unavailable" : $" · {scene.Name}";
            if (row.Occurrence.SceneRunRequested) detail += " · Run requested";
        }
        return new($"reminder:{row.Id:N}:{row.Occurrence.Nominal.Ticks}", row.Occurrence.At, priority, row.Reminder.Draft.Title, when, detail,
            row, scene?.Name);
    }

    private static string FormatTime(DateTimeOffset at, bool use24) => at.ToString(use24 ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);

    private void SetAgendaRows(IEnumerable<AgendaData> ordered)
    {
        var items = ordered.ToArray();
        var desired = items.Select(item => item.Key).ToHashSet();
        for (var index = _agendaRows.Count - 1; index >= 0; index--)
            if (!desired.Contains(_agendaRows[index].Key)) _agendaRows.RemoveAt(index);
        var existing = _agendaRows.ToDictionary(row => row.Key);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!existing.TryGetValue(item.Key, out var row))
                _agendaRows.Insert(index, new(item));
            else
            {
                row.Update(item);
                var position = _agendaRows.IndexOf(row);
                if (position != index) _agendaRows.Move(position, index);
            }
        }
    }

    private void SetDay(DateOnly? value)
    {
        _selectedDay = value == _reminders.Snapshot.Today ? null : value;
        _allReminders = false;
        _eventOffset = 0;
        RefreshAgenda();
    }

    private void OnDaySelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingDay && AgendaDate.SelectedDate is { } date) SetDay(DateOnly.FromDateTime(date));
    }
    private void OnToday(object sender, RoutedEventArgs e) => SetDay(null);
    private void OnPreviousDay(object sender, RoutedEventArgs e) { if (SelectedDay > DateOnly.MinValue) SetDay(SelectedDay.AddDays(-1)); }
    private void OnNextDay(object sender, RoutedEventArgs e) { if (SelectedDay < DateOnly.MaxValue) SetDay(SelectedDay.AddDays(1)); }
    private void OnAllReminders(object sender, RoutedEventArgs e) { _allReminders = !_allReminders; RefreshAgenda(); }
    private void OnMoreAgendaItems(object sender, RoutedEventArgs e) { _eventOffset += EventsPerPage; RefreshAgenda(); }
    private void OnPreviousAgendaItems(object sender, RoutedEventArgs e) { _eventOffset = Math.Max(0, _eventOffset - EventsPerPage); RefreshAgenda(); }
    private void OnAddReminder(object sender, RoutedEventArgs e) => NavigateAgenda("new-reminder");
    private void OnAgendaDateInvalid(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        AgendaFeedback.Show("Enter a valid agenda date.", "agenda-status", error: true);
    }
    private void OnReminderDateInvalid(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        ReminderDate.SelectedDate = null;
        AgendaFeedback.Show("Enter a valid date.", "reminder-editor-status", error: true);
    }

    private void OnReminderEdit(object sender, RoutedEventArgs e)
    {
        if (!_busy && ReminderEditor.Visibility != Visibility.Visible && sender is Button { DataContext: AgendaEntry { Reminder: { } row } } button)
            OpenEditor(row.Reminder, button);
    }

    private void OpenEditor(Reminder? original, FrameworkElement entryPoint)
    {
        CloseSceneReview(focus: false);
        var snapshot = _reminders.Snapshot;
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, snapshot.Zone).DateTime;
        var suggested = SelectedDay > snapshot.Today ? SelectedDay.ToDateTime(new TimeOnly(9, 0)) : now.AddMinutes(5);
        var draft = original?.Draft ?? new("", suggested, ReminderRepeat.Once);
        _editing = original;
        _editorEntryPoint = entryPoint;
        ReminderTitle.Text = draft.Title;
        ReminderDate.SelectedDate = draft.LocalTime.Date;
        ReminderTime.Text = draft.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        ReminderRepeatBox.SelectedIndex = (int)draft.Repeat;
        LoadSceneChoices(draft.SceneId);
        EditorHeading.Text = original is null ? "New reminder" : "Edit reminder";
        AgendaFeedback.Hide("reminder-editor-status");
        ReminderEditor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => { ReminderEditor.BringIntoView(); ReminderTitle.Focus(); ReminderTitle.SelectAll(); }, DispatcherPriority.Loaded);
        RefreshAgenda();
    }

    private void CloseEditor(bool focus)
    {
        ReminderEditor.Visibility = Visibility.Collapsed;
        AgendaFeedback.Hide("reminder-editor-status");
        _editing = null;
        ReminderSceneBox.ItemsSource = null;
        ReminderTitle.Clear();
        if (focus)
        {
            var target = _editorEntryPoint;
            Dispatcher.BeginInvoke(() =>
            {
                if (_visible is not null && ReminderEditor.Visibility != Visibility.Visible)
                    (target is { IsVisible: true } ? target : AddReminder).Focus();
            }, DispatcherPriority.Input);
        }
        _editorEntryPoint = null;
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        if (!_busy && e.Key == Key.Escape && !ReminderRepeatBox.IsDropDownOpen && !ReminderDate.IsDropDownOpen && !ReminderSceneBox.IsDropDownOpen)
        {
            CloseEditor(focus: true);
            e.Handled = true;
        }
    }
    private void OnCancelEditor(object sender, RoutedEventArgs e) { if (!_busy) CloseEditor(focus: true); }

    private async void OnSaveReminder(object sender, RoutedEventArgs e)
    {
        if (ReminderDate.SelectedDate is not { } date) { AgendaFeedback.Show("Choose a date.", "reminder-editor-status", error: true); ReminderDate.Focus(); return; }
        if (!TimeOnly.TryParseExact(ReminderTime.Text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            AgendaFeedback.Show("Enter a 24-hour time, such as 19:00.", "reminder-editor-status", error: true);
            ReminderTime.Focus();
            return;
        }
        var draft = new ReminderDraft(ReminderTitle.Text, date.Date.Add(time.ToTimeSpan()), (ReminderRepeat)ReminderRepeatBox.SelectedIndex,
            (ReminderSceneBox.SelectedItem as SceneChoice)?.Id);
        var original = _editing;
        await RunAsync(async ct =>
        {
            var saved = await _reminders.SaveAsync(draft, original, ct);
            if (ct.IsCancellationRequested) return;
            CloseEditor(focus: true);
            var nextDay = saved.Draft.Repeat == ReminderRepeat.Once
                ? _reminders.Snapshot.LocalDay(saved.Pending?.At ?? saved.FirstInstant)
                : DateOnly.FromDateTime(saved.NextLocal ?? date);
            if (!_allReminders) SetDay(nextDay);
        }, editor: true);
    }

    private async void OnRetryReminders(object sender, RoutedEventArgs e) => await RunAsync(ct => _reminders.RefreshAsync(ct));
    private async void OnReminderDone(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgendaEntry { Reminder: { } row } }) await RunAsync(ct => _reminders.DoneAsync(row, ct));
    }
    private async void OnReminderSnooze(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AgendaEntry { Reminder: { } row } }) await RunAsync(ct => _reminders.SnoozeAsync(row, ct));
    }
    private async void OnReminderDelete(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { DataContext: AgendaEntry { Reminder: { } row } }) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete “{row.Reminder.Draft.Title}” and its schedule?", "Delete reminder",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
            await RunAsync(ct => _reminders.DeleteAsync(row.Reminder, ct));
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, bool editor = false)
    {
        if (_busy || _visible is null) return;
        var ct = _visible.Token;
        _busy = true;
        var noticeId = editor ? "reminder-editor-status" : "agenda-status";
        EditorFields.IsEnabled = AddReminder.IsEnabled = AgendaList.IsEnabled = AgendaRetry.IsEnabled = false;
        AgendaFeedback.Show(editor ? "Saving reminder…" : "Working…", noticeId, busy: true);
        RefreshAgenda();
        try
        {
            await work(ct);
            if (!ct.IsCancellationRequested) AgendaFeedback.Hide(noticeId);
        }
        catch (OperationCanceledException) { if (!ct.IsCancellationRequested) AgendaFeedback.Hide(noticeId); }
        catch (ReminderException ex)
        {
            if (!ct.IsCancellationRequested)
            {
                AgendaFeedback.Show(ex.Message, noticeId, error: true);
                if (ex.Field == "title") _ = Dispatcher.BeginInvoke(() => ReminderTitle.Focus(), DispatcherPriority.Input);
                if (ex.Field == "time") _ = Dispatcher.BeginInvoke(() => ReminderTime.Focus(), DispatcherPriority.Input);
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _busy = false;
                RestoreActions();
                RefreshAgenda();
            }
        }
    }

    private void RestoreActions() => EditorFields.IsEnabled = AddReminder.IsEnabled = AgendaList.IsEnabled = AgendaRetry.IsEnabled = true;

    private sealed record AgendaData(string Key, DateTimeOffset At, int Priority, string Title, string When, string Detail, ReminderRow? Reminder,
        string? SceneName = null);

    private sealed class AgendaEntry(AgendaData data) : ObservableObject
    {
        private AgendaData _data = data;
        public string Key => _data.Key;
        public string Title => _data.Title;
        public string When => _data.When;
        public string Detail => _data.Detail;
        public ReminderRow? Reminder => _data.Reminder;
        public bool Editable => Reminder is not null;
        public bool Actionable => Reminder is { Occurrence.Status: not ReminderStatus.Scheduled };
        public bool CanSnooze => Reminder is { Occurrence.Status: ReminderStatus.Due or ReminderStatus.Missed };
        public bool CanRunScene => Reminder?.CanRunScene == true && _data.SceneName is not null;
        public string RunSceneName => $"Review scene {_data.SceneName} for {Title}";
        public string DoneName => $"Mark {Title} done";
        public string SnoozeName => $"Snooze {Title} for 10 minutes";
        public string EditName => $"Edit {Title}";
        public string DeleteName => $"Delete {Title}";

        public void Update(AgendaData data)
        {
            if (_data == data) return;
            _data = data;
            RaiseAll();
        }
    }
}
