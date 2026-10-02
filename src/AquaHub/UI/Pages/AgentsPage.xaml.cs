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

public sealed record StageVM(int Number, string Name, string Description, List<AgentVM> Agents)
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => $"{Number}. {Name}";
}
public sealed record RunVM(string Time, string Agent, string Message, string Duration, Brush Brush)
{
    public override string ToString() => $"{Time} {Agent}: {Message}";
}
/// <summary>One model output in the inspector; kept across refreshes so an open entry stays open.</summary>
public sealed class OutputVM : ObservableObject
{
    public override string ToString() => $"{Purpose}: {Meta}";

    private bool _expanded;
    private string _meta = "";
    public OutputVM(string key, string purpose, string output) { Key = key; Purpose = purpose; Output = output; }
    public string Key { get; }
    public string Purpose { get; }
    public string Output { get; }
    public string Meta { get => _meta; set => Set(ref _meta, value); }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

public partial class AgentsPage : UserControl, IPage
{
    private readonly Dictionary<string, AgentVM> _agents = new();
    private readonly Dictionary<string, OutputVM> _outputs = new();
    private readonly UiThrottle _refresh;
    private readonly System.Windows.Threading.DispatcherTimer _tick;

    private static readonly (string Name, string Description, string[] Roles)[] StageDefs =
    {
        ("Collect", "Scouts fetch fresh data from reliable sources", new[] { "collector" }),
        ("Understand", "Curate, cluster and analyse", new[] { "curator", "analyst" }),
        ("Write", "Editors turn it into briefs you can read in a minute", new[] { "editor" }),
        ("Watch & keep", "Alerts, model health and housekeeping", new[] { "sentinel", "keeper" }),
    };

    public AgentsPage()
    {
        InitializeComponent();
        _refresh = new UiThrottle(Refresh, 300);
        _tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _tick.Tick += (_, _) => Refresh();
    }

    public void OnNavigatedTo(string? arg)
    {
        Hub.State.Changed -= OnChanged; // navigating to the page already shown must not subscribe twice
        Hub.State.Changed += OnChanged;
        Hub.Ollama.Changed -= OnOllamaChanged;
        Hub.Ollama.Changed += OnOllamaChanged;
        UpdateOllama();
        _tick.Start();
        Build();
        Refresh();
    }

    public void OnNavigatedFrom()
    {
        Hub.State.Changed -= OnChanged;
        Hub.Ollama.Changed -= OnOllamaChanged;
        _tick.Stop();
        _refresh.Stop();
    }

    private void OnChanged(string topic)
    {
        if (topic is Topics.Agents or Topics.Ai) _refresh.Request();
    }

    private void Build()
    {
        _agents.Clear();
        var statuses = Hub.Core.Agents.Statuses;
        var stages = new List<StageVM>();
        var n = 1;
        foreach (var (name, desc, roles) in StageDefs)
        {
            var list = statuses.Where(s => roles.Contains(s.Role)).Select(s =>
            {
                var vm = new AgentVM(s);
                _agents[s.Id] = vm;
                return vm;
            }).ToList();
            stages.Add(new StageVM(n++, name, desc, list));
        }
        Stages.ItemsSource = stages;
    }

    private void Refresh()
    {
        foreach (var s in Hub.Core.Agents.Statuses)
            if (_agents.TryGetValue(s.Id, out var vm)) vm.Update(s);

        var statuses = Hub.Core.Agents.Statuses;
        var ai = statuses.Count(s => s.UsesAi);
        var running = statuses.Count(s => s.State == Core.Models.AgentState.Running);
        Subtitle.Text = $"{statuses.Count} agents, {ai} of them using the local model." + (running > 0 ? $" {running} working now." : "");
        PauseText.Text = Hub.Core.Agents.Paused ? "Resume all" : "Pause all";
        PauseIcon.Kind = Hub.Core.Agents.Paused ? "play" : "pause";

        Runs.ItemsSource = Hub.Core.Db.GetRuns(18).Select(r => new RunVM(
            r.Started.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
            statuses.FirstOrDefault(s => s.Id == r.Agent)?.Name ?? r.Agent,
            r.Message,
            r.Duration.TotalSeconds >= 1 ? $"{r.Duration.TotalSeconds:0.0}s" : $"{r.Duration.TotalMilliseconds:0}ms",
            Fmt.Res(r.Ok ? "B.Up" : "B.Down"))).ToList();

        var latest = Hub.Core.Llm.RecentOutputs.Reverse().Take(8).ToList();
        var outputs = latest.Select(o =>
        {
            var key = o.Purpose + "|" + o.Time.UtcTicks;
            var vm = _outputs.TryGetValue(key, out var existing) ? existing : new OutputVM(key, o.Purpose, HtmlText.Truncate(o.Output, 2500));
            vm.Meta = $"  ·  {o.Model}  ·  {TimeText.AgoPhrase(o.Time)}";
            return vm;
        }).ToList();
        _outputs.Clear();
        foreach (var vm in outputs) _outputs[vm.Key] = vm;
        if (Outputs.ItemsSource is not List<OutputVM> shown || !shown.SequenceEqual(outputs)) Outputs.ItemsSource = outputs;
        NoOutputs.Visibility = outputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRunAll(object sender, RoutedEventArgs e) => Hub.Core.Agents.RunAll();

    private void OnOllamaChanged() => Hub.OnUi(UpdateOllama);

    /// <summary>"Ollama: Running · Turn off" in the header — the server every AI agent depends on.</summary>
    private void UpdateOllama()
    {
        var local = OllamaManager.IsLocalOllama && OllamaManager.Installed;
        OllamaButton.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        if (!local) return;
        var running = Hub.Ollama.Running == true;
        OllamaDot.Fill = Fmt.Res(Hub.Ollama.Transitioning ? "B.Info" : running ? "B.Up" : "B.Text3");
        OllamaText.Text = Hub.Ollama.Transitioning ? (running ? "Stopping Ollama…" : "Starting Ollama…")
            : running ? "Ollama on · Turn off" : "Ollama off · Turn on";
        OllamaButton.IsEnabled = !Hub.Ollama.Transitioning;
        OllamaButton.ToolTip = $"Local model server: {Hub.Ollama.StatusText}";
    }

    private async void OnToggleOllama(object sender, RoutedEventArgs e)
    {
        if (Hub.Ollama.Running == true) await Hub.Ollama.TurnOffAsync();
        else await Hub.Ollama.TurnOnAsync();
        UpdateOllama();
    }

    private void OnPauseAll(object sender, RoutedEventArgs e)
    {
        Hub.Core.Agents.Paused = !Hub.Core.Agents.Paused;
        Refresh();
    }
}
