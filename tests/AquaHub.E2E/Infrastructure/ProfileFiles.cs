using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AquaHub.E2E.Infrastructure;

public sealed record JournalEntry(int Index, string Time, string Action, string Detail)
{
    public override string ToString() => $"{Time} {Action} {Detail}";
}

/// <summary>
/// Reads <c>e2e-journal.log</c>: in dry-run mode the app records every side effect it would have had on the PC
/// (launch, open-url, media, volume, speak, clipboard, toast, unload-model, …) instead of performing it.
/// </summary>
public sealed class Journal
{
    private readonly string _path;
    public Journal(string profileDir) => _path = Path.Combine(profileDir, "e2e-journal.log");

    public string FilePath => _path;

    public List<JournalEntry> ReadAll()
    {
        var text = FileUtil.ReadShared(_path);
        var list = new List<JournalEntry>();
        var i = 0;
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0) continue;
            var parts = l.Split('\t');
            list.Add(new JournalEntry(i++, parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "", parts.ElementAtOrDefault(2) ?? ""));
        }
        return list;
    }

    /// <summary>Position to compare against later (number of entries so far).</summary>
    public int Mark() => ReadAll().Count;

    public List<JournalEntry> Since(int mark) => ReadAll().Skip(mark).ToList();

    public JournalEntry? Find(int mark, string action, Func<string, bool>? detail = null) =>
        Since(mark).FirstOrDefault(e => e.Action == action && (detail?.Invoke(e.Detail) ?? true));

    /// <summary>Waits until an entry with the action (and optional detail predicate) appears after the mark.</summary>
    public JournalEntry WaitFor(int mark, string action, Func<string, bool>? detail = null, TimeSpan? timeout = null, string? what = null)
    {
        JournalEntry? hit = null;
        if (!Wait.Until(() => (hit = Find(mark, action, detail)) is not null, timeout ?? E2EConfig.UiTimeout, 120))
        {
            var seen = string.Join(" | ", Since(mark).Select(e => e.ToString()));
            throw new TimeoutException($"Journal: expected '{action}'{(what is null ? "" : " (" + what + ")")} but saw: [{seen}]");
        }
        return hit!;
    }

    /// <summary>Waits for any of the given actions (e.g. media vs media-key depending on whether a media session exists).</summary>
    public JournalEntry WaitForAny(int mark, IReadOnlyCollection<string> actions, Func<JournalEntry, bool>? filter = null, TimeSpan? timeout = null)
    {
        JournalEntry? hit = null;
        if (!Wait.Until(() => (hit = Since(mark).FirstOrDefault(e => actions.Contains(e.Action) && (filter?.Invoke(e) ?? true))) is not null,
                timeout ?? E2EConfig.UiTimeout, 120))
        {
            var seen = string.Join(" | ", Since(mark).Select(e => e.ToString()));
            throw new TimeoutException($"Journal: expected one of [{string.Join(", ", actions)}] but saw: [{seen}]");
        }
        return hit!;
    }
}

/// <summary>Reads the profile's settings.json (camelCase, comments allowed).</summary>
public sealed class SettingsFile
{
    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private readonly string _path;
    public SettingsFile(string profileDir) => _path = Path.Combine(profileDir, "settings.json");

    public string FilePath => _path;

