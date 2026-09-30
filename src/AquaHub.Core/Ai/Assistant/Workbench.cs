using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AquaHub.Core.Analysis;
using AquaHub.Core.Data;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// A skill you taught Ask in the Workbench: when to use it, how to do it (instructions the model follows) and,
/// optionally, fixed steps — calls to Ask's own tools, run in order with the values from your request. Skills never run
/// code: every step is an existing tool with its usual rules, so actions still ask first.
/// </summary>
public sealed class AskSkill
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>When to use it ("When I ask for my standup notes").</summary>
    public string Description { get; set; } = "";
    /// <summary>Example requests that should trigger it.</summary>
    public List<string> Triggers { get; set; } = new();
    public string Instructions { get; set; } = "";
    public List<SkillStep> Steps { get; set; } = new();
    public List<SkillParameter> Parameters { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public int Uses { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    /// <summary>The request you described it with (kept so it can be improved later).</summary>
    public string TaughtAs { get; set; } = "";

    /// <summary>Needs the web or Use my PC (from its steps).</summary>
    public bool NeedsWeb(IReadOnlyCollection<ToolInfo> catalogue) => Steps.Any(s => catalogue.FirstOrDefault(t => t.Name == s.Tool)?.Access == ToolAccess.Web);
    public bool NeedsPc(IReadOnlyCollection<ToolInfo> catalogue) => Steps.Any(s => catalogue.FirstOrDefault(t => t.Name == s.Tool)?.Access is ToolAccess.Private or ToolAccess.Act);
}

public sealed class SkillStep
{
    public string Tool { get; set; } = "";
    /// <summary>Argument values; {name} is replaced by the skill's parameter, {input} by your whole request.</summary>
    public Dictionary<string, string> Args { get; set; } = new(StringComparer.Ordinal);
    public string Note { get; set; } = "";
}

public sealed class SkillParameter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>Something you asked Aqua to remember ("my logos are in Pictures/Brand").</summary>
public sealed class AskMemory
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    /// <summary>"chat" (you said "remember …") or "workbench".</summary>
    public string Source { get; set; } = "workbench";
}

