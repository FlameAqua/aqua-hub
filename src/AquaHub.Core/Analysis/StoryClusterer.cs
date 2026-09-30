using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Analysis;

/// <summary>
/// Groups articles from different outlets that describe the same story (one card, many sources),
/// then ranks stories by coverage breadth, source reliability, freshness, personal interests and locality.
/// Uses an inverted index over title signatures so clustering stays ~O(n) for typical volumes.
/// An article joins a story only if it fits the story as a whole (its core words), not merely one member — so
/// stories can't chain together through intermediate headlines — and place names never tie stories together on
/// their own: a weak match between headlines that name different places is rejected.
/// </summary>
public static class StoryClusterer
{
    private sealed class Working
    {
        public required StoryCluster Cluster;
        public readonly List<HashSet<string>> Signatures = new();
        public readonly Dictionary<string, int> TokenCounts = new(StringComparer.Ordinal);
        public readonly HashSet<string> Places = new(StringComparer.Ordinal);

        public void Add(HashSet<string> sig, HashSet<string> places)
        {
            Signatures.Add(sig);
            foreach (var t in sig)
            {
                TokenCounts[t] = TokenCounts.GetValueOrDefault(t) + 1;
                if (places.Contains(t)) Places.Add(t);
            }
        }
    }

    /// <summary>A strong textual match (near-identical headline) overrides the structural guards.</summary>
    private const double StrongMatch = 0.45;

    /// <summary>
    /// Guards against over-merging: headlines naming only different places, and (for stories with 3+ articles)
    /// articles that share fewer than two of the story's core words — the words most of its articles use.
    /// </summary>
    private static bool FitsStory(Working w, HashSet<string> sig, double jac, HashSet<string> places)
    {
        if (jac >= StrongMatch) return true;
        var mine = sig.Where(places.Contains).ToList();
        if (mine.Count > 0 && w.Places.Count > 0 && !mine.Any(w.Places.Contains)) return false;
        var members = w.Signatures.Count;
        if (members < 3) return true;
        var threshold = Math.Max(2, (int)Math.Ceiling(members * 0.3));
        var core = sig.Count(t => !places.Contains(t) && w.TokenCounts.GetValueOrDefault(t) >= threshold);
        return core >= 2;
    }

    public static List<StoryCluster> Build(IReadOnlyList<FeedItem> items, NewsSettings news, LocationSettings location,
        DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var places = Places.With(location.LocalKeywords.Append(location.City).Append(location.Region));
        var ordered = items.OrderBy(i => i.Published).ToList();
        var working = new List<Working>();
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var docFreq = new Dictionary<string, int>(StringComparer.Ordinal);

        var sigs = ordered.Select(i => TextTools.Signature(i.Title)).ToList();
        foreach (var sig in sigs)
            foreach (var t in sig) docFreq[t] = docFreq.GetValueOrDefault(t) + 1;
        var n = Math.Max(1, ordered.Count);

        for (var k = 0; k < ordered.Count; k++)
        {
            var item = ordered[k];
            var sig = sigs[k];
            if (sig.Count == 0) continue;

            // Candidate clusters share at least one reasonably rare token.
            var candidates = new HashSet<int>();
            foreach (var t in sig)
            {
                if (docFreq.GetValueOrDefault(t) > n * 0.08 && docFreq[t] > 6) continue; // too common to be informative
                if (index.TryGetValue(t, out var list)) foreach (var c in list) candidates.Add(c);
            }

            var best = -1;
            var bestScore = 0.0;
            foreach (var c in candidates)
            {
                var w = working[c];
                if ((item.Published - w.Cluster.Latest).Duration() > TimeSpan.FromHours(30)) continue;
                if (w.Cluster.Items.Any(x => x.SourceId == item.SourceId && x.Title == item.Title)) { best = c; bestScore = 1; break; }
                var clusterBest = 0.0;
                foreach (var other in w.Signatures)
                {
                    var jac = TextTools.Jaccard(sig, other, out _);
                    // Place names say where, not what: they don't count towards the words two headlines share.
                    var topical = sig.Count(t => other.Contains(t) && !places.Contains(t));
                    var rareOverlap = sig.Count(t => other.Contains(t) && !places.Contains(t) && docFreq.GetValueOrDefault(t) <= Math.Max(3, n * 0.02));
                    var match = (topical >= 3 && jac >= 0.22) || (topical >= 2 && jac >= 0.4) || (rareOverlap >= 2 && jac >= 0.2);
                    if (match && jac > clusterBest) clusterBest = jac;
                }
                if (clusterBest > bestScore && FitsStory(w, sig, clusterBest, places)) { bestScore = clusterBest; best = c; }
            }

            if (best >= 0)
            {
                var w = working[best];
                if (!w.Cluster.Items.Any(x => x.Id == item.Id))
                {
                    w.Cluster.Items.Add(item);
                    w.Add(sig, places);
                    if (item.Published > w.Cluster.Latest) w.Cluster.Latest = item.Published;
                    foreach (var t in sig) AddIndex(index, t, best);
                }
            }
            else
            {
                var cluster = new StoryCluster
                {
                    Id = "s" + Hash.Short(item.Id),
                    Title = item.Title,
                    FirstSeen = item.Published,
                    Latest = item.Published,
                };
                cluster.Items.Add(item);
                var w = new Working { Cluster = cluster };
                w.Add(sig, places);
                working.Add(w);
                foreach (var t in sig) AddIndex(index, t, working.Count - 1);
            }
        }

        var clusters = working.Select(w => w.Cluster).ToList();
        foreach (var c in clusters) Finalise(c, news, location, clock);
        return clusters.Where(c => c.Importance > -5).OrderByDescending(c => c.Importance).ToList();
    }

