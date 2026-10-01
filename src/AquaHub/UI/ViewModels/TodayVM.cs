using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Models;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.UI.ViewModels;

public sealed record BriefSectionVM(string Title, string Icon, List<string> Bullets)
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => Title;
}
public sealed record PulseTopicVM(string Title, string Summary, int Heat, double HeatValue, Brush SentimentBrush, string Sentiment, string Platforms)
{
    public override string ToString() => Title;
}

/// <summary>State for the Today dashboard (and, in part, the taskbar flyout).</summary>
public sealed class TodayVM : ObservableObject
{
    private readonly UiThrottle _refresh;
    private readonly HashSet<string> _dirty = new();
    private System.Threading.Timer? _volumeDebounce;
    private int _volume = 50;
    private bool _suppressVolume;

    public TodayVM()
    {
        _refresh = new UiThrottle(Flush, 150);
        ReadBriefCommand = new RelayCommand(async () => { await Hub.Actions.ReadBriefAsync(); Raise(nameof(ReadIcon)); Raise(nameof(ReadLabel)); });
        RegenerateBriefCommand = new RelayCommand(() =>
        {
            BriefingAgent.ManualFlag.Request();
            Hub.Core.Agents.RunNow("briefing");
            BriefRegenerating = true;
        });
        RefreshCommand = new RelayCommand(() => _ = Hub.Actions.ExecuteAsync(new HubCommand("refresh")));
        PlayPauseCommand = new RelayCommand(() => _ = Hub.Actions.ExecuteAsync(new HubCommand("media_toggle")));
        NextCommand = new RelayCommand(() => _ = Hub.Actions.ExecuteAsync(new HubCommand("media_next")));
        PrevCommand = new RelayCommand(() => _ = Hub.Actions.ExecuteAsync(new HubCommand("media_previous")));
        OpenMusicCommand = new RelayCommand(() => _ = Hub.Actions.ExecuteAsync(new HubCommand("media_play")));
        MuteCommand = new RelayCommand(() => { Hub.Volume.ToggleMute(); RefreshVolume(); });
        Go = new RelayCommand(p => Hub.Windows.ShowMain(p as string ?? "today"));
        SetPlace = new RelayCommand(_ => Hub.Windows.ShowMain("settings", "location"));
    }

    private bool _attached;

    /// <summary>Idempotent: navigating to the page that is already shown calls this again.</summary>
    public void Attach()
    {
        if (_attached)
        {
            foreach (var t in new[] { "all" }) _dirty.Add(t);
            Flush();
            return;
        }
        _attached = true;
        Hub.State.Changed += OnChanged;
        Hub.Media.Changed += OnMedia;
        Hub.Speech.StateChanged += OnSpeech;
        foreach (var t in new[] { "all" }) _dirty.Add(t);
        Flush();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        Hub.State.Changed -= OnChanged;
        Hub.Media.Changed -= OnMedia;
        Hub.Speech.StateChanged -= OnSpeech;
        _refresh.Stop();
    }

    private void OnChanged(string topic)
    {
        lock (_dirty) _dirty.Add(topic);
        _refresh.Request();
    }

    private void OnMedia() { lock (_dirty) _dirty.Add("media"); _refresh.Request(); }

    /// <summary>Raise only what a section changed (a full refresh on every 2.5 s telemetry tick re-rendered the whole dashboard).</summary>
    private void RaiseMany(params string[] names)
    {
        foreach (var n in names) Raise(n);
    }
    private void OnSpeech() => Hub.OnUi(() => { Raise(nameof(ReadIcon)); Raise(nameof(ReadLabel)); });

    private void Flush()
    {
        HashSet<string> dirty;
        lock (_dirty) { dirty = new HashSet<string>(_dirty); _dirty.Clear(); }
        var all = dirty.Contains("all");
        if (all || !dirty.All(d => d is Topics.System or "media")) UpdateHeader();
        if (all || dirty.Contains(Topics.Brief)) UpdateBrief();
        if (all || dirty.Contains(Topics.Weather)) UpdateWeather();
        if (all || dirty.Contains(Topics.News) || dirty.Contains(Topics.Freshness)) UpdateStories();
        if (all || dirty.Contains(Topics.Markets) || dirty.Contains(Topics.MarketBrief) || dirty.Contains(Topics.Freshness)) UpdateMarkets();
        if (all || dirty.Contains(Topics.Pulse) || dirty.Contains(Topics.Social)) UpdatePulse();
        if (all || dirty.Contains(Topics.Events)) UpdateEvents();
        if (all || dirty.Contains(Topics.Predictions)) UpdatePredictions();
        if (all || dirty.Contains(Topics.System)) UpdateSystem();
        if (all || dirty.Contains("media")) UpdateMedia();
        if (all) UpdateLaunchpad();
    }