/// <summary>A tool Ask can use, as the Workbench and the skill drafter see it.</summary>
public sealed record ToolInfo(string Name, string Description, ToolAccess Access, IReadOnlyList<string> Parameters, IReadOnlyList<string> Required, string Icon)
{
    public static ToolInfo From(AskTool t)
    {
        var props = t.Parameters["properties"] as JsonObject;
        var required = (t.Parameters["required"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList() ?? new();
        return new ToolInfo(t.Name, t.Description, t.Access, props?.Select(p => p.Key).ToList() ?? new(), required, t.Icon);
    }
}

/// <summary>
/// The Workbench's store: skills and memories, kept in the local database. Also the pure helpers Ask uses with them
/// (matching a request to a skill, filling in step values, the "remember …" / "forget …" commands).
/// </summary>
public sealed partial class Workbench
{
    private const string SkillsKey = "ask:skills";
    private const string MemoryKey = "ask:memory";
    public const int MaxMemories = 60;
    public const int MaxSkills = 60;
    private readonly HubDatabase? _db;
    private readonly object _gate = new();
    private List<AskSkill>? _skills;
    private List<AskMemory>? _memories;

    public Workbench(HubDatabase? db) => _db = db;

    /// <summary>Raised after any change (any thread).</summary>
    public event Action? Changed;

    public List<AskSkill> Skills()
    {
        lock (_gate)
        {
            _skills ??= _db?.GetJson<List<AskSkill>>(SkillsKey) ?? new();
            return _skills.Select(Clone).ToList();
        }
    }

    public List<AskMemory> Memories()
    {
        lock (_gate)
        {
            _memories ??= _db?.GetJson<List<AskMemory>>(MemoryKey) ?? new();
            return _memories.Select(m => new AskMemory { Id = m.Id, Text = m.Text, Created = m.Created, Source = m.Source }).ToList();
        }
    }

    private static AskSkill Clone(AskSkill s) => JsonSerializer.Deserialize<AskSkill>(JsonSerializer.Serialize(s))!;

    public AskSkill SaveSkill(AskSkill skill)
    {
        lock (_gate)
        {
            _skills ??= _db?.GetJson<List<AskSkill>>(SkillsKey) ?? new();
            var now = DateTimeOffset.Now;
            if (string.IsNullOrEmpty(skill.Id)) skill.Id = "k" + Hash.Short(skill.Name, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            if (skill.Created == default) skill.Created = now;
            skill.Updated = now;
            var copy = Clone(skill);
            var at = _skills.FindIndex(s => s.Id == skill.Id);
            if (at >= 0) _skills[at] = copy;
            else
            {
                if (_skills.Count >= MaxSkills) throw new InvalidOperationException($"You can keep up to {MaxSkills} skills — delete one first.");
                _skills.Add(copy);
            }
            _db?.PutJson(SkillsKey, _skills);
        }
        Changed?.Invoke();
        return skill;
    }

    public void DeleteSkill(string id)
    {
        lock (_gate)
        {
            _skills ??= _db?.GetJson<List<AskSkill>>(SkillsKey) ?? new();
            if (_skills.RemoveAll(s => s.Id == id) == 0) return;
            _db?.PutJson(SkillsKey, _skills);
        }
        Changed?.Invoke();
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            _skills ??= _db?.GetJson<List<AskSkill>>(SkillsKey) ?? new();
            if (_skills.FirstOrDefault(s => s.Id == id) is not { } skill || skill.Enabled == enabled) return;
            skill.Enabled = enabled;
            _db?.PutJson(SkillsKey, _skills);
        }
        Changed?.Invoke();
    }

    public void RecordUse(string id, DateTimeOffset when)
    {
        lock (_gate)
        {
            _skills ??= _db?.GetJson<List<AskSkill>>(SkillsKey) ?? new();
            if (_skills.FirstOrDefault(s => s.Id == id) is not { } skill) return;
            skill.Uses++;
            skill.LastUsed = when;
            _db?.PutJson(SkillsKey, _skills);
        }
        Changed?.Invoke();
    }

    public AskMemory Remember(string text, string source = "workbench")
    {
        text = CleanFact(text);
        if (text.Length < 3) throw new ArgumentException("That's too short to remember.");
        AskMemory memory;
        lock (_gate)
        {
            _memories ??= _db?.GetJson<List<AskMemory>>(MemoryKey) ?? new();
            var same = _memories.FirstOrDefault(m => string.Equals(m.Text, text, StringComparison.OrdinalIgnoreCase));
            if (same is not null) return same;
            if (_memories.Count >= MaxMemories) _memories.RemoveAt(0);
            memory = new AskMemory { Id = "m" + Hash.Short(text, DateTimeOffset.Now.Ticks.ToString(CultureInfo.InvariantCulture)), Text = text, Created = DateTimeOffset.Now, Source = source };
            _memories.Add(memory);
            _db?.PutJson(MemoryKey, _memories);
        }
        Changed?.Invoke();
        return memory;
    }

    public void UpdateMemory(string id, string text)
    {
        text = CleanFact(text);
        lock (_gate)
        {
            _memories ??= _db?.GetJson<List<AskMemory>>(MemoryKey) ?? new();
            if (_memories.FirstOrDefault(m => m.Id == id) is not { } memory) return;
            if (text.Length < 3) _memories.Remove(memory);
            else memory.Text = text;
            _db?.PutJson(MemoryKey, _memories);
        }
        Changed?.Invoke();
    }

    /// <summary>The memory with this id, or the ones that best match some words (nothing when the match is weak).</summary>
    public List<AskMemory> Matching(string idOrWords)
    {
        lock (_gate)
        {
            _memories ??= _db?.GetJson<List<AskMemory>>(MemoryKey) ?? new();
            var byId = _memories.Where(m => m.Id == idOrWords).ToList();
            if (byId.Count > 0) return byId;
            var words = TextTools.Signature(idOrWords);
            if (words.Count == 0) return new();
            var scored = _memories.Select(m => (Memory: m, Hits: words.Count(TextTools.Signature(m.Text).Contains))).Where(x => x.Hits > 0).ToList();
            if (scored.Count == 0) return new();
            var best = scored.Max(x => x.Hits);
            if (best < Math.Min(2, words.Count)) return new();
            return scored.Where(x => x.Hits == best).Select(x => x.Memory).ToList();
        }
    }

    /// <summary>Forgets a memory by id, or the ones that best match some words. Returns what was forgotten.</summary>
    public List<AskMemory> Forget(string idOrWords)
    {
        var gone = Matching(idOrWords);
        if (gone.Count == 0) return gone;
        lock (_gate)
        {
            foreach (var m in gone) _memories!.Remove(m);
            _db?.PutJson(MemoryKey, _memories);
        }
        Changed?.Invoke();
        return gone;
    }

    /// <summary>Puts forgotten memories back (Undo).</summary>
    public void Restore(IEnumerable<AskMemory> memories)
    {
        lock (_gate)
        {
            _memories ??= _db?.GetJson<List<AskMemory>>(MemoryKey) ?? new();
            foreach (var m in memories)
                if (!_memories.Any(x => x.Id == m.Id)) _memories.Add(m);
            _db?.PutJson(MemoryKey, _memories);
        }
        Changed?.Invoke();
    }

    private static string CleanFact(string text)
    {
        var t = Regex.Replace(text.Trim(), @"\s+", " ").Trim().TrimEnd('.');
        if (t.Length > 0) t = char.ToUpperInvariant(t[0]) + t[1..];
        return HtmlText.Truncate(t, 400);
    }

    // ───────────────────────────── "remember …" / "forget …" ─────────────────────────────

    [GeneratedRegex(@"^\s*(?:(?:hey|ok|okay)\s+)?(?:aqua[,:]?\s+)?(?:please\s+)?(?:remember|keep in mind|don'?t forget)(?:\s+that|\s*:)?\s+(?<fact>.{3,400}?)\s*[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RememberRx();

    /// <summary>A second request after the fact ("… — what's the weather then", "…, then search for …").</summary>
    [GeneratedRegex(@"(?:—|–|\s-\s|;|,|\bthen\b|\band\b|\bso\b|\bbut\b)\s*(?:what|what's|whats|when|where|how|who|which|why|(?:can|could|would|will) you|please|search|find|look|tell|show|check|give|open|play|set|remind|let's|let me|instead)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FollowOnRx();

    /// <summary>More than one sentence (not counting "Dr." and the like).</summary>
    [GeneratedRegex(@"(?<!\b(?:Dr|Mr|Mrs|Ms|St|Jr|Sr|vs|etc|e\.g|i\.e|approx|no))[.!]\s+\p{Lu}")]
    private static partial Regex SecondSentenceRx();

    [GeneratedRegex(@"^\s*(?:(?:hey|ok|okay)\s+)?(?:aqua[,:]?\s+)?(?:please\s+)?forget(?:\s+(?:that|about|what i said about))?\s+(?<fact>.{3,200}?)\s*[.!]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ForgetRx();

    /// <summary>
    /// "Remember that …" / "Forget …" as a command (pure): (kind, fact) or null. Only a single statement counts: questions
    /// ("remember when…?"), reminders ("remember to…"), a second request ("remember my flight is at 9 — what's the weather
    /// then") and "forget that, search for … instead" are answered like any other message.
    /// </summary>
    public static (string Kind, string Fact)? MemoryCommand(string text)
    {
        if (text.Contains('?') || text.Contains('\n')) return null;
        if (RememberRx().Match(text) is { Success: true } r)
        {
            var fact = r.Groups["fact"].Value.Trim();
            if (Regex.IsMatch(fact, @"^(when|what|how|who|where|why|which|if|to)\b", RegexOptions.IgnoreCase) || FollowOnRx().IsMatch(fact) || SecondSentenceRx().IsMatch(fact)) return null;
            return ("remember", fact);
        }
        if (ForgetRx().Match(text) is { Success: true } f)
        {
            var fact = f.Groups["fact"].Value.Trim();
            // "Forget that" / "forget it, …" is conversation, not a memory to remove.
            if (Regex.IsMatch(fact, @"^(?:that|it|this|those|them|everything)\b\s*(?:[,;—–-]|$)", RegexOptions.IgnoreCase) || FollowOnRx().IsMatch(fact) || SecondSentenceRx().IsMatch(fact)) return null;
            return ("forget", fact);
        }
        return null;
    }

    // ───────────────────────────── Matching and filling in ─────────────────────────────

    /// <summary>
    /// The skill whose example requests best match a question (pure; unit-tested) — used when the planner didn't pick
    /// one (it runs without the model too). Needs a close match: most of an example's words, or its exact phrase.
    /// </summary>
    public static AskSkill? Match(IEnumerable<AskSkill> skills, string question)
    {
        var q = TextTools.Signature(question);
        var folded = TextTools.Fold(question).ToLowerInvariant();
        AskSkill? best = null;
        double bestScore = 0;
        foreach (var s in skills.Where(s => s.Enabled))
        {
            foreach (var trigger in s.Triggers.Append(s.Name))
            {
                if (trigger.Trim().Length < 3) continue;
                var t = TextTools.Signature(trigger);
                if (t.Count == 0) continue;
                var phrase = TextTools.Fold(trigger).ToLowerInvariant().Trim();
                var score = folded.Contains(phrase, StringComparison.Ordinal) && phrase.Length >= 5 ? 1.0 : t.Count(q.Contains) / (double)t.Count;
                if (t.Count == 1 && score < 1) continue;
                if (score > bestScore) { bestScore = score; best = s; }
            }
        }
        return bestScore >= 0.75 ? best : null;
    }

    /// <summary>Fills {name} placeholders in a step's arguments ({input} = the request). Unfilled ones become empty.</summary>
    public static Dictionary<string, string> Fill(IReadOnlyDictionary<string, string> args, IReadOnlyDictionary<string, string> values, string input)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, raw) in args)
        {
            var isUrl = raw.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            var value = Regex.Replace(raw, @"\{([A-Za-z_][A-Za-z0-9_ -]{0,40})\}", m =>
            {
                var name = m.Groups[1].Value.Trim();
                var v = name.Equals("input", StringComparison.OrdinalIgnoreCase) ? input
                    : values.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "";
                return isUrl ? Uri.EscapeDataString(v) : v;
            });
            result[key] = value.Trim();
        }
        return result;
    }

    /// <summary>
    /// Checks a drafted skill against the tools that exist (pure; unit-tested): unknown tools and arguments are removed,
    /// names and text are trimmed to size. Returns the notes to show the user.
    /// </summary>
    public static List<string> Validate(AskSkill skill, IReadOnlyCollection<ToolInfo> catalogue)
    {
        var notes = new List<string>();
        skill.Name = HtmlText.Truncate(Regex.Replace(skill.Name ?? "", @"\s+", " ").Trim(), 60);
        if (skill.Name.Length == 0) skill.Name = "New skill";
        skill.Description = HtmlText.Truncate((skill.Description ?? "").Trim(), 300);
        skill.Instructions = HtmlText.Truncate((skill.Instructions ?? "").Trim(), 2500);
        skill.Triggers = (skill.Triggers ?? new()).Select(t => HtmlText.Truncate(t.Trim(), 120)).Where(t => t.Length >= 3)
                                                  .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        skill.Parameters = (skill.Parameters ?? new()).Where(p => Regex.IsMatch(p.Name ?? "", @"^[A-Za-z_][A-Za-z0-9_]{0,30}$"))
                                                      .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(6).ToList();
        var steps = new List<SkillStep>();
        foreach (var step in skill.Steps ?? new())
        {
            var tool = catalogue.FirstOrDefault(t => t.Name == step.Tool);
            if (tool is null) { notes.Add($"Removed a step that used “{step.Tool}”, which Aqua doesn't have."); continue; }
            var args = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in step.Args ?? new())
                if (tool.Parameters.Contains(k)) args[k] = HtmlText.Truncate(v ?? "", 500);
                else notes.Add($"Dropped “{k}” from {tool.Name} (it doesn't take that).");
            var missing = tool.Required.Where(r => !args.ContainsKey(r) || args[r].Length == 0).ToList();
            if (missing.Count > 0) { notes.Add($"Removed a {tool.Name} step: it needs {string.Join(", ", missing)}."); continue; }
            steps.Add(new SkillStep { Tool = tool.Name, Args = args, Note = HtmlText.Truncate(step.Note ?? "", 160) });
            if (steps.Count >= 8) { notes.Add("Kept the first 8 steps."); break; }
        }
        skill.Steps = steps;
        if (skill.Instructions.Length == 0 && skill.Steps.Count == 0) notes.Add("This skill has no instructions or steps yet — add what Aqua should do.");
        return notes;
    }

    /// <summary>One line per step for people ("Open app · name = Teams").</summary>
    public static string DescribeStep(SkillStep step) =>
        step.Tool + (step.Args.Count > 0 ? " · " + string.Join(", ", step.Args.Select(a => a.Key + " = " + a.Value)) : "") + (step.Note.Length > 0 ? " — " + step.Note : "");
}

/// <summary>
/// Drafts and improves skills from a plain description, using the local model and only the tools Ask has (listed to it
/// with their parameters). The result is validated before you see it and saved only when you say so.
/// </summary>
public sealed class SkillDrafter
{
    private readonly LlmClient _llm;
    public SkillDrafter(LlmClient llm) => _llm = llm;

    internal static JsonObject Schema()
    {
        JsonObject Str() => new() { ["type"] = "string" };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = Str(),
                ["description"] = Str(),
                ["triggers"] = new JsonObject { ["type"] = "array", ["items"] = Str() },
                ["instructions"] = Str(),
                ["parameters"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["name"] = Str(), ["description"] = Str() }, ["required"] = new JsonArray("name") },
                },
                ["steps"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["tool"] = Str(),
                            ["args"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["name"] = Str(), ["value"] = Str() }, ["required"] = new JsonArray("name", "value") },
                            },
                            ["note"] = Str(),
                        },
                        ["required"] = new JsonArray("tool", "args"),
                    },
                },
            },
            ["required"] = new JsonArray("name", "description", "triggers", "instructions", "steps"),
        };
    }

    internal static string Prompt(string request, IReadOnlyCollection<ToolInfo> catalogue, AskSkill? current, string? feedback)
    {
        var sb = new StringBuilder();
        sb.Append("Design a skill for Aqua, a private assistant on the user's Windows PC. A skill says when to use it, how to do it, and optionally fixed steps that call Aqua's tools.\n\n");
        sb.Append("TOOLS AQUA HAS (use only these, with exactly these argument names):\n");
        foreach (var t in catalogue)
            sb.Append("- ").Append(t.Name).Append('(').Append(string.Join(", ", t.Parameters)).Append("): ").Append(HtmlText.Truncate(t.Description, 180)).Append('\n');
        sb.Append("""

            Rules:
            - name: 2 to 5 words. description: when to use it, one sentence. triggers: 3 to 5 different ways the user might ask for it.
            - instructions: what Aqua should do and how to answer, written to Aqua ("Check…, then tell the user…"). Put judgement here.
            - steps: only for fixed actions or lookups that are always the same (open an app, read a page, search files). Leave empty when it needs judgement.
              Arguments may use {name} for a value that changes each time (declare it in parameters) or {input} for the user's whole request.
            - Never invent tools, programs or URLs you aren't sure of. No shell commands. Keep it small.

            """);
        if (current is not null)
        {
            // In the reply's own shape (lower-case keys): the model copies whatever shape it is shown.
            sb.Append("THE CURRENT SKILL (change what the feedback asks; keep the rest):\n").Append(JsonSerializer.Serialize(new
            {
                name = current.Name, description = current.Description, triggers = current.Triggers, instructions = current.Instructions,
                steps = current.Steps.Select(s => new { tool = s.Tool, args = s.Args.Select(a => new { name = a.Key, value = a.Value }), note = s.Note }),
                parameters = current.Parameters.Select(p => new { name = p.Name, description = p.Description }),
            })).Append("\n\n");
        }
        sb.Append("WHAT THE USER WANTS AQUA TO LEARN:\n\"").Append(HtmlText.Truncate(request, 2000)).Append("\"\n");
        if (!string.IsNullOrWhiteSpace(feedback)) sb.Append("\nTHEIR FEEDBACK ON THE CURRENT VERSION:\n\"").Append(HtmlText.Truncate(feedback, 1200)).Append("\"\n");
        return sb.ToString();
    }

    /// <summary>Parses the model's skill (pure; unit-tested). Arguments come as name/value pairs.</summary>
    internal static AskSkill Parse(JsonElement root)
    {
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
        var skill = new AskSkill { Name = Str(root, "name"), Description = Str(root, "description"), Instructions = Str(root, "instructions") };
        if (root.TryGetProperty("triggers", out var tr) && tr.ValueKind == JsonValueKind.Array)
            skill.Triggers = tr.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!.Trim()).ToList();
        if (root.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
            skill.Parameters = ps.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object)
                                 .Select(p => new SkillParameter { Name = Str(p, "name"), Description = Str(p, "description") }).ToList();
        if (root.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array)
            foreach (var s in st.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Object))
            {
                var step = new SkillStep { Tool = Str(s, "tool"), Note = Str(s, "note") };
                if (s.TryGetProperty("args", out var args))
                {
                    if (args.ValueKind == JsonValueKind.Array)
                        foreach (var a in args.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
                        {
                            if (Str(a, "name") is { Length: > 0 } n) step.Args[n] = Str(a, "value");
                        }
                    else if (args.ValueKind == JsonValueKind.Object)
                        foreach (var p in args.EnumerateObject())
                            step.Args[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
                }
                skill.Steps.Add(step);
            }
        return skill;
    }

    /// <summary>Drafts a new skill, or a revised one from <paramref name="current"/> and <paramref name="feedback"/>.</summary>
    public async Task<(AskSkill Skill, List<string> Notes)> DraftAsync(string request, IReadOnlyCollection<ToolInfo> catalogue, AskSkill? current, string? feedback, CancellationToken ct)
    {
        var req = new LlmRequest
        {
            Purpose = "skill-draft",
            Priority = LlmPriority.Interactive,
            System = "You design small, safe skills for a personal assistant. Reply with JSON only. " + Prompts.UntrustedNotice,
            Messages = { new LlmMessage("user", Prompt(request, catalogue, current, feedback)) },
            Schema = Schema(),
            Temperature = 0.2,
            MaxTokens = 1400,
            Think = false,
        };
        var (doc, _) = await _llm.CompleteJsonAsync(req, ct).ConfigureAwait(false);
        using (doc)
        {
            var skill = Parse(doc.RootElement);
            if (current is not null)
            {
                skill.Id = current.Id; skill.Created = current.Created; skill.Uses = current.Uses; skill.LastUsed = current.LastUsed; skill.Enabled = current.Enabled;
            }
            skill.TaughtAs = current?.TaughtAs is { Length: > 0 } taught ? taught : request;
            var notes = Workbench.Validate(skill, catalogue);
            return (skill, notes);
        }
    }
}
