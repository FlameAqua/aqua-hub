using System.Text;
using System.Text.RegularExpressions;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// A chat's compression: Aqua's summary standing in for its first messages when it asks the model. The messages
/// themselves stay in the chat; only what's sent changes, and removing the compression (Undo) sends them again.
/// </summary>
public sealed record ChatCompression
{
    public string Summary { get; init; } = "";
    /// <summary>How many of the chat's first messages the summary stands in for.</summary>
    public int Covers { get; init; }
    /// <summary>A fingerprint of those messages: once they change (an edit, Ask again), the summary no longer applies.</summary>
    public string Fingerprint { get; init; } = "";
    /// <summary>Those messages' size and the summary's, in estimated tokens (what compressing saved).</summary>
    public int TokensBefore { get; init; }
    public int TokensAfter { get; init; }
    public DateTimeOffset At { get; init; }
    /// <summary>Done on its own as the chat grew (else with the Compress button).</summary>
    public bool Automatic { get; init; }

    public int Saved => Math.Max(0, TokensBefore - TokensAfter);
}

/// <summary>A message as compression sees it: who wrote it, its text and, for answers, the sources it cited.</summary>
public sealed record ChatTurn(bool User, string Text, IReadOnlyList<Citation>? Citations = null);

/// <summary>
/// Compresses the start of a long chat: the oldest messages — whole exchanges, never the latest few — become a summary
/// that keeps names, numbers, dates, decisions, open questions and sources (titles and links), and the model is sent
/// that instead. A compressed chat compresses again from its summary as it grows. Pure apart from the model call; unit-tested.
/// </summary>
public static partial class ChatCompressor
{
    /// <summary>Messages always sent in full: the last two exchanges.</summary>
    public const int Keep = 4;
    /// <summary>The fewest new messages worth a summary.</summary>
    public const int Least = 2;
    /// <summary>Most text one summary call reads (a longer chat is compressed in steps, oldest first).</summary>
    public const int MaxChars = 24_000;
    /// <summary>Longest part of a single message that goes to the summary.</summary>
    private const int MaxMessageChars = 4000;

    /// <summary>Tokens a text uses, roughly (the same estimate Ask sizes its context window with).</summary>
    public static int Tokens(string text) => (int)Math.Ceiling(text.Length / 3.2);

    public static string Fingerprint(IReadOnlyList<ChatTurn> turns, int count) =>
        Hash.Short(turns.Take(count).Select(t => (t.User ? "U:" : "A:") + t.Text).ToArray());

    /// <summary>The compression if it still matches these messages (null when they changed since, or it never fitted).</summary>
    public static ChatCompression? Valid(ChatCompression? compression, IReadOnlyList<ChatTurn> turns) =>
        compression is { Covers: > 0, Summary.Length: > 0 } c && c.Covers <= turns.Count && Fingerprint(turns, c.Covers) == c.Fingerprint ? c : null;

    /// <summary>
    /// How many of the first messages a compression should stand in for: all but the last <paramref name="keep"/>, ending
    /// after an answer, at least <see cref="Least"/> more than <paramref name="already"/> covers, and no more than one call
    /// can read. 0 when there's nothing worth compressing.
    /// </summary>
    public static int CoverCount(IReadOnlyList<ChatTurn> turns, int already, int keep = Keep)
    {
        var end = turns.Count - keep;
        while (end > already && turns[end - 1].User) end--;
        // One call reads so much: a chat longer than that is compressed in steps, oldest first.
        var chars = 0;
        for (var k = already; k < end; k++)
        {
            chars += Math.Min(turns[k].Text.Length, MaxMessageChars) + 40;
            if (chars <= MaxChars) continue;
            end = k;
            while (end > already && turns[end - 1].User) end--;
            break;
        }
        return end - already >= Least ? end : 0;
    }

    /// <summary>
    /// Whether to compress on its own after an answer: when the newer messages no longer all go with a question — more
    /// of them than Ask sends (<paramref name="historyMessages"/>), or the last answer used most of the context window.
    /// </summary>
    public static bool ShouldCompress(IReadOnlyList<ChatTurn> turns, int covered, int historyMessages, int contextUsed, int contextLimit)
    {
        if (CoverCount(turns, covered) == 0) return false;
        var uncompressed = turns.Count - covered;
        return uncompressed > historyMessages || contextLimit > 0 && contextUsed >= contextLimit * 0.8;
    }

