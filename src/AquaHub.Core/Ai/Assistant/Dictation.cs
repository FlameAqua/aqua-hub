using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>One phrase the speech recognizer heard, how sure it was (0–1) and the other wordings it considered.</summary>
public sealed record HeardPhrase(string Text, double Confidence, IReadOnlyList<string> Alternates);

/// <summary>
/// Which of a recognizer's languages to dictate in. Windows only offers some varieties (English (Ireland) usually isn't
/// one), and asking for a missing one fails with "The requested language is not supported" — so the nearest offered
/// variety is used instead. Pure; unit-tested.
/// </summary>
public static class SpeechLanguage
{
    // Close relatives to try, in order, when a recognizer doesn't offer your exact variety.
    private static readonly Dictionary<string, string[]> Near = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en-IE"] = new[] { "en-GB" }, ["en-MT"] = new[] { "en-GB" }, ["en-ZA"] = new[] { "en-GB" }, ["en-IN"] = new[] { "en-GB" },
        ["en-NZ"] = new[] { "en-AU", "en-GB" }, ["en-SG"] = new[] { "en-GB" }, ["en-HK"] = new[] { "en-GB" },
        ["en-CA"] = new[] { "en-US" }, ["en-PH"] = new[] { "en-US" },
        ["fr-BE"] = new[] { "fr-FR" }, ["fr-CH"] = new[] { "fr-FR" }, ["fr-LU"] = new[] { "fr-FR" }, ["fr-CA"] = new[] { "fr-FR" },
        ["de-AT"] = new[] { "de-DE" }, ["de-CH"] = new[] { "de-DE" }, ["de-LU"] = new[] { "de-DE" },
        ["es-US"] = new[] { "es-MX", "es-ES" }, ["es-AR"] = new[] { "es-MX", "es-ES" }, ["es-CO"] = new[] { "es-MX", "es-ES" }, ["es-CL"] = new[] { "es-MX", "es-ES" },
        ["pt-PT"] = new[] { "pt-BR" }, ["pt-BR"] = new[] { "pt-PT" },
        ["it-CH"] = new[] { "it-IT" }, ["nl-BE"] = new[] { "nl-NL" }, ["sv-FI"] = new[] { "sv-SE" },
        ["zh-HK"] = new[] { "zh-TW", "zh-CN" }, ["zh-SG"] = new[] { "zh-CN" },
    };

    /// <summary>
    /// The offered language to use for <paramref name="wanted"/>: the exact one; else Windows' own speech language when
    /// it is the same language; else a close relative (en-IE → en-GB); else any variety of the same language. Null when
    /// nothing offered speaks it.
    /// </summary>
    public static string? Pick(string wanted, IEnumerable<string> offered, string? system)
    {
        wanted = wanted.Trim().Replace('_', '-');
        var list = offered.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? Find(string tag) => list.FirstOrDefault(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (list.Count == 0 || wanted.Length == 0) return null;
        if (Find(wanted) is { } exact) return exact;
        var language = Primary(wanted);
        if (system is { Length: > 0 } && Primary(system) == language && Find(system) is { } own) return own;
        if (Near.TryGetValue(wanted, out var near))
            foreach (var tag in near)
                if (Find(tag) is { } relative) return relative;
        return list.FirstOrDefault(t => Primary(t) == language);
    }

    private static string Primary(string tag) => tag.Replace('_', '-').Split('-')[0].ToLowerInvariant();
}

/// <summary>
/// "Tidy up what I say": when the speech recognizer was unsure, the local model corrects words it misheard — only where
/// the message as written doesn't make sense — preferring the other wordings the recognizer considered. Most dictation is
/// right, so a sentence that reads naturally is left exactly as it is. It never answers or rewrites: a reply that looks
/// like an answer, drops part of what was said, or has little to do with it is thrown away and the words stay as heard.
/// </summary>
public static partial class DictationTidy
{
    // No example mishearings here: given "whether" → "weather", a model "fixes" every "whether" it sees.
    internal const string Instructions = """
        You fix speech-recognition mistakes in a message someone dictated to their AI assistant. The recognizer sometimes
        writes a word or a few words that only sound like what was said — but most of what it writes is right.
        Change words only where the message as written doesn't make sense and words that sound alike would make it make
        sense; when the recognizer's other guesses are listed, prefer them to your own. A sentence that reads naturally stays
        exactly as it is, even if other words would sound the same. Leave names, numbers and unusual words as they are.
        You may add punctuation and capital letters and drop fillers like "um". Keep their language, their order and their
        meaning; never answer the message or add anything. If nothing needs fixing, return it unchanged.
        """;

    /// <summary>Longer dictations are left as heard: tidying part of one would cut its end.</summary>
    public const int MaxChars = 1500, MaxPhrases = 12;

    /// <summary>
    /// Why this dictation isn't worth tidying, or null when it is: the recognizer was sure of every phrase (0.8 or more —
    /// Windows online speech's "high"), or it's too long to send whole.
    /// </summary>
    public static string? Skip(IReadOnlyList<HeardPhrase> heard)
    {
        if (heard.Count == 0 || Joined(heard).Length == 0) return "nothing was heard";
        if (Joined(heard).Length > MaxChars || heard.Count > MaxPhrases) return "long dictations are left as heard";
        return heard.All(h => h.Confidence >= 0.8) ? "the recognizer was sure of every word" : null;
    }

