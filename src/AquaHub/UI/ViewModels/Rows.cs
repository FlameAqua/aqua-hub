using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Models;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.Services;

namespace AquaHub.UI.ViewModels;

public sealed record SourceLink(string Name, string? Url, ICommand Open)
{
    public override string ToString() => Name;
}

public sealed class StoryVM : ObservableObject
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => Title;

    private bool _expanded;
    private bool _saved;
    private string _signature = "";

    public StoryVM(StoryCluster c)
    {
        Id = c.Id;
        Apply(c);
        OpenCommand = new RelayCommand(() => AppLauncher.OpenUrl(Url));
        ToggleCommand = new RelayCommand(() => Expanded = !Expanded);
        SaveCommand = new RelayCommand(ToggleSave);
        // The story itself goes to Ask (full coverage, summaries and the articles), not just its headline.
        AskCommand = new RelayCommand(() => Hub.Windows.ShowMain("ask", "@story:" + Id));
        CopyCommand = new RelayCommand(() =>
        {
            if (Sandbox.Intercept("clipboard", Url ?? Title)) return;
            try { System.Windows.Clipboard.SetText(Url ?? Title); } catch { }
        });
    }

    /// <summary>Refreshes this row in place (keeps it expanded, keeps the list's scroll position).</summary>
    public StoryVM Update(StoryCluster c)
    {
        if (Signature(c) == _signature) return this;
        Apply(c);
        RaiseAll();
        return this;
    }

    private static string Signature(StoryCluster c) =>
        string.Join("|", c.Title, c.Summary?.Headline, c.Summary?.Tldr, c.Summary?.WhyItMatters, c.Summary?.IsAi, c.SourceNames.Count,
            c.Latest.ToUnixTimeSeconds() / 60, c.ImageUrl, c.Items.Count, c.Items.Any(i => i.Saved));

    private void Apply(StoryCluster c)
    {
        _signature = Signature(c);
        Title = c.Summary is { IsAi: true, Headline.Length: > 0 } s ? s.Headline : c.Title;
        OriginalTitle = c.Title;
        Tldr = c.Summary?.Tldr ?? "";
        KeyPoints = c.Summary?.KeyPoints ?? new();
        Why = c.Summary?.WhyItMatters ?? "";
        IsAi = c.Summary?.IsAi == true;
        Model = c.Summary?.Model;
        Category = c.Category;
        CategoryLabel = Fmt.Category(c.Category);
        CategoryBrush = Fmt.CategoryBrush(c.Category);
        var names = c.SourceNames;
        SourcesText = names.Count <= 2 ? string.Join(" · ", names) : $"{names[0]} · {names[1]} +{names.Count - 2}";
        SourceCount = names.Count;
        CoverageText = names.Count > 1 ? $"{names.Count} sources" : names.FirstOrDefault() ?? "";
        TimeAgo = TimeText.Ago(c.Latest);
        TimeAgoPhrase = TimeText.AgoPhrase(c.Latest);
        ImageUrl = c.ImageUrl;
        Url = c.Url;
        IsLocal = c.IsLocal;
        Interests = c.Matches.Count > 0 ? string.Join(", ", c.Matches) : "";
        _saved = c.Items.Any(i => i.Saved);
        Sources = c.Items.GroupBy(i => i.SourceName).Select(g => g.First())
            .Select(i => new SourceLink(i.SourceName, i.Url, new RelayCommand(() => AppLauncher.OpenUrl(i.Url)))).ToList();
        ItemIds = c.Items.Select(i => i.Id).ToList();
    }

    public string Id { get; }
    public string Title { get; private set; } = "";
    public string OriginalTitle { get; private set; } = "";
    public string Tldr { get; private set; } = "";
    public List<string> KeyPoints { get; private set; } = new();
    public string Why { get; private set; } = "";
    public bool IsAi { get; private set; }
    public string? Model { get; private set; }
    public string Category { get; private set; } = "";
    public string CategoryLabel { get; private set; } = "";
    public Brush CategoryBrush { get; private set; } = Brushes.Transparent;
    public string SourcesText { get; private set; } = "";
    public string CoverageText { get; private set; } = "";
    public int SourceCount { get; private set; }
    public string TimeAgo { get; private set; } = "";
    public string TimeAgoPhrase { get; private set; } = "";
    public string? ImageUrl { get; private set; }
    public string? Url { get; private set; }
    public bool IsLocal { get; private set; }
    public string Interests { get; private set; } = "";
    public List<SourceLink> Sources { get; private set; } = new();
    public List<string> ItemIds { get; private set; } = new();
    public bool HasImage => !string.IsNullOrEmpty(ImageUrl) && Hub.S.Privacy.LoadRemoteImages;
    public bool MultiSource => SourceCount > 1;

    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool Saved { get => _saved; set { if (Set(ref _saved, value)) Raise(nameof(SaveIcon)); } }
    public string SaveIcon => Saved ? "bookmark-filled" : "bookmark";

    public ICommand OpenCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AskCommand { get; }
    public ICommand CopyCommand { get; }

    private void ToggleSave()
    {
        Saved = !Saved;
        foreach (var id in ItemIds.Take(1)) Hub.Core.Db.SetSaved(id, Saved);
    }
}

