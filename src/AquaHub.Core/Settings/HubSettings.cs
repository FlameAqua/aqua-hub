using System.Text.Json.Serialization;
namespace AquaHub.Core.Settings;

/// <summary>
/// The complete user configuration. Persisted as JSON (comments allowed) in the profile directory.
/// Secrets (API keys, app passwords) are NEVER stored here — they live in Windows Credential Manager.
/// </summary>
public sealed class HubSettings
{
    /// <summary>Settings format; older files get one-time additions on load (see SettingsStore.Migrate).</summary>
    public int Version { get; set; } = SettingsStore.CurrentVersion;
    public bool OnboardingComplete { get; set; }
    public GeneralSettings General { get; set; } = new();
    public LocationSettings Location { get; set; } = new();
    public NewsSettings News { get; set; } = new();
    public SocialSettings Social { get; set; } = new();
    public MarketSettings Markets { get; set; } = new();
    public EventSettings Events { get; set; } = new();
    public PredictionSettings Predictions { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public AskSettings Ask { get; set; } = new();
    public List<AppEntry> Apps { get; set; } = new();
    public List<Scene> Scenes { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
}

public sealed class GeneralSettings
{
    public string UserName { get; set; } = "";
    public bool LaunchAtStartup { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>system | dark | light</summary>
    public string Theme { get; set; } = "system";
    /// <summary>aqua | system | #RRGGBB</summary>
    public string Accent { get; set; } = "aqua";
    public string HotkeyFlyout { get; set; } = "Ctrl+Alt+H";
    public string HotkeyPalette { get; set; } = "Ctrl+Alt+Space";
    public bool Use24Hour { get; set; } = true;
    /// <summary>Settings › Debug: the diagnostics log also records routine detail (debug level), for chasing a problem.</summary>
    public bool VerboseLog { get; set; }
    /// <summary>Reduce background refresh rates while the PC is idle or on battery.</summary>
    public bool EcoMode { get; set; } = true;
    public bool ShowTaskbarTicker { get; set; }
    public bool ReduceMotion { get; set; }
    /// <summary>Settings › About: installed copies look for a new version on the project's GitHub releases about once a
    /// day. They only say so; downloading is up to you.</summary>
    public bool CheckForUpdates { get; set; } = true;
}

/// <summary>
/// Where you are: nothing until you pick a place (during onboarding or in Settings › Location), so a new profile
/// isn't anyone else's city. Until then there's no weather or local news (<see cref="IsSet"/>). Choosing a place
/// applies that country's defaults (<see cref="LocalePacks"/>).
/// </summary>
public sealed class LocationSettings
{
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2; empty until a place is chosen.</summary>
    public string Country { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>IANA time zone of the place; empty means Windows' own.</summary>
    public string Timezone { get; set; } = "";
    /// <summary>metric | imperial</summary>
    public string Units { get; set; } = "metric";
    /// <summary>BCP-47 language used for summaries and regional feeds (the place's English edition once chosen).</summary>
    public string Language { get; set; } = "en-US";
    /// <summary>Words that mark a story as local.</summary>
    public List<string> LocalKeywords { get; set; } = new();

    /// <summary>A place has been chosen, so there's local news and a local section in the brief.</summary>
    [JsonIgnore]
    public bool IsSet => City.Trim().Length > 0;

    /// <summary>The place has coordinates, so there's weather (a place from the picker always has them).</summary>
    [JsonIgnore]
    public bool HasCoordinates => IsSet && (Latitude != 0 || Longitude != 0);

    /// <summary>"Galway, Ireland"; empty until a place is chosen.</summary>
    [JsonIgnore]
    public string Label => !IsSet ? "" : Region.Length > 0 && !Region.Equals(City, StringComparison.OrdinalIgnoreCase) ? $"{City}, {Region}" : City;

    /// <summary>What the local section is called: the city, or "Local" before a place is chosen.</summary>
    [JsonIgnore]
    public string LocalTitle => IsSet ? City : "Local";
}

public sealed class NewsSource
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => Name;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>rss | google</summary>
    public string Kind { get; set; } = "rss";
    public string Url { get; set; } = "";
    /// <summary>For Kind = google: a Google News search query, e.g. "site:reuters.com when:1d".</summary>
    public string Query { get; set; } = "";
    public string Category { get; set; } = "world";
    public int Tier { get; set; } = 2;
    public bool Local { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class NewsSettings
{
    public int RefreshMinutes { get; set; } = 10;
    public int MaxAgeHours { get; set; } = 36;
    public int SummarizeTop { get; set; } = 12;
    public List<string> Interests { get; set; } = new() { "AI", "technology", "economy", "housing", "climate", "energy" };
    public List<string> Muted { get; set; } = new() { "horoscope", "celebrity gossip" };
    public List<NewsSource> Sources { get; set; } = DefaultSources();

    public static List<NewsSource> DefaultSources() => new()
    {
        // Wire services & public broadcasters (tier 1)
        new() { Id = "reuters", Name = "Reuters", Kind = "google", Query = "site:reuters.com when:1d", Category = "world", Tier = 1 },
        new() { Id = "ap", Name = "AP News", Kind = "google", Query = "site:apnews.com when:1d", Category = "world", Tier = 1 },
        new() { Id = "bbc-world", Name = "BBC News", Url = "https://feeds.bbci.co.uk/news/world/rss.xml", Category = "world", Tier = 1 },
        new() { Id = "bbc-business", Name = "BBC Business", Url = "https://feeds.bbci.co.uk/news/business/rss.xml", Category = "business", Tier = 1 },
        new() { Id = "ft", Name = "Financial Times", Url = "https://www.ft.com/rss/home", Category = "business", Tier = 1 },
        // Local: your place in Google News (once a place is chosen; that country's own outlets come with its pack)
        new() { Id = "gnews-local", Name = "Local (Google News)", Kind = "google", Query = "{city} {country} when:1d", Category = "local", Tier = 3, Local = true },
        // Reputable international outlets (tier 2)
        new() { Id = "guardian-world", Name = "The Guardian", Url = "https://www.theguardian.com/world/rss", Category = "world", Tier = 2 },
        new() { Id = "aljazeera", Name = "Al Jazeera", Url = "https://www.aljazeera.com/xml/rss/all.xml", Category = "world", Tier = 2 },
        new() { Id = "dw", Name = "DW", Url = "https://rss.dw.com/rdf/rss-en-all", Category = "world", Tier = 2 },
        new() { Id = "euronews", Name = "Euronews", Url = "https://www.euronews.com/rss?level=theme&name=news", Category = "europe", Tier = 2 },
        new() { Id = "politico-eu", Name = "Politico Europe", Url = "https://www.politico.eu/feed/", Category = "europe", Tier = 2 },
        new() { Id = "cnbc", Name = "CNBC", Url = "https://www.cnbc.com/id/100003114/device/rss/rss.html", Category = "business", Tier = 2 },
        // Technology
        new() { Id = "ars", Name = "Ars Technica", Url = "https://feeds.arstechnica.com/arstechnica/index", Category = "tech", Tier = 2 },
        new() { Id = "verge", Name = "The Verge", Url = "https://www.theverge.com/rss/index.xml", Category = "tech", Tier = 2 },
        new() { Id = "gamersnexus", Name = "Gamers Nexus", Url = "https://gamersnexus.net/rss.xml", Category = "tech", Tier = 2 },
        // Gaming & internet culture
        new() { Id = "kotaku", Name = "Kotaku", Url = "https://kotaku.com/feed", Category = "gaming", Tier = 2 },
        new() { Id = "eurogamer", Name = "Eurogamer", Url = "https://www.eurogamer.net/feed", Category = "gaming", Tier = 2 },
        new() { Id = "vgc", Name = "Video Games Chronicle", Url = "https://www.videogameschronicle.com/feed/", Category = "gaming", Tier = 2 },
        new() { Id = "dexerto", Name = "Dexerto", Url = "https://www.dexerto.com/feed/", Category = "gaming", Tier = 3 },
        new() { Id = "insider-gaming", Name = "Insider Gaming", Url = "https://insider-gaming.com/feed/", Category = "gaming", Tier = 3 },
        new() { Id = "dailydot", Name = "The Daily Dot", Url = "https://dailydot.com/feed", Category = "gaming", Tier = 3 },
        new() { Id = "gnews-top", Name = "Top Stories (Google News)", Kind = "google", Query = "", Category = "world", Tier = 3, Enabled = false },
    };
}

public sealed class ChannelRef
{
    public override string ToString() => Name.Length > 0 ? Name : Handle;

    public string Name { get; set; } = "";
    public string ChannelId { get; set; } = "";
    /// <summary>The channel's @handle, when known.</summary>
    public string Handle { get; set; } = "";
    /// <summary>The channel's avatar (fetched when the channel is added).</summary>
    public string AvatarUrl { get; set; } = "";
}

public sealed class SocialSettings
{
    public int RefreshMinutes { get; set; } = 15;
    /// <summary>Your place's communities once it's chosen (e.g. r/ireland and r/Galway).</summary>
    public List<string> Subreddits { get; set; } = new();
    public string MastodonInstance { get; set; } = "mastodon.social";
    public List<string> MastodonHashtags { get; set; } = new();
    public bool BlueskyTrending { get; set; } = true;
    /// <summary>Your handle for reading your own Following timeline (the app password lives in Credential Manager).</summary>
    public string BlueskyHandle { get; set; } = "";
    public bool BlueskyTimeline { get; set; } = true;
    /// <summary>Read your Mastodon home timeline when an access token is saved (Credential Manager).</summary>
    public bool MastodonHome { get; set; } = true;
    /// <summary>Bluesky handles whose posts are followed, e.g. "someone.bsky.social".</summary>
    public List<string> BlueskyAccounts { get; set; } = new();
    /// <summary>Custom feed AT-URIs, e.g. at://did:plc:…/app.bsky.feed.generator/…</summary>
    public List<string> BlueskyFeeds { get; set; } = new();
    public bool HackerNews { get; set; } = true;
    public int HackerNewsCount { get; set; } = 15;
    public List<ChannelRef> YouTubeChannels { get; set; } = new() { GamersNexusChannel() };

    public static ChannelRef GamersNexusChannel() => new()
    {
        Name = "Gamers Nexus", ChannelId = "UChIs72whgZI9w6d6FhwGGHA", Handle = "@GamersNexus",
        AvatarUrl = "https://yt3.googleusercontent.com/ytc/AIdro_n50Rj8oqwUnusv4I6_DllKflZrSdSxmXhYvpHs0hOEj5o=s176-c-k-c0x00ffffff-no-rj",
    };
    /// <summary>Any extra RSS/Atom feeds treated as social (e.g. a self-hosted RSSHub bridge).</summary>
    public List<NewsSource> ExtraFeeds { get; set; } = new();
}

public sealed class WatchSymbol
{
    public override string ToString() => Name.Length > 0 ? $"{Symbol}, {Name}" : Symbol;

    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>index | equity | etf | fx | crypto | commodity</summary>
    public string Kind { get; set; } = "equity";
    /// <summary>How many you own (fractions allowed). Set, it makes this a holding.</summary>
    public double? Shares { get; set; }
    /// <summary>Average cost of one, in <see cref="CostCurrency"/>.</summary>
    public double? CostBasis { get; set; }
    /// <summary>The currency the cost was paid in (e.g. "EUR"); empty when it's the one the price is in.</summary>
    public string CostCurrency { get; set; } = "";
    public double? AlertAbove { get; set; }
    public double? AlertBelow { get; set; }
}

public sealed class MarketSettings
{
    public int RefreshSeconds { get; set; } = 120;
    public double AlertMovePercent { get; set; } = 3.0;
    /// <summary>For holdings values; set to your country's currency when you choose a place.</summary>
    public string BaseCurrency { get; set; } = "USD";
    /// <summary>conservative | balanced | growth | aggressive — shapes AI idea generation.</summary>
    public string RiskProfile { get; set; } = "balanced";
    public string Horizon { get; set; } = "long-term";
    public List<WatchSymbol> Indices { get; set; } = new()
    {
        new() { Symbol = "^GSPC", Name = "S&P 500", Kind = "index" },
        new() { Symbol = "^IXIC", Name = "Nasdaq", Kind = "index" },
        new() { Symbol = "^STOXX50E", Name = "Euro Stoxx 50", Kind = "index" },
        new() { Symbol = "^FTSE", Name = "FTSE 100", Kind = "index" },
    };
    public List<WatchSymbol> Macro { get; set; } = new()
    {
        new() { Symbol = "EURUSD=X", Name = "EUR/USD", Kind = "fx" },
        new() { Symbol = "BTC-USD", Name = "Bitcoin", Kind = "crypto" },
        new() { Symbol = "GC=F", Name = "Gold", Kind = "commodity" },
        new() { Symbol = "BZ=F", Name = "Brent", Kind = "commodity" },
    };
    public List<WatchSymbol> Watchlist { get; set; } = new()
    {
        new() { Symbol = "NVDA", Name = "NVIDIA", Kind = "equity" },
        new() { Symbol = "MSFT", Name = "Microsoft", Kind = "equity" },
        new() { Symbol = "AAPL", Name = "Apple", Kind = "equity" },
        new() { Symbol = "IWDA.AS", Name = "iShares MSCI World", Kind = "etf" },
    };
}

public sealed class CalendarSource
{
    public string Name { get; set; } = "";
    /// <summary>https:// ICS URL (e.g. Google/Outlook "secret address") or a local .ics file path.</summary>
    public string Url { get; set; } = "";
    public string Color { get; set; } = "#4CC2FF";
    public bool Enabled { get; set; } = true;
}

public sealed class EventSettings
{
    public List<CalendarSource> Calendars { get; set; } = new();
    /// <summary>Your country's public holidays once a place is chosen.</summary>
    public List<string> HolidayCountries { get; set; } = new();
    public bool Economic { get; set; } = true;
    public List<string> EconomicCurrencies { get; set; } = new() { "USD", "EUR", "GBP" };
    /// <summary>Low | Medium | High</summary>
    public string EconomicMinImpact { get; set; } = "High";
    public int LookaheadDays { get; set; } = 14;
    /// <summary>Earnings dates via Finnhub (free API key stored in Credential Manager).</summary>
    public bool Earnings { get; set; } = true;
    public int ReminderMinutes { get; set; } = 10;
}

public sealed class PredictionSettings
{
    public bool Polymarket { get; set; } = true;
    public List<string> PolymarketTags { get; set; } = new() { "economy", "politics", "geopolitics", "tech" };
    public bool Kalshi { get; set; }
    public List<string> KalshiCategories { get; set; } = new() { "Economics", "Politics", "Financials", "Science and Technology", "World" };
    public double MinVolume24h { get; set; } = 5000;
    public int MaxItems { get; set; } = 24;
    public int RefreshMinutes { get; set; } = 20;
    public double SwingAlertPoints { get; set; } = 10;
}

public sealed class AiSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>ollama | openai (any OpenAI-compatible local server: LM Studio, llama.cpp, vLLM)</summary>
    public string Provider { get; set; } = "ollama";
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    /// <summary>"auto" picks the best installed model from <see cref="PreferredModels"/>.</summary>
    public string Model { get; set; } = "auto";
    /// <summary>Optional larger model for the daily brief and market analysis ("" = same as Model).</summary>
    public string DeepModel { get; set; } = "";
    public List<string> PreferredModels { get; set; } = new()
    {
        "qwen3.5:9b", "qwen3.5:4b", "qwen3:8b", "qwen3:14b", "gemma3:12b", "qwen3:4b", "gemma3:4b",
        "llama3.1:8b", "mistral-small", "phi4-mini",
    };
    public double Temperature { get; set; } = 0.3;
    public int ContextTokens { get; set; } = 8192;
    public string KeepAlive { get; set; } = "10m";
    /// <summary>Allow "thinking" for reasoning models. Off = faster, which suits summarisation.</summary>
    public bool Think { get; set; }
    public bool PauseWhenFullscreen { get; set; } = true;
    public List<string> BriefTimes { get; set; } = new() { "07:30", "18:00" };
    /// <summary>Refuse non-loopback endpoints unless explicitly allowed (keeps data on this PC).</summary>
    public bool AllowRemoteEndpoint { get; set; }
    /// <summary>Let Aqua start and stop the local Ollama server (it never touches a remote endpoint).</summary>
    public bool ManageOllama { get; set; } = true;
    /// <summary>Start Ollama when you ask something, regenerate the brief, or a scheduled brief is due.</summary>
    public bool StartOllamaOnDemand { get; set; } = true;
    /// <summary>Stop Ollama while a game or another app keeps the GPU busy, and start it again afterwards.</summary>
    public bool StopOllamaForGpu { get; set; } = true;
    /// <summary>Stop Ollama after this many minutes with no model loaded and no requests (0 = never). It holds RAM even when idle.</summary>
    public int StopOllamaIdleMinutes { get; set; } = 15;
    /// <summary>Full path to ollama.exe; empty = the standard install location or PATH.</summary>
    public string OllamaPath { get; set; } = "";
}

/// <summary>What Ask Aqua may use beyond what your agents collected: the web, your files, your screen and your PC.</summary>
public sealed class AskSettings
{
    /// <summary>Ask may search the web and read public pages (the Web switch in Ask starts on).</summary>
    public bool Web { get; set; } = true;
    /// <summary>duckduckgo | searxng | brave (Brave needs an API key in Credential Manager).</summary>
    public string SearchEngine { get; set; } = "duckduckgo";
    /// <summary>Your SearXNG instance, e.g. https://searx.example.org (for SearchEngine = searxng).</summary>
    public string SearxngUrl { get; set; } = "";
    /// <summary>Pages Research reads before writing (3–12).</summary>
    public int ResearchPages { get; set; } = 6;
    /// <summary>"Tell me more" about a story reads the outlets' articles, not just their headlines.</summary>
    public bool ReadStoryArticles { get; set; } = true;
    /// <summary>The Think switch starts on (slower, more careful answers).</summary>
    public bool ThinkByDefault { get; set; }
    /// <summary>Allow the "Use my PC" switch at all (files, screenshots, opening things, media).</summary>
    public bool Computer { get; set; } = true;
    /// <summary>Folders Ask may search and read. Tokens: %DOCUMENTS%, %DESKTOP%, %DOWNLOADS%, %PICTURES%, %USERPROFILE%.</summary>
    public List<string> Folders { get; set; } = new() { "%DOCUMENTS%", "%DESKTOP%", "%DOWNLOADS%", "%PICTURES%" };
    /// <summary>Ask before Ask opens files, links or apps, controls media or reads the clipboard.</summary>
    public bool ConfirmActions { get; set; } = true;
    /// <summary>Ask before Ask takes a screenshot on its own (your own Screenshot button never asks).</summary>
    public bool ConfirmScreenshots { get; set; } = true;
    /// <summary>PDF pages read from an attachment or a web PDF (each page is OCR'd on this PC).</summary>
    public int MaxPdfPages { get; set; } = 15;
    /// <summary>With "Use my PC": Ask may operate app windows (read them, click buttons, type, press keys) — each action asks first.</summary>
    public bool ControlApps { get; set; } = true;
    /// <summary>Which apps: "any" (every app except the off-limits ones) or "listed" (only <see cref="AllowedApps"/>).</summary>
    public string OperateApps { get; set; } = "any";
    /// <summary>With OperateApps = "listed": the apps Ask may operate, by app (process) name, e.g. notepad, spotify.</summary>
    public List<string> AllowedApps { get; set; } = new();
    /// <summary>Unstarred chats are deleted after this many days without use (0 = keep them).</summary>
    public int ChatRetentionDays { get; set; } = 30;
    /// <summary>Context window for Ask in tokens (0 = automatic: grows when an answer needs it).</summary>
    public int ContextWindow { get; set; }
    /// <summary>Earlier messages of the chat sent with each question (older ones are left out).</summary>
    public int HistoryMessages { get; set; } = 10;
    /// <summary>
    /// Voice input: offline (Windows' on-device recognizer), online (Windows online speech recognition), whisper (Whisper,
    /// installed on request, on this PC) or off.
    /// </summary>
    public string Voice { get; set; } = "offline";
    /// <summary>The Whisper model size to install: base, small or turbo.</summary>
    public string WhisperModel { get; set; } = "small";
    /// <summary>While dictating with Windows' recognizers, a note under the Ask box suggests Whisper.</summary>
    public bool WhisperTip { get; set; } = true;
    /// <summary>Send the question as soon as you stop speaking.</summary>
    public bool VoiceAutoSend { get; set; }
    /// <summary>The microphone the offline recognizer and Whisper listen on, by its Windows name ("" = Windows' default).</summary>
    public string Microphone { get; set; } = "";
    /// <summary>The local model corrects what the recognizer misheard before it goes in the Ask box.</summary>
    public bool VoiceTidy { get; set; } = true;
    /// <summary>Use what you asked Aqua to remember (Workbench › Memory) in answers.</summary>
    public bool UseMemory { get; set; } = true;
    /// <summary>Use the skills you taught (Workbench › Skills).</summary>
    public bool UseSkills { get; set; } = true;
    /// <summary>Let the model plan searches (better queries; adds a second or two). Off: rules only.</summary>
    public bool PlanWithModel { get; set; } = true;

    /// <summary>The model Ask answers with (its picker); empty means the one set in Settings › AI.</summary>
    public string Model { get; set; } = "";

    /// <summary>Think and Research as you last left them with each model, so switching model brings its own back.</summary>
    public Dictionary<string, AskModelModes> ModelModes { get; set; } = new();

    /// <summary>Think and Research for <paramref name="model"/>: as last left with it, else the defaults.</summary>
    public AskModelModes ModesFor(string model) =>
        ModelModes.TryGetValue(model, out var m) ? m : new AskModelModes { Think = ThinkByDefault };

    /// <summary>Remembers the switches for <paramref name="model"/> (a few dozen models at most are kept).</summary>
    public void RememberModes(string model, bool think, bool research)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        ModelModes[model] = new AskModelModes { Think = think, Research = research };
        foreach (var extra in ModelModes.Keys.Where(k => k != model).Skip(MaxModelModes - 1).ToList()) ModelModes.Remove(extra);
    }

    public const int MaxModelModes = 40;
}

public sealed class AskModelModes
{
    public bool Think { get; set; }
    public bool Research { get; set; }
}

public sealed class AppEntry
{
    public override string ToString() => Name;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>exe | shortcut | uwp | url</summary>
    public string Kind { get; set; } = "exe";
    /// <summary>Path to .exe/.lnk, an AppUserModelID for Store apps, or an https/app URI.</summary>
    public string Target { get; set; } = "";
    public string Args { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public bool Pinned { get; set; } = true;
    public bool IsMusicPlayer { get; set; }
}

public sealed class SceneStep
{
    /// <summary>launch | close | media | volume | mute | dnd | ai | open-url | open-page | wait | music-search | focus</summary>
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class Scene
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "spark";
    public string Description { get; set; } = "";
    public List<SceneStep> Steps { get; set; } = new();
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public bool DoNotDisturb { get; set; }
    public string QuietStart { get; set; } = "23:00";
    public string QuietEnd { get; set; } = "07:00";
    public bool SuppressWhenFullscreen { get; set; } = true;
    public bool PriceAlerts { get; set; } = true;
    public bool BigStories { get; set; } = true;
    public List<string> Keywords { get; set; } = new();
    public bool PredictionSwings { get; set; } = true;
    public bool CalendarReminders { get; set; } = true;
    public bool SystemWarnings { get; set; } = true;
    public bool BriefReady { get; set; } = true;
}

public sealed class PrivacySettings
{
    /// <summary>Remote thumbnails reveal your IP address to publishers' CDNs.</summary>
    public bool LoadRemoteImages { get; set; } = true;
    /// <summary>Keep Ask Aqua chats between sessions (stored only in the local database; see Ask › Delete chats after).</summary>
    public bool KeepAskHistory { get; set; } = true;
    public int RetentionDays { get; set; } = 7;
}