    // ───────────── Header ─────────────
    public string Greeting { get; private set; } = "";
    public string Subline { get; private set; } = "";

    private void UpdateHeader()
    {
        var s = Hub.S;
        var name = string.IsNullOrWhiteSpace(s.General.UserName) ? "" : ", " + s.General.UserName;
        var greeting = Fmt.Greeting(DateTime.Now) + name;
        var parts = new List<string> { DateTime.Now.ToString("dddd d MMMM", CultureInfo.CurrentCulture) };
        if (Hub.State.Weather?.Now is { } w)
            parts.Add($"{(s.Location.IsSet ? s.Location.City + " " : "")}{Fmt.Temp(w.Temp)} {WeatherSource.Describe(w.Code).ToLowerInvariant()}");
        var big = Hub.State.Stories.Count(c => c.SourceCount >= 4 && DateTimeOffset.UtcNow - c.Latest < TimeSpan.FromHours(12));
        if (big > 0) parts.Add($"{big} major {(big == 1 ? "story" : "stories")} developing");
        var next = Hub.State.Events.FirstOrDefault(e => !e.AllDay && e.Start > DateTimeOffset.Now && e.Kind == EventKind.Calendar);
        if (next is not null) parts.Add($"next: {next.Title} {TimeText.Until(next.Start)}");
        var subline = string.Join("  ·  ", parts);
        if (Greeting != greeting) { Greeting = greeting; Raise(nameof(Greeting)); }
        if (Subline != subline) { Subline = subline; Raise(nameof(Subline)); }
    }

    // ───────────── Brief ─────────────
    public bool HasBrief { get; private set; }
    public string BriefTitle { get; private set; } = "Your brief";
    public string BriefSummary { get; private set; } = "";
    public List<BriefSectionVM> BriefSections { get; private set; } = new();
    public string BriefBadge { get; private set; } = "";
    public string BriefMeta { get; private set; } = "";
    private bool _briefRegenerating;
    public bool BriefRegenerating { get => _briefRegenerating; set => Set(ref _briefRegenerating, value); }
    public string ReadIcon => Hub.Speech.IsSpeaking ? "pause" : "volume";
    public string ReadLabel => Hub.Speech.IsSpeaking ? "Stop" : "Read aloud";

    private void UpdateBrief()
    {
        var b = Hub.State.Brief;
        HasBrief = b is not null;
        if (b is not null)
        {
            BriefTitle = string.IsNullOrWhiteSpace(b.Title) ? $"Your {b.Period} brief" : b.Title;
            BriefSummary = b.Summary;
            BriefSections = b.Sections.Select(s => new BriefSectionVM(s.Title, IconFor(s.Icon), s.Bullets)).ToList();
            BriefBadge = b.IsAi ? "AI" : "";
            BriefMeta = (b.IsAi ? $"{b.Model} · " : "Quick digest · ") + Core.Util.TimeText.Stamp(b.GeneratedAt, Hub.S.General.Use24Hour);
            BriefRegenerating = false;
        }
        RaiseMany(nameof(HasBrief), nameof(BriefTitle), nameof(BriefSummary), nameof(BriefSections), nameof(BriefBadge), nameof(BriefMeta));
    }

    private static string IconFor(string icon) => icon switch
    {
        "local" => "location",
        "markets" => "markets",
        "agenda" => "upcoming",
        "weather" => "cloud",
        "predictions" => "target",
        "tech" => "bolt",
        _ => "globe",
    };

    // ───────────── Weather ─────────────
    public bool HasWeather { get; private set; }
    /// <summary>No place is chosen, so there's no forecast to wait for: the card asks for one instead.</summary>
    public bool NeedsPlace { get; private set; }
    public bool WeatherLoading => !HasWeather && !NeedsPlace;
    public ICommand SetPlace { get; }
    public string Temp { get; private set; } = "";
    public string Condition { get; private set; } = "";
    public string WeatherDetail { get; private set; } = "";
    public string HiLo { get; private set; } = "";
    public int WeatherCode { get; private set; }
    public bool IsDay { get; private set; } = true;
    public List<HourVM> Hours { get; private set; } = new();
    public List<DayVM> Days { get; private set; } = new();
    public string WeatherLocation { get; private set; } = "";