/// <summary>Reuses story rows by id across refreshes so expansion state and scroll position survive updates.</summary>
public sealed class StoryVMCache
{
    private Dictionary<string, StoryVM> _rows = new();

    public List<StoryVM> Map(IEnumerable<StoryCluster> stories)
    {
        var next = new Dictionary<string, StoryVM>();
        var list = new List<StoryVM>();
        foreach (var c in stories)
        {
            if (next.ContainsKey(c.Id)) continue;
            var vm = _rows.TryGetValue(c.Id, out var existing) ? existing.Update(c) : new StoryVM(c);
            next[c.Id] = vm;
            list.Add(vm);
        }
        _rows = next;
        return list;
    }
}

public sealed class PostVM
{
    public override string ToString() => HtmlText.OneLine(Title.Length > 0 ? Title : Body, 140);

    public PostVM(FeedItem p)
    {
        Title = p.Title;
        Body = p.Summary.Length > 0 && !p.Summary.StartsWith(p.Title[..Math.Min(p.Title.Length, 40)], StringComparison.Ordinal) ? HtmlText.Truncate(p.Summary, 280) : "";
        Source = p.SourceName;
        Platform = p.Platform;
        PlatformLabel = Fmt.Platform(p.Platform);
        PlatformBrush = Fmt.PlatformBrush(p.Platform);
        Author = p.Author ?? "";
        TimeAgo = TimeText.Ago(p.Published);
        Meta = string.Join(" · ", new[] { Source == PlatformLabel ? null : Source, string.IsNullOrEmpty(Author) ? null : Author, TimeAgo }.Where(s => !string.IsNullOrEmpty(s)));
        ScoreText = p.Score > 0 ? Fmt.Compact(p.Score) : "";
        CommentsText = p.Comments > 0 ? Fmt.Compact(p.Comments) : "";
        Url = p.Url ?? p.CommentsUrl;
        ImageUrl = p.ImageUrl;
        OpenCommand = new RelayCommand(() => AppLauncher.OpenUrl(Url));
        DiscussCommand = new RelayCommand(() => AppLauncher.OpenUrl(p.CommentsUrl ?? Url));
    }

    public string Title { get; }
    public string Body { get; }
    public string Source { get; }
    public string Platform { get; }
    public string PlatformLabel { get; }
    public Brush PlatformBrush { get; }
    public string Author { get; }
    public string TimeAgo { get; }
    public string Meta { get; }
    public string ScoreText { get; }
    public string CommentsText { get; }
    public string? Url { get; }
    public string? ImageUrl { get; }
    public bool HasImage => !string.IsNullOrEmpty(ImageUrl) && Hub.S.Privacy.LoadRemoteImages;
    public ICommand OpenCommand { get; }
    public ICommand DiscussCommand { get; }
}

