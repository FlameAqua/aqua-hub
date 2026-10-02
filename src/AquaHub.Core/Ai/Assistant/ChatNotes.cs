using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AquaHub.Core.Analysis;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>One note in a chat's scratchpad: a fact, a decision or an open question.</summary>
public sealed record ChatNote(string Kind, string Text)
{
    public DateTimeOffset At { get; init; }
    /// <summary>Who wrote it: "aqua" (the model, through update_notes) or "you" (edited beside the chat).</summary>
    public string By { get; init; } = "aqua";
}

/// <summary>
/// A chat's scratchpad: the facts, decisions and open questions that matter for this one chat. They stay with it when
/// older messages are no longer sent, and every answer reads them. The model adds and removes notes with update_notes,
/// and you can edit them beside the chat. Separate from Memory, which is about you across all chats. Thread-safe.
/// </summary>
public sealed partial class ChatNotes
{
    public const string Fact = "fact", Decision = "decision", Question = "question";
    public const int MaxNotes = 60;
    /// <summary>Longest note the model may write (yours may be longer).</summary>
    public const int MaxModelLength = 400;
    public const int MaxLength = 1000;
    /// <summary>Most of the notes' text that goes into a prompt.</summary>
    public const int PromptChars = 6000;

    private readonly object _gate = new();
    private List<ChatNote> _items;

    public ChatNotes(IEnumerable<ChatNote>? items = null) => _items = Clean(items ?? Array.Empty<ChatNote>()).ToList();

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event Action? Changed;

    public IReadOnlyList<ChatNote> Items { get { lock (_gate) return _items.ToList(); } }
    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>"facts", "Decided", "open question", "todo" … as fact, decision or question (null when it's none of them).</summary>
    public static string? KindOf(string? kind)
    {
        var k = (kind ?? "").Trim().TrimEnd(':', 's', 'S').Trim().ToLowerInvariant();
        return k switch
        {
            "" or "fact" or "note" or "detail" or "info" => Fact,
            "decision" or "decided" or "choice" or "plan" => Decision,
            "question" or "open question" or "open" or "todo" or "to do" or "to-do" or "unanswered" => Question,
            _ => null,
        };
    }

    private static string Tidy(string text, int max) => HtmlText.Truncate(Regex.Replace(text.Trim().TrimStart('-', '*', '•', ' '), @"\s+", " "), max);

    private static IEnumerable<ChatNote> Clean(IEnumerable<ChatNote> notes) =>
        notes.Select(n => n with { Kind = KindOf(n.Kind) ?? Fact, Text = Tidy(n.Text, MaxLength) })
             .Where(n => n.Text.Length > 0).DistinctBy(n => n.Text.ToLowerInvariant()).Take(MaxNotes);

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberRx();

    /// <summary>
    /// The same note in other words? The same numbers (a new budget is a change, not a repeat) and most of the shorter
    /// one's words, three at least — or simply the same text.
    /// </summary>
    private static bool Same(string a, string b)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var numbers = NumberRx().Matches(a).Select(m => m.Value).Order().SequenceEqual(NumberRx().Matches(b).Select(m => m.Value).Order());
        if (!numbers) return false;
        var x = TextTools.Signature(a);
        var y = TextTools.Signature(b);
        var shared = x.Count(y.Contains);
        return shared >= 3 && shared * 5 >= Math.Min(x.Count, y.Count) * 4;
    }

