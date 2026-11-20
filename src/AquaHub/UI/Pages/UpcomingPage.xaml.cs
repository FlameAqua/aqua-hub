using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Reminders;
using AquaHub.Core.Util;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public sealed record ForesightVM(string Title, string When, string Detail, string Icon, Brush Brush)
{
    public override string ToString() => $"{Title}, {When}";
}

public sealed class UpcomingVM : ObservableObject
{
    public string ForesightOverview { get; set; } = "";
    public string ForesightBadge { get; set; } = "";
    public string ForesightMeta { get; set; } = "";
    public List<ForesightVM> ForesightItems { get; set; } = new();
    public List<PredictionVM> Predictions { get; set; } = new();
    public void Changed() => RaiseAll();
}

public partial class UpcomingPage : UserControl, IPage
{
    private readonly UpcomingVM _vm = new();
    private readonly UiThrottle _refresh;

    public UpcomingPage(ReminderService reminders)
    {
        InitializeComponent();
        _reminders = reminders;
        DataContext = _vm;
        _refresh = new UiThrottle(Refresh, 250);
        _agendaRefresh = new UiThrottle(RefreshAgenda, 100);
        AgendaList.ItemsSource = _agendaRows;
    }

    public void OnNavigatedTo(string? arg)
    {
        Hub.State.Changed -= OnChanged; // navigating to the page already shown must not subscribe twice
        Hub.State.Changed += OnChanged;
        _reminders.Changed -= OnRemindersChanged;
        _reminders.Changed += OnRemindersChanged;
        Hub.Core.Settings.Changed -= OnAgendaSettingsChanged;
        Hub.Core.Settings.Changed += OnAgendaSettingsChanged;
        _visible ??= new CancellationTokenSource();
        if (!_busy) RestoreActions();
        Refresh();
        NavigateAgenda(arg);
    }

    public void OnNavigatedFrom()
    {
        Hub.State.Changed -= OnChanged;
        _refresh.Stop();
        _agendaRefresh.Stop();
        _reminders.Changed -= OnRemindersChanged;
        Hub.Core.Settings.Changed -= OnAgendaSettingsChanged;
        _visible?.Cancel();
        _visible?.Dispose();
        _visible = null;
        _busy = false;
        _storageError = null;
        AgendaFeedback.Hide();
        CloseEditor(focus: false);
        CloseSceneReview(focus: false);
    }

    private void OnChanged(string topic)
    {
        if (topic is Topics.Predictions or Topics.Foresight) _refresh.Request();
        if (topic is Topics.Events or Topics.Freshness) _agendaRefresh.Request();
    }

    private void Refresh()
    {

        var f = Hub.State.Foresight;
        if (f is not null)
        {
            _vm.ForesightOverview = f.Overview;
            _vm.ForesightBadge = f.IsAi ? "AI" : "";
            _vm.ForesightMeta = f.IsAi ? $"{f.Model} · {TimeText.AgoPhrase(f.GeneratedAt)}" : "List view";
            _vm.ForesightItems = f.Items.Select(i => new ForesightVM(i.Title, i.When, i.Detail,
                i.Kind switch { "prediction" => "target", "market" => "markets", _ => "upcoming" },
                Fmt.Res(i.Importance >= 3 ? "B.Warn" : "B.AccentText"))).ToList();
        }
        else _vm.ForesightOverview = "Looking ahead… your Foresight agent will summarise the week shortly.";

        _vm.Predictions = Hub.State.Predictions.Take(16).Select(m => new PredictionVM(m)).ToList();
        _vm.Changed();
    }

    private void OnAddCalendar(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings", "agenda:calendars");
}