public sealed class QuoteVM
{
    public override string ToString() => $"{Name}, {PriceText}, {ChangeText}";

    public QuoteVM(Quote q, Indicators? ind, WatchInsight? insight = null, double? shares = null)
    {
        Symbol = q.Symbol;
        Name = q.Name;
        PriceText = Fmt.Price(q.Price, q.Kind is InstrumentKind.Index or InstrumentKind.Fx ? "" : q.Currency);
        ChangeText = Fmt.Pct(q.ChangePercent);
        AbsChangeText = Fmt.Signed(q.Change, q.Price >= 1000 ? 0 : 2);
        ChangeBrush = Fmt.UpDown(q.ChangePercent);
        ChangeSoft = Fmt.UpDownSoft(q.ChangePercent);
        Arrow = q.ChangePercent >= 0 ? "arrow-up" : "arrow-down";
        Intraday = q.Intraday;
        Baseline = q.PreviousClose;
        IsOpen = q.MarketState == "open";
        StateText = IsOpen ? "Live" : "Closed";
        Signal = ind?.Signal.ToString().ToLowerInvariant() ?? "";
        SignalText = ind is null ? "" : $"{ind.Signal} {ind.TechScore:+0;-0}";
        SignalBrush = Fmt.StanceBrush(Signal);
        SignalSoft = Fmt.StanceSoft(Signal);
        Stance = insight?.Stance ?? "";
        StanceText = insight is null ? "" : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(insight.Stance);
        StanceBrush = Fmt.StanceBrush(insight?.Stance ?? "");
        StanceSoft = Fmt.StanceSoft(insight?.Stance ?? "");
        Kind = q.Kind;
        if (shares is > 0) HoldingText = Fmt.Price(q.Price * shares.Value, q.Currency);
        OpenCommand = new RelayCommand(() => Hub.Windows.ShowMain("markets", Symbol));
    }

    public string Symbol { get; }
    public string Name { get; }
    public string PriceText { get; }
    public string ChangeText { get; }
    public string AbsChangeText { get; }
    public Brush ChangeBrush { get; }
    public Brush ChangeSoft { get; }
    public string Arrow { get; }
    public double[] Intraday { get; }
    public double Baseline { get; }
    public bool IsOpen { get; }
    public string StateText { get; }
    public string Signal { get; }
    public string SignalText { get; }
    public Brush SignalBrush { get; }
    public Brush SignalSoft { get; }
    public string Stance { get; }
    public string StanceText { get; }
    public Brush StanceBrush { get; }
    public Brush StanceSoft { get; }
    public bool HasStance => StanceText.Length > 0;
    public InstrumentKind Kind { get; }
    public string? HoldingText { get; }
    public ICommand OpenCommand { get; }
}

public sealed class EventVM
{
    public override string ToString() => $"{Title}, {When}";

    public EventVM(HubEvent e, bool use24)
    {
        Title = e.Title;
        When = Fmt.EventWhen(e, use24);
        var local = e.Start.ToLocalTime();
        DayNumber = local.Day.ToString(CultureInfo.CurrentCulture);
        Month = local.ToString("MMM", CultureInfo.CurrentCulture).ToUpperInvariant();
        Weekday = local.ToString("ddd", CultureInfo.CurrentCulture);
        TimeText = e.AllDay ? "All day" : local.ToString(use24 ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);
        Relative = e.AllDay ? "" : Core.Util.TimeText.Until(e.Start);
        Detail = string.Join(" · ", new[] { e.Location, e.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
        KindLabel = e.Kind switch
        {
            EventKind.Calendar => e.Source,
            EventKind.Holiday => "Holiday",
            EventKind.Economic => "Economy",
            EventKind.Earnings => "Earnings",
            _ => "Reminder",
        };
        KindBrush = e.Kind switch
        {
            EventKind.Calendar when e.Color is { } hex => TryBrush(hex),
            EventKind.Calendar => Fmt.Res("B.Accent"),
            EventKind.Holiday => Fmt.Res("B.Up"),
            EventKind.Economic => Fmt.Res("B.Warn"),
            EventKind.Earnings => Fmt.Res("B.Info"),
            _ => Fmt.Res("B.Neutral"),
        };
        Importance = e.Importance;
        DayKey = local.Date;
        Url = e.Url;
        OpenCommand = new RelayCommand(() => AppLauncher.OpenUrl(Url));
    }

    private static Brush TryBrush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return Fmt.Res("B.Accent"); }
    }