    /// <summary>Adds a note; false, with the reason, when it's empty, already there or the notes are full.</summary>
    public bool Add(string kind, string text, string by, DateTimeOffset now, out string why)
    {
        var note = new ChatNote(KindOf(kind) ?? Fact, Tidy(text, by == "you" ? MaxLength : MaxModelLength)) { At = now, By = by };
        lock (_gate)
        {
            if (note.Text.Length == 0) { why = "the note is empty"; return false; }
            if (_items.FirstOrDefault(n => Same(n.Text, note.Text)) is { } existing) { why = "it's already noted: “" + existing.Text + "”"; return false; }
            if (_items.Count >= MaxNotes) { why = $"the notes are full ({MaxNotes}); remove one that's settled first"; return false; }
            _items.Add(note);
        }
        why = "";
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes the note <paramref name="text"/> means: the same text, one containing it, or the closest in words.</summary>
    public ChatNote? Remove(string text)
    {
        var t = Tidy(text, MaxLength);
        if (t.Length == 0) return null;
        ChatNote? gone;
        lock (_gate)
        {
            gone = _items.FirstOrDefault(n => string.Equals(n.Text, t, StringComparison.OrdinalIgnoreCase))
                   ?? _items.FirstOrDefault(n => n.Text.Contains(t, StringComparison.OrdinalIgnoreCase) || t.Contains(n.Text, StringComparison.OrdinalIgnoreCase) && n.Text.Length >= 12)
                   ?? _items.FirstOrDefault(n => Same(n.Text, t));
            if (gone is null) return null;
            _items.Remove(gone);
        }
        Changed?.Invoke();
        return gone;
    }

    /// <summary>Replaces every note (an edit beside the chat).</summary>
    public void Replace(IEnumerable<ChatNote> notes)
    {
        var clean = Clean(notes).ToList();
        lock (_gate)
        {
            if (clean.Count == _items.Count && clean.Zip(_items).All(p => p.First == p.Second)) return;
            _items = clean;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies what you typed beside the chat: your text becomes the notes, keeping when and by whom each unchanged note
    /// was written — plus any Aqua added while you were typing (since <paramref name="editStarted"/>), so they aren't lost.
    /// </summary>
    public void ApplyEdit(string text, DateTimeOffset editStarted, DateTimeOffset now)
    {
        var before = Items;
        var edited = FromText(text, now, before);
        var addedMeanwhile = before.Where(n => n.By == "aqua" && n.At > editStarted && !edited.Any(e => Same(e.Text, n.Text)));
        Replace(edited.Concat(addedMeanwhile));
    }

    private static readonly (string Kind, string Heading)[] Sections = { (Fact, "Facts"), (Decision, "Decisions"), (Question, "Open questions") };

    /// <summary>The notes as text to read and edit: a heading for each kind that has notes, then a line per note.</summary>
    public static string ToText(IReadOnlyList<ChatNote> notes)
    {
        var sb = new StringBuilder();
        foreach (var (kind, heading) in Sections)
        {
            var these = notes.Where(n => n.Kind == kind).ToList();
            if (these.Count == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(heading).Append('\n');
            foreach (var n in these) sb.Append("- ").Append(n.Text).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"^\s*#*\s*(?<heading>facts?|decisions?|(?:open\s+)?questions?|to-?\s?dos?)\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingRx();

    /// <summary>
    /// Notes from their text: "Facts", "Decisions" and "Open questions" headings say what the lines under them are (lines
    /// before any heading are facts); each other line is a note, its bullet optional. Notes that didn't change keep when
    /// and by whom they were written; new or changed ones are yours.
    /// </summary>
    public static List<ChatNote> FromText(string text, DateTimeOffset now, IReadOnlyList<ChatNote>? before = null)
    {
        var notes = new List<ChatNote>();
        var kind = Fact;
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            if (HeadingRx().Match(raw) is { Success: true } h)
            {
                kind = KindOf(h.Groups["heading"].Value) ?? Fact;
                continue;
            }
            var line = Tidy(raw, MaxLength);
            if (line.Length == 0) continue;
            var same = before?.FirstOrDefault(n => n.Kind == kind && string.Equals(n.Text, line, StringComparison.Ordinal));
            notes.Add(same ?? new ChatNote(kind, line) { At = now, By = "you" });
        }
        return notes;
    }

    /// <summary>The notes for a prompt (empty when there are none).</summary>
    public string ForPrompt()
    {
        var items = Items;
        if (items.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var (kind, heading) in Sections)
        {
            var these = items.Where(n => n.Kind == kind).ToList();
            if (these.Count == 0) continue;
            sb.Append(heading).Append(":\n");
            foreach (var n in these)
            {
                if (sb.Length + n.Text.Length > PromptChars) { sb.Append("- (more notes left out for room)\n"); return sb.ToString(); }
                sb.Append("- ").Append(n.Text).Append('\n');
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// update_notes: the model keeps the chat's notes — one fact, decision or open question at a time, or removes one that's
/// settled. Only touches this chat's own notes, which you can see and edit, so it needs no OK; at most six changes an answer.
/// </summary>
public sealed class UpdateNotesTool : AskTool
{
    public const int MaxChangesPerAnswer = 6;

    public override string Name => "update_notes";
    public override string Description =>
        "Keep this chat's notes: a fact worth keeping (names, numbers, what the user wants in this chat), a decision made, or a question left open. " +
        "They stay with the chat after older messages are no longer sent, and you see them in the CONTEXT every turn. One note per call; " +
        "remove a note that's settled or wrong. Don't repeat what's already noted, and don't note things about the user in general (that's Memory).";
    public override JsonObject Parameters => Schema(
        ("add", "string", "A short note to add, e.g. \"Budget for the heat pump: €9,000\"", false),
        ("kind", "string", "fact, decision or question (for add)", false),
        ("remove", "string", "The text of a note to remove (settled, answered or wrong)", false));
    public override string Icon => "note";
    public override string Describe(JsonElement args) =>
        Arg(args, "add") is { Length: > 0 } add ? "Noted " + Quote(add)
        : Arg(args, "remove") is { Length: > 0 } remove ? "Took " + Quote(remove) + " off the notes"
        : "Updated the notes";

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        if (run.Notes is not { } notes) return Task.FromResult(ToolResult.Fail("this chat has no notes"));
        var changes = run.Bag.TryGetValue("notes:changes", out var n) ? (int)n : 0;
        if (changes >= MaxChangesPerAnswer) return Task.FromResult(ToolResult.Fail($"that's {MaxChangesPerAnswer} changes to the notes for this answer — answer now"));
        var add = Arg(args, "add");
        var remove = Arg(args, "remove");
        if (add.Length == 0 && remove.Length == 0) return Task.FromResult(ToolResult.Fail("give a note to add or one to remove"));
        var said = new List<string>();
        if (remove.Length > 0)
        {
            var gone = notes.Remove(remove);
            said.Add(gone is null ? "No note matched “" + HtmlText.Truncate(remove, 80) + "”." : "Removed “" + gone.Text + "”.");
            if (gone is not null) changes++;
        }
        if (add.Length > 0)
        {
            if (notes.Add(Arg(args, "kind"), add, "aqua", run.Now, out var why)) { said.Add("Noted."); changes++; }
            else said.Add("Not added: " + why + ".");
        }
        run.Bag["notes:changes"] = changes;
        return Task.FromResult(new ToolResult(string.Join(" ", said) + $" The chat has {Plural.Of(notes.Count, "note")}.",
            said.Count > 0 && said[^1].StartsWith("Not ", StringComparison.Ordinal) ? "already there" : Plural.Of(notes.Count, "note")));
    }
}
