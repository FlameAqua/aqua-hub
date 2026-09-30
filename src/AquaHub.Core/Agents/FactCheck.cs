using System.Globalization;
using System.Text.RegularExpressions;
using AquaHub.Core.Analysis;
using AquaHub.Core.Models;

namespace AquaHub.Core.Agents;

/// <summary>
/// Deterministic checks of what a small local model writes against the data it was given: numbers it quotes must
/// exist in its inputs, and when it names the crowd's favourite outcome that must be the leading one. Where a check
/// fails, the sentence is replaced with a plain statement of the data.
/// </summary>
public static partial class FactCheck
{
    [GeneratedRegex(@"(\d{1,3}(?:\.\d+)?)\s?(?:%|per ?cent)", RegexOptions.IgnoreCase)]
    private static partial Regex Percentages();

    [GeneratedRegex(@"\b(favou?r|expect|most likely|likeliest|leading|front-?runner|odds-on)", RegexOptions.IgnoreCase)]
    private static partial Regex FavouriteClaim();

    /// <summary>Percentages in <paramref name="text"/> that match none of <paramref name="allowed"/> (within <paramref name="tolerance"/> points).</summary>
    public static List<double> UnsupportedPercentages(string text, IReadOnlyCollection<double> allowed, double tolerance = 1.5)
    {
        var bad = new List<double>();
        foreach (Match m in Percentages().Matches(text))
        {
            var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!allowed.Any(a => Math.Abs(a - v) <= tolerance)) bad.Add(v);
        }
        return bad;
    }

    /// <summary>Every percentage the Foresight prompt shows the model: outcome odds, their 24h moves, and figures in event details.</summary>
    public static List<double> ForesightInputs(IEnumerable<HubEvent> events, IEnumerable<PredictionMarket> markets)
    {
        var values = new List<double>();
        foreach (var m in markets)
            foreach (var o in m.Outcomes)
            {
                values.Add(Math.Round(o.Probability * 100));
                if (m.IsBinary) values.Add(Math.Round(100 - o.Probability * 100));
                if (o.Change24h is { } c) values.Add(Math.Round(Math.Abs(c) * 100));
            }
        foreach (var e in events)
            foreach (Match m in Percentages().Matches(e.Title + " " + e.Detail))
                values.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return values;
    }

    /// <summary>
    /// True when the text claims a favourite that isn't the market's leading outcome
    /// ("the crowd favours no change" while a hike leads at 65%). Only multi-outcome markets have named outcomes.
    /// </summary>
    public static bool MisnamesFavourite(string text, PredictionMarket market)
    {
        if (market.IsBinary || market.Lead is not { } lead || !FavouriteClaim().IsMatch(text)) return false;
        static bool Mentions(string text, string label) =>
            label.Length > 2 && Regex.IsMatch(text, @"\b" + Regex.Escape(label) + @"\b", RegexOptions.IgnoreCase);
        return !Mentions(text, lead.Label) && market.Outcomes.Any(o => !ReferenceEquals(o, lead) && Mentions(text, o.Label));
    }

    /// <summary>
    /// Corrects a model-written outlook. A figure has to belong to the market its sentence is clearly about ("97%"
    /// must not borrow another market's odds). An item whose own title clearly names a market gets a plain statement
    /// of that market's leading outcome; otherwise only the unsupported sentences are removed — the checker never
    /// substitutes a market it isn't sure about.
    /// </summary>
    public static (Foresight Result, int Fixes) Foresight(Foresight f, IReadOnlyList<HubEvent> events, IReadOnlyList<PredictionMarket> markets, string fallbackOverview)
    {
        var allowed = ForesightInputs(events, markets);
        List<double> AllowedFor(PredictionMarket? m) => m is null ? allowed : ForesightInputs(events, new[] { m });
        bool SentenceWrong(string sentence)
        {
            var m = MatchMarket(sentence, markets, out _);
            return UnsupportedPercentages(sentence, AllowedFor(m)).Count > 0 || (m is not null && MisnamesFavourite(sentence, m));
        }

        var fixes = 0;
        var overview = f.Overview;
        if (Sentences(overview).Any(SentenceWrong))
        {
            overview = fallbackOverview;
            fixes++;
        }
        var kept = new List<ForesightItem>();
        foreach (var item in f.Items)
        {
            var text = item.Title + " " + item.Detail;
            var market = MatchMarket(text, markets, out _);
            var wrongNumbers = UnsupportedPercentages(text, AllowedFor(market)).Count > 0;
            var wrongFavourite = market is not null && MisnamesFavourite(text, market);
            if (!wrongNumbers && !wrongFavourite) { kept.Add(item); continue; }
            fixes++;
            if (MatchMarket(item.Title, markets, out var titleClear) is { Lead: { } lead } titled && titleClear)
            {
                kept.Add(item with { Detail = LeadStatement(titled, lead) });
                continue;
            }
            // Judge each sentence in the item's context (the market it's about, when that's clear).
            bool Wrong(string sentence)
            {
                var m = market ?? MatchMarket(sentence, markets, out _);
                return UnsupportedPercentages(sentence, AllowedFor(m)).Count > 0 || (m is not null && MisnamesFavourite(sentence, m));
            }
            var detail = string.Join(" ", Sentences(item.Detail).Where(s => !Wrong(s)));
            if (detail.Length > 0) kept.Add(item with { Detail = detail });
        }
        return (f with { Overview = overview, Items = kept }, fixes);
    }

    public static string LeadStatement(PredictionMarket market, PredictionOutcome lead) =>
        market.IsBinary
            ? string.Create(CultureInfo.InvariantCulture, $"The crowd puts “{lead.Label}” at {lead.Probability * 100:0}%.")
            : string.Create(CultureInfo.InvariantCulture, $"The crowd favours “{lead.Label}” at {lead.Probability * 100:0}%") +
              (market.Outcomes.Count > 1 ? $" (of {market.Outcomes.Count} outcomes)." : ".");

    private static IEnumerable<string> Sentences(string text) =>
        Regex.Split(text, @"(?<=[.!?])\s+").Where(s => s.Length > 0);

    /// <summary>
    /// Keeps only the posts that actually share a meaningful word with the topic the model filed them under
    /// (a podcast question doesn't belong under "Football match tensions").
    /// </summary>
    public static List<int> RelatedPosts(string topicTitle, string topicSummary, IReadOnlyList<int> postNumbers, IReadOnlyList<FeedItem> posts)
    {
        var topic = TextTools.Signature(topicTitle + " " + topicSummary);
        topic.RemoveWhere(t => t.Length < 4);
        if (topic.Count == 0) return postNumbers.ToList();
        return postNumbers.Where(n => n >= 1 && n <= posts.Count &&
            TextTools.Signature(posts[n - 1].Title + " " + posts[n - 1].Summary).Any(topic.Contains)).ToList();
    }

    /// <summary>Words that say when or how rather than what: they never identify a market.</summary>
    private static readonly HashSet<string> NotDistinctive = new(StringComparer.Ordinal)
    {
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "date", "decision", "end", "week", "month", "outcome", "crowd", "odd", "chance", "market", "probability", "percent",
        "favour", "favor", "favourite", "favorite", "expect", "expected", "likely", "lead", "leading", "bet", "trader",
    };

    private static HashSet<string> Distinctive(string text)
    {
        var words = TextTools.Signature(text);
        words.RemoveWhere(w => NotDistinctive.Contains(w) || w.All(char.IsDigit));
        return words;
    }

    /// <summary>
    /// The market a piece of text is about, or null when none is a clear match. Words are weighted by how few market
    /// titles use them (so "October" or "election" count for little); the best market needs most of its title's
    /// weight, two shared words (or one that is most of its title), and a clear lead over the runner-up.
    /// <paramref name="clear"/> is true for a strong match (two or more shared words and about half the title's weight,
    /// or every distinctive word of a one-word title such as "Fed").
    /// </summary>
    internal static PredictionMarket? MatchMarket(string text, IReadOnlyList<PredictionMarket> markets, out bool clear)
    {
        clear = false;
        var words = Distinctive(text);
        if (words.Count == 0 || markets.Count == 0) return null;
        var titles = markets.Select(m => Distinctive(m.Title)).ToList();
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in titles)
            foreach (var w in t) df[w] = df.GetValueOrDefault(w) + 1;
        double Idf(string w) => Math.Log(1 + markets.Count / (double)Math.Max(1, df.GetValueOrDefault(w)));

        var scored = markets.Select((m, i) =>
        {
            var total = titles[i].Sum(Idf);
            var shared = titles[i].Where(words.Contains).ToList();
            return (Market: m, Score: total > 0 ? shared.Sum(Idf) / total : 0, Shared: shared.Count);
        }).OrderByDescending(x => x.Score).ToList();

        var best = scored[0];
        var runnerUp = scored.Count > 1 ? scored[1].Score : 0;
        if (best.Shared == 0 || best.Score < 0.34) return null;
        if (best.Shared < 2 && best.Score < 0.6) return null;
        if (runnerUp > 0 && best.Score < runnerUp * 1.5) return null;
        clear = best.Score >= 0.45 && (best.Shared >= 2 || best.Score >= 0.99); // two shared words, or all of a one-word title
        return best.Market;
    }
}