    public string Title { get; }
    public string When { get; }
    public string DayNumber { get; }
    public string Month { get; }
    public string Weekday { get; }
    public string TimeText { get; }
    public string Relative { get; }
    public string Detail { get; }
    public string KindLabel { get; }
    public Brush KindBrush { get; }
    public int Importance { get; }
    public DateTime DayKey { get; }
    public string? Url { get; }
    public ICommand OpenCommand { get; }
}

public sealed record OutcomeVM(string Label, double Probability, string ProbText, string ChangeText, Brush ChangeBrush, bool IsLead)
{
    public override string ToString() => $"{Label}: {ProbText}";

    /// <summary>The crowd's favourite is drawn in the accent colour; the rest are muted.</summary>
    public Brush BarBrush => Fmt.Res(IsLead ? "B.Accent" : "B.Text3");
    public FontWeight LabelWeight => IsLead ? FontWeights.SemiBold : FontWeights.Normal;
}

public sealed class PredictionVM
{
    public override string ToString() => $"{Title}: {LeadLabel} {LeadProbText}";

    public PredictionVM(PredictionMarket m)
    {
        Title = m.Title;
        Source = m.Source;
        var lead = m.Lead;
        LeadLabel = lead?.Label ?? "";
        LeadProb = lead?.Probability ?? 0;
        LeadProbText = lead is null ? "" : Fmt.Prob(lead.Probability);
        ChangeText = Fmt.Points(lead?.Change24h);
        ChangeBrush = Fmt.UpDown(lead?.Change24h ?? 0);
        Outcomes = m.Outcomes.Take(4).Select(o => new OutcomeVM(o.Label, o.Probability, Fmt.Prob(o.Probability), Fmt.Points(o.Change24h), Fmt.UpDown(o.Change24h ?? 0),
            ReferenceEquals(o, lead))).ToList();
        VolumeText = m.Volume24h > 0 ? $"${Fmt.Compact(m.Volume24h)} traded 24h" : "";
        EndText = m.EndDate is { } end ? "Resolves " + end.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "";
        Meta = string.Join(" · ", new[] { Source, VolumeText, EndText }.Where(s => s.Length > 0));
        Url = m.Url;
        IsBinary = m.IsBinary;
        OpenCommand = new RelayCommand(() => AppLauncher.OpenUrl(Url));
    }

    public string Title { get; }
    public string Source { get; }
    public string LeadLabel { get; }
    public double LeadProb { get; }
    public string LeadProbText { get; }
    public string ChangeText { get; }
    public Brush ChangeBrush { get; }
    public List<OutcomeVM> Outcomes { get; }
    public string VolumeText { get; }
    public string EndText { get; }
    public string Meta { get; }
    public string? Url { get; }
    public bool IsBinary { get; }
    public ICommand OpenCommand { get; }
}

public sealed class AgentVM : ObservableObject
{
    public override string ToString() => $"{Name}, {StateText}";

    private AgentStatus _s;

    public AgentVM(AgentStatus s)
    {
        _s = s;
        RunCommand = new RelayCommand(() => Hub.Core.Agents.RunNow(Id));
    }

    public void Update(AgentStatus s)
    {
        _s = s;
        RaiseAll();
    }

