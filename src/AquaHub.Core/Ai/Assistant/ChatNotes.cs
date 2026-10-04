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
    // Notes taken off lately (by Aqua, or a newer message), so an edit started before doesn't bring them back.
    private readonly List<(string Text, DateTimeOffset At)> _removed = new();

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

    /// <summary>A text's numbers in order, written the same way ("€9,000" and "9000" are both 9000).</summary>
    private static IEnumerable<string> Numbers(string text) => NumberRx().Matches(text).Select(m => Regex.Replace(m.Value, @"[.,](?=\d{3}(?:\D|$))", ""));

    /// <summary>
    /// The same note in other words? The same numbers in the same order (a new budget is a change, not a repeat, and
    /// "2 bedrooms and 3 bathrooms" corrects "3 bedrooms and 2 bathrooms") and most of the shorter one's words, three at
    /// least — or simply the same text.
    /// </summary>
    private static bool Same(string a, string b)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        if (!Numbers(a).SequenceEqual(Numbers(b))) return false;
        var x = Words(a);
        var y = Words(b);
        var shared = x.Count(y.Contains);
        return shared >= 3 && shared * 5 >= Math.Min(x.Count, y.Count) * 4;
    }

    /// <summary>A note's words, its numbers written one way ("€9,000" is the word "9000", as "9000 euro" is, not "9" and "000").</summary>
    private static HashSet<string> Words(string text) => TextTools.Signature(Regex.Replace(text, @"(?<=\d)[.,](?=\d{3}(?:\D|$))", ""));

    /// <summary>
    /// A note that says nothing <paramref name="existing"/> doesn't: every word of it is in that note already, and its
    /// numbers too, in the same order ("Heat pump budget: €11,000" beside "Heat pump budget: €11,000, up from €9,000 with a
    /// grant" — live, the model wrote both from one message). "2 bedrooms and 3 bathrooms" after "3 bedrooms and 2
    /// bathrooms" is a correction, not a repeat.
    /// </summary>
    private static bool Within(string existing, string text)
    {
        var words = Words(text);
        if (words.Count < 2 || !words.IsSubsetOf(Words(existing))) return false;
        using var numbers = Numbers(existing).GetEnumerator();
        return Numbers(text).All(n =>
        {
            while (numbers.MoveNext())
                if (numbers.Current == n) return true;
            return false;
        });
    }

    /// <summary>Adds a note; false, with the reason, when it's empty, already there or the notes are full.</summary>
    public bool Add(string kind, string text, string by, DateTimeOffset now, out string why)
    {
        var note = new ChatNote(KindOf(kind) ?? Fact, Tidy(text, by == "you" ? MaxLength : MaxModelLength)) { At = now, By = by };
        lock (_gate)
        {
            if (note.Text.Length == 0) { why = "the note is empty"; return false; }
            if (_items.FirstOrDefault(n => Same(n.Text, note.Text) || Within(n.Text, note.Text)) is { } existing) { why = "it's already noted: “" + existing.Text + "”"; return false; }
            if (_items.Count >= MaxNotes) { why = $"the notes are full ({MaxNotes}); remove one that's settled first"; return false; }
            _items.Add(note);
        }
        why = "";
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Removes the note <paramref name="text"/> means: the same text, the one note holding it as whole words (or held
    /// whole in it), or the one note that says the same in other words. Text that fits several notes ("the", "budget",
    /// "Budget: €4,000" beside two budgets of €4,000) removes none. <paramref name="aquasOnly"/>: only a note Aqua wrote
    /// (the model's update_notes can't take off one you wrote).
    /// </summary>
    public ChatNote? Remove(string text, bool aquasOnly = false)
    {
        var t = Tidy(text, MaxLength);
        if (t.Length == 0) return null;
        ChatNote? gone;
        lock (_gate)
        {
            var items = aquasOnly ? _items.Where(n => n.By != "you").ToList() : _items;
            var holding = items.Where(n => Inside(t, n.Text, 4) || Inside(n.Text, t, 12)).Take(2).ToList();
            gone = items.FirstOrDefault(n => string.Equals(n.Text, t, StringComparison.OrdinalIgnoreCase))
                   ?? (holding.Count == 1 ? holding[0] : null)
                   ?? (items.Where(n => Same(n.Text, t)).Take(2).ToList() is [var same] ? same : null);
            if (gone is null) return null;
            _items.Remove(gone);
            Removed(gone);
        }
        Changed?.Invoke();
        return gone;
    }

    /// <summary>Whether <paramref name="text"/> means a note you wrote (what update_notes is told when it may not remove it).</summary>
    public bool IsYours(string text)
    {
        var t = Tidy(text, MaxLength);
        lock (_gate) return t.Length > 0 && _items.Any(n => n.By == "you" && (string.Equals(n.Text, t, StringComparison.OrdinalIgnoreCase) || Inside(t, n.Text, 4) || Same(n.Text, t)));
    }

    private void Removed(ChatNote note)
    {
        _removed.Add((note.Text, DateTimeOffset.Now));
        if (_removed.Count > 20) _removed.RemoveAt(0);
    }

    /// <summary>
    /// Takes off the note a newer message overturns, as a model quoted it ("Budget for the heat pump: €9,000" for the note
    /// "Budget: €9,000"): as <see cref="Remove"/> does, or else the one note that shares a number and a word with the
    /// quote, or two words. Nothing when two notes fit.
    /// </summary>
    public ChatNote? RemoveOutdated(string text)
    {
        if (Remove(text) is { } removed) return removed;
        var t = Tidy(text, MaxLength);
        if (t.Length == 0) return null;
        var words = TextTools.Signature(t);
        var numbers = NumberRx().Matches(t).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        ChatNote gone;
        lock (_gate)
        {
            var close = _items.Where(n =>
            {
                var shared = TextTools.Signature(n.Text).Count(words.Contains);
                return shared >= 2 || shared >= 1 && NumberRx().Matches(n.Text).Any(m => numbers.Contains(m.Value));
            }).Take(2).ToList();
            if (close.Count != 1) return null;
            gone = close[0];
            _items.Remove(gone);
            Removed(gone);
        }
        Changed?.Invoke();
        return gone;
    }

    /// <summary>Whether <paramref name="part"/> (at least <paramref name="min"/> characters) is in <paramref name="whole"/> as whole words.</summary>
    private static bool Inside(string part, string whole, int min)
    {
        if (part.Length < min) return false;
        for (var i = whole.IndexOf(part, StringComparison.OrdinalIgnoreCase); i >= 0; i = whole.IndexOf(part, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = i + part.Length;
            if ((i == 0 || !char.IsLetterOrDigit(whole[i - 1])) && (end == whole.Length || !char.IsLetterOrDigit(whole[end]))) return true;
        }
        return false;
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
    /// was written — plus any Aqua added while you were typing (since <paramref name="editStarted"/>), so they aren't lost,
    /// and without any it took off meanwhile, which your text still had. Returns how many lines didn't fit
    /// (<see cref="MaxNotes"/> is the limit), for the panel to say so.
    /// </summary>
    public int ApplyEdit(string text, DateTimeOffset editStarted, DateTimeOffset now)
    {
        var before = Items;
        List<string> removedMeanwhile;
        lock (_gate) removedMeanwhile = _removed.Where(r => r.At > editStarted).Select(r => r.Text).ToList();
        var edited = FromText(text, now, before).Where(n => !removedMeanwhile.Contains(n.Text, StringComparer.OrdinalIgnoreCase)).ToList();
        var addedMeanwhile = before.Where(n => n.By == "aqua" && n.At > editStarted && !edited.Any(e => Same(e.Text, n.Text))).ToList();
        var all = edited.Concat(addedMeanwhile).ToList();
        Replace(all);
        return Math.Max(0, all.DistinctBy(n => n.Text.Trim().ToLowerInvariant()).Count() - MaxNotes);
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
/// update_notes: the model keeps the chat's notes — one fact, decision or open question at a time, or removes one of its
/// own that's settled (never one you wrote). Only touches this chat's own notes, which you can see and edit, so it needs
/// no OK; at most six changes an answer.
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
            // Only notes Aqua wrote: a page the answer read, or a confused model, mustn't take off one the user wrote.
            var gone = notes.Remove(remove, aquasOnly: true);
            said.Add(gone is not null ? "Removed “" + gone.Text + "”."
                : notes.IsYours(remove) ? "That note is the user's own: leave it (they can change it themselves)."
                : "No note matched “" + HtmlText.Truncate(remove, 80) + "”.");
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
