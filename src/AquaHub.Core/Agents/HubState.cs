using AquaHub.Core.Data;
using AquaHub.Core.Markets;
using AquaHub.Core.Models;
using AquaHub.Core.Util;

namespace AquaHub.Core.Agents;

public static class Topics
{
    public const string News = "news";
    public const string Social = "social";
    public const string Pulse = "pulse";
    public const string Markets = "markets";
    public const string MarketBrief = "marketBrief";
    public const string Predictions = "predictions";
    public const string Events = "events";
    public const string Foresight = "foresight";
    public const string Weather = "weather";
    public const string Brief = "brief";
    public const string Alerts = "alerts";
    public const string System = "system";
    public const string Ai = "ai";
    public const string Agents = "agents";
    /// <summary>Per-area freshness or connectivity changed.</summary>
    public const string Freshness = "freshness";
}

/// <summary>
/// The hub's blackboard: agents publish the latest state here and the UI subscribes to topic changes.
/// Snapshots are persisted so the app shows the last known state instantly after a restart.
/// Collections are replaced wholesale (never mutated in place) so readers on any thread are safe.
/// </summary>
public sealed class HubState
{
    private readonly HubDatabase? _db;

    public HubState(HubDatabase? db) => _db = db;

    public event Action<string>? Changed;

    public IReadOnlyList<StoryCluster> Stories { get; private set; } = Array.Empty<StoryCluster>();
    public IReadOnlyList<FeedItem> Social { get; private set; } = Array.Empty<FeedItem>();
    public SocialPulse? Pulse { get; private set; }
    public IReadOnlyDictionary<string, Quote> Quotes { get; private set; } = new Dictionary<string, Quote>();
    public IReadOnlyDictionary<string, Indicators> Indicators { get; private set; } = new Dictionary<string, Indicators>();
    /// <summary>Exchange rates for valuing holdings in your currency ("EURUSD" → dollars per euro).</summary>
    public FxRates Fx { get; private set; } = FxRates.None;
    public MarketBrief? MarketBrief { get; private set; }
    public IReadOnlyList<PredictionMarket> Predictions { get; private set; } = Array.Empty<PredictionMarket>();
    public IReadOnlyList<HubEvent> Events { get; private set; } = Array.Empty<HubEvent>();
    public Foresight? Foresight { get; private set; }
    public WeatherSnapshot? Weather { get; private set; }
    public DailyBrief? Brief { get; private set; }
    public IReadOnlyList<HubAlert> Alerts { get; private set; } = Array.Empty<HubAlert>();
    public SystemSnapshot? System { get; private set; }
    public Ai.LlmHealth? Ai { get; private set; }

    /// <summary>When each area (news, social, markets, predictions, events, weather) last fetched successfully.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Fresh { get; private set; } = new Dictionary<string, DateTimeOffset>();

    /// <summary>False while requests to several hosts keep failing at the network level.</summary>
    public bool Online { get; private set; } = true;

    public DateTimeOffset? FreshAt(string area) => Fresh.TryGetValue(area, out var t) ? t : null;

    /// <summary>Market headlines (business/markets category) kept separately for the analyst.</summary>
    public IReadOnlyList<FeedItem> MarketHeadlines { get; private set; } = Array.Empty<FeedItem>();

    public int UnreadAlerts => Alerts.Count(a => !a.Read);

    public void Load()
    {
        if (_db is null) return;
        try
        {
            Stories = _db.GetJson<List<StoryCluster>>("state:news") ?? new();
            Social = _db.GetJson<List<FeedItem>>("state:social") ?? new();
            Pulse = _db.GetJson<SocialPulse>("state:pulse");
            Quotes = _db.GetJson<Dictionary<string, Quote>>("state:quotes") ?? new();
            Indicators = _db.GetJson<Dictionary<string, Indicators>>("state:indicators") ?? new();
            Fx = new FxRates(_db.GetJson<Dictionary<string, double>>("state:fx") ?? new());
            MarketBrief = _db.GetJson<MarketBrief>("state:marketBrief");
            Predictions = _db.GetJson<List<PredictionMarket>>("state:predictions") ?? new();
            Events = _db.GetJson<List<HubEvent>>("state:events") ?? new();
            Foresight = _db.GetJson<Foresight>("state:foresight");
            Weather = _db.GetJson<WeatherSnapshot>("state:weather");
            Brief = _db.GetJson<DailyBrief>("state:brief");
            MarketHeadlines = _db.GetJson<List<FeedItem>>("state:marketHeadlines") ?? new();
            Alerts = _db.GetAlerts();
            Fresh = _db.GetJson<Dictionary<string, DateTimeOffset>>("state:fresh") ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn("state", "Could not restore snapshot", ex);
        }
    }