    private void UpdateWeather()
    {
        var w = Hub.State.Weather;
        HasWeather = w?.Now is not null;
        NeedsPlace = !HasWeather && !Hub.S.Location.HasCoordinates;
        if (w?.Now is { } now)
        {
            var metric = w.Units != "imperial";
            Temp = Fmt.Temp(now.Temp);
            Condition = WeatherSource.Describe(now.Code);
            WeatherDetail = $"Feels {Fmt.Temp(now.FeelsLike)}  ·  {now.Humidity}% humidity  ·  {now.Wind:0} {(metric ? "km/h" : "mph")} wind";
            var today = w.Days.FirstOrDefault();
            HiLo = today is null ? "" : $"H {Fmt.Temp(today.Max)}  L {Fmt.Temp(today.Min)}";
            WeatherCode = now.Code;
            IsDay = now.IsDay;
            (Hours, Days) = WeatherVM.Build(w, Hub.S.General.Use24Hour, 7, 5);
            WeatherLocation = w.Location;
        }
        RaiseMany(nameof(HasWeather), nameof(NeedsPlace), nameof(WeatherLoading), nameof(Temp), nameof(Condition), nameof(WeatherDetail), nameof(HiLo),
            nameof(WeatherCode), nameof(IsDay), nameof(Hours), nameof(Days), nameof(WeatherLocation));
    }

    // ───────────── Stories ─────────────
    public List<StoryVM> TopStories { get; private set; } = new();
    public List<StoryVM> LocalStories { get; private set; } = new();
    public string StoriesSubtitle { get; private set; } = "";
    public bool HasStories => TopStories.Count > 0;

    private readonly StoryVMCache _topRows = new(), _localRows = new();

    private void UpdateStories()
    {
        var stories = Hub.State.Stories;
        // Rows are reused by story id, so an expanded story stays open when summaries or new stories arrive.
        var top = _topRows.Map(stories.Where(c => !c.IsLocal).Take(5));
        var local = _localRows.Map(stories.Where(c => c.IsLocal).Take(3));
        if (!top.SequenceEqual(TopStories)) { TopStories = top; Raise(nameof(TopStories)); Raise(nameof(HasStories)); }
        if (!local.SequenceEqual(LocalStories)) { LocalStories = local; Raise(nameof(LocalStories)); }
        var multi = stories.Count(c => c.SourceCount > 1);
        var fresh = Hub.State.FreshAt("news");
        StoriesSubtitle = stories.Count == 0 ? "" :
            $"{Plural.Of(stories.Count, "story", "stories")} · {multi} cross-checked" + (fresh is { } t ? " · updated " + TimeText.AgoPhrase(t) : "");
        Raise(nameof(StoriesSubtitle));
    }

    // ───────────── Markets ─────────────
    public List<QuoteVM> Indices { get; private set; } = new();
    public List<QuoteVM> FlyoutTiles { get; private set; } = new();
    public string MarketsSubtitle { get; private set; } = "";
    public List<QuoteVM> Movers { get; private set; } = new();
    public string MarketLine { get; private set; } = "";
    public bool HasMarkets => Indices.Count + Movers.Count > 0;

    private void UpdateMarkets()
    {
        var s = Hub.S.Markets;
        var q = Hub.State.Quotes;
        var ind = Hub.State.Indicators;
        var brief = Hub.State.MarketBrief;
        Indices = s.Indices.Concat(s.Macro).Where(w => q.ContainsKey(w.Symbol)).Take(5)
            .Select(w => new QuoteVM(q[w.Symbol], ind.GetValueOrDefault(w.Symbol))).ToList();
        Movers = s.Watchlist.Where(w => q.ContainsKey(w.Symbol))
            .OrderByDescending(w => Math.Abs(q[w.Symbol].ChangePercent)).Take(4)
            .Select(w => new QuoteVM(q[w.Symbol], ind.GetValueOrDefault(w.Symbol), brief?.Insights.FirstOrDefault(i => i.Symbol == w.Symbol))).ToList();
        FlyoutTiles = Indices.Take(4).ToList();
        MarketLine = brief?.Overview is { Length: > 0 } o ? FirstSentence(o) : "";
        MarketsSubtitle = MarketSession.Short(q.Values, DateTimeOffset.Now);
        RaiseMany(nameof(Indices), nameof(FlyoutTiles), nameof(Movers), nameof(MarketLine), nameof(HasMarkets), nameof(MarketsSubtitle));
    }

