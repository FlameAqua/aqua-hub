using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AquaHub.Core.Agents;
using AquaHub.Core.Models;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>Runs one of Aqua's agents now and waits for it, up to the timeout (<see cref="AgentRuntime.RunAndWaitAsync"/>).</summary>
public delegate Task<AgentRunReport?> AgentRunner(string id, TimeSpan timeout, CancellationToken ct);

/// <summary>
/// What Ask may have Aqua's own agents do for the user — "refresh the news", "update my markets", "write me a fresh
/// brief" — and how long it waits for each. Only these: the model picks a job by name, never an agent.
/// </summary>
public static partial class AgentJobs
{
    /// <param name="What">How the chat names it: "Refreshing the news…".</param>
    /// <param name="Agents">The agents that do it, in order (each waits for the one before).</param>
    /// <param name="Wait">How long the answer waits for all of them.</param>
    public sealed record Job(string Key, string What, string[] Agents, TimeSpan Wait)
    {
        /// <summary>The agents write with the model (minutes on a local one), so an answer lets go of it while they work.</summary>
        public bool UsesAi { get; init; }
    }

    /// <param name="Summary">One line for the activity list ("12 new articles from 38 sources", "switched off in Settings").</param>
    /// <param name="Text">What the model is told.</param>
    public sealed record Outcome(Job Job, bool Ok, bool Finished, string Summary, string Text);

    public static readonly IReadOnlyList<Job> All = new Job[]
    {
        new("news", "the news", new[] { "news-scout", "curator" }, TimeSpan.FromSeconds(90)),
        new("social", "social posts", new[] { "social-scout" }, TimeSpan.FromSeconds(60)),
        new("markets", "market prices", new[] { "market-watch" }, TimeSpan.FromSeconds(60)),
        new("predictions", "prediction markets", new[] { "prediction-scout" }, TimeSpan.FromSeconds(60)),
        new("agenda", "your agenda", new[] { "events-scout" }, TimeSpan.FromSeconds(60)),
        new("weather", "the weather", new[] { "weather" }, TimeSpan.FromSeconds(45)),
        new("brief", "your brief", new[] { "briefing" }, TimeSpan.FromMinutes(4)) { UsesAi = true },
        new("market_analysis", "the market analysis", new[] { "market-analyst" }, TimeSpan.FromMinutes(4)) { UsesAi = true },
        new("pulse", "the social pulse", new[] { "pulse" }, TimeSpan.FromMinutes(4)) { UsesAi = true },
        new("foresight", "Foresight", new[] { "foresight" }, TimeSpan.FromMinutes(4)) { UsesAi = true },
    };

    /// <summary>What "everything" and "all the agents" refresh: the collectors (the AI agents are asked for by name).</summary>
    private static readonly string[] Everything = { "news", "social", "markets", "predictions", "agenda", "weather" };