    public string Id => _s.Id;
    public string Name => _s.Name;
    public string Description => _s.Description;
    public string Role => _s.Role;
    public string RoleLabel => _s.Role switch
    {
        "collector" => "Collector",
        "curator" => "Curator",
        "editor" => "Editor",
        "analyst" => "Analyst",
        "sentinel" => "Sentinel",
        _ => "Keeper",
    };
    public bool UsesAi => _s.UsesAi;
    public bool IsRunning => _s.State == AgentState.Running;
    public string StateText => _s.State switch
    {
        AgentState.Running => "Working",
        AgentState.Waiting => "Queued",
        AgentState.Paused => "Waiting",
        AgentState.Error => "Needs attention",
        AgentState.Disabled => "Off",
        _ => "Idle",
    };
    public Brush StateBrush => Fmt.Res(_s.State switch
    {
        AgentState.Running => "B.Accent",
        AgentState.Waiting => "B.Info",
        AgentState.Paused => "B.Warn",
        AgentState.Error => "B.Down",
        AgentState.Disabled => "B.Text3",
        _ => "B.Up",
    });
    public string Message => _s.LastMessage;
    public string LastRunText => _s.LastRun is { } t ? "Ran " + TimeText.AgoPhrase(t) : "Not run yet";
    public string NextRunText => _s.NextRun is { } n && _s.State != AgentState.Running ? (n <= DateTimeOffset.Now ? "Due now" : "Next " + TimeText.Until(n)) : "";
    public string DurationText => _s.LastDuration is { } d ? (d.TotalSeconds >= 1 ? $"{d.TotalSeconds:0.0}s" : $"{d.TotalMilliseconds:0}ms") : "";
    public string RunsText => $"{_s.Runs} runs" + (_s.Errors > 0 ? $" · {_s.Errors} errors" : "");
    /// <summary>"Ran 2m ago · 40ms · Next in 5 min", skipping parts that don't apply (no stray separators).</summary>
    public string FooterText => string.Join("  ·  ", new[] { LastRunText, DurationText, NextRunText }.Where(p => p.Length > 0));
    public ICommand RunCommand { get; }
}

public sealed class AppVM : ObservableObject
{
    public override string ToString() => Name;

    private bool _running;
    private bool _editing;

    public AppVM(Core.Settings.AppEntry app)
    {
        Entry = app;
        Icon = Hub.Catalog.IconFor(app, 64);
        LaunchCommand = new RelayCommand(() => Hub.Launcher.Launch(app));
        CloseCommand = new RelayCommand(() => Hub.Launcher.Close(app));
        RemoveCommand = new RelayCommand(() => Remove(app));
    }

    public Core.Settings.AppEntry Entry { get; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon is not null;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public bool IsRunning { get => _running; set => Set(ref _running, value); }
    /// <summary>The Launchpad's Edit mode shows a remove badge on each tile.</summary>
    public bool IsEditing { get => _editing; set => Set(ref _editing, value); }
    public ICommand LaunchCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RemoveCommand { get; }

    /// <summary>Removes the app from the Launchpad (and quick panel), with Undo — scenes that open it just skip it.</summary>
    private static void Remove(Core.Settings.AppEntry app)
    {
        var index = Hub.S.Apps.FindIndex(a => a.Id == app.Id);
        if (index < 0) return;
        Hub.Core.Settings.Update(s => s.Apps.RemoveAll(a => a.Id == app.Id));
        var usedBy = Hub.S.Scenes.Where(sc => sc.Steps.Any(st => st.Target == app.Id)).Select(sc => sc.Name).ToList();
        Hub.Windows.Main?.ShowActionBanner($"Removed {app.Name}",
            usedBy.Count > 0 ? $"{string.Join(" and ", usedBy.Take(2))} will skip it. Add it back any time with Add app." : "Add it back any time with Add app.",
            "Undo", () =>
            {
                Hub.Core.Settings.Update(s =>
                {
                    if (s.Apps.All(a => a.Id != app.Id)) s.Apps.Insert(Math.Clamp(index, 0, s.Apps.Count), app);
                });
                return Task.CompletedTask;
            }, icon: "trash", automationId: "app-removed", seconds: 8);
    }
}

public sealed class SceneVM
{
    public override string ToString() => Name;

    public SceneVM(Core.Settings.Scene scene)
    {
        Scene = scene;
        Steps = scene.Steps.Select(s => ActionExecutor.Describe(s, Hub.S)).ToList();
        StepsText = string.Join(" → ", Steps);
        RunCommand = new RelayCommand(async () => await Hub.Actions.RunSceneAsync(scene));
    }

