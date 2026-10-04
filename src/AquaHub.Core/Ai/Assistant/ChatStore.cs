using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Analysis;
using AquaHub.Core.Data;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>One chat in the history list.</summary>
public sealed class ChatSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    /// <summary>Starred chats are kept forever; others are deleted after Settings › Ask Aqua › "Delete chats after".</summary>
    public bool Starred { get; set; }
    public int Count { get; set; }
    /// <summary>The start of the last answer, for the history list's tooltip.</summary>
    public string Preview { get; set; } = "";
    /// <summary>The model named it (otherwise it's the first question, shortened).</summary>
    public bool Named { get; set; }
    /// <summary>Tokens the last answer used and the window it had (the context meter).</summary>
    public int ContextUsed { get; set; }
    public int ContextWindow { get; set; }
    /// <summary>You undid a compression: this chat isn't compressed on its own again (the Compress button still works).</summary>
    public bool NoAutoCompress { get; set; }
    /// <summary>
    /// An answer in this chat used your files, screen, attachments or holdings: what it read can be in the chat's messages,
    /// notes and summary, so later answers ask before reaching the web. Once set, it stays.
    /// </summary>
    public bool ReadPrivate { get; set; }
}

/// <summary>A message as stored (the same shape older versions used for their single conversation).</summary>
public sealed record SavedChatMessage(bool User, string Text, List<Citation>? Citations, string? Footer, bool Chat,
    List<string>? Steps = null, string? Reasoning = null, List<string>? Attachments = null, string? Mode = null, string? Question = null)
{
    public AskOptions? Options { get; init; }
    public DateTimeOffset? At { get; init; }
    /// <summary>What the answer read (excerpts), so a later question in the chat can use it again; kept for recent answers only.</summary>
    public List<ChatSource>? Sources { get; init; }
    /// <summary>Pictures: the ones you attached to a question, the ones Ask looked at for an answer.</summary>
    public List<SavedPicture>? Pictures { get; init; }
}