    private void Save<T>(string key, T value)
    {
        if (_db is null) return;
        try { _db.PutJson("state:" + key, value); }
        catch (Exception ex) { Log.Warn("state", $"Persist {key} failed", ex); }
    }

    private void Raise(string topic)
    {
        try { Changed?.Invoke(topic); }
        catch (Exception ex) { Log.Error("state", $"Subscriber failed for {topic}", ex); }
    }

    public void SetStories(List<StoryCluster> stories) { Stories = stories; Save("news", stories); Raise(Topics.News); }
    public void SetMarketHeadlines(List<FeedItem> items) { MarketHeadlines = items; Save("marketHeadlines", items); }
    public void SetSocial(List<FeedItem> items) { Social = items; Save("social", items); Raise(Topics.Social); }
    public void SetPulse(SocialPulse pulse) { Pulse = pulse; Save("pulse", pulse); Raise(Topics.Pulse); }

    public void SetQuotes(Dictionary<string, Quote> quotes)
    {
        Quotes = quotes;
        Save("quotes", quotes);
        Raise(Topics.Markets);
    }

    public void SetIndicators(Dictionary<string, Indicators> indicators) { Indicators = indicators; Save("indicators", indicators); Raise(Topics.Markets); }
    public void SetFx(Dictionary<string, double> rates) { Fx = new FxRates(rates); Save("fx", rates); }
    public void SetMarketBrief(MarketBrief brief) { MarketBrief = brief; Save("marketBrief", brief); Raise(Topics.MarketBrief); }
    public void SetPredictions(List<PredictionMarket> markets) { Predictions = markets; Save("predictions", markets); Raise(Topics.Predictions); }
    public void SetEvents(List<HubEvent> events) { Events = events; Save("events", events); Raise(Topics.Events); }
    public void SetForesight(Foresight f) { Foresight = f; Save("foresight", f); Raise(Topics.Foresight); }
    public void SetWeather(WeatherSnapshot w) { Weather = w; Save("weather", w); Raise(Topics.Weather); }
    public void SetBrief(DailyBrief b) { Brief = b; Save("brief", b); Raise(Topics.Brief); }
    public void SetSystem(SystemSnapshot s) { System = s; Raise(Topics.System); }
    public void SetAi(Ai.LlmHealth h) { Ai = h; Raise(Topics.Ai); }

    public void ReloadAlerts()
    {
        if (_db is null) return;
        Alerts = _db.GetAlerts();
        Raise(Topics.Alerts);
    }

    public void MarkAlertsRead()
    {
        _db?.MarkAlertsRead();
        ReloadAlerts();
    }

    /// <summary>Opening an alert (from the bell, a banner or a Windows notification) marks it read.</summary>
    public void MarkAlertRead(string id)
    {
        if (_db is null || !_db.MarkAlertRead(id)) return;
        ReloadAlerts();
    }

    public void ClearAlerts()
    {
        _db?.ClearAlerts();
        ReloadAlerts();
    }

    /// <summary>Removes the alerts that no longer hold (say, an update that has since been installed).</summary>
    public void RemoveAlerts(Func<HubAlert, bool> which)
    {
        if (_db is null) return;
        var ids = _db.GetAlerts(500).Where(which).Select(a => a.Id).ToList();
        if (ids.Count > 0 && _db.DeleteAlerts(ids) > 0) ReloadAlerts();
    }

    /// <summary>Updates one story's summary without re-publishing everything else.</summary>
    public void UpdateStorySummary(string clusterId, StorySummary summary)
    {
        var list = Stories;
        foreach (var c in list)
            if (c.Id == clusterId) c.Summary = summary;
        Save("news", list);
        Raise(Topics.News);
    }

    /// <summary>Applies a batch of story summaries and publishes once (not once per story).</summary>
    public void UpdateStorySummaries(IReadOnlyDictionary<string, StorySummary> summaries)
    {
        if (summaries.Count == 0) return;
        var list = Stories;
        foreach (var c in list)
            if (summaries.TryGetValue(c.Id, out var summary)) c.Summary = summary;
        Save("news", list);
        Raise(Topics.News);
    }

    /// <summary>Records a successful fetch for an area (drives "updated 5m ago" and stale markers).</summary>
    public void MarkFresh(string area)
    {
        var next = new Dictionary<string, DateTimeOffset>(Fresh) { [area] = DateTimeOffset.Now };
        Fresh = next;
        Save("fresh", next);
        Raise(Topics.Freshness);
    }

    public void SetOnline(bool online)
    {
        if (Online == online) return;
        Online = online;
        Raise(Topics.Freshness);
    }

    public void NotifyAgents() => Raise(Topics.Agents);
}
