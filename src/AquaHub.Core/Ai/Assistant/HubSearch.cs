using System.Globalization;
using System.Text;
using AquaHub.Core.Agents;
using AquaHub.Core.Analysis;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>One thing your agents collected that matches a question.</summary>
/// <param name="Local">A story from the user's own area (local outlets or places).</param>
public sealed record HubHit(string Kind, string Id, string Title, string Detail, string Source, string? Url, DateTimeOffset? When, double Score, bool Local = false);

/// <summary>
/// Retrieval over everything the agents collected: clustered stories (with their AI summaries and every outlet),
/// individual articles and posts (full-text index, 30 days), the social pulse, prediction markets, your agenda and
/// market quotes. Used to prime Ask with context and as the model's search_hub tool.
/// </summary>
public static class HubSearch
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Question words that say what kind of answer is wanted, not what it's about.</summary>
    internal static readonly HashSet<string> AskingWords = new(StringComparer.Ordinal)
    {
        "tell", "show", "give", "happen", "happening", "going", "anything", "something", "think", "know", "expect", "predict",
        "please", "whats", "explain", "summarise", "summarize", "detail", "details", "info", "information", "about", "mean",
        "story", "stories", "article", "articles", "could", "would", "should", "can", "need", "want", "find", "search", "look",
    };

    public static HashSet<string> Terms(string query)
    {
        var terms = TextTools.Signature(query);
        terms.RemoveWhere(AskingWords.Contains);
        return terms;
    }

    /// <param name="kind">any | news | social | markets | predictions | agenda</param>
    public static List<HubHit> Search(HubState state, HubDatabase? db, string query, string kind = "any", int max = 8, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.Now;
        var terms = Terms(query);
        if (terms.Count == 0) return new();
        // Most of a long question must match (two of five words is a coincidence, not the same topic).
        var need = Math.Clamp((int)Math.Ceiling(terms.Count * 0.4), 1, 3);
        bool Want(string k) => kind is "any" or "" || kind == k;
        var hits = new List<HubHit>();
        var inStories = new HashSet<string>(StringComparer.Ordinal);

        double Recency(DateTimeOffset when) => Math.Max(0, 1 - (at - when).TotalHours / 96.0) * 0.4;
        (int Overlap, double Score) Match(string title, string body)
        {
            var t = TextTools.Signature(title);
            var b = TextTools.Signature(body);
            var inTitle = terms.Count(t.Contains);
            var inAny = terms.Count(x => t.Contains(x) || b.Contains(x));
            return (inAny, (inTitle * 1.5 + inAny) / terms.Count);
        }

        if (Want("news"))
        {
            foreach (var c in state.Stories)
            {
                foreach (var i in c.Items) inStories.Add(i.Id);
                var body = c.Summary is { } sum ? sum.Tldr + " " + string.Join(' ', sum.KeyPoints) + " " + sum.WhyItMatters : "";
                body += " " + string.Join(' ', c.Items.Take(8).Select(i => i.Title));
                var (overlap, score) = Match((c.Summary?.Headline ?? "") + " " + c.Title, body);
                if (overlap < need) continue;
                hits.Add(StoryHit(c, score + Recency(c.Latest) + Math.Min(0.3, c.SourceCount * 0.05)));
            }
        }

        if (db is not null && (Want("news") || Want("social")))
        {
            foreach (var item in db.Search(query, 12, at.AddDays(-30)))
            {
                if (inStories.Contains(item.Id)) continue;
                var isPost = item.Kind == ItemKind.Social;
                if (isPost ? !Want("social") : !Want("news")) continue;
                var (overlap, score) = Match(item.Title, item.Summary);
                if (overlap < need) continue;
                hits.Add(new HubHit(isPost ? "post" : "article", item.Id, item.Title, HtmlText.Truncate(item.Summary, 280), item.SourceName,
                    item.Url ?? item.CommentsUrl, item.Published, score + Recency(item.Published)));
            }
        }

        if (Want("social"))
        {
            foreach (var p in state.Social)
            {
                if (hits.Any(h => h.Id == p.Id)) continue;
                var (overlap, score) = Match(p.Title, p.Summary);
                if (overlap < need) continue;
                hits.Add(new HubHit("post", p.Id, p.Title, HtmlText.Truncate(p.Summary, 240), p.SourceName, p.Url ?? p.CommentsUrl, p.Published, score * 0.9 + Recency(p.Published)));
            }
            if (state.Pulse is { } pulse)
                foreach (var t in pulse.Topics)
                {
                    var (overlap, score) = Match(t.Title, t.Summary);
                    if (overlap < need) continue;
                    hits.Add(new HubHit("topic", "topic:" + t.Title, t.Title, t.Summary, "Social pulse (" + string.Join(", ", t.Platforms) + ")", null, pulse.GeneratedAt, score));
                }
        }

        if (Want("predictions"))
        {
            foreach (var m in state.Predictions)
            {
                var outcomes = string.Join("; ", m.Outcomes.OrderByDescending(o => o.Probability).Take(4).Select(o => $"{o.Label} {(o.Probability * 100).ToString("0", Inv)}%"));
                var (overlap, score) = Match(m.Title, string.Join(' ', m.Outcomes.Select(o => o.Label)));
                if (overlap < need) continue;
                hits.Add(new HubHit("prediction", m.Id, m.Title, outcomes + (m.EndDate is { } end ? $" · resolves {end.ToLocalTime():d MMM yyyy}" : ""),
                    m.Source, m.Url, null, score));
            }
        }

        if (Want("agenda"))
        {
            foreach (var e in state.Events.Where(e => e.Start >= at.AddDays(-1)))
            {
                var (overlap, score) = Match(e.Title, e.Detail ?? "");
                if (overlap < need) continue;
                hits.Add(new HubHit("event", e.Id, e.Title,
                    e.Start.ToLocalTime().ToString(e.AllDay ? "ddd d MMM" : "ddd d MMM HH:mm", Inv) + (e.Location is { Length: > 0 } loc ? " · " + loc : ""),
                    e.Source, e.Url, e.Start, score));
            }
        }

        if (Want("markets"))
        {
            foreach (var q in state.Quotes.Values)
            {
                var names = TextTools.Signature(q.Name + " " + q.Symbol);
                if (!terms.Any(names.Contains) && !query.Contains(q.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
                hits.Add(new HubHit("quote", q.Symbol, $"{q.Name} ({q.Symbol})",
                    string.Create(Inv, $"{q.Price:0.##} {q.Currency}, {q.ChangePercent:+0.0;-0.0}% today"), "Markets", null, null, 1.5));
            }
        }

        return hits.OrderByDescending(h => h.Score).Take(max).ToList();
    }

    /// <summary>Formats hits for the model, registering each in the source book so it can be cited as [n].</summary>
    /// <summary>A story as a hit (its AI headline and summary when it has them).</summary>
    public static HubHit StoryHit(StoryCluster c, double score)
    {
        var names = c.SourceNames;
        var source = names.Count <= 2 ? string.Join(", ", names) : $"{names[0]}, {names[1]} +{names.Count - 2}";
        var detail = c.Summary is { Tldr.Length: > 0 } s ? s.Tldr : c.Items.FirstOrDefault(i => i.Summary.Length > 0)?.Summary ?? "";
        return new HubHit("story", c.Id, c.Summary is { IsAi: true, Headline.Length: > 0 } h ? h.Headline : c.Title, HtmlText.Truncate(detail, 320),
            source, c.Url, c.Latest, score, c.IsLocal);
    }

    public static string Format(IEnumerable<HubHit> hits, SourceBook book, DateTimeOffset? now = null)
    {
        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            var n = book.Add(h.Title, h.Source, h.Url, h.Kind is "post" or "topic" ? "social" : h.Kind is "prediction" or "event" or "quote" ? "hub" : "news", h.Detail);
            sb.Append('[').Append(n).Append("] (").Append(h.Local ? "local " : "").Append(h.Kind).Append(" · ").Append(h.Source);
            if (h.When is { } w) sb.Append(" · ").Append(TimeText.Dated(w, now));
            sb.Append(") ").Append(h.Title);
            if (h.Detail.Length > 0) sb.Append(" — ").Append(h.Detail);
            if (h.Kind == "story") sb.Append(" {story_id: ").Append(h.Id).Append('}');
            else if (h.Kind is "article" or "post" && Readable(h.Url)) sb.Append(" <").Append(h.Url).Append('>');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Everything about one story: the AI summary, then every outlet's headline, time, snippet and link (each citable).
    /// </summary>
    public static string Story(StoryCluster c, SourceBook book, DateTimeOffset? now = null)
    {
        var sb = new StringBuilder();
        sb.Append("Headline: ").Append(c.Summary is { Headline.Length: > 0 } h ? h.Headline : c.Title).Append('\n');
        sb.Append("Category: ").Append(c.Category).Append(c.IsLocal ? " (local)" : "").Append(" · first seen ").Append(TimeText.Dated(c.FirstSeen, now))
          .Append(" · updated ").Append(TimeText.Dated(c.Latest, now)).Append(" · ").Append(c.SourceCount).Append(" outlets\n");
        if (c.Summary is { } s)
        {
            if (s.Tldr.Length > 0) sb.Append("Summary").Append(s.IsAi ? " (written earlier by Aqua's News Editor from these outlets)" : "").Append(": ").Append(s.Tldr).Append('\n');
            foreach (var k in s.KeyPoints) sb.Append("- ").Append(k).Append('\n');
            if (s.WhyItMatters.Length > 0) sb.Append("Why it matters: ").Append(s.WhyItMatters).Append('\n');
        }
        sb.Append("Coverage:\n");
        // The summary was written from these outlets, so a claim from it may cite any of them.
        var summary = c.Summary is { } sum ? sum.Tldr + " " + string.Join(' ', sum.KeyPoints) + " " + sum.WhyItMatters : "";
        foreach (var i in c.Items.OrderBy(i => i.Tier).ThenByDescending(i => i.Published).Take(14))
        {
            var n = book.Add(i.Title, i.SourceName, i.Url, "news", i.Summary + " " + summary);
            sb.Append('[').Append(n).Append("] ").Append(i.SourceName).Append(", ").Append(TimeText.Dated(i.Published, now)).Append(": ").Append(i.Title);
            if (i.Summary.Length > 0) sb.Append(" — ").Append(HtmlText.Truncate(i.Summary, 360));
            if (Readable(i.Url)) sb.Append(" <").Append(i.Url).Append('>');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Links worth giving the model (it can read them); Google News redirect links can't be read.</summary>
    private static bool Readable(string? url) =>
        url is { Length: > 0 } && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !url.Contains("news.google.com", StringComparison.OrdinalIgnoreCase);

    public static StoryCluster? FindStory(HubState state, string id) =>
        state.Stories.FirstOrDefault(c => c.Id == id) ?? state.Stories.FirstOrDefault(c => c.Items.Any(i => i.Id == id));
}