    public JsonNode Read()
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return JsonNode.Parse(FileUtil.ReadShared(_path), documentOptions: DocOptions) ?? new JsonObject();
            }
            catch (Exception ex) when ((ex is JsonException or IOException) && i < 10)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Resolves a dotted path such as <c>notifications.doNotDisturb</c> or <c>markets.watchlist[0].symbol</c>.</summary>
    public static JsonNode? Resolve(JsonNode? node, string path)
    {
        foreach (var raw in path.Split('.'))
        {
            if (node is null) return null;
            var part = raw;
            int? index = null;
            var b = part.IndexOf('[');
            if (b >= 0)
            {
                index = int.Parse(part[(b + 1)..part.IndexOf(']')]);
                part = part[..b];
            }
            if (part.Length > 0) node = node is JsonObject o && o.TryGetPropertyValue(part, out var child) ? child : null;
            if (index is { } ix) node = node is JsonArray a && ix < a.Count ? a[ix] : null;
        }
        return node;
    }

    public JsonNode? Get(string path) => Resolve(Read(), path);

    public string? GetString(string path) => Get(path) is JsonValue v && v.TryGetValue<string>(out var s) ? s : Get(path)?.ToJsonString();

    public bool? GetBool(string path) => Get(path) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public double? GetNumber(string path) => Get(path) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    public List<string> GetStrings(string path) =>
        Get(path) is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "").ToList() : new();

    public List<string> Symbols(string listPath) =>
        Get(listPath) is JsonArray a ? a.Select(n => n is JsonObject o && o["symbol"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList() : new();

    public int Count(string listPath) => Get(listPath) is JsonArray a ? a.Count : -1;

    public void WaitFor(string path, Func<JsonNode?, bool> predicate, string what, TimeSpan? timeout = null)
    {
        if (!Wait.Until(() => predicate(Get(path)), timeout ?? E2EConfig.PersistTimeout, 150))
            throw new TimeoutException($"settings.json: {path} never satisfied '{what}' (now: {Get(path)?.ToJsonString() ?? "<missing>"})");
    }

    public void WaitForBool(string path, bool expected, TimeSpan? timeout = null) =>
        WaitFor(path, n => n is JsonValue v && v.TryGetValue<bool>(out var b) && b == expected, $"== {expected}", timeout);

    public void WaitForString(string path, string expected, TimeSpan? timeout = null) =>
        WaitFor(path, n => n is JsonValue v && v.TryGetValue<string>(out var s) && s == expected, $"== \"{expected}\"", timeout);

    public static string Value(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "<missing>";

    /// <summary>Edits a settings.json on disk (only used on a profile copy before the app starts).</summary>
    public static void Edit(string profileDir, Action<JsonObject> edit)
    {
        var path = Path.Combine(profileDir, "settings.json");
        var root = (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8), documentOptions: DocOptions) as JsonObject)
                   ?? throw new InvalidOperationException("settings.json is not an object");
        edit(root);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
    }
}

/// <summary>Reads the app log (<c>logs\aquahub.log</c>) and spots crashes.</summary>
public sealed class AppLog
{
    private readonly string _path;
    public AppLog(string profileDir) => _path = Path.Combine(profileDir, "logs", "aquahub.log");

    public string FilePath => _path;

    public List<string> Lines() => FileUtil.ReadShared(_path).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

    public int Mark() => Lines().Count;

    public List<string> Since(int mark) => Lines().Skip(mark).ToList();

    public static bool IsCrash(string line) =>
        line.Contains("Unhandled UI exception", StringComparison.Ordinal) || line.Contains("Unhandled exception", StringComparison.Ordinal);

    public List<string> CrashesSince(int mark) => Since(mark).Where(IsCrash).ToList();

    public List<string> ErrorsSince(int mark) => Since(mark).Where(l => l.Contains(" ERROR ", StringComparison.Ordinal)).ToList();

    public string WaitForLine(int mark, Func<string, bool> predicate, string what, TimeSpan? timeout = null)
    {
        string? hit = null;
        if (!Wait.Until(() => (hit = Since(mark).FirstOrDefault(predicate)) is not null, timeout ?? E2EConfig.UiTimeout, 200))
            throw new TimeoutException($"Log: never saw {what}");
        return hit!;
    }
}

public static class FileUtil
{
    /// <summary>Reads a file that another process may be appending to.</summary>
    public static string ReadShared(string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < 20)
            {
                Thread.Sleep(50);
            }
        }
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target), overwrite: true);
    }
}
