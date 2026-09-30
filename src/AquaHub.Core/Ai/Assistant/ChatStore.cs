using System.Globalization;
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
}

/// <summary>A message as stored (the same shape older versions used for their single conversation).</summary>
public sealed record SavedChatMessage(bool User, string Text, List<Citation>? Citations, string? Footer, bool Chat,
    List<string>? Steps = null, string? Reasoning = null, List<string>? Attachments = null, string? Mode = null, string? Question = null)
{
    public AskOptions? Options { get; init; }
    public DateTimeOffset? At { get; init; }
    /// <summary>What the answer read (excerpts), so a later question in the chat can use it again; kept for recent answers only.</summary>
    public List<ChatSource>? Sources { get; init; }
}

/// <summary>
/// Ask's chat history in the local database: an index of chats plus each chat's messages. Nothing leaves the PC.
/// Unstarred chats expire after the number of days in settings (0 keeps them); the single conversation older versions
/// kept is imported as a chat once.
/// </summary>
public sealed class ChatStore
{
    private const string IndexKey = "ask:chats";
    private const string ChatPrefix = "ask:chat:";
    private const string LegacyKey = "ask:conversation";
    public const int MaxChats = 500;
    public const int MaxMessagesPerChat = 300;
    private readonly HubDatabase? _db;
    private readonly object _gate = new();
    private List<ChatSummary>? _index;
    // Without a database (tests), chats live in memory.
    private readonly Dictionary<string, List<SavedChatMessage>> _memory = new(StringComparer.Ordinal);

    public ChatStore(HubDatabase? db) => _db = db;

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
        Named = c.Named, ContextUsed = c.ContextUsed, ContextWindow = c.ContextWindow,
    };

    public List<SavedChatMessage> Load(string id)
    {
        lock (_gate)
        {
            if (_db is null) return _memory.TryGetValue(id, out var m) ? m.ToList() : new();
            return _db.GetJson<List<SavedChatMessage>>(ChatPrefix + id) ?? new();
        }
    }

    /// <summary>Saves a chat's messages and its entry in the list (created if new).</summary>
    public void Save(ChatSummary summary, IReadOnlyList<SavedChatMessage> messages)
    {
        lock (_gate)
        {
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
                existing.ContextUsed = summary.ContextUsed; existing.ContextWindow = summary.ContextWindow;
            }
            existing.Count = keep.Count;
            existing.Preview = HtmlText.Truncate(keep.LastOrDefault(m => !m.User)?.Text.ReplaceLineEndings(" ") ?? "", 140);
            if (_db is null) _memory[summary.Id] = keep;
            else _db.PutJson(ChatPrefix + summary.Id, keep);
            SaveIndex();
        }
        Changed?.Invoke();
    }

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
            Index().Clear();
            SaveIndex();
            _db?.DeleteJson(LegacyKey);
        }
        Changed?.Invoke();
    }

    private void DeleteMessages(string id)
    {
        if (_db is null) _memory.Remove(id);
        else _db.DeleteJson(ChatPrefix + id);
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