/// <summary>A picture kept with a message: a screenshot (whole, in the chat's folder) or a picture on the PC (its path and a thumbnail).</summary>
public sealed record SavedPicture(string Name, string Kind)
{
    /// <summary>The picture on the PC (files only).</summary>
    public string? Path { get; init; }
    /// <summary>Its file in the chat's picture folder (<see cref="ChatMedia"/>): the whole screenshot, or a file's thumbnail.</summary>
    public string? File { get; init; }
    public bool? Match { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// Ask's chat history in the local database: an index of chats plus each chat's messages, notes and compression, and a
/// folder per chat for the pictures it keeps. Nothing leaves the PC. Unstarred chats expire after the number of days in
/// settings (0 keeps them), and everything a chat keeps goes with it; the single conversation older versions kept is
/// imported as a chat once.
/// </summary>
public sealed partial class ChatStore
{
    private const string IndexKey = "ask:chats";
    private const string ChatPrefix = "ask:chat:";
    private const string NotesPrefix = "ask:chat-notes:";
    private const string CompressionPrefix = "ask:chat-compressed:";
    private const string LegacyKey = "ask:conversation";
    public const int MaxChats = 500;
    public const int MaxMessagesPerChat = 300;
    private readonly HubDatabase? _db;
    private readonly string? _mediaRoot;
    private readonly object _gate = new();
    private List<ChatSummary>? _index;
    // Without a database (tests), chats live in memory.
    private readonly Dictionary<string, List<SavedChatMessage>> _memory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ChatNote>> _memoryNotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatCompression> _memoryCompression = new(StringComparer.Ordinal);
    // Chats deleted while Aqua runs: a late save (an answer that was still streaming when you deleted its chat) can't bring
    // one back, with its notes or pictures.
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    /// <param name="mediaRoot">Where chats keep their pictures (a folder per chat); null keeps none.</param>
    public ChatStore(HubDatabase? db, string? mediaRoot = null)
    {
        _db = db;
        _mediaRoot = mediaRoot;
    }

    /// <summary>Raised after the list changes (any thread).</summary>
    public event Action? Changed;

    private List<ChatSummary> Index()
    {
        _index ??= _db?.GetJson<List<ChatSummary>>(IndexKey) ?? new();
        return _index;
    }

    private void SaveIndex() => _db?.PutJson(IndexKey, _index ?? new());

    /// <summary>All chats, most recently used first.</summary>
    public List<ChatSummary> List()
    {
        lock (_gate) return Index().OrderByDescending(c => c.Updated).Select(Copy).ToList();
    }

    public ChatSummary? Get(string id)
    {
        lock (_gate) return Index().FirstOrDefault(c => c.Id == id) is { } c ? Copy(c) : null;
    }

    private static ChatSummary Copy(ChatSummary c) => new()
    {
        Id = c.Id, Title = c.Title, Created = c.Created, Updated = c.Updated, Starred = c.Starred, Count = c.Count, Preview = c.Preview,
        Named = c.Named, ContextUsed = c.ContextUsed, ContextWindow = c.ContextWindow, NoAutoCompress = c.NoAutoCompress,
        ReadPrivate = c.ReadPrivate,
    };

    public List<SavedChatMessage> Load(string id)
    {
        lock (_gate)
        {
            if (_db is null) return _memory.TryGetValue(id, out var m) ? m.ToList() : new();
            return _db.GetJson<List<SavedChatMessage>>(ChatPrefix + id) ?? new();
        }
    }

    /// <summary>Saves a chat's messages and its entry in the list (created if new; not for a chat deleted since Aqua started).</summary>
    public void Save(ChatSummary summary, IReadOnlyList<SavedChatMessage> messages)
    {
        lock (_gate)
        {
            if (_deleted.Contains(summary.Id)) return;
            var list = Index();
            var keep = messages.TakeLast(MaxMessagesPerChat).ToList();
            var existing = list.FirstOrDefault(c => c.Id == summary.Id);
            if (existing is null)
            {
                existing = Copy(summary);
                list.Add(existing);
                // Keep the list bounded: the oldest unstarred chats go first.
                while (list.Count > MaxChats && list.Where(c => !c.Starred).OrderBy(c => c.Updated).FirstOrDefault() is { } oldest)
                {
                    list.Remove(oldest);
                    DeleteMessages(oldest.Id);
                }
            }
            else
            {
                existing.Title = summary.Title; existing.Updated = summary.Updated; existing.Starred = summary.Starred; existing.Named = summary.Named;
                existing.ContextUsed = summary.ContextUsed; existing.ContextWindow = summary.ContextWindow; existing.NoAutoCompress = summary.NoAutoCompress;
                existing.ReadPrivate |= summary.ReadPrivate;
            }
            if (keep.Count < messages.Count)
            {
                Rebase(summary.Id, messages, messages.Count - keep.Count);
                TidyPictures(summary.Id, keep);
            }
            existing.Count = keep.Count;
            existing.Preview = HtmlText.Truncate(keep.LastOrDefault(m => !m.User)?.Text.ReplaceLineEndings(" ") ?? "", 140);
            if (_db is null) _memory[summary.Id] = keep;
            else _db.PutJson(ChatPrefix + summary.Id, keep);
            SaveIndex();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Whether a chat's messages show an answer used private data, for chats saved before <see cref="ChatSummary.ReadPrivate"/>
    /// was kept: attachments, files or folders among the sources, or pictures (screenshots and pictures on the PC).
    /// </summary>
    public static bool ShowsPrivate(IEnumerable<SavedChatMessage> messages) =>
        messages.Any(m => m.Attachments is { Count: > 0 } || m.Pictures is { Count: > 0 }
                          || (m.Citations ?? new()).Any(c => c.Kind is "file" or "folder") || (m.Sources ?? new()).Any(s => s.Kind is "file" or "folder"));

    /// <summary>Changes a chat's entry (title, star) without touching its messages.</summary>
    public void Update(string id, Action<ChatSummary> change)
    {
        lock (_gate)
        {
            if (Index().FirstOrDefault(c => c.Id == id) is not { } c) return;
            change(c);
            SaveIndex();
        }
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            _deleted.Add(id);
            if (Index().RemoveAll(c => c.Id == id) == 0) return;
            DeleteMessages(id);
            SaveIndex();
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes every chat (starred too) — "Remember Ask chats" switched off.</summary>
    public void DeleteAll()
    {
        lock (_gate)
        {
            foreach (var c in Index()) DeleteMessages(c.Id);
            _deleted.UnionWith(Index().Select(c => c.Id));
            Index().Clear();
            SaveIndex();
            _db?.DeleteJson(LegacyKey);
            DeleteFolder(_mediaRoot);
        }
        Changed?.Invoke();
    }

    /// <summary>Everything a chat keeps: its messages, notes, compression and pictures (and it can't be saved again).</summary>
    private void DeleteMessages(string id)
    {
        _deleted.Add(id);
        if (_db is null)
        {
            _memory.Remove(id);
            _memoryNotes.Remove(id);
            _memoryCompression.Remove(id);
        }
        else
        {
            _db.DeleteJson(ChatPrefix + id);
            _db.DeleteJson(NotesPrefix + id);
            _db.DeleteJson(CompressionPrefix + id);
        }
        DeleteFolder(FolderOf(id));
    }

    /// <summary>
    /// A chat over the message limit loses its oldest messages: its summary goes on standing in for the ones it covered
    /// that remain (a long chat keeps its compression just when it needs it). When more go than it covered, it goes too.
    /// </summary>
    private void Rebase(string id, IReadOnlyList<SavedChatMessage> all, int dropped)
    {
        var compression = _db is null ? _memoryCompression.GetValueOrDefault(id) : _db.GetJson<ChatCompression>(CompressionPrefix + id);
        if (compression is null) return;
        var turns = all.Select(ChatCompressor.TurnOf).OfType<ChatTurn>().ToList();
        var gone = all.Take(dropped).Count(m => ChatCompressor.TurnOf(m) is not null);
        ChatCompression? rebased = null;
        if (ChatCompressor.Valid(compression, turns) is { } valid && valid.Covers > gone)
            rebased = valid with { Covers = valid.Covers - gone, Fingerprint = ChatCompressor.Fingerprint(turns.Skip(gone).ToList(), valid.Covers - gone) };
        if (_db is null)
        {
            if (rebased is null) _memoryCompression.Remove(id);
            else _memoryCompression[id] = rebased;
        }
        else if (rebased is null) _db.DeleteJson(CompressionPrefix + id);
        else _db.PutJson(CompressionPrefix + id, rebased);
    }

    /// <summary>Deletes the pictures in a chat's folder that none of <paramref name="kept"/> messages use any more.</summary>
    private void TidyPictures(string id, IReadOnlyList<SavedChatMessage> kept)
    {
        if (FolderOf(id) is not { } folder || !Directory.Exists(folder)) return;
        var used = kept.SelectMany(m => m.Pictures ?? new()).Select(p => p.File).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
                if (!file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !used.Contains(Path.GetFileName(file))) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug("ask", "Couldn't tidy a chat's pictures: " + ex.Message); }
    }

    // ───────────────────────────── Notes and compression ─────────────────────────────

    public List<ChatNote> LoadNotes(string id)
    {
        lock (_gate)
        {
            if (_db is null) return _memoryNotes.TryGetValue(id, out var n) ? n.ToList() : new();
            return _db.GetJson<List<ChatNote>>(NotesPrefix + id) ?? new();
        }
    }

    /// <summary>Saves a chat's notes (for a chat in the list; none removes them).</summary>
    public void SaveNotes(string id, IReadOnlyList<ChatNote> notes)
    {
        lock (_gate)
        {
            if (!Index().Any(c => c.Id == id)) return;
            if (_db is null)
            {
                if (notes.Count == 0) _memoryNotes.Remove(id);
                else _memoryNotes[id] = notes.ToList();
            }
            else if (notes.Count == 0) _db.DeleteJson(NotesPrefix + id);
            else _db.PutJson(NotesPrefix + id, notes.ToList());
        }
    }

    public ChatCompression? LoadCompression(string id)
    {
        lock (_gate)
        {
            if (_db is null) return _memoryCompression.TryGetValue(id, out var c) ? c : null;
            return _db.GetJson<ChatCompression>(CompressionPrefix + id);
        }
    }

    /// <summary>Saves (or with null, removes — Undo) a chat's compression.</summary>
    public void SaveCompression(string id, ChatCompression? compression)
    {
        lock (_gate)
        {
            if (!Index().Any(c => c.Id == id)) return;
            if (_db is null)
            {
                if (compression is null) _memoryCompression.Remove(id);
                else _memoryCompression[id] = compression;
            }
            else if (compression is null) _db.DeleteJson(CompressionPrefix + id);
            else _db.PutJson(CompressionPrefix + id, compression);
        }
    }

    // ───────────────────────────── Pictures ─────────────────────────────

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,40}\z")]
    private static partial Regex SafeId();

    /// <summary>
    /// The folder a chat keeps its pictures in (it may not exist yet); null without one, for an id that isn't a plain name,
    /// or for a chat deleted since Aqua started (nothing is written for it again).
    /// </summary>
    public string? MediaFolder(string id)
    {
        lock (_gate) if (_deleted.Contains(id)) return null;
        return FolderOf(id);
    }

    private string? FolderOf(string id) => _mediaRoot is null || !SafeId().IsMatch(id) ? null : Path.Combine(_mediaRoot, id);

    /// <summary>Deletes every chat's pictures ("Keep pictures with chats" switched off).</summary>
    public void DeleteAllMedia() => DeleteFolder(_mediaRoot);

    /// <summary>How much room the kept pictures take, and in how many chats (shown beside the setting).</summary>
    public (long Bytes, int Chats) PictureUse()
    {
        if (_mediaRoot is null || !Directory.Exists(_mediaRoot)) return (0, 0);
        try
        {
            var folders = Directory.EnumerateDirectories(_mediaRoot).Select(f => new DirectoryInfo(f).EnumerateFiles().Sum(x => x.Length)).Where(n => n > 0).ToList();
            return (folders.Sum(), folders.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (0, 0); }
    }

    /// <summary>
    /// "Keep pictures with chats" switched off: every chat's pictures go, and with them what its messages kept about each
    /// one — the picture's path on the PC and what it showed. Returns how many chats changed.
    /// </summary>
    public int ForgetPictures()
    {
        var changed = 0;
        List<string> ids;
        lock (_gate) ids = Index().Select(c => c.Id).ToList();
        foreach (var id in ids)
            lock (_gate)
            {
                var messages = Load(id);
                if (!messages.Any(m => m.Pictures is { Count: > 0 })) continue;
                PutMessages(id, messages.Select(m => m.Pictures is null ? m : m with { Pictures = null }).ToList());
                changed++;
            }
        DeleteAllMedia();
        return changed;
    }

    private void PutMessages(string id, List<SavedChatMessage> messages)
    {
        if (_db is null) _memory[id] = messages;
        else _db.PutJson(ChatPrefix + id, messages);
    }

    private const string TidiedKey = "ask:chats-tidied";

    /// <summary>
    /// Once: rewrites the chats saved by versions before 1.3, which kept the paths of the folders you attached in each
    /// message's options (they're never saved now). Returns how many chats changed.
    /// </summary>
    public int ForgetFolders()
    {
        if (_db is null || _db.GetJson<int>(TidiedKey) >= 1) return 0;
        var changed = 0;
        List<string> ids;
        lock (_gate) ids = Index().Select(c => c.Id).ToList();
        foreach (var id in ids)
            lock (_gate)
            {
                var raw = _db.GetJson<JsonElement>(ChatPrefix + id);
                if (raw.ValueKind != JsonValueKind.Array || !raw.EnumerateArray().Any(HasFolders)) continue;
                // Folders aren't serialised any more: read and written again, the paths are gone.
                PutMessages(id, Load(id));
                changed++;
            }
        _db.PutJson(TidiedKey, 1);
        return changed;

        static bool HasFolders(JsonElement m) =>
            Property(m, "options") is { ValueKind: JsonValueKind.Object } o && Property(o, "folders") is { ValueKind: JsonValueKind.Array } f && f.GetArrayLength() > 0;

        // Names as the database writes them (camelCase), or as anything else did.
        static JsonElement Property(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object) return default;
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return default;
        }
    }

    /// <summary>Deletes picture folders whose chat is gone (one deleted while Aqua was closing, say). Returns how many.</summary>
    public int PruneMedia()
    {
        if (_mediaRoot is null || !Directory.Exists(_mediaRoot)) return 0;
        HashSet<string> ids;
        lock (_gate) ids = Index().Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var gone = 0;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(_mediaRoot))
            {
                if (ids.Contains(Path.GetFileName(folder))) continue;
                DeleteFolder(folder);
                gone++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug("ask", "Couldn't tidy chat pictures: " + ex.Message); }
        return gone;
    }

    private static void DeleteFolder(string? folder)
    {
        if (folder is null) return;
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ask", "Couldn't delete a chat's pictures", ex); }
    }

    /// <summary>Deletes unstarred chats not used for <paramref name="days"/> days (0 = keep everything). Returns how many.</summary>
    public int Prune(DateTimeOffset now, int days, string? keepId = null)
    {
        if (days <= 0) return 0;
        List<ChatSummary> old;
        lock (_gate)
        {
            var cutoff = now.AddDays(-days);
            old = Index().Where(c => !c.Starred && c.Updated < cutoff && c.Id != keepId).ToList();
            if (old.Count == 0) return 0;
            foreach (var c in old) { Index().Remove(c); DeleteMessages(c.Id); }
            SaveIndex();
        }
        Log.Info("ask", $"Deleted {old.Count} chat(s) older than {days} days");
        Changed?.Invoke();
        return old.Count;
    }

    /// <summary>Chats whose title or messages contain every word of <paramref name="query"/>, newest first, with a snippet.</summary>
    public List<(ChatSummary Chat, string Snippet)> Search(string query, int max = 40)
    {
        var words = TextTools.Tokenize(query).Where(w => w.Length > 1).ToList();
        if (words.Count == 0) return List().Take(max).Select(c => (c, "")).ToList();
        var found = new List<(ChatSummary, string)>();
        foreach (var chat in List())
        {
            var title = TextTools.Fold(chat.Title).ToLowerInvariant();
            if (words.All(title.Contains)) { found.Add((chat, "")); }
            else
            {
                var text = string.Join("\n", Load(chat.Id).Select(m => m.Text));
                var folded = TextTools.Fold(text).ToLowerInvariant();
                if (!words.All(w => folded.Contains(w, StringComparison.Ordinal))) continue;
                var at = folded.IndexOf(words[0], StringComparison.Ordinal);
                var start = Math.Max(0, at - 40);
                found.Add((chat, "…" + text.Substring(start, Math.Min(120, text.Length - start)).ReplaceLineEndings(" ").Trim() + "…"));
            }
            if (found.Count >= max) break;
        }
        return found;
    }

    /// <summary>Imports the single conversation older versions kept (once). Returns the new chat, if there was one.</summary>
    public ChatSummary? ImportLegacy(DateTimeOffset now)
    {
        if (_db is null) return null;
        List<SavedChatMessage>? legacy;
        lock (_gate) legacy = _db.GetJson<List<SavedChatMessage>>(LegacyKey);
        if (legacy is null) return null;
        _db.DeleteJson(LegacyKey);
        if (legacy.Count == 0) return null;
        var first = legacy.FirstOrDefault(m => m.User)?.Text ?? "Earlier chat";
        var summary = new ChatSummary { Id = NewId(now), Title = TitleFrom(first), Created = now, Updated = now };
        Save(summary, legacy);
        return summary;
    }

    public static string NewId(DateTimeOffset now) =>
        "c" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);

    /// <summary>A title from the first question: its first line or sentence, polite openers and "Tell me more about:" removed.</summary>
    public static string TitleFrom(string question)
    {
        var t = question.ReplaceLineEndings(" ").Trim();
        t = Regex.Replace(t, @"^(?:(?:hey|hi|ok|okay)\s+)?(?:aqua[,:]?\s+)?(?:(?:please|can you|could you|would you)\s+)*(?:tell me more about:?\s*)?", "", RegexOptions.IgnoreCase).Trim();
        var cut = t.IndexOfAny(new[] { '?', '.', '!', '\n' });
        if (cut > 12) t = t[..cut];
        t = Regex.Replace(t, @"\s+", " ").Trim().TrimEnd(',', ';', ':');
        if (t.Length == 0) t = "New chat";
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return HtmlText.Truncate(t, 60);
    }
}
