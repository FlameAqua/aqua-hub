using System.Runtime.CompilerServices;
using System.Text;
using AquaHub.Core.Agents;
using AquaHub.Core.Analysis;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai;

/// <summary>A numbered source an answer can cite as [n]. Kind: news | social | hub | web | file.</summary>
public sealed record Citation(int Number, string Title, string Source, string? Url, string Kind = "news");

/// <summary>Finds and tidies [n] citation markers in model output.</summary>
public static partial class CitationText
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d{1,2}(?:\s*[,;–-]\s*\d{1,2})+)\]")]
    private static partial System.Text.RegularExpressions.Regex Grouped();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial System.Text.RegularExpressions.Regex Single();

    /// <summary>"[PAGE 5]", "[Source 5]": the model copying the context's labels.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:page|source|file|link|result|item|ref)\s*#?\s*(\d{1,2})\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Labelled();

    /// <summary>"[1, 3]" → "[1][3]", "[2-4]" → "[2][3][4]" and "[PAGE 5]" → "[5]", so each number becomes its own link.</summary>
    public static string Normalize(string text) => Grouped().Replace(Labelled().Replace(text, "[$1]"), m =>
    {
        var sb = new StringBuilder();
        var parts = System.Text.RegularExpressions.Regex.Split(m.Groups[1].Value, @"\s*([,;–-])\s*");
        var prev = -1;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var n)) continue;
            var range = i >= 2 && parts[i - 1] is "-" or "–" && prev > 0 && n > prev && n - prev <= 8;
            if (range) for (var k = prev + 1; k <= n; k++) sb.Append('[').Append(k).Append(']');
            else sb.Append('[').Append(n).Append(']');
            prev = n;
        }
        return sb.ToString();
    });

    public static HashSet<int> Numbers(string text)
    {
        var set = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match m in Single().Matches(Normalize(text)))
            set.Add(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        return set;
    }
}

public sealed record AskContext(string Text, List<Citation> Citations);

/// <summary>
/// "Ask Aqua": retrieval-augmented Q&A over everything the hub has ingested (FTS5/BM25 retrieval +
/// the live situation). Runs entirely on the local model; the answering model has no tools, so
/// retrieved web content cannot trigger actions.
/// </summary>
public sealed class AskService
{
    private readonly HubDatabase _db;
    private readonly HubState _state;
    private readonly LlmClient _llm;
    private readonly Func<HubSettings> _settings;

    public AskService(HubDatabase db, HubState state, LlmClient llm, Func<HubSettings> settings)
    {
        _db = db; _state = state; _llm = llm; _settings = settings;
    }

    /// <summary>Question words that say what kind of answer is wanted, not what it's about.</summary>
    private static readonly HashSet<string> AskingWords = new(StringComparer.Ordinal)
    {
        "tell", "show", "give", "happen", "happening", "going", "anything", "something", "think", "know", "expect", "predict",
        "market", "agenda", "calendar", "schedule", "weather", "today", "tomorrow", "next", "please", "latest", "news", "whats",
        "coming", "upcoming", "chance", "odd", "likely", "forecast",
    };

