using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AquaHub.Core.Search;

public sealed record SearchFilter(long? Root = null, string Extension = "", DateTime? ModifiedSince = null, string? Folder = null)
{
    public bool Intersects(SearchRoot root) =>
        Folder is null || SearchStore.Inside(root.Path, Folder) || SearchStore.Inside(Folder, root.Path);
}

public sealed partial class SearchStore
{
    private sealed record Candidate(long Id, SearchHit Hit, int Rank, double Score);
    private static readonly HashSet<string> Common = new(
        "a an the on to in into at of is are do did does for and or my me their that this them there it i we can how what which when who have with from by be had as much more after before should".Split(' '),
        StringComparer.OrdinalIgnoreCase);

    internal List<SearchHit> Candidates(string query, SearchFilter filter, float[]? embedding, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length is 0 or > 256) return [];
        var words = Regex.Matches(query, @"[\p{L}\p{N}]+").Select(m => m.Value).Where(w => !Common.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        using var db = Connect();
        db.CreateFunction<string, string, bool>("within_folder", SearchStore.Inside, isDeterministic: true);
        using var tx = db.BeginTransaction(deferred: true);
        var names = new List<Candidate>();
        var lexical = new List<Candidate>();
        var semantic = new Dictionary<long, Candidate>();
        bool Allowed(SearchHit h) => (filter.Root == null || h.RootId == filter.Root)
            && (filter.Folder == null || Inside(h.Path, filter.Folder))
            && (filter.Extension.Length == 0 || Path.GetExtension(h.Path).Equals(filter.Extension, StringComparison.OrdinalIgnoreCase))
            && (filter.ModifiedSince == null || h.Stamp.Modified >= filter.ModifiedSince.Value.ToFileTimeUtc());
        static SearchHit Hit(SqliteDataReader r) => new(r.GetInt64(1), r.GetString(2),
            new(r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)), "", "");
        const string fields = "d.id,d.root,d.path,d.identity,d.size,d.modified,d.changed";
        using (var cmd = Sql(db, "SELECT " + fields + " FROM docs d"))
        {
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var h = Hit(r);
                if (!Allowed(h)) continue;
                var tier = NameTier(h.Path, query, words);
                if (tier > 0) names.Add(new(r.GetInt64(0), h with { Reason = tier switch { 5 => "Exact path", 4 => "Exact filename", 1 => "Similar filename", _ => "Name or path match" } }, tier, 0));
            }
        }
        if (words.Length > 0)
        {
            using var cmd = Sql(db, "SELECT " + fields + """
                ,c.text FROM passage JOIN chunks c ON c.id=passage.rowid JOIN docs d ON d.id=c.doc
                WHERE passage MATCH $q AND ($root=0 OR d.root=$root) AND d.modified>=$since
                AND ($folder='' OR within_folder(d.path,$folder))
                AND ($ext='' OR substr(d.path,-length($ext))=$ext COLLATE NOCASE) ORDER BY rank LIMIT 1600
                """, ("$q", string.Join(" OR ", words.Select(w => "\"" + w + "\"*"))),
                ("$root", filter.Root ?? 0), ("$since", filter.ModifiedSince?.ToFileTimeUtc() ?? 0), ("$ext", filter.Extension),
                ("$folder", filter.Folder ?? ""));
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            var seen = new HashSet<long>();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var h = Hit(r);
                var id = r.GetInt64(0);
                if (!Allowed(h) || !seen.Add(id)) continue;
                var passage = r.GetString(7);
                var phrase = passage.Contains(query, StringComparison.OrdinalIgnoreCase);
                lexical.Add(new(id, h with { Snippet = Excerpt(passage, phrase ? [query] : words), Reason = phrase ? "Phrase in document" : "Words in document" }, phrase ? 1 : 0, 0));
            }
        }
        if (embedding != null)
        {
            using var cmd = Sql(db, "SELECT " + fields + ",c.text,c.vector FROM chunks c JOIN docs d ON d.id=c.doc WHERE c.space=$s", ("$s", Space));
            cmd.Transaction = tx;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var h = Hit(r);
                if (!Allowed(h)) continue;
                var bytes = (byte[])r[8];
                if (bytes.Length != embedding.Length * 4) continue;
                var v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
                var score = 0d;
                for (int i = 0; i < v.Length; i++) score += v[i] * embedding[i];
                var id = r.GetInt64(0);
                if (!double.IsFinite(score) || score < SemanticFloor || semantic.TryGetValue(id, out var best) && score <= best.Score) continue;
                semantic[id] = new(id, h with { Snippet = Excerpt(r.GetString(7), words), Reason = "Similar passage" }, 0, score);
            }
        }
        var fused = new Dictionary<long, (SearchHit Hit, int Tier, double Score)>();
        void Merge(IEnumerable<Candidate> list, bool name)
        {
            int pos = 0;
            foreach (var c in list.Take(250))
            {
                fused.TryGetValue(c.Id, out var prev);
                var hit = prev.Hit ?? c.Hit;
                if (hit.Snippet.Length == 0 && c.Hit.Snippet.Length > 0) hit = hit with { Snippet = c.Hit.Snippet };
                fused[c.Id] = (hit, Math.Max(prev.Tier, name && c.Rank >= 4 ? c.Rank : 0), prev.Score + 1d / (60 + ++pos));
            }
        }
        Merge(names.OrderByDescending(c => c.Rank).ThenBy(c => c.Hit.Path, StringComparer.OrdinalIgnoreCase), true);
        Merge(lexical.OrderByDescending(c => c.Rank), false);
        Merge(semantic.Values.OrderByDescending(c => c.Score), false);
        return fused.Values.OrderByDescending(v => v.Tier).ThenByDescending(v => v.Score).ThenBy(v => v.Hit.Path, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Hit).ToList();
    }

    internal const double SemanticFloor = 0.40;
    private static string Excerpt(string passage, string[] words)
    {
        var match = words.Select(w => passage.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, match - 55);
        return (start > 0 ? "…" : "") + passage.Substring(start, Math.Min(200, passage.Length - start)) + (passage.Length > start + 200 ? "…" : "");
    }
    private static int NameTier(string path, string query, string[] words)
    {
        var name = Path.GetFileName(path);
        if (path.Equals(query, StringComparison.OrdinalIgnoreCase)) return 5;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase) || words.Length > 1 && words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return 2;
        if (words.Length == 1 && query.Length is >= 4 and <= 48 &&
            Regex.Matches(name, @"[\p{L}\p{N}]+").Any(m => OneEdit(m.Value.ToUpperInvariant(), query.ToUpperInvariant()))) return 1;
        return 0;
    }

    private static bool OneEdit(string a, string b)
    {
        if (a.Length > b.Length) return OneEdit(b, a);
        if (b.Length - a.Length > 1) return false;
        int i = 0;
        while (i < a.Length && a[i] == b[i]) i++;
        if (i == a.Length) return true;
        if (a.Length != b.Length) return a.AsSpan(i).SequenceEqual(b.AsSpan(i + 1));
        return a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1)) || i + 1 < a.Length && a[i] == b[i + 1] && a[i + 1] == b[i] && a.AsSpan(i + 2).SequenceEqual(b.AsSpan(i + 2));
    }
}