    internal static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("text"),
    };

    /// <summary>
    /// A "phrase" that is really background noise: a very unsure guess, or a lone short word the recognizer wasn't
    /// sure of ("the", "if"). Anything longer is kept, however unsure — the tidy-up can fix it; dropping it loses words.
    /// </summary>
    public static bool IsNoise(string text, double confidence)
    {
        var words = WordRx().Matches(text).Count;
        return confidence < 0.1 || (confidence < 0.3 && words <= 1 && text.Trim().Length <= 4);
    }

    /// <summary>The words as heard, one phrase after another.</summary>
    public static string Joined(IReadOnlyList<HeardPhrase> heard) =>
        Regex.Replace(string.Join(" ", heard.Select(h => h.Text.Trim()).Where(t => t.Length > 0)), @"\s+", " ");

    internal static string Prompt(IReadOnlyList<HeardPhrase> heard)
    {
        // Whole: Skip() keeps anything too long from getting here. With other guesses, each phrase lists them all, best
        // first — shown as mere footnotes, the model keeps the first.
        if (heard.All(h => h.Alternates.Count == 0))
            return "Heard: \"" + Joined(heard) + "\"\n" + Unsure(heard);
        var sb = new StringBuilder("What the recognizer heard, phrase by phrase, with its guesses best first. Write the message they " +
                                   "most likely said: keep each phrase's first guess when it makes sense, otherwise take the guess that does:\n");
        var n = 0;
        foreach (var h in heard)
        {
            sb.Append(++n).Append(". \"").Append(h.Text.Trim()).Append('"');
            foreach (var a in h.Alternates.Take(3)) sb.Append("  or  \"").Append(HtmlText.Truncate(a, 300)).Append('"');
            sb.Append('\n');
        }
        return sb.Append(Unsure(heard)).ToString();
    }

    private static string Unsure(IReadOnlyList<HeardPhrase> heard)
    {
        var unsure = heard.Where(h => h.Confidence is > 0 and < 0.35).Select(h => h.Text).Take(4).ToList();
        return unsure.Count == 0 ? "" : "The recognizer was unsure of: " + string.Join("; ", unsure.Select(u => "\"" + HtmlText.Truncate(u, 120) + "\"")) + "\n";
    }

    /// <summary>
    /// The model's correction when it is one, else null (keep the words as heard): not empty, not much longer or
    /// shorter, not an answer ("Sure, …"), and about the same words or sounds.
    /// </summary>
    public static string? Accept(string heard, string? tidied)
    {
        var text = Regex.Replace((tidied ?? "").Trim().Trim('"', '“', '”').Trim(), @"\s+", " ");
        heard = heard.Trim();
        if (text.Length == 0 || heard.Length == 0) return null;
        // Fillers and punctuation change the length a little; a reply much shorter has lost part of what was said.
        if (text.Length > heard.Length * 1.6 + 30 || text.Length < heard.Length * 0.7 - 10) return null;
        if (AnswerRx().IsMatch(text) && !AnswerRx().IsMatch(heard)) return null;
        if (text.Contains('\n')) return null;
        var before = Words(heard);
        if (before.Count >= 4 && Overlap(before, Words(text)) < 0.3 && Similarity(Letters(heard), Letters(text)) < 0.55) return null;
        return text;
    }

    /// <summary>Asks the model; null when it isn't there, is too slow, or its reply isn't a correction.</summary>
    public static async Task<string?> TidyAsync(LlmClient llm, IReadOnlyList<HeardPhrase> heard, CancellationToken ct)
    {
        var said = Joined(heard);
        if (Skip(heard) is { } reason)
        {
            Log.Debug("voice", "Tidy-up not needed: " + reason);
            return null;
        }
        try
        {
            var (json, _) = await llm.CompleteJsonAsync(new LlmRequest
            {
                Purpose = "dictation-tidy",
                Priority = LlmPriority.Interactive,
                System = Instructions,
                Messages = { new LlmMessage("user", Prompt(heard)) },
                Schema = Schema(),
                Temperature = 0.1,
                MaxTokens = Math.Clamp(said.Length / 2 + 80, 120, 900),
                Think = false,
            }, ct).ConfigureAwait(false);
            using (json)
            {
                var text = json.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var accepted = Accept(said, text);
                Log.Debug("voice", accepted is null ? $"Tidy-up kept the words as heard ({said.Length} chars)" : $"Tidy-up changed {said.Length} → {accepted.Length} chars");
                return accepted;
            }
        }
        catch (Exception ex) when (ex is LlmUnavailableException or FormatException or HttpRequestException or OperationCanceledException or JsonException)
        {
            Log.Debug("voice", "Tidy-up skipped: " + ex.Message);
            return null;
        }
    }

    private static readonly string[] Small = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

    private static HashSet<string> Words(string text) => WordRx().Matches(text.ToLowerInvariant())
        .Select(m => int.TryParse(m.Value, out var n) && n is >= 0 and <= 10 ? Small[n] : m.Value).ToHashSet();

    private static double Overlap(HashSet<string> before, HashSet<string> after) =>
        before.Count == 0 ? 1 : before.Count(after.Contains) / (double)before.Count;

    private static string Letters(string text)
    {
        var s = new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return s.Length > 600 ? s[..600] : s;
    }

    /// <summary>1 − edit distance ÷ the longer length: how alike two strings sound when written out.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 1 : 0;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1 - previous[b.Length] / (double)Math.Max(a.Length, b.Length);
    }

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordRx();
    [GeneratedRegex(@"^(?:sure\b|certainly\b|of course\b|here(?:'s| is| are)\b|i (?:can|can't|cannot|am|'m|will|would)\b|as an ai\b|okay,? here\b|the answer\b)", RegexOptions.IgnoreCase)]
    private static partial Regex AnswerRx();
}
