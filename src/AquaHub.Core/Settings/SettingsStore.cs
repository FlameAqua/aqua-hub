using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AquaHub.Core.Util;

namespace AquaHub.Core.Settings;

/// <summary>Secrets are kept out of the JSON config (implemented with Windows Credential Manager by the app).</summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string? value);
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) _values.Remove(key); else _values[key] = value;
    }
}

public static class SecretKeys
{
    public const string FinnhubApiKey = "finnhub.apiKey";
    public const string BlueskyAppPassword = "bluesky.appPassword";
    public const string MastodonToken = "mastodon.accessToken";
    public const string OpenAiCompatibleKey = "ai.apiKey";
    public const string BraveSearchKey = "ask.braveSearchKey";
}

/// <summary>
/// Copy-on-write settings store: readers always see an immutable-in-practice snapshot, writers
/// produce a new validated object and swap it atomically, then persist it with an atomic file replace.
/// </summary>
public sealed partial class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private HubSettings _current;

    public event Action<HubSettings>? Changed;

    public SettingsStore(string path)
    {
        _path = path;
        _current = LoadOrDefault(path, out var isNew);
        IsFirstRun = isNew || !_current.OnboardingComplete;
        var migrated = !isNew && Migrate(_current);
        if (isNew || migrated) Persist(_current);
    }

    public const int CurrentVersion = 3;

    /// <summary>
    /// One-time additions for settings saved by older versions. Each step runs once (the version is saved), so anything
    /// you remove afterwards stays removed.
    /// </summary>
    internal static bool Migrate(HubSettings s)
    {
        if (s.Version >= CurrentVersion) return false;
        if (s.Version < 2)
        {
            // v2: Gamers Nexus — its site's news in News and its videos in Social.
            if (!s.News.Sources.Any(x => x.Id == "gamersnexus" || x.Url.Contains("gamersnexus.net", StringComparison.OrdinalIgnoreCase)))
                s.News.Sources.Add(NewsSettings.DefaultSources().First(x => x.Id == "gamersnexus"));
            var gn = SocialSettings.GamersNexusChannel();
            if (!s.Social.YouTubeChannels.Any(c => c.ChannelId == gn.ChannelId)) s.Social.YouTubeChannels.Add(gn);
        }
        if (s.Version < 3)
        {
            // v3: Pictures joins the folders Ask may search — only for people who kept the original three.
            var old = new[] { "%DOCUMENTS%", "%DESKTOP%", "%DOWNLOADS%" };
            if (s.Ask.Folders.Count == old.Length && old.All(f => s.Ask.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)))
                s.Ask.Folders.Add("%PICTURES%");
        }
        s.Version = CurrentVersion;
        Log.Info("settings", $"Settings updated to version {CurrentVersion}");
        return true;
    }

    public string FilePath => _path;
    public bool IsFirstRun { get; private set; }
    public HubSettings Current => Volatile.Read(ref _current);

    /// <summary>Returns a deep copy that callers may freely edit (e.g. a settings form).</summary>
    public HubSettings Clone() => Copy(Current);

    public void Update(Action<HubSettings> mutate)
    {
        lock (_gate)
        {
            var next = Copy(_current);
            mutate(next);
            Validate(next);
            Volatile.Write(ref _current, next);
            Persist(next);
        }
        Raise();
    }

    public void Replace(HubSettings settings)
    {
        lock (_gate)
        {
            var next = Copy(settings);
            Validate(next);
            Volatile.Write(ref _current, next);
            Persist(next);
        }
        Raise();
    }

    /// <summary>
    /// Three-way merge for forms that edit a copy: applies only what the form changed (<paramref name="original"/> →
    /// <paramref name="edited"/>) on top of the current settings, so changes made meanwhile elsewhere (do-not-disturb
    /// from the tray, hotkey fallbacks, first-run app discovery) are kept. Lists are merged as whole values.
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public bool Merge(HubSettings original, HubSettings edited)
    {
        var before = JsonSerializer.SerializeToNode(original, JsonUtil.Options);
        var after = JsonSerializer.SerializeToNode(edited, JsonUtil.Options);
        lock (_gate)
        {
            var target = JsonSerializer.SerializeToNode(_current, JsonUtil.Options)!;
            if (!ApplyChanges(before, after, target)) return false;
            var next = target.Deserialize<HubSettings>(JsonUtil.Options)!;
            Validate(next);
            Volatile.Write(ref _current, next);
            Persist(next);
        }
        Raise();
        return true;
    }

    internal static bool ApplyChanges(JsonNode? before, JsonNode? after, JsonNode target)
    {
        if (after is not JsonObject afterObj || target is not JsonObject targetObj) return false;
        var changed = false;
        foreach (var (key, value) in afterObj.ToList())
        {
            var old = (before as JsonObject)?[key];
            if (value is JsonObject && old is JsonObject && targetObj[key] is JsonObject child)
                changed |= ApplyChanges(old, value, child);
            else if (!JsonNode.DeepEquals(old, value))
            {
                targetObj[key] = value?.DeepClone();
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Deep copy through JSON (settings are plain data).</summary>
    public static HubSettings DeepCopy(HubSettings s) => Copy(s);

    /// <summary>Re-read the file (e.g. after the user edited settings.json by hand).</summary>
    public bool Reload(out string? error)
    {
        error = null;
        try
        {
            var text = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<HubSettings>(text, JsonUtil.Options) ?? new HubSettings();
            Validate(parsed);
            Volatile.Write(ref _current, parsed);
            Raise();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn("settings", "Reload failed", ex);
            return false;
        }
    }

    private void Raise()
    {
        try { Changed?.Invoke(Current); }
        catch (Exception ex) { Log.Error("settings", "Change handler failed", ex); }
    }

    private static HubSettings Copy(HubSettings s) =>
        JsonSerializer.Deserialize<HubSettings>(JsonSerializer.Serialize(s, JsonUtil.Options), JsonUtil.Options)!;

    private static HubSettings LoadOrDefault(string path, out bool isNew)
    {
        isNew = false;
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<HubSettings>(File.ReadAllText(path), JsonUtil.Options);
                if (parsed is not null)
                {
                    Validate(parsed);
                    return parsed;
                }
            }
        }
        catch (Exception ex)
        {
            // Keep the broken file for the user and start from defaults.
            Log.Error("settings", "Settings file unreadable; using defaults", ex);
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
        }
        isNew = true;
        var fresh = new HubSettings();
        fresh.General.UserName = Environment.UserName;
        Validate(fresh);
        return fresh;
    }

    private void Persist(HubSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonUtil.Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings", "Failed to save settings", ex);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9\^\.\-=_]{1,24}$")]
    private static partial Regex SymbolPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_]{2,40}$")]
    private static partial Regex SubredditPattern();

    [GeneratedRegex(@"^(?:[01]?\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimePattern();

    /// <summary>Clamp numbers, drop invalid entries, and enforce URL safety rules.</summary>
    public static void Validate(HubSettings s)
    {
        s.General ??= new(); s.Location ??= new(); s.News ??= new(); s.Social ??= new();
        s.Markets ??= new(); s.Events ??= new(); s.Predictions ??= new(); s.Ai ??= new(); s.Ask ??= new();
        s.Apps ??= new(); s.Scenes ??= new(); s.Notifications ??= new(); s.Privacy ??= new();

        s.News.RefreshMinutes = Math.Clamp(s.News.RefreshMinutes, 3, 240);
        s.News.MaxAgeHours = Math.Clamp(s.News.MaxAgeHours, 6, 168);
        s.News.SummarizeTop = Math.Clamp(s.News.SummarizeTop, 0, 40);
        s.News.Sources = (s.News.Sources ?? new()).Where(src =>
            !string.IsNullOrWhiteSpace(src.Id) &&
            (src.Kind == "google" || IsSafeHttpUrl(src.Url))).ToList();
        foreach (var src in s.News.Sources) src.Tier = Math.Clamp(src.Tier, 1, 3);

        s.Social.RefreshMinutes = Math.Clamp(s.Social.RefreshMinutes, 5, 240);
        s.Social.HackerNewsCount = Math.Clamp(s.Social.HackerNewsCount, 5, 50);
        s.Social.Subreddits = Clean(s.Social.Subreddits).Select(x => x.TrimStart('/').Replace("r/", "", StringComparison.OrdinalIgnoreCase))
            .Where(x => SubredditPattern().IsMatch(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.Social.MastodonHashtags = Clean(s.Social.MastodonHashtags).Select(x => x.TrimStart('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.Social.MastodonInstance = (s.Social.MastodonInstance ?? "").Trim().Replace("https://", "").TrimEnd('/');
        s.Social.BlueskyAccounts = Clean(s.Social.BlueskyAccounts).Select(x => x.TrimStart('@')).ToList();
        s.Social.BlueskyHandle = (s.Social.BlueskyHandle ?? "").Trim().TrimStart('@');
        s.Social.BlueskyFeeds = Clean(s.Social.BlueskyFeeds).Where(x => x.StartsWith("at://", StringComparison.Ordinal)).ToList();
        s.Social.ExtraFeeds = (s.Social.ExtraFeeds ?? new()).Where(f => IsSafeHttpUrl(f.Url)).ToList();
        s.Social.YouTubeChannels = (s.Social.YouTubeChannels ?? new()).Where(c => !string.IsNullOrWhiteSpace(c.ChannelId)).ToList();

        s.Markets.RefreshSeconds = Math.Clamp(s.Markets.RefreshSeconds, 30, 3600);
        s.Markets.AlertMovePercent = Math.Clamp(s.Markets.AlertMovePercent, 0.5, 50);
        s.Markets.Indices = CleanSymbols(s.Markets.Indices);
        s.Markets.Macro = CleanSymbols(s.Markets.Macro);
        s.Markets.Watchlist = CleanSymbols(s.Markets.Watchlist);

        s.Events.LookaheadDays = Math.Clamp(s.Events.LookaheadDays, 1, 60);
        s.Events.ReminderMinutes = Math.Clamp(s.Events.ReminderMinutes, 0, 240);
        s.Events.Calendars = (s.Events.Calendars ?? new()).Where(c =>
            IsSafeHttpUrl(c.Url) || c.Url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ||
            (Path.IsPathFullyQualified(c.Url) && c.Url.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))).ToList();
        s.Events.HolidayCountries = Clean(s.Events.HolidayCountries).Select(c => c.ToUpperInvariant()).Where(c => c.Length == 2).Distinct().ToList();

        s.Predictions.MaxItems = Math.Clamp(s.Predictions.MaxItems, 4, 80);
        s.Predictions.RefreshMinutes = Math.Clamp(s.Predictions.RefreshMinutes, 5, 240);
        s.Predictions.SwingAlertPoints = Math.Clamp(s.Predictions.SwingAlertPoints, 2, 50);

        s.Ai.Temperature = Math.Clamp(s.Ai.Temperature, 0, 1.5);
        s.Ai.ContextTokens = Math.Clamp(s.Ai.ContextTokens, 2048, 131072);
        s.Ai.StopOllamaIdleMinutes = Math.Clamp(s.Ai.StopOllamaIdleMinutes, 0, 1440);
        s.Ai.OllamaPath = (s.Ai.OllamaPath ?? "").Trim().Trim('"');
        s.Ai.Endpoint = string.IsNullOrWhiteSpace(s.Ai.Endpoint) ? "http://127.0.0.1:11434" : s.Ai.Endpoint.Trim().TrimEnd('/');
        s.Ai.BriefTimes = Clean(s.Ai.BriefTimes).Where(t => TimePattern().IsMatch(t)).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(s.Ai.Model)) s.Ai.Model = "auto";

        s.Ask.ResearchPages = Math.Clamp(s.Ask.ResearchPages, 3, 12);
        s.Ask.MaxPdfPages = Math.Clamp(s.Ask.MaxPdfPages, 1, 60);
        s.Ask.SearchEngine = s.Ask.SearchEngine is "duckduckgo" or "searxng" or "brave" ? s.Ask.SearchEngine : "duckduckgo";
        s.Ask.SearxngUrl = (s.Ask.SearxngUrl ?? "").Trim().TrimEnd('/');
        if (s.Ask.SearxngUrl.Length > 0 && !IsSafeHttpUrl(s.Ask.SearxngUrl)) s.Ask.SearxngUrl = "";
        s.Ask.Folders = Clean(s.Ask.Folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.Ask.ChatRetentionDays = Math.Clamp(s.Ask.ChatRetentionDays, 0, 3650);
        s.Ask.ContextWindow = s.Ask.ContextWindow is 0 or 8192 or 16384 or 32768 or 65536 or 131072 ? s.Ask.ContextWindow : 0;
        s.Ask.HistoryMessages = Math.Clamp(s.Ask.HistoryMessages, 2, 40);
        s.Ask.Voice = s.Ask.Voice is "offline" or "online" or "whisper" or "off" ? s.Ask.Voice : "offline";
        s.Ask.WhisperModel = Speech.WhisperCatalog.Choice(s.Ask.WhisperModel).Id;
        s.Ask.Microphone = (s.Ask.Microphone ?? "").Trim() is { Length: <= 200 } mic ? mic : "";
        s.Ask.OperateApps = s.Ask.OperateApps is "any" or "listed" ? s.Ask.OperateApps : "any";
        s.Ask.AllowedApps = Clean(s.Ask.AllowedApps ?? new())
            .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();

        if (!TimePattern().IsMatch(s.Notifications.QuietStart ?? "")) s.Notifications.QuietStart = "23:00";
        if (!TimePattern().IsMatch(s.Notifications.QuietEnd ?? "")) s.Notifications.QuietEnd = "07:00";
        s.Notifications.Keywords = Clean(s.Notifications.Keywords);
        s.Privacy.RetentionDays = Math.Clamp(s.Privacy.RetentionDays, 1, 90);

        s.Apps = s.Apps.Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Target)).ToList();
        s.Scenes = s.Scenes.Where(sc => !string.IsNullOrWhiteSpace(sc.Id)).ToList();
        foreach (var sc in s.Scenes) sc.Steps ??= new();
    }

    private static List<string> Clean(List<string>? values) =>
        (values ?? new()).Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).ToList();

    private static List<WatchSymbol> CleanSymbols(List<WatchSymbol>? list) =>
        (list ?? new()).Where(w => w is not null && SymbolPattern().IsMatch(w.Symbol ?? ""))
            .GroupBy(w => w.Symbol.ToUpperInvariant()).Select(g => g.First()).ToList();

    public static bool IsSafeHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) &&
        string.IsNullOrEmpty(u.UserInfo);

    public static bool IsLoopbackEndpoint(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host is "localhost");
}