    public static Job? Find(string key)
    {
        var k = key.Trim().Replace(' ', '_').Replace('-', '_');
        return All.FirstOrDefault(j => j.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
    }

    private const string Done = "agent-jobs";

    /// <summary>What was refreshed for this answer so far ("the news").</summary>
    public static IReadOnlyList<string> Ran(AskRun run)
    {
        lock (run.Bag)
            return run.Bag.TryGetValue(Done, out var o)
                ? ((Dictionary<string, Task<Outcome>>)o).Where(d => d.Value.IsCompletedSuccessfully && d.Value.Result.Ok).Select(d => d.Value.Result.Job.What).ToList()
                : Array.Empty<string>();
    }

    /// <summary>Whether this job has already run (or is running) for this answer.</summary>
    public static bool HasRun(AskRun run, Job job)
    {
        lock (run.Bag) return run.Bag.TryGetValue(Done, out var o) && ((Dictionary<string, Task<Outcome>>)o).ContainsKey(job.Key);
    }

    /// <summary>
    /// Runs a job's agents in turn and waits for them, at most once per answer: asking again gets the same outcome back.
    /// After the news, the stories that weren't there before are listed (numbered for citing).
    /// </summary>
    public static async Task<Outcome> RunAsync(Job job, AskRun run, CancellationToken ct)
    {
        TaskCompletionSource<Outcome>? mine = null;
        Task<Outcome> task;
        lock (run.Bag)
        {
            if (!run.Bag.TryGetValue(Done, out var o)) run.Bag[Done] = o = new Dictionary<string, Task<Outcome>>(StringComparer.Ordinal);
            var done = (Dictionary<string, Task<Outcome>>)o;
            if (!done.TryGetValue(job.Key, out task!))
            {
                mine = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                done[job.Key] = task = mine.Task;
            }
        }
        if (mine is not null)
        {
            try { mine.SetResult(await RunOnceAsync(job, run, ct).ConfigureAwait(false)); }
            catch (OperationCanceledException) { mine.SetCanceled(ct); }
            catch (Exception ex) { mine.SetException(ex); }
        }
        return await task.ConfigureAwait(false);
    }

    private static async Task<Outcome> RunOnceAsync(Job job, AskRun run, CancellationToken ct)
    {
        if (run.RunAgent is null) return new(job, false, false, "not available here", "Aqua's agents can't be run from here.");
        // The Chief of Staff only rewrites a current brief when asked to (as the Today page's refresh button does).
        if (job.Key == "brief") BriefingAgent.ManualFlag.Request();
        var before = job.Key == "news" ? run.State.Stories.Select(c => c.Id).ToHashSet(StringComparer.Ordinal) : null;
        var sw = Stopwatch.StartNew();
        var lines = new List<string>();
        string? summary = null;
        bool ok = true, finished = true;
        foreach (var id in job.Agents)
        {
            var left = job.Wait - sw.Elapsed;
            if (left <= TimeSpan.Zero) { finished = false; break; }
            var report = await run.RunAgent(id, left, ct).ConfigureAwait(false);
            if (report is null) { ok = false; summary ??= "not available here"; lines.Add($"The agent for {job.What} isn't available."); break; }
            var s = report.Status;
            if (report.End == AgentRunEnd.Disabled)
            {
                ok = false; summary ??= "switched off in Settings";
                lines.Add($"{s.Name} is switched off in Settings, so it didn't run.");
                break;
            }
            if (report.End == AgentRunEnd.Paused)
            {
                ok = false; summary ??= "agents are paused";
                lines.Add($"The user paused Aqua's agents (Agents page › Resume all), so {s.Name} didn't run.");
                break;
            }
            if (report.End == AgentRunEnd.StillRunning)
            {
                finished = false; summary ??= "still working";
                lines.Add($"{s.Name} is still working after {Seconds(sw.Elapsed)}; what it brings in will show up in Aqua when it's done.");
                break;
            }
            var failed = s.State == AgentState.Error;
            summary ??= failed ? "failed: " + s.LastMessage : s.LastMessage;
            lines.Add($"{s.Name}: {s.LastMessage}" + (failed ? " (it failed)" : s.State == AgentState.Paused ? " (it's waiting)" : ""));
            if (failed) { ok = false; break; }
        }

        var sb = new StringBuilder();
        sb.Append(ok && finished ? $"Refreshed {job.What} just now ({run.Now.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}):\n"
            : ok ? $"Started refreshing {job.What}:\n" : $"Couldn't refresh {job.What}:\n");
        foreach (var line in lines) sb.Append("- ").Append(line).Append('\n');
        if (before is not null && ok && finished)
        {
            var fresh = run.State.Stories.Where(c => !before.Contains(c.Id)).OrderByDescending(c => c.Importance).Take(8).ToList();
            if (fresh.Count == 0) sb.Append("No new stories came in.\n");
            else
            {
                sb.Append("NEW STORIES SINCE THE REFRESH:\n").Append(HubSearch.Format(fresh.Select(c => HubSearch.StoryHit(c, 1)), run.Book, run.Now)).Append('\n');
                run.SawUntrusted = true;
            }
        }
        if (ok && finished) sb.Append(job.UsesAi ? "Its new output is in the user's feeds now.\n" : "search_hub finds what it brought in.\n");
        return new(job, ok, finished, summary ?? "done", sb.ToString());
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds < 90
        ? Plural.Of((int)Math.Round(t.TotalSeconds), "second")
        : Plural.Of((int)Math.Round(t.TotalMinutes), "minute");

    // ───────────────────────────── Asked for in so many words ─────────────────────────────

    // "Refresh the news", "can you update my markets and the weather?", "rerun the market analyst", "rewrite my brief" —
    // a request, not "an update on…" or "update me on…".
    [GeneratedRegex(@"(?:^|[.!?;:,]\s*|\b(?:please|pls|aqua|hey|ok|okay|can you|could you|would you|will you|can u|go|and|then|now|just|also|to)\s+)(?<verb>re-?run|refresh|reload|update(?!\s+(?:me|us|on|about|you)\b)|re-?check|re-?fetch|run|redo|re-?write|regenerate|remake|rebuild)\s+(?<what>[^.!?;\n]{1,90})", RegexOptions.IgnoreCase)]
    private static partial Regex AskedRx();

    // "write me a fresh brief", "give me an updated market analysis", "get the latest market prices" (not "a new story
    // about…", nor "what's the latest news?", which the feeds already answer).
    [GeneratedRegex(@"\b(?:write|make|give|get|create|generate|produce|do|fetch|pull)\s+(?:me\s+|us\s+)?(?:(?:a|an|the|some|my)\s+)?(?:new|fresh|updated|latest|newest|current)\s+(?<what>(?:morning\s+|evening\s+|daily\s+)?brief(?:ing)?|digest|market\s+(?:analysis|brief)|analysis|(?:social\s+)?pulse|foresight|forecast|weather|(?:(?:market|stock|share|watchlist|crypto)\s+)?(?:prices|quotes)|headlines|news|posts|odds)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FreshRx();

    // Where the request ends and the question about it begins: "refresh the news and tell me what's new".
    [GeneratedRegex(@",|\b(?:then|so|and\s+(?:then|tell|show|give|let|summari[sz]e|list|explain|say|answer|what|how|why|which|who|is|are|see|check))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseEndRx();

    // "refresh my memory", "update the app", "run a health check", "run the numbers"
    [GeneratedRegex(@"^(?:(?:the|my|your|our|a|an|all|of|this|that|it)\s+)*(?:memory|app|aqua|windows|drivers?|page|screen|health|numbers|scan|scene|test|game|program|script|command)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotAJobRx();

    [GeneratedRegex(@"^(?:(?:the|my|your|our|a|an|all|of|some|this|today'?s)\s+)+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingWordsRx();

    private static readonly (string[] Keys, Regex Rx)[] Topics =
    {
        (Everything, Rx(@"\beverything\b|\ball\b(?!\s+(?:of\s+)?(?:the\s+|my\s+|your\s+)?(?:news|headlines|stories|social|posts|markets?|stocks|prices|quotes|predictions|odds|events|weather|forecasts?|briefs?|feeds?))|\bagents\b")),
        (new[] { "market_analysis" }, Rx(@"\bmarket\s+(?:analys[ie]s|analyst|brief|insights?|ideas)\b|\banalyst\b|\banalys[ie]s\b")),
        (new[] { "pulse" }, Rx(@"\b(?:social\s+)?pulse\b")),
        (new[] { "foresight" }, Rx(@"\bforesight\b|\bwhat'?s\s+coming(?:\s+up)?\b")),
        (new[] { "brief" }, Rx(@"\b(?:morning\s+|evening\s+|daily\s+)?brief(?:ing)?s?\b|\bdigest\b|\bchief\s+of\s+staff\b")),
        (new[] { "predictions" }, Rx(@"\bprediction(?:\s+(?:markets?|scout)|s)?\b|\bodds\b|\bpolymarket\b|\bkalshi\b|\bmanifold\b")),
        (new[] { "news", "social" }, Rx(@"\bfeeds?\b")),
        (new[] { "news" }, Rx(@"\bnews(?:\s+scout)?\b|\bheadlines?\b|\bstor(?:y|ies)\b|\b(?:story\s+)?curator\b|\barticles?\b")),
        (new[] { "social" }, Rx(@"\bsocial(?:\s+(?:media|posts?|scout))?\b|\bposts\b|\breddit\b|\bsubreddits?\b|\bbluesky\b|\bmastodon\b")),
        (new[] { "markets" }, Rx(@"\bmarkets?(?:\s+watch)?\b|\bstocks?\b|\bshares\b|\bprices\b|\bquotes\b|\bwatch\s*list\b|\bportfolio\b|\bindices\b|\bcrypto\b|\bexchange\s+rates\b")),
        (new[] { "agenda" }, Rx(@"\bagenda(?:\s+scout)?\b|\bcalendars?\b|\bevents\b|\bschedule\b")),
        (new[] { "weather" }, Rx(@"\bweather\b|\bforecasts?\b")),
    };

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The jobs a message asks for in so many words — "refresh the news", "update my markets and the weather", "rerun the
    /// market analyst", "write me a fresh brief" — so they run before the answer even when the model wouldn't call tools.
    /// "Run" needs an agent's name straight after it ("run the numbers on my portfolio" isn't a request), and "rewrite"
    /// or "redo" only fit what the AI agents write ("rewrite this story" is about the user's text).
    /// </summary>
    /// <param name="earlier">The user's previous message, for "refresh it" when this one doesn't say what "it" is.</param>
    public static List<Job> Requested(string message, string? earlier = null)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in AskedRx().Matches(message))
        {
            var verb = m.Groups["verb"].Value.ToLowerInvariant().Replace("-", "");
            var rewrite = verb is "redo" or "rewrite" or "regenerate" or "remake" or "rebuild";
            if (PronounRx().IsMatch(m.Groups["what"].Value))
            {
                // "Is anything new on the weather? Refresh it": "it" is what the rest of the message, or the last one, is about.
                var found = Mentioned(message.Remove(m.Index, m.Length));
                if (found.Count == 0 && earlier is not null) found = Mentioned(earlier);
                keys.UnionWith(rewrite ? found.Where(k => Find(k) is { UsesAi: true }) : found);
                continue;
            }
            Collect(m.Groups["what"].Value, nameFirst: rewrite || verb == "run", aiOnly: rewrite, keys);
        }
        foreach (Match m in FreshRx().Matches(message)) Collect(m.Groups["what"].Value, nameFirst: true, aiOnly: false, keys);
        return All.Where(j => keys.Contains(j.Key)).ToList();
    }

    [GeneratedRegex(@"^\s*(?:it|them|that|those|this|these)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PronounRx();

    /// <summary>What a text mentions that Ask can refresh ("the weather front", "my watchlist"); "everything" doesn't count here.</summary>
    private static HashSet<string> Mentioned(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (topicKeys, rx) in Topics.Skip(1))
        {
            if (!rx.IsMatch(text)) continue;
            found.UnionWith(topicKeys);
            text = rx.Replace(text, m => new string(' ', m.Length));
        }
        return found;
    }

    private static void Collect(string what, bool nameFirst, bool aiOnly, HashSet<string> keys)
    {
        var end = ClauseEndRx().Match(what);
        var raw = (end.Success ? what[..end.Index] : what).Trim();
        if (raw.Length == 0 || NotAJobRx().IsMatch(raw)) return;
        var clause = LeadingWordsRx().Replace(raw, "");
        // "Run" and "a fresh …" name the thing straight away: "run the market analyst", "run all agents".
        if (nameFirst && !Topics.Any(t => t.Rx.Match(clause) is { Success: true, Index: 0 }) && Topics[0].Rx.Match(raw) is not { Success: true, Index: 0 })
            return;
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (Topics[0].Rx.IsMatch(raw)) found.UnionWith(Topics[0].Keys);
        foreach (var (topicKeys, rx) in Topics.Skip(1))
        {
            if (!rx.IsMatch(clause)) continue;
            found.UnionWith(topicKeys);
            // A matched name isn't read twice: "prediction markets" isn't also markets, "social pulse" isn't social posts.
            clause = rx.Replace(clause, m => new string(' ', m.Length));
        }
        keys.UnionWith(aiOnly ? found.Where(k => Find(k) is { UsesAi: true }) : found);
    }
}

/// <summary>Has one of Aqua's agents run now and waits for it: "refresh the news", "update my markets", "write me a fresh brief".</summary>
public sealed class RunAgentTool : AskTool
{
    public override string Name => "run_agent";
    public override string Description =>
        "Have Aqua's own agents refresh something now and wait for them, when the user asks to refresh or update what Aqua collects " +
        "(news, social posts, market prices, prediction markets, their agenda, the weather) or for a fresh brief, market analysis, social pulse " +
        "or Foresight. Collecting takes seconds; the brief and analyses take a few minutes. Afterwards search_hub finds what came in.";
    public override JsonObject Parameters
    {
        get
        {
            var schema = Schema(("job", "string", "What to refresh", true));
            schema["properties"]!["job"]!["enum"] = new JsonArray(AgentJobs.All.Select(j => (JsonNode)j.Key).ToArray());
            return schema;
        }
    }
    public override string Icon => "agents";
    public override bool WaitsForModel(JsonElement args) => AgentJobs.Find(Arg(args, "job"))?.UsesAi == true;
    public override bool AlreadyDone(JsonElement args, AskRun run) => AgentJobs.Find(Arg(args, "job")) is { } job && AgentJobs.HasRun(run, job);
    public override string Describe(JsonElement args) => AgentJobs.Find(Arg(args, "job")) is { } job ? "Refreshed " + job.What : "Ran an agent";

    public override async Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var job = AgentJobs.Find(Arg(args, "job"));
        if (job is null) return ToolResult.Fail("unknown job — use one of " + string.Join(", ", AgentJobs.All.Select(j => j.Key)));
        run.Host.Status($"Refreshing {job.What}…");
        var outcome = await AgentJobs.RunAsync(job, run, ct).ConfigureAwait(false);
        run.Host.Status("");
        return new ToolResult(outcome.Text, outcome.Summary, outcome.Ok);
    }
}