    /// <summary>
    /// The answer when no model is available: what the agents already hold — your agenda, the crowd's odds, markets,
    /// weather — for questions about those, plus news items that match the question (cited), instead of an error.
    /// </summary>
    public string OfflineAnswer(string question, AskContext context, string reason, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var s = _settings();
        var q = " " + TextTools.Fold(question).ToLowerInvariant() + " ";
        bool Asks(params string[] words) => words.Any(w => q.Contains(w, StringComparison.Ordinal));
        var sb = new StringBuilder();

        if (Asks("agenda", "calendar", "schedule", "this week", "today", "tomorrow", "meeting", "event", "on my", "coming up", "upcoming", "diary"))
        {
            var events = _state.Events.Where(e => e.Start >= at.AddHours(-1) && e.Start <= at.AddDays(7)).Take(8).ToList();
            if (events.Count > 0)
            {
                sb.Append("**Your agenda, next 7 days**\n");
                foreach (var e in events)
                    sb.Append("- ").Append(e.Start.ToLocalTime().ToString(e.AllDay ? "ddd d MMM" : "ddd d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture))
                      .Append(" · ").Append(e.Title).Append('\n');
                sb.Append('\n');
            }
        }
        if (Asks("predict", "odds", "chance", "likely", "forecast", "expect", "crowd", " bet", "prediction market"))
        {
            var markets = _state.Predictions.Where(m => m.Lead is not null).Take(6).ToList();
            if (markets.Count > 0)
            {
                sb.Append("**What the crowd expects**\n");
                foreach (var m in markets)
                    sb.Append("- ").Append(m.Title.TrimEnd('?')).Append(": ").Append(m.Lead!.Label).Append(' ')
                      .Append((m.Lead.Probability * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture)).Append("%\n");
                sb.Append('\n');
            }
        }
        if (Asks("market", "stock", "shares", "portfolio", "watchlist", "index", "invest", "s&p", "nasdaq") ||
            s.Markets.Watchlist.Any(w => q.Contains(" " + w.Symbol.ToLowerInvariant()) || (w.Name.Length > 2 && q.Contains(w.Name.ToLowerInvariant()))))
        {
            var lines = Fallbacks.MarketBullets(_state, s.Markets, at);
            if (lines.Count > 0)
            {
                sb.Append("**Markets**\n");
                foreach (var l in lines) sb.Append("- ").Append(l).Append('\n');
                sb.Append('\n');
            }
        }
        if (Asks("weather", "rain", "temperature", "umbrella", " cold", " warm", "sunny", " wind", "forecast for"))
        {
            var lines = Fallbacks.WeatherBullets(_state.Weather, at);
            if (lines.Count > 0)
            {
                sb.Append("**Weather**\n");
                foreach (var l in lines) sb.Append("- ").Append(l).Append('\n');
                sb.Append('\n');
            }
        }

        // News: only items that match the question's own words (two of them, or its only one) — not every item
        // that happens to contain "week" or "next".
        var terms = TextTools.Signature(question).Where(t => !AskingWords.Contains(t)).ToList();
        var needed = Math.Min(2, terms.Count);
        var cited = needed == 0 ? new List<Citation>() :
            context.Citations.Where(c => { var title = TextTools.Signature(c.Title); return terms.Count(t => title.Contains(t)) >= needed; }).Take(6).ToList();
        if (cited.Count > 0)
        {
            sb.Append("**From the news**\n");
            foreach (var c in cited) sb.Append("- ").Append(c.Title).Append(" — ").Append(c.Source).Append(" [").Append(c.Number).Append("]\n");
            sb.Append('\n');
        }

        if (sb.Length == 0)
            return $"I couldn't find anything about that in your news, agenda or markets, and {reason} to answer from general knowledge. " +
                   "Try other words, or ask again once the model is back.";
        return char.ToUpperInvariant(reason[0]) + reason[1..] + ", so here is what your agents have on this:\n\n" + sb +
               "Ask again when the model is back for a written answer.";
    }

    /// <summary>
    /// The quick-answer context (command palette, offline answers): the situation plus the items across all your feeds
    /// that match the question — stories with their summaries, articles, posts, odds, agenda and quotes — each citable.
    /// </summary>
    public AskContext BuildContext(string question)
    {
        var s = _settings();
        var sb = new StringBuilder();
        sb.Append(Digest.Situation(_state, s)).Append('\n');
        var book = new Assistant.SourceBook();
        var hits = Assistant.HubSearch.Search(_state, _db, question, "any", 8);
        if (hits.Count > 0) sb.Append("RETRIEVED ITEMS:\n").Append(Assistant.HubSearch.Format(hits, book));
        // A single matching story gets its full coverage: every outlet's headline and snippet.
        if (hits.FirstOrDefault(h => h.Kind == "story") is { } top && Assistant.HubSearch.FindStory(_state, top.Id) is { } story &&
            hits.Count(h => h.Kind == "story") == 1)
            sb.Append("\nSTORY DETAIL:\n").Append(Assistant.HubSearch.Story(story, book));

        var q = question.ToLowerInvariant();
        if (q.Contains("market") || q.Contains("stock") || q.Contains("invest") || q.Contains("portfolio") ||
            s.Markets.Watchlist.Any(w => q.Contains(w.Symbol.ToLowerInvariant()) || (w.Name.Length > 2 && q.Contains(w.Name.ToLowerInvariant()))))
        {
            if (_state.MarketBrief is { } mb)
            {
                sb.Append("\nMARKET BRIEF: ").Append(mb.Overview).Append('\n');
                foreach (var i in mb.Insights) sb.Append("- ").Append(i.Symbol).Append(": ").Append(i.Stance).Append(" — ").Append(i.Summary).Append('\n');
            }
            foreach (var (sym, ind) in _state.Indicators.Take(12))
                sb.Append("- ").Append(sym).Append(" technicals: ").Append(ind.Trend).Append(", score ").Append(ind.TechScore)
                  .Append(ind.Rsi14 is { } r ? $", RSI {r:0}" : "").Append('\n');
        }
        if (q.Contains("predict") || q.Contains("odds") || q.Contains("chance") || q.Contains("likely") || q.Contains("forecast"))
        {
            sb.Append("\nPREDICTION MARKETS:\n");
            foreach (var m in _state.Predictions.Take(10))
                sb.Append("- ").Append(m.Title).Append(": ").Append(string.Join("; ", m.Outcomes.Take(3).Select(o => $"{o.Label} {o.Probability * 100:0}%"))).Append('\n');
        }
        if (q.Contains("social") || q.Contains("reddit") || q.Contains("people") || q.Contains("talking") || q.Contains("local"))
        {
            if (_state.Pulse is { } p)
            {
                sb.Append("\nSOCIAL PULSE: ").Append(p.Overview).Append('\n');
                foreach (var t in p.Topics) sb.Append("- ").Append(t.Title).Append(": ").Append(t.Summary).Append('\n');
            }
        }
        return new AskContext(sb.ToString(), book.All.ToList());
    }

    public async IAsyncEnumerable<string> AskAsync(string question, IReadOnlyList<LlmMessage> history, AskContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var s = _settings();
        var request = new LlmRequest
        {
            Purpose = "ask",
            Priority = LlmPriority.Interactive,
            System = Prompts.AskSystem(s) + "\n\n" + Prompts.UntrustedNotice + "\n\nCONTEXT:\n<<<DATA\n" + context.Text + "\nDATA>>>",
            Messages = history.TakeLast(6).Append(new LlmMessage("user", question)).ToList(),
            Temperature = 0.35,
            MaxTokens = 900,
        };
        await foreach (var piece in _llm.StreamAsync(request, ct).ConfigureAwait(false))
            yield return piece;
    }
}
