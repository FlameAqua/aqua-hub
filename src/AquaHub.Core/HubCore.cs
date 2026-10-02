using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Data;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core;

/// <summary>Resolved on-disk locations for one profile.</summary>
public sealed class HubPaths
{
    public required string Root { get; init; }
    public string Settings => Path.Combine(Root, "settings.json");
    public string Database => Path.Combine(Root, "hub.db");
    public string Logs => Path.Combine(Root, "logs");
    public string ImageCache => Path.Combine(Root, "cache", "images");
    /// <summary>Pictures Ask's chats keep (a folder per chat, deleted with it).</summary>
    public string ChatMedia => Path.Combine(Root, "chat-media");

    /// <summary>%LOCALAPPDATA%\AquaHub, or AQUAHUB_HOME / --data-dir when set.</summary>
    public static HubPaths Resolve(string? overrideDir = null)
    {
        var root = overrideDir
                   ?? Environment.GetEnvironmentVariable("AQUAHUB_HOME")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaHub");
        Directory.CreateDirectory(root);
        return new HubPaths { Root = Path.GetFullPath(root) };
    }
}

/// <summary>Composition root for all platform-neutral services and agents.</summary>
public sealed class HubCore : IDisposable
{
    public HubPaths Paths { get; }
    public SettingsStore Settings { get; }
    public HubDatabase Db { get; }
    public HttpFetcher Http { get; }
    public LlmClient Llm { get; }
    public HubState State { get; }
    public AgentRuntime Agents { get; }
    public AskService Ask { get; }
    /// <summary>Ask Aqua's tool-using engine (web, files, research).</summary>
    public Ai.Assistant.AskAgent Assistant { get; }
    public WebSearch WebSearch { get; }
    public WebReader WebReader { get; }
    /// <summary>Skills and memories you taught Ask (Workbench).</summary>
    public Ai.Assistant.Workbench Workbench { get; }
    public Ai.Assistant.ChatStore Chats { get; }
    public Ai.Assistant.SkillDrafter SkillDrafter { get; }
    public CommandInterpreter Commands { get; }
    public HubContext Context { get; }
    public ISecretStore Secrets { get; }
    /// <summary>Whisper speech recognition, installed on request (Settings › Ask Aqua › Voice).</summary>
    public Speech.WhisperSetup Whisper { get; }

    public HubCore(HubPaths paths, ISecretStore secrets, IPlatform platform)
    {
        Paths = paths;
        Secrets = secrets;
        Log.Init(paths.Logs);
        Log.Info("core", $"Aqua Hub core starting (profile {paths.Root})");
        Settings = new SettingsStore(paths.Settings);
        Log.MinLevel = Settings.Current.General.VerboseLog ? LogLevel.Debug : LogLevel.Info;
        Settings.Changed += s => Log.MinLevel = s.General.VerboseLog ? LogLevel.Debug : LogLevel.Info;
        Db = new HubDatabase(paths.Database);
        Http = new HttpFetcher(Db);
        Whisper = new Speech.WhisperSetup(Path.Combine(paths.Root, "whisper"), Http.Client);
        Llm = new LlmClient(() => Settings.Current.Ai, secrets);
        State = new HubState(Db);
        State.Load();
        Context = new HubContext
        {
            Settings = Settings, Db = Db, Http = Http, Llm = Llm, State = State, Secrets = secrets, Platform = platform,
        };
        Agents = new AgentRuntime(Context);
        foreach (var agent in CreateAgents()) Agents.Register(agent);
        Ask = new AskService(Db, State, Llm, () => Settings.Current);
        WebSearch = new WebSearch(Http, () => Settings.Current, secrets);
        WebReader = new WebReader();
        Workbench = new Ai.Assistant.Workbench(Db);
        Chats = new Ai.Assistant.ChatStore(Db, paths.ChatMedia);
        SkillDrafter = new Ai.Assistant.SkillDrafter(Llm);
        Assistant = new Ai.Assistant.AskAgent(State, Db, Llm, () => Settings.Current, WebSearch, WebReader, Workbench) { RunAgent = Agents.RunAndWaitAsync };
        Commands = new CommandInterpreter(Llm, () => Settings.Current);
        Llm.HealthChanged += h => State.SetAi(h);
        Http.ConnectivityChanged += online =>
        {
            State.SetOnline(online);
            Log.Info("net", online ? "Back online — catching up" : "Looks offline — showing the last collected data");
            if (online) foreach (var id in new[] { "news-scout", "social-scout", "market-watch", "weather", "prediction-scout", "events-scout" }) Agents.RunNow(id);
        };
    }

    public static IEnumerable<Agent> CreateAgents() => new Agent[]
    {
        new ModelWardenAgent(),
        new WeatherAgent(),
        new NewsScoutAgent(),
        new StoryCuratorAgent(),
        new SocialScoutAgent(),
        new MarketWatchAgent(),
        new PredictionScoutAgent(),
        new EventsScoutAgent(),
        new NewsEditorAgent(),
        new PulseAgent(),
        new MarketAnalystAgent(),
        new ForesightAgent(),
        new BriefingAgent(),
        new SentinelAgent(),
        new KeeperAgent(),
    };

    public void Start() => Agents.Start();

    public void Dispose()
    {
        Agents.Dispose();
        Llm.Dispose();
        WebReader.Dispose();
        Http.Dispose();
    }
}