    private static string FirstSentence(string s)
    {
        var i = s.IndexOf(". ", StringComparison.Ordinal);
        return i > 40 ? s[..(i + 1)] : s;
    }

    // ───────────── Pulse ─────────────
    public string PulseOverview { get; private set; } = "";
    public List<PulseTopicVM> PulseTopics { get; private set; } = new();
    public string PulseBadge { get; private set; } = "";
    public string PulseMeta { get; private set; } = "";
    public bool HasPulse => PulseTopics.Count > 0 || PulseOverview.Length > 0;

    private void UpdatePulse()
    {
        var p = Hub.State.Pulse;
        if (p is not null)
        {
            PulseOverview = p.Overview;
            PulseTopics = p.Topics.Take(4).Select(t => new PulseTopicVM(t.Title, t.Summary, t.Heat, t.Heat / 5.0, Fmt.SentimentBrush(t.Sentiment), t.Sentiment,
                string.Join(", ", t.Platforms.Select(Fmt.Platform)))).ToList();
            PulseBadge = p.IsAi ? "AI" : "";
            PulseMeta = $"{Plural.Of(p.PostCount, "post")} · {TimeText.AgoPhrase(p.GeneratedAt)}";
        }
        RaiseMany(nameof(PulseOverview), nameof(PulseTopics), nameof(PulseBadge), nameof(PulseMeta), nameof(HasPulse));
    }

    // ───────────── Upcoming & predictions ─────────────
    public List<EventVM> Events { get; private set; } = new();
    public List<PredictionVM> Predictions { get; private set; } = new();

    private void UpdateEvents()
    {
        Events = Hub.State.Events.Where(e => e.Start >= DateTimeOffset.Now.AddHours(-1)).Take(5).Select(e => new EventVM(e, Hub.S.General.Use24Hour)).ToList();
        Raise(nameof(Events));
    }

    private void UpdatePredictions()
    {
        Predictions = Hub.State.Predictions.Take(4).Select(m => new PredictionVM(m)).ToList();
        Raise(nameof(Predictions));
    }

    // ───────────── System ─────────────
    public double Cpu { get; private set; }
    public double Ram { get; private set; }
    public double Gpu { get; private set; }
    public string RamText { get; private set; } = "";
    public string CpuDetail { get; private set; } = "";
    public string GpuName { get; private set; } = "";
    public string VramText { get; private set; } = "";
    public double VramValue { get; private set; }
    public string DiskText { get; private set; } = "";
    public double DiskValue { get; private set; }
    public string NetText { get; private set; } = "";
    public string UptimeText { get; private set; } = "";
    public Brush CpuBrush { get; private set; } = Brushes.Gray;
    public Brush RamBrush { get; private set; } = Brushes.Gray;
    public Brush GpuBrush { get; private set; } = Brushes.Gray;

    private void UpdateSystem()
    {
        var s = Hub.State.System;
        if (s is null) return;
        Cpu = s.CpuPercent;
        Ram = s.RamPercent;
        Gpu = s.Gpu?.Utilization ?? 0;
        CpuBrush = Fmt.LoadBrush(Cpu);
        RamBrush = Fmt.LoadBrush(Ram);
        GpuBrush = Fmt.LoadBrush(Gpu);
        RamText = $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB";
        CpuDetail = $"{s.LogicalCores} threads";
        GpuName = ShortGpu(s.Gpu?.Name);
        VramText = s.Gpu is { VramTotalGb: > 0 } g ? $"{g.VramUsedGb:0.0} / {g.VramTotalGb:0} GB" : "—";
        VramValue = s.Gpu is { VramTotalGb: > 0 } g2 ? g2.VramUsedGb / g2.VramTotalGb : 0;
        var sys = s.Disks.FirstOrDefault(d => d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) ?? s.Disks.FirstOrDefault();
        DiskText = sys is null ? "—" : $"{sys.FreeGb:0} GB free of {sys.TotalGb:0}";
        DiskValue = sys is null ? 0 : sys.UsedPercent / 100;
        NetText = $"↓ {Fmt.Rate(s.NetDownBps)}   ↑ {Fmt.Rate(s.NetUpBps)}";
        UptimeText = "Up " + Fmt.Uptime(s.Uptime);
        RaiseMany(nameof(Cpu), nameof(Ram), nameof(Gpu), nameof(CpuBrush), nameof(RamBrush), nameof(GpuBrush), nameof(RamText), nameof(CpuDetail),
            nameof(GpuName), nameof(VramText), nameof(VramValue), nameof(DiskText), nameof(DiskValue), nameof(NetText), nameof(UptimeText));
    }