    /// <summary>The request that writes the summary of <paramref name="turns"/> (after <paramref name="previous"/>, the summary so far).</summary>
    public static LlmRequest Request(string previous, IReadOnlyList<ChatTurn> turns, string? model, bool interactive, HubSettings settings, int window)
    {
        var material = new StringBuilder();
        if (previous.Trim().Length > 0) material.Append("SUMMARY OF THE CHAT BEFORE THESE MESSAGES:\n").Append(previous.Trim()).Append("\n\n");
        material.Append("MESSAGES (oldest first):\n");
        foreach (var t in turns)
        {
            material.Append(t.User ? "User: " : "Aqua: ").Append(HtmlText.Truncate(t.Text.Trim(), MaxMessageChars)).Append('\n');
            if (!t.User && t.Citations is { Count: > 0 } cites)
                material.Append("  Sources: ").Append(string.Join("; ", cites.Select(c => $"[{c.Number}] {c.Title}" + (c.Url is { Length: > 0 } u ? " <" + u + ">" : "")))).Append('\n');
            material.Append('\n');
        }
        var before = Tokens(material.ToString());
        var words = Math.Clamp(before / 6, 120, 500);
        var system =
            "You compress the start of a chat between a user and their assistant, Aqua, so the chat can carry on without those messages. " +
            "Keep every name, number, date, price, decision, preference, open question and source (its title and link) that a later answer could need; " +
            "drop greetings, repetition and anything later messages replaced. Write short bullet points under these headings, leaving out any that would be empty: " +
            "What was asked, What Aqua found, Decisions, Open questions. No introduction, and don't address anyone. " + Prompts.UntrustedNotice;
        var messages = new List<LlmMessage> { new("user", "<<<DATA\n" + material + "DATA>>>\n\nWrite the summary now, in at most " + words + " words.") };
        var need = AskAgent.ContextFor(system, messages, settings, words * 2 + 300);
        return new LlmRequest
        {
            Purpose = "ask-compress",
            Priority = interactive ? LlmPriority.Interactive : LlmPriority.Background,
            Model = model,
            System = system,
            Messages = messages,
            Temperature = 0.2,
            MaxTokens = words * 2 + 300,
            Think = false,
            // The chat's own window when the material fits it (a new size makes the model server reload the model).
            ContextTokens = window > 0 && (need ?? 0) <= window ? window : need,
        };
    }

    [GeneratedRegex(@"^\s*(?:#+\s*)?(?:\*\*)?(?:here(?:'s| is) (?:the|a) )?summary(?: of the (?:chat|conversation))?(?:\*\*)?\s*:?\s*(?:\*\*)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SummaryHeading();

    /// <summary>The summary in a reply: narration and a "Summary:" heading or code fence removed; null when it isn't one (too short, or no shorter than what it summarises).</summary>
    public static string? Clean(string reply, int tokensBefore)
    {
        var (answer, _) = AnswerText.Split(reply ?? "");
        var t = answer.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal)) t = Regex.Replace(t, @"^```[\w-]*\s*|\s*```$", "").Trim();
        var heading = SummaryHeading().Match(t);
        if (heading.Success && heading.Index == 0) t = t[heading.Length..].Trim();
        if (t.Length < 40 || Tokens(t) >= tokensBefore * 0.9) return null;
        return t;
    }

    /// <summary>
    /// Compresses the first <paramref name="covers"/> messages — the ones <paramref name="previous"/> didn't cover, on top
    /// of its summary. Null when the model's reply wasn't a usable summary.
    /// </summary>
    public static async Task<ChatCompression?> CompressAsync(LlmClient llm, HubSettings settings, string? model, ChatCompression? previous, IReadOnlyList<ChatTurn> turns,
        int covers, bool automatic, int window, DateTimeOffset now, CancellationToken ct)
    {
        var from = previous?.Covers ?? 0;
        if (covers <= from || covers > turns.Count) return null;
        var fresh = turns.Skip(from).Take(covers - from).ToList();
        var before = (previous?.TokensBefore ?? 0) + fresh.Sum(t => Tokens(t.Text));
        var request = Request(previous?.Summary ?? "", fresh, model, interactive: !automatic, settings, window);
        var result = await llm.CompleteAsync(request, ct).ConfigureAwait(false);
        var summary = Clean(result.Text, before);
        return summary is null ? null : new ChatCompression
        {
            Summary = summary, Covers = covers, Fingerprint = Fingerprint(turns, covers), TokensBefore = before, TokensAfter = Tokens(summary),
            At = now, Automatic = automatic,
        };
    }
}
