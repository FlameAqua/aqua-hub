using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace AquaHub.Core.Search;

internal sealed class SearchIndexLimitException() : InvalidOperationException("Search index limit reached. Remove a folder to make space.");

public sealed partial class SearchStore
{
    private readonly string _connection;
    private const int MaxFiles = 20000, MaxChunks = 16000;
    internal const string Space = "embeddinggemma-6fa0c02a/b11387/mean/768/title-query-v1";
    public SearchStore(string path)
    {
        _connection = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 3 }.ToString();
        using var db = Connect();
        using var setup = Sql(db, """
            PRAGMA auto_vacuum=INCREMENTAL;
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS roots(id INTEGER PRIMARY KEY AUTOINCREMENT,path TEXT NOT NULL UNIQUE,identity TEXT NOT NULL,content INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS docs(id INTEGER PRIMARY KEY AUTOINCREMENT,root INTEGER REFERENCES roots ON DELETE CASCADE,
                path TEXT NOT NULL,identity TEXT NOT NULL,size INTEGER NOT NULL,modified INTEGER NOT NULL,changed INTEGER NOT NULL,version INTEGER NOT NULL,UNIQUE(root,path));
            CREATE INDEX IF NOT EXISTS by_identity ON docs(root,identity);
            CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY AUTOINCREMENT,doc INTEGER REFERENCES docs ON DELETE CASCADE,
                text TEXT NOT NULL,space TEXT,vector BLOB);
            CREATE INDEX IF NOT EXISTS by_doc ON chunks(doc);
            CREATE VIRTUAL TABLE IF NOT EXISTS passage USING fts5(text,content='chunks',content_rowid='id');
            CREATE TRIGGER IF NOT EXISTS chunk_added AFTER INSERT ON chunks BEGIN INSERT INTO passage(rowid,text) VALUES(new.id,new.text); END;
            CREATE TRIGGER IF NOT EXISTS chunk_removed AFTER DELETE ON chunks BEGIN INSERT INTO passage(passage,rowid,text) VALUES('delete',old.id,old.text); END;
            CREATE TABLE IF NOT EXISTS options(paused INTEGER NOT NULL);
            INSERT INTO options SELECT 0 WHERE NOT EXISTS(SELECT 1 FROM options);
            """);
        setup.ExecuteNonQuery();
        using var migrate = Sql(db, "DELETE FROM docs WHERE version<>$v; UPDATE chunks SET vector=NULL,space=NULL WHERE space<>$s", ("$v", SearchText.Version), ("$s", Space));
        migrate.ExecuteNonQuery();
    }
    private SqliteConnection Connect()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        using var cap = Sql(db, "PRAGMA max_page_count=126976; PRAGMA wal_autocheckpoint=256;");
        cap.ExecuteNonQuery();
        return db;
    }

    private static SqliteCommand Sql(SqliteConnection db, string sql, params (string, object)[] values)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    public List<SearchRoot> Roots()
    {
        using var db = Connect();
        using var cmd = Sql(db, "SELECT id,path,identity,content FROM roots ORDER BY path");
        using var r = cmd.ExecuteReader();
        var roots = new List<SearchRoot>();
        while (r.Read()) roots.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)));
        return roots;
    }

    internal void Add(string path, string identity, bool content)
    {
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var read = Sql(db, "SELECT path,identity FROM roots");
        read.Transaction = tx;
        var count = 0;
        using (var r = read.ExecuteReader())
            while (r.Read())
            {
                count++;
                var existing = r.GetString(0);
                if (r.GetString(1) == identity || Inside(existing, path) || Inside(path, existing)) throw new InvalidOperationException("This folder overlaps an approved folder.");
            }
        if (count >= 16) throw new InvalidOperationException("Search supports up to 16 folders.");
        using var cmd = Sql(db, "INSERT INTO roots(path,identity,content) VALUES($p,$i,$c)", ("$p", path), ("$i", identity), ("$c", content));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    internal static bool Inside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal void Remove(long root)
    {
        using var db = Connect();
        using var cmd = Sql(db, "DELETE FROM roots WHERE id=$r; PRAGMA incremental_vacuum(128);", ("$r", root));
        cmd.ExecuteNonQuery();
    }

    internal void ChangeContent(SearchRoot root, bool content)
    {
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var remove = Sql(db, "DELETE FROM roots WHERE id=$r AND identity=$i", ("$r", root.Id), ("$i", root.Identity));
        remove.Transaction = tx;
        if (remove.ExecuteNonQuery() != 1) throw new InvalidOperationException("Folder approval has changed.");
        using var add = Sql(db, "INSERT INTO roots(path,identity,content) VALUES($p,$i,$c)", ("$p", root.Path), ("$i", root.Identity), ("$c", content));
        add.Transaction = tx;
        add.ExecuteNonQuery();
        tx.Commit();
    }

    public bool Paused
    {
        get { using var db = Connect(); using var cmd = Sql(db, "SELECT paused FROM options"); return Convert.ToInt32(cmd.ExecuteScalar()) != 0; }
        internal set { using var db = Connect(); using var cmd = Sql(db, "UPDATE options SET paused=$p", ("$p", value)); cmd.ExecuteNonQuery(); }
    }

    internal List<(string Path, SearchStamp Stamp)> Inventory(long root)
    {
        using var db = Connect();
        using var cmd = Sql(db, "SELECT path,identity,size,modified,changed FROM docs WHERE root=$r", ("$r", root));
        using var r = cmd.ExecuteReader();
        var rows = new List<(string, SearchStamp)>();
        while (r.Read()) rows.Add((r.GetString(0), new(r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4))));
        return rows;
    }

    internal void Forget(long root, string path)
    {
        using var db = Connect();
        using var cmd = Sql(db, "DELETE FROM docs WHERE root=$r AND path=$p", ("$r", root), ("$p", path));
        cmd.ExecuteNonQuery();
    }

    internal void Replace(SearchRoot root, string path, SearchStamp stamp, List<string> chunks)
    {
        using var db = Connect();
        using var tx = db.BeginTransaction();
        using var grant = Sql(db, "SELECT content FROM roots WHERE id=$r AND identity=$i", ("$r", root.Id), ("$i", root.Identity));
        grant.Transaction = tx;
        if (grant.ExecuteScalar() is not long content || root.Content != (content != 0)) return;
        using var remove = Sql(db, "DELETE FROM docs WHERE root=$r AND (path=$p OR identity=$i)", ("$r", root.Id), ("$p", path), ("$i", stamp.Identity));
        remove.Transaction = tx;
        remove.ExecuteNonQuery();
        using var count = Sql(db, "SELECT (SELECT count(*) FROM docs)>=$f OR (SELECT count(*) FROM chunks)+$n>$c",
            ("$f", MaxFiles), ("$n", chunks.Count), ("$c", MaxChunks));
        count.Transaction = tx;
        if ((long)count.ExecuteScalar()! != 0) throw new SearchIndexLimitException();
        using var doc = Sql(db, """
            INSERT INTO docs(root,path,identity,size,modified,changed,version) VALUES($r,$p,$i,$s,$m,$change,$v) RETURNING id
            """, ("$r", root.Id), ("$p", path), ("$i", stamp.Identity), ("$s", stamp.Length), ("$m", stamp.Modified), ("$change", stamp.Changed), ("$v", SearchText.Version));
        doc.Transaction = tx;
        var id = (long)doc.ExecuteScalar()!;
        using var insert = Sql(db, "INSERT INTO chunks(doc,text) VALUES($d,$t)", ("$d", id), ("$t", ""));
        insert.Transaction = tx;
        if (root.Content) foreach (var chunk in chunks)
        {
            insert.Parameters["$t"].Value = chunk;
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal (long Id, SearchHit Hit, string Text)? NextPassage(long after)
    {
        using var db = Connect();
        using var cmd = Sql(db, """
            SELECT c.id,d.root,d.path,d.identity,d.size,d.modified,d.changed,c.text FROM chunks c JOIN docs d ON c.doc=d.id
            WHERE c.vector IS NULL AND c.id>$after ORDER BY c.id LIMIT 1
            """, ("$after", after));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), new(r.GetInt64(1), r.GetString(2), new(r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)), "", ""), r.GetString(7)) : null;
    }

    internal void SaveVector(long chunk, float[] vector)
    {
        var bytes = new byte[768 * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        using var db = Connect();
        using var cmd = Sql(db, "UPDATE chunks SET space=$s,vector=$v WHERE id=$id AND vector IS NULL",
            ("$s", Space), ("$v", bytes), ("$id", chunk));
        cmd.ExecuteNonQuery();
    }

    internal void ReleaseConnections()
    {
        using var connection = new SqliteConnection(_connection);
        SqliteConnection.ClearPool(connection);
    }

    public int Count()
    {
        using var db = Connect();
        using var cmd = Sql(db, "SELECT count(*) FROM docs");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
