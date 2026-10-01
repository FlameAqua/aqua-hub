using System.Globalization;
using System.Text.RegularExpressions;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai;

public sealed record HubCommand(string Action, string Target = "", string Value = "", string Reply = "", bool FromModel = false)
{
    public static readonly HubCommand None = new("none");
}

/// <summary>
/// Maps natural language to one allow-listed hub action. A deterministic fast path handles common
/// phrasings instantly; everything else goes to the local model constrained by a JSON schema whose
/// action field is an enum. Targets are validated against configured apps/scenes/pages, so the model
/// can never invent executables, URLs or shell commands.
/// </summary>
public sealed partial class CommandInterpreter
{
    public static readonly string[] Pages = { "today", "news", "social", "markets", "upcoming", "system", "launchpad", "ask", "agents", "settings" };

    private static readonly Dictionary<string, string> PageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = "today", ["dashboard"] = "today", ["brief"] = "today", ["headlines"] = "news", ["stories"] = "news",
        ["reddit"] = "social", ["pulse"] = "social", ["stocks"] = "markets", ["market"] = "markets", ["portfolio"] = "markets",
        ["calendar"] = "upcoming", ["events"] = "upcoming", ["agenda"] = "upcoming", ["predictions"] = "upcoming",
        ["pc"] = "system", ["computer"] = "system", ["apps"] = "launchpad", ["music"] = "launchpad", ["scenes"] = "launchpad",
        ["chat"] = "ask", ["assistant"] = "ask", ["preferences"] = "settings", ["options"] = "settings",
    };

    private readonly LlmClient? _llm;
    private readonly Func<HubSettings> _settings;

    public CommandInterpreter(LlmClient? llm, Func<HubSettings> settings)
    {
        _llm = llm;
        _settings = settings;
    }

    [GeneratedRegex(@"^(?:open|launch|start|run)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpenRx();
    [GeneratedRegex(@"^(?:close|quit|exit|kill)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CloseRx();
    [GeneratedRegex(@"^(?:set\s+)?(?:volume|vol)\s*(?:to\s*)?(\d{1,3})\s*%?$", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeRx();
    [GeneratedRegex(@"^(?:go\s+to|show|open)\s+(?:the\s+)?(\w+)(?:\s+page)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PageRx();
    [GeneratedRegex(@"^\$?([A-Za-z\^][A-Za-z0-9\.\-=\^]{0,11})$")]
    private static partial Regex TickerRx();
    [GeneratedRegex(@"^play\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayRx();
    // "could you make it quieter" is a polite request, not a question: without a "?" it goes to the command agent.
    [GeneratedRegex(@"^(?!(?:can|could|would|will)\s+you\b)(?:what|who|why|how|when|where|which|is|are|was|were|will|should|could|can|do|does|did|summari[sz]e|explain|tell me|give me)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionRx();
    [GeneratedRegex(@"^(?:research|investigate|deep ?dive(?: into)?|dig into)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ResearchRx();
    [GeneratedRegex(@"^(?:search the web for|search the web|web search|google|look up|look online for)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WebRx();
    [GeneratedRegex(@"^(?:find|search (?:for|my)|look for|where(?:'s| is| are))\s+(?:[\w'-]+\s+){0,3}?(?:files?|documents?|docs?|pdfs?|spreadsheets?|pictures?|photos?|downloads?|notes)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FilesRx();
    [GeneratedRegex(@"^(?:what(?:'s| is) on (?:my|the) screen|(?:ask|tell me) about (?:my|this|the) screen|explain (?:my|this|the) screen|screenshot)\??$", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenRx();

    public HubCommand? TryFastPath(string input)
    {
        var text = input.Trim().TrimEnd('.', '!');
        if (text.Length == 0) return HubCommand.None;
        var lower = text.ToLowerInvariant();
        var s = _settings();

        switch (lower)
        {
            case "pause" or "pause music" or "stop music" or "stop" or "pause playback": return new("media_pause", Reply: "Paused");
            case "play" or "resume" or "play music" or "resume music" or "unpause": return new("media_play", Reply: "Playing");
            case "next" or "skip" or "next track" or "next song" or "skip song": return new("media_next", Reply: "Next track");
            case "previous" or "prev" or "previous track" or "last song" or "back": return new("media_previous", Reply: "Previous track");
            case "mute" or "unmute" or "toggle mute": return new("mute_toggle", Reply: "Toggled mute");
            case "volume up" or "louder" or "turn it up": return new("volume_up", Reply: "Volume up");
            case "volume down" or "quieter" or "turn it down": return new("volume_down", Reply: "Volume down");
            case "refresh" or "sync" or "refresh all" or "update": return new("refresh", Reply: "Refreshing everything");
            case "brief" or "daily brief" or "my brief" or "show brief": return new("show_brief");
            case "read brief" or "read my brief" or "read the brief" or "read it" or "read aloud": return new("read_brief", Reply: "Reading your brief");
            case "dnd" or "do not disturb" or "dnd on" or "focus mode on" or "quiet": return new("dnd_on", Reply: "Do not disturb is on");
            case "dnd off" or "do not disturb off" or "notifications on": return new("dnd_off", Reply: "Notifications back on");
            case "pause ai" or "stop ai" or "game mode": return new("pause_ai", Reply: "AI paused — VRAM freed");
            case "resume ai" or "start ai": return new("resume_ai", Reply: "AI resumed");
            case "summarize clipboard" or "summarise clipboard" or "tldr clipboard" or "summarize this" or "summarise this":
                return new("summarize_clipboard");
        }

        // Ask modes: research, the web, your files, your screen (the Ask page reads the @-prefix).
        if (ScreenRx().IsMatch(text)) return new("ask", "@screen");
        if (ResearchRx().Match(text) is { Success: true } rm) return new("ask", "@research:" + rm.Groups[1].Value.Trim());
        if (WebRx().Match(text) is { Success: true } wm) return new("ask", "@web:" + wm.Groups[1].Value.Trim());
        if (s.Ask.Computer && FilesRx().IsMatch(text)) return new("ask", "@pc:" + text);

        if (VolumeRx().Match(text) is { Success: true } vm)
        {
            var v = Math.Clamp(int.Parse(vm.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100);
            return new("volume_set", Value: v.ToString(CultureInfo.InvariantCulture), Reply: $"Volume {v}%");
        }

        var scene = MatchScene(text, s);
        if (scene is not null) return new("run_scene", scene.Id, Reply: $"Running {scene.Name}");

        if (PageRx().Match(text) is { Success: true } pm && ResolvePage(pm.Groups[1].Value) is { } page)
            return new("open_page", page);
        if (ResolvePage(text) is { } directPage) return new("open_page", directPage);

        if (OpenRx().Match(text) is { Success: true } om)
        {
            var name = om.Groups[1].Value;
            if (MatchScene(name, s) is { } sc) return new("run_scene", sc.Id, Reply: $"Running {sc.Name}");
            if (MatchApp(name, s) is { } app) return new("launch_app", app.Id, Reply: $"Opening {app.Name}");
            if (ResolvePage(name) is { } p2) return new("open_page", p2);
        }
        if (CloseRx().Match(text) is { Success: true } cm && MatchApp(cm.Groups[1].Value, s) is { } closeApp)
            return new("close_app", closeApp.Id, Reply: $"Closing {closeApp.Name}");

        // "play lofi": your music app from the Launchpad, or a web search when you haven't set one.
        if (PlayRx().Match(text) is { Success: true } play)
            return new("music_search", play.Groups[1].Value, Reply: $"Searching music for “{play.Groups[1].Value}”");

        var allSymbols = s.Markets.Watchlist.Concat(s.Markets.Indices).Concat(s.Markets.Macro).ToList();
        var sym = allSymbols.FirstOrDefault(w => w.Symbol.Equals(text.TrimStart('$'), StringComparison.OrdinalIgnoreCase) ||
                                                 w.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (sym is not null) return new("open_ticker", sym.Symbol);
        if (text.StartsWith('$') && TickerRx().IsMatch(text)) return new("open_ticker", text.TrimStart('$').ToUpperInvariant());

        if (MatchApp(text, s) is { } direct && text.Length >= 3) return new("launch_app", direct.Id, Reply: $"Opening {direct.Name}");

        if (QuestionRx().IsMatch(text) || text.EndsWith('?')) return new("ask", text);
        return null;
    }

    public async Task<HubCommand> InterpretAsync(string input, CancellationToken ct = default)
    {
        var fast = TryFastPath(input);
        if (fast is not null) return fast;
        if (_llm is null || !_llm.Health.Available) return new HubCommand("ask", input.Trim());

        var s = _settings();
        try
        {
            var (json, _) = await _llm.CompleteJsonAsync(Prompts.Command(input, s.Apps, s.Scenes, Pages), ct).ConfigureAwait(false);
            using (json)
            {
                var r = json.RootElement;
                var action = r.Str("action") ?? "none";
                var target = (r.Str("target") ?? "").Trim();
                var value = (r.Str("value") ?? "").Trim();
                var reply = HtmlText.Truncate(r.Str("reply") ?? "", 120);
                return Validate(new HubCommand(action, target, value, reply, FromModel: true), input, s);
            }
        }
        catch (Exception ex) when (ex is FormatException or LlmUnavailableException or HttpRequestException)
        {
            Log.Warn("command", "Intent parsing failed", ex);
            return new HubCommand("ask", input.Trim());
        }
    }

    /// <summary>Rejects anything outside the allow-list or referencing unknown ids.</summary>
    public static HubCommand Validate(HubCommand cmd, string input, HubSettings s)
    {
        if (!Prompts.CommandActions.Contains(cmd.Action)) return HubCommand.None;
        switch (cmd.Action)
        {
            case "launch_app" or "close_app":
                var app = s.Apps.FirstOrDefault(a => a.Id.Equals(cmd.Target, StringComparison.OrdinalIgnoreCase)) ?? MatchApp(cmd.Target, s);
                return app is null ? HubCommand.None : cmd with { Target = app.Id };
            case "run_scene":
                var scene = s.Scenes.FirstOrDefault(x => x.Id.Equals(cmd.Target, StringComparison.OrdinalIgnoreCase)) ?? MatchScene(cmd.Target, s);
                return scene is null ? HubCommand.None : cmd with { Target = scene.Id };
            case "open_page":
                var page = ResolvePage(cmd.Target);
                return page is null ? HubCommand.None : cmd with { Target = page };
            case "volume_set":
                return int.TryParse(cmd.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                    ? cmd with { Value = Math.Clamp(v, 0, 100).ToString(CultureInfo.InvariantCulture) } : HubCommand.None;
            case "open_ticker":
                return Regex.IsMatch(cmd.Target, @"^[A-Za-z0-9\^\.\-=]{1,15}$") ? cmd with { Target = cmd.Target.ToUpperInvariant() } : HubCommand.None;
            case "music_search":
                return cmd.Target.Trim().Length > 0 ? cmd with { Target = HtmlText.Truncate(cmd.Target, 100) } : HubCommand.None;
            case "ask":
                return cmd with { Target = input.Trim() };
            default:
                return cmd;
        }
    }

    public static string? ResolvePage(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (Pages.Contains(n)) return n;
        return PageAliases.TryGetValue(n, out var alias) ? alias : null;
    }

    public static AppEntry? MatchApp(string name, HubSettings s)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n.Length == 0) return null;
        return s.Apps.FirstOrDefault(a => a.Id.Equals(n, StringComparison.OrdinalIgnoreCase) || a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
               ?? s.Apps.FirstOrDefault(a => a.Keywords.Any(k => k.Equals(n, StringComparison.OrdinalIgnoreCase)))
               ?? s.Apps.FirstOrDefault(a => n.Length >= 3 && a.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase))
               ?? s.Apps.FirstOrDefault(a => n.Length >= 4 && a.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    public static Scene? MatchScene(string name, HubSettings s)
    {
        var n = name.Trim().ToLowerInvariant().Replace(" mode", "").Replace(" scene", "");
        return s.Scenes.FirstOrDefault(x => x.Id.Equals(n, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }
}