    public Core.Settings.Scene Scene { get; }
    public string Name => Scene.Name;
    public string Icon => string.IsNullOrWhiteSpace(Scene.Icon) ? "wand" : Scene.Icon;
    public string Description => Scene.Description;
    public List<string> Steps { get; }
    public string StepsText { get; }
    public ICommand RunCommand { get; }
}

public sealed class AlertVM
{
    public AlertVM(HubAlert a)
    {
        Alert = a;
        Title = a.Title;
        Body = a.Body;
        TimeAgo = TimeText.Ago(a.Created);
        Icon = a.Kind switch
        {
            "price" => "markets",
            "news" => "news",
            "keyword" => "hash",
            "prediction" => "target",
            "calendar" => "upcoming",
            "system" => "system",
            "brief" => "sparkle",
            "update" => "download",
            _ => "bell",
        };
        Brush = Fmt.Res(a.Severity switch
        {
            AlertSeverity.Critical => "B.Down",
            AlertSeverity.Important => "B.Warn",
            _ => "B.AccentText",
        });
        Unread = !a.Read;
        OpenCommand = new RelayCommand(() => Hub.Windows.OpenAlert(a));
    }

    public HubAlert Alert { get; }
    public string Title { get; }
    public string Body { get; }
    public string TimeAgo { get; }
    public string Icon { get; }
    public Brush Brush { get; }
    public bool Unread { get; }
    public ICommand OpenCommand { get; }
}

public sealed record HourVM(string Time, int Code, bool IsDay, string Temp, string Rain)
{
    public override string ToString() => $"{Time}: {Temp}" + (Rain.Length > 0 ? $", {Rain} rain" : "");
}

public sealed record DayVM(string Day, int Code, string Min, string Max, double BarStart, double BarWidth, string Rain)
{
    public override string ToString() => $"{Day}: {Min} to {Max}" + (Rain.Length > 0 ? $", {Rain} rain" : "");
}

public static class WeatherVM
{
    public static (List<HourVM> Hours, List<DayVM> Days) Build(WeatherSnapshot w, bool use24, int hours = 8, int days = 6)
    {
        var hourList = w.Hours.Where(h => h.Time > DateTimeOffset.Now.AddMinutes(-30)).Take(hours)
            .Select(h => new HourVM(h.Time.ToString(use24 ? "HH:mm" : "h tt", CultureInfo.CurrentCulture), IconCode(h.Code, h.PrecipProb), h.IsDay, Fmt.Temp(h.Temp), h.PrecipProb >= 20 ? $"{h.PrecipProb}%" : "")).ToList();
        var ds = w.Days.Take(days).ToList();
        var lo = ds.Count > 0 ? ds.Min(d => d.Min) : 0;
        var hi = ds.Count > 0 ? ds.Max(d => d.Max) : 1;
        var span = Math.Max(1, hi - lo);
        var dayList = ds.Select((d, i) => new DayVM(i == 0 ? "Today" : d.Date.ToString("ddd", CultureInfo.CurrentCulture), IconCode(d.Code, d.PrecipProb),
            Fmt.Temp(d.Min), Fmt.Temp(d.Max), (d.Min - lo) / span, Math.Max(0.06, (d.Max - d.Min) / span), d.PrecipProb >= 20 ? $"{d.PrecipProb}%" : "")).ToList();
        return (hourList, dayList);
    }

    public static string Describe(int code) => WeatherSource.Describe(code);

    /// <summary>
    /// The model's weather code and its rain probability can disagree (a sun beside "70%"). Make the icon agree with
    /// the chance shown next to it: likely rain shows showers, a real chance of rain at least shows cloud.
    /// </summary>
    public static int IconCode(int code, int precipProb) => code switch
    {
        <= 3 or 45 or 48 when precipProb >= 55 => 80,
        <= 1 when precipProb >= 35 => 2,
        _ => code,
    };
}
