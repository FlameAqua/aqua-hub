using System.Text;
using AquaHub.Core.Models;
using AquaHub.Core.Util;
using Microsoft.Data.Sqlite;

namespace AquaHub.Core.Data;

/// <summary>
/// Local SQLite store (WAL mode). Holds ingested items (with an FTS5 index for Ask/search),
/// JSON snapshots of the latest state (so the UI is instant on startup), LLM output cache,
/// HTTP validators for conditional GETs, alerts, agent run history and daily price history.
/// All statements are parameterised.
/// </summary>
public sealed class HubDatabase
{
    private readonly string _cs;
    public string Path { get; }

    public HubDatabase(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();

        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA auto_vacuum=INCREMENTAL;");
        Migrate(c);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Migrate(SqliteConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS items(
              id TEXT PRIMARY KEY, kind INTEGER NOT NULL, source_id TEXT NOT NULL, source_name TEXT NOT NULL,
              platform TEXT NOT NULL, category TEXT NOT NULL, tier INTEGER NOT NULL, title TEXT NOT NULL,
              summary TEXT NOT NULL DEFAULT '', url TEXT, image_url TEXT, author TEXT,
              published INTEGER NOT NULL, fetched INTEGER NOT NULL, score INTEGER NOT NULL DEFAULT 0,
              comments INTEGER NOT NULL DEFAULT 0, comments_url TEXT, is_local INTEGER NOT NULL DEFAULT 0,
              saved INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_items_kind_pub ON items(kind, published DESC);
            CREATE INDEX IF NOT EXISTS ix_items_source ON items(source_id, published DESC);
            CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
              title, summary, source_name, content='items', content_rowid='rowid',
              tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS items_ai AFTER INSERT ON items BEGIN
              INSERT INTO items_fts(rowid, title, summary, source_name) VALUES (new.rowid, new.title, new.summary, new.source_name);
            END;
            CREATE TRIGGER IF NOT EXISTS items_ad AFTER DELETE ON items BEGIN
              INSERT INTO items_fts(items_fts, rowid, title, summary, source_name) VALUES ('delete', old.rowid, old.title, old.summary, old.source_name);
            END;
            CREATE TRIGGER IF NOT EXISTS items_au AFTER UPDATE OF title, summary, source_name ON items BEGIN
              INSERT INTO items_fts(items_fts, rowid, title, summary, source_name) VALUES ('delete', old.rowid, old.title, old.summary, old.source_name);
              INSERT INTO items_fts(rowid, title, summary, source_name) VALUES (new.rowid, new.title, new.summary, new.source_name);
            END;
            CREATE TABLE IF NOT EXISTS kv(key TEXT PRIMARY KEY, value TEXT NOT NULL, updated INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS llm_cache(key TEXT PRIMARY KEY, model TEXT, created INTEGER NOT NULL, output TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS http_cache(url TEXT PRIMARY KEY, etag TEXT, last_modified TEXT, updated INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS alerts(id TEXT PRIMARY KEY, kind TEXT NOT NULL, severity INTEGER NOT NULL, title TEXT NOT NULL,
              body TEXT NOT NULL, url TEXT, target TEXT, created INTEGER NOT NULL, read INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_alerts_created ON alerts(created DESC);
            CREATE TABLE IF NOT EXISTS agent_runs(id INTEGER PRIMARY KEY AUTOINCREMENT, agent TEXT NOT NULL, started INTEGER NOT NULL,
              duration_ms INTEGER NOT NULL, ok INTEGER NOT NULL, message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS price_history(symbol TEXT NOT NULL, day INTEGER NOT NULL, close REAL NOT NULL,
              PRIMARY KEY(symbol, day)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS seen(key TEXT PRIMARY KEY, at INTEGER NOT NULL) WITHOUT ROWID;
            PRAGMA user_version = 1;
            """);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ───────────────────────────── Items ─────────────────────────────

    /// <summary>Inserts new items and refreshes engagement counters on existing ones. Returns the number of new items.</summary>
    public int UpsertItems(IEnumerable<FeedItem> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var ins = c.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT OR IGNORE INTO items(id, kind, source_id, source_name, platform, category, tier, title, summary, url, image_url,
              author, published, fetched, score, comments, comments_url, is_local)
            VALUES($id, $kind, $sid, $sname, $platform, $cat, $tier, $title, $summary, $url, $img, $author, $pub, $fetched,
              $score, $comments, $curl, $local);
            """;
        var pId = ins.Parameters.Add("$id", SqliteType.Text);
        var pKind = ins.Parameters.Add("$kind", SqliteType.Integer);
        var pSid = ins.Parameters.Add("$sid", SqliteType.Text);
        var pSname = ins.Parameters.Add("$sname", SqliteType.Text);
        var pPlatform = ins.Parameters.Add("$platform", SqliteType.Text);
        var pCat = ins.Parameters.Add("$cat", SqliteType.Text);
        var pTier = ins.Parameters.Add("$tier", SqliteType.Integer);
        var pTitle = ins.Parameters.Add("$title", SqliteType.Text);
        var pSummary = ins.Parameters.Add("$summary", SqliteType.Text);
        var pUrl = ins.Parameters.Add("$url", SqliteType.Text);
        var pImg = ins.Parameters.Add("$img", SqliteType.Text);
        var pAuthor = ins.Parameters.Add("$author", SqliteType.Text);
        var pPub = ins.Parameters.Add("$pub", SqliteType.Integer);
        var pFetched = ins.Parameters.Add("$fetched", SqliteType.Integer);
        var pScore = ins.Parameters.Add("$score", SqliteType.Integer);
        var pComments = ins.Parameters.Add("$comments", SqliteType.Integer);
        var pCurl = ins.Parameters.Add("$curl", SqliteType.Text);
        var pLocal = ins.Parameters.Add("$local", SqliteType.Integer);

        using var upd = c.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE items SET score=$score, comments=$comments WHERE id=$id AND (score<>$score OR comments<>$comments);";
        var uId = upd.Parameters.Add("$id", SqliteType.Text);
        var uScore = upd.Parameters.Add("$score", SqliteType.Integer);
        var uComments = upd.Parameters.Add("$comments", SqliteType.Integer);

        var fetched = Now();
        var inserted = 0;
        foreach (var it in items)
        {
            pId.Value = it.Id;
            pKind.Value = (int)it.Kind;
            pSid.Value = it.SourceId;
            pSname.Value = it.SourceName;
            pPlatform.Value = it.Platform;
            pCat.Value = it.Category;
            pTier.Value = it.Tier;
            pTitle.Value = it.Title;
            pSummary.Value = it.Summary ?? "";
            pUrl.Value = (object?)it.Url ?? DBNull.Value;
            pImg.Value = (object?)it.ImageUrl ?? DBNull.Value;
            pAuthor.Value = (object?)it.Author ?? DBNull.Value;
            pPub.Value = it.Published.ToUnixTimeSeconds();
            pFetched.Value = fetched;
            pScore.Value = it.Score;
            pComments.Value = it.Comments;
            pCurl.Value = (object?)it.CommentsUrl ?? DBNull.Value;
            pLocal.Value = it.IsLocal ? 1 : 0;
            if (ins.ExecuteNonQuery() == 1)
            {
                inserted++;
            }
            else if (it.Score != 0 || it.Comments != 0)
            {
                uId.Value = it.Id; uScore.Value = it.Score; uComments.Value = it.Comments;
                upd.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return inserted;
    }

    private const string ItemColumns =
        "i.id, i.kind, i.source_id, i.source_name, i.platform, i.category, i.tier, i.title, i.summary, i.url, i.image_url, " +
        "i.author, i.published, i.score, i.comments, i.comments_url, i.is_local, i.saved";

    private static FeedItem ReadItem(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Kind = (ItemKind)r.GetInt32(1),
        SourceId = r.GetString(2),
        SourceName = r.GetString(3),
        Platform = r.GetString(4),
        Category = r.GetString(5),
        Tier = r.GetInt32(6),
        Title = r.GetString(7),
        Summary = r.GetString(8),
        Url = r.IsDBNull(9) ? null : r.GetString(9),
        ImageUrl = r.IsDBNull(10) ? null : r.GetString(10),
        Author = r.IsDBNull(11) ? null : r.GetString(11),
        Published = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(12)),
        Score = r.GetInt32(13),
        Comments = r.GetInt32(14),
        CommentsUrl = r.IsDBNull(15) ? null : r.GetString(15),
        IsLocal = r.GetInt32(16) == 1,
        Saved = r.GetInt32(17) == 1,
    };

    public List<FeedItem> GetItems(ItemKind kind, DateTimeOffset since, int limit = 1000)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ItemColumns} FROM items i WHERE i.kind=$k AND i.published>=$since ORDER BY i.published DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$k", (int)kind);
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<FeedItem>();
        while (r.Read()) list.Add(ReadItem(r));
        return list;
    }

    public List<FeedItem> GetSaved(int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ItemColumns} FROM items i WHERE i.saved=1 ORDER BY i.published DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<FeedItem>();
        while (r.Read()) list.Add(ReadItem(r));
        return list;
    }

    public void SetSaved(string id, bool saved)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE items SET saved=$s WHERE id=$id;";
        cmd.Parameters.AddWithValue("$s", saved ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Full-text search over titles and snippets, ranked with BM25 (titles weighted 5×).</summary>
    public List<FeedItem> Search(string query, int limit = 12, DateTimeOffset? since = null)
    {
        var fts = BuildFtsQuery(query);
        if (fts.Length == 0) return new();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {ItemColumns} FROM items_fts f JOIN items i ON i.rowid = f.rowid
            WHERE items_fts MATCH $q AND i.published >= $since
            ORDER BY bm25(items_fts, 5.0, 1.0, 0.5) + (($now - i.published) / 86400.0) * 0.4
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$q", fts);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$since", (since ?? DateTimeOffset.UtcNow.AddDays(-30)).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<FeedItem>();
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadItem(r));
        }
        catch (SqliteException ex)
        {
            Log.Warn("db", $"FTS query failed for '{query}'", ex);
        }
        return list;
    }

    internal static string BuildFtsQuery(string query)
    {
        var terms = Analysis.TextTools.Tokenize(query)
            .Where(t => t.Length > 1 && !Analysis.TextTools.IsStopword(t))
            .Distinct()
            .Take(12)
            .Select(t => "\"" + t.Replace("\"", "\"\"") + "\"" + (t.Length >= 4 ? "*" : ""))
            .ToList();
        return string.Join(" OR ", terms);
    }

    // ───────────────────────────── Key/value snapshots ─────────────────────────────

    public void PutJson<T>(string key, T value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO kv(key, value, updated) VALUES($k, $v, $t) ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated=excluded.updated;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", JsonUtil.Serialize(value));
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.ExecuteNonQuery();
    }

    public void DeleteJson(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM kv WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    public T? GetJson<T>(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM kv WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return JsonUtil.Deserialize<T>(cmd.ExecuteScalar() as string);
    }

    // ───────────────────────────── LLM cache ─────────────────────────────

    public string? GetLlm(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT output FROM llm_cache WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void PutLlm(string key, string model, string output)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO llm_cache(key, model, created, output) VALUES($k, $m, $t, $o) ON CONFLICT(key) DO UPDATE SET model=excluded.model, created=excluded.created, output=excluded.output;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.Parameters.AddWithValue("$o", output);
        cmd.ExecuteNonQuery();
    }

    // ───────────────────────────── HTTP validators ─────────────────────────────

    public (string? ETag, string? LastModified) GetValidators(string url)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT etag, last_modified FROM http_cache WHERE url=$u;";
        cmd.Parameters.AddWithValue("$u", url);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : (null, null);
    }

    public void PutValidators(string url, string? etag, string? lastModified)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO http_cache(url, etag, last_modified, updated) VALUES($u, $e, $l, $t) ON CONFLICT(url) DO UPDATE SET etag=excluded.etag, last_modified=excluded.last_modified, updated=excluded.updated;";
        cmd.Parameters.AddWithValue("$u", url);
        cmd.Parameters.AddWithValue("$e", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", (object?)lastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.ExecuteNonQuery();
    }

    // ───────────────────────────── Alerts ─────────────────────────────

    public bool AddAlert(HubAlert a)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO alerts(id, kind, severity, title, body, url, target, created, read) VALUES($id, $k, $s, $t, $b, $u, $g, $c, 0);";
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.Parameters.AddWithValue("$k", a.Kind);
        cmd.Parameters.AddWithValue("$s", (int)a.Severity);
        cmd.Parameters.AddWithValue("$t", a.Title);
        cmd.Parameters.AddWithValue("$b", a.Body);
        cmd.Parameters.AddWithValue("$u", (object?)a.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$g", (object?)a.Target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", a.Created.ToUnixTimeSeconds());
        return cmd.ExecuteNonQuery() == 1;
    }

    public List<HubAlert> GetAlerts(int limit = 50)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, kind, severity, title, body, url, target, created, read FROM alerts ORDER BY created DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<HubAlert>();
        while (r.Read())
        {
            list.Add(new HubAlert
            {
                Id = r.GetString(0), Kind = r.GetString(1), Severity = (AlertSeverity)r.GetInt32(2), Title = r.GetString(3),
                Body = r.GetString(4), Url = r.IsDBNull(5) ? null : r.GetString(5), Target = r.IsDBNull(6) ? null : r.GetString(6),
                Created = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7)), Read = r.GetInt32(8) == 1,
            });
        }
        return list;
    }

    public void MarkAlertsRead()
    {
        using var c = Open();
        Exec(c, "UPDATE alerts SET read=1 WHERE read=0;");
    }

    /// <summary>Marks one alert read (it was opened); false if it was read already or isn't stored.</summary>
    public bool MarkAlertRead(string id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE alerts SET read=1 WHERE id=$id AND read=0;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void ClearAlerts()
    {
        using var c = Open();
        Exec(c, "DELETE FROM alerts;");
    }

    /// <summary>Returns true the first time a key is seen (used to de-duplicate alerts).</summary>
    public bool TryMarkSeen(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO seen(key, at) VALUES($k, $t);";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$t", Now());
        return cmd.ExecuteNonQuery() == 1;
    }

    // ───────────────────────────── Agent runs ─────────────────────────────

    public void AddRun(AgentRunRecord run)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_runs(agent, started, duration_ms, ok, message) VALUES($a, $s, $d, $o, $m);";
        cmd.Parameters.AddWithValue("$a", run.Agent);
        cmd.Parameters.AddWithValue("$s", run.Started.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$d", (long)run.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$o", run.Ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$m", run.Message);
        cmd.ExecuteNonQuery();
    }

    public List<AgentRunRecord> GetRuns(int limit = 100)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT agent, started, duration_ms, ok, message FROM agent_runs ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AgentRunRecord>();
        while (r.Read())
            list.Add(new AgentRunRecord(r.GetString(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)),
                TimeSpan.FromMilliseconds(r.GetInt64(2)), r.GetInt32(3) == 1, r.GetString(4)));
        return list;
    }

    // ───────────────────────────── Price history ─────────────────────────────

    public void UpsertCloses(string symbol, IEnumerable<(long Day, double Close)> closes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO price_history(symbol, day, close) VALUES($s, $d, $c) ON CONFLICT(symbol, day) DO UPDATE SET close=excluded.close;";
        var pS = cmd.Parameters.Add("$s", SqliteType.Text);
        var pD = cmd.Parameters.Add("$d", SqliteType.Integer);
        var pC = cmd.Parameters.Add("$c", SqliteType.Real);
        pS.Value = symbol;
        foreach (var (day, close) in closes)
        {
            if (double.IsNaN(close) || close <= 0) continue;
            pD.Value = day; pC.Value = close;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Daily closes (oldest first). Day = unix seconds at the bar's timestamp.</summary>
    public List<(long Day, double Close)> GetCloses(string symbol, int maxDays = 400)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT day, close FROM (SELECT day, close FROM price_history WHERE symbol=$s ORDER BY day DESC LIMIT $n) ORDER BY day ASC;";
        cmd.Parameters.AddWithValue("$s", symbol);
        cmd.Parameters.AddWithValue("$n", maxDays);
        using var r = cmd.ExecuteReader();
        var list = new List<(long, double)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetDouble(1)));
        return list;
    }

    public DateTimeOffset? LatestCloseDay(string symbol)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(day) FROM price_history WHERE symbol=$s;";
        cmd.Parameters.AddWithValue("$s", symbol);
        return cmd.ExecuteScalar() is long v ? DateTimeOffset.FromUnixTimeSeconds(v) : null;
    }

    // ───────────────────────────── Maintenance ─────────────────────────────

    public string Prune(int retentionDays)
    {
        using var c = Open();
        var sb = new StringBuilder();
        long Run(string sql, long threshold)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$t", threshold);
            return cmd.ExecuteNonQuery();
        }
        var now = Now();
        var items = Run("DELETE FROM items WHERE saved=0 AND published < $t;", now - retentionDays * 86400L);
        var llm = Run("DELETE FROM llm_cache WHERE created < $t;", now - 14 * 86400L);
        var seen = Run("DELETE FROM seen WHERE at < $t;", now - 10 * 86400L);
        var alerts = Run("DELETE FROM alerts WHERE created < $t;", now - 30 * 86400L);
        var http = Run("DELETE FROM http_cache WHERE updated < $t;", now - 30 * 86400L);
        Exec(c, "DELETE FROM agent_runs WHERE id NOT IN (SELECT id FROM agent_runs ORDER BY id DESC LIMIT 1500);");
        Exec(c, "INSERT INTO items_fts(items_fts) VALUES('optimize');");
        Exec(c, "PRAGMA incremental_vacuum(2000); PRAGMA optimize;");
        Log.Debug("keeper", $"pruned items={items}, llm={llm}, seen={seen}, alerts={alerts}, http={http}");
        var parts = new List<string>();
        if (items > 0) parts.Add(Plural.Of((int)items, "old article"));
        if (llm > 0) parts.Add(Plural.Of((int)llm, "stale AI result"));
        if (alerts > 0) parts.Add(Plural.Of((int)alerts, "old alert"));
        sb.Append(parts.Count == 0 ? "Database tidy — nothing to remove" : "Tidied up: removed " + string.Join(", ", parts));
        return sb.ToString();
    }

    public (long Items, long Saved, long CacheEntries, long Bytes) Stats()
    {
        using var c = Open();
        long Scalar(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar() is long v ? v : 0;
        }
        var bytes = 0L;
        try
        {
            bytes = new FileInfo(Path).Length;
            var wal = new FileInfo(Path + "-wal");
            if (wal.Exists) bytes += wal.Length;
        }
        catch { }
        return (Scalar("SELECT COUNT(*) FROM items;"), Scalar("SELECT COUNT(*) FROM items WHERE saved=1;"),
                Scalar("SELECT COUNT(*) FROM llm_cache;"), bytes);
    }

    public void ClearCaches()
    {
        using var c = Open();
        Exec(c, "DELETE FROM llm_cache; DELETE FROM http_cache; DELETE FROM kv;");
    }
}