    public static string ShortGpu(string? name) => string.IsNullOrWhiteSpace(name) ? "GPU" :
        name.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("AMD ", "").Replace("(R)", "").Replace("(TM)", "").Trim();

    // ───────────── Media ─────────────
    public bool HasMedia { get; private set; }
    public string MediaTitle { get; private set; } = "";
    public string MediaArtist { get; private set; } = "";
    public string MediaApp { get; private set; } = "";
    public ImageSource? MediaArt { get; private set; }
    public bool HasArt => MediaArt is not null;
    public string PlayIcon { get; private set; } = "play";
    public string MusicAppName => Hub.S.Apps.FirstOrDefault(a => a.IsMusicPlayer)?.Name ?? Platform.DefaultApps.For("music")?.Name ?? "music";

    public int Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value) || _suppressVolume) return;
            _volumeDebounce?.Dispose();
            _volumeDebounce = new System.Threading.Timer(_ => Hub.Volume.SetVolume(value), null, 60, Timeout.Infinite);
            Raise(nameof(VolumeIcon));
        }
    }

    public string VolumeIcon => Hub.Volume.IsMuted() == true || _volume == 0 ? "mute" : "volume";

    private void UpdateMedia()
    {
        var m = Hub.Media.Current;
        HasMedia = m is not null;
        if (m is not null)
        {
            MediaTitle = m.Title;
            MediaArtist = string.IsNullOrWhiteSpace(m.Artist) ? m.Album : m.Artist;
            MediaApp = m.SourceApp;
            // Decoded once at a size that stays sharp on the Launchpad's large artwork as well.
            if (!ReferenceEquals(m.Thumbnail, _artBytes))
            {
                _artBytes = m.Thumbnail;
                MediaArt = m.Thumbnail is { Length: > 0 } bytes ? Img.Decode(bytes, 512) : null;
            }
            PlayIcon = m.IsPlaying ? "pause" : "play";
        }
        else
        {
            // The player closed: don't leave its last track, artwork or a pause button on screen.
            MediaTitle = "Nothing playing";
            MediaArtist = "";
            MediaApp = "";
            _artBytes = null;
            MediaArt = null;
            PlayIcon = "play";
        }
        RefreshVolume();
        RaiseMany(nameof(HasMedia), nameof(MediaTitle), nameof(MediaArtist), nameof(MediaApp), nameof(MediaArt), nameof(HasArt), nameof(PlayIcon), nameof(MusicAppName));
    }

    private byte[]? _artBytes;

    public void RefreshVolume()
    {
        _suppressVolume = true;
        Volume = Hub.Volume.GetVolume() ?? _volume;
        _suppressVolume = false;
        Raise(nameof(VolumeIcon));
    }

    // ───────────── Launchpad ─────────────
    public List<AppVM> PinnedApps { get; private set; } = new();
    public List<AppVM> FlyoutApps { get; private set; } = new();
    public List<SceneVM> Scenes { get; private set; } = new();

    public void UpdateLaunchpad()
    {
        PinnedApps = Hub.S.Apps.Where(a => a.Pinned).Take(8).Select(a => new AppVM(a)).ToList();
        foreach (var app in PinnedApps) app.IsRunning = Hub.Launcher.IsRunning(app.Entry);
        FlyoutApps = PinnedApps.Take(6).ToList();
        Scenes = Hub.S.Scenes.Take(4).Select(s => new SceneVM(s)).ToList();
        Raise(nameof(PinnedApps));
        Raise(nameof(FlyoutApps));
        Raise(nameof(Scenes));
    }

    public ICommand ReadBriefCommand { get; }
    public ICommand RegenerateBriefCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand OpenMusicCommand { get; }
    public ICommand MuteCommand { get; }
    public ICommand Go { get; }
}
