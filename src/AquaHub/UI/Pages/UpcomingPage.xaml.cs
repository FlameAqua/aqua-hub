using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Util;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public sealed record DayGroupVM(string Label, List<EventVM> Events)
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => Label;
}
public sealed record ForesightVM(string Title, string When, string Detail, string Icon, Brush Brush)
{
    public override string ToString() => $"{Title}, {When}";
}

public sealed class UpcomingVM : ObservableObject
{
    public List<DayGroupVM> Days { get; set; } = new();
    public string AgendaMeta { get; set; } = "";
    public bool NoCalendars { get; set; }
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

    public UpcomingPage()
    {
        InitializeComponent();
        DataContext = _vm;
        _refresh = new UiThrottle(Refresh, 250);
    }

    public void OnNavigatedTo(string? arg)
    {
        Hub.State.Changed -= OnChanged; // navigating to the page already shown must not subscribe twice
        Hub.State.Changed += OnChanged;
        Refresh();
    }

    public void OnNavigatedFrom()
    {
        Hub.State.Changed -= OnChanged;
        _refresh.Stop();
    }

    private void OnChanged(string topic)
    {
        if (topic is Topics.Events or Topics.Predictions or Topics.Foresight) _refresh.Request();
    }

    private void Refresh()
    {
        var use24 = Hub.S.General.Use24Hour;
        var today = DateTime.Today;
        _vm.Days = Hub.State.Events
            .Where(e => e.Start >= DateTimeOffset.Now.AddHours(-1))
            .Select(e => new EventVM(e, use24))
            .GroupBy(e => e.DayKey)
            .OrderBy(g => g.Key)
            .Select(g => new DayGroupVM(
                g.Key == today ? "Today" : g.Key == today.AddDays(1) ? "Tomorrow" : g.Key.ToString("dddd d MMMM", CultureInfo.CurrentCulture),
                g.ToList()))
            .ToList();
        _vm.NoCalendars = Hub.S.Events.Calendars.Count == 0;
        _vm.AgendaMeta = $"next {Hub.S.Events.LookaheadDays} days · {Hub.State.Events.Count} items";

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

    private void OnAddCalendar(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings", "agenda");
}