    private static void AddIndex(Dictionary<string, List<int>> index, string token, int cluster)
    {
        if (!index.TryGetValue(token, out var list)) index[token] = list = new List<int>();
        if (list.Count == 0 || list[^1] != cluster) list.Add(cluster);
    }

    private static void Finalise(StoryCluster c, NewsSettings news, LocationSettings location, DateTimeOffset now)
    {
        c.Items = c.Items.OrderBy(i => i.Tier).ThenByDescending(i => i.Published).ToList();
        var rep = c.Items
            .OrderBy(i => i.Tier)
            .ThenBy(i => i.Title.Length is >= 35 and <= 130 ? 0 : 1)
            .ThenByDescending(i => i.Summary.Length > 60 ? 1 : 0)
            .First();
        c.Title = rep.Title;
        c.ImageUrl = c.Items.FirstOrDefault(i => !string.IsNullOrEmpty(i.ImageUrl))?.ImageUrl;
        c.ContentKey = Hash.Short(string.Join("|", c.Items.Select(i => i.Id).OrderBy(x => x, StringComparer.Ordinal)));

        var text = TextTools.Fold(string.Join(" \n ", c.Items.Take(6).Select(i => i.Title + " " + i.Summary))).ToLowerInvariant();

        c.Matches = news.Interests.Where(k => TextTools.ContainsPhrase(text, k)).Distinct().ToList();
        var muted = news.Muted.Any(k => TextTools.ContainsPhrase(text, k));

        // Local means most of the coverage is local — not that one headline among many mentions the city.
        var localWords = location.LocalKeywords.Append(location.City).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        bool Mentions(string text) => localWords.Any(k => TextTools.ContainsPhrase(TextTools.Fold(text).ToLowerInvariant(), k));
        // Local means the story is about the area: most headlines name it, or the lead headline (or the lead local
        // outlet's own summary) does and most of the outlets covering it are local. Counted per publisher, so two
        // Irish Independent pieces are one outlet — and an "Irish actor at the VMAs" angle doesn't make the VMAs local.
        var publishers = c.Items.GroupBy(i => Publishers.Key(i.SourceName)).ToList();
        var localPublishers = publishers.Count(g => g.Any(i => i.IsLocal));
        var headlinesNaming = c.Items.Count(i => Mentions(i.Title));
        var leadIsLocal = Mentions(rep.Title) || (rep.IsLocal && Mentions(rep.Summary));
        c.IsLocal = headlinesNaming * 2 >= c.Items.Count || (leadIsLocal && localPublishers * 2 >= publishers.Count);

        c.Category = c.IsLocal
            ? "local"
            : c.Items.Where(i => i.Category != "local").GroupBy(i => i.Category)
                  .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(i => i.Tier))
                  .Select(g => g.Key).FirstOrDefault() ?? "world";

        var distinctSources = c.SourceCount;
        var bestTier = c.Items.Min(i => i.Tier);
        var tierBonus = bestTier switch { 1 => 1.0, 2 => 0.55, _ => 0.2 };
        var ageHours = Math.Max(0, (now - c.Latest).TotalHours);
        var recency = Math.Exp(-ageHours / 9.0);
        var engagement = Math.Log10(1 + c.Items.Sum(i => Math.Max(0, i.Score)));

        c.Importance = 1.7 * Math.Log2(1 + distinctSources)
                       + 1.0 * tierBonus
                       + 2.2 * recency
                       + 0.8 * Math.Min(2, c.Matches.Count)
                       + (c.IsLocal ? 0.7 : 0)
                       + 0.2 * engagement
                       - (muted ? 6 : 0);
    }

    /// <summary>Deterministic, model-free story summary.</summary>
    public static StorySummary ExtractiveSummary(StoryCluster c)
    {
        var docs = c.Items.Select(i => i.Summary).Where(s => s.Length > 30).Take(6).ToList();
        var points = TextTools.Extract(docs, c.Items.Select(i => i.Title), count: 3, maxChars: 180);
        var tldr = points.FirstOrDefault() ?? c.Title;
        return new StorySummary
        {
            Headline = c.Title,
            Tldr = tldr,
            KeyPoints = points.Skip(1).ToList(),
            WhyItMatters = "",
            IsAi = false,
        };
    }
}
