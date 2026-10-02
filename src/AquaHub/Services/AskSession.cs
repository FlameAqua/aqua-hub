using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Net;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.UI.ViewModels;

namespace AquaHub.Services;

/// <summary>One chat: its messages, notes and compression, what was allowed for it, and the answer being written (if any).</summary>
public sealed class ChatThread
{
    public ChatThread(ChatNotes? notes = null) => Notes = notes ?? new ChatNotes();

    public ChatSummary? Summary { get; set; }
    public ObservableCollection<ChatMessageVM> Messages { get; } = new();
    public HashSet<string> AllowedForChat { get; } = new(StringComparer.Ordinal);
    /// <summary>Folders attached to this chat: Ask may search and read them while the chat is open (not kept with it).</summary>
    public List<string> Folders { get; } = new();
    public CancellationTokenSource? Cts { get; set; }
    public bool IsBusy { get; set; }
    /// <summary>Tokens the last answer used and the window it had (the context meter).</summary>
    public int ContextUsed { get; set; }
    public int ContextWindow { get; set; }
    /// <summary>The chat's notes: read with every question, kept with the chat.</summary>
    public ChatNotes Notes { get; }
    /// <summary>The summary standing in for the chat's first messages (null until it's compressed).</summary>
    public ChatCompression? Compression { get; set; }
    /// <summary>A summary is being written.</summary>
    public bool Compressing { get; set; }
    /// <summary>How many messages the chat had when an automatic compression last failed (it waits for the chat to grow).</summary>
    public int AutoFailedAt { get; set; }
    public string Id => Summary?.Id ?? "";
}

/// <summary>
/// Ask Aqua's chats. They live here rather than in the page, so answers keep streaming while you look at another page
/// or another chat, and (unless turned off in Privacy) every chat is kept in the local database — unstarred ones until
/// they expire (Settings › Ask Aqua). Also holds the composer's switches (Web, Think, Research, Use my PC), the
/// attachments waiting to be sent, and voice input.
/// </summary>
public sealed class AskSession
{
    private readonly Dictionary<string, ChatThread> _open = new(StringComparer.Ordinal);
    private bool _loaded;
    private string _filter = "";

    public AskSession()
    {
        // Turning "Keep Ask chats" off deletes the stored ones straight away; "Keep pictures with chats" off, their pictures.
        Hub.Core.Settings.Changed += s =>
        {
            if (!s.Privacy.KeepAskHistory && Hub.Core.Chats.List().Count > 0) Hub.Core.Chats.DeleteAll();
            if (!s.Ask.KeepPictures) Hub.Core.Chats.DeleteAllMedia();
        };
        Hub.Core.Chats.Changed += () => Hub.OnUi(RefreshChats);
        Web = Hub.S.Ask.Web;
        Think = Hub.S.Ask.ThinkByDefault;
        Current = NewThread(null);
    }

    public AskPlatform Platform { get; } = new();
    public VoiceInput Voice { get; } = new();

    /// <summary>The chat on screen.</summary>
    public ChatThread Current { get; private set; }

    /// <summary>A chat whose notes are saved, and shown, as they change (Aqua's changes arrive from the answer's thread).</summary>
    private ChatThread NewThread(ChatSummary? summary, ChatNotes? notes = null)
    {
        var thread = new ChatThread(notes) { Summary = summary };
        thread.Notes.Changed += () => Hub.OnUi(() =>
        {
            SaveNotes(thread);
            if (ReferenceEquals(thread, Current)) NotesChanged?.Invoke();
        });
        return thread;
    }
    public ObservableCollection<ChatMessageVM> Messages => Current.Messages;
    /// <summary>The history list (with group headers), filtered by <see cref="Filter"/>.</summary>
    public ObservableCollection<ChatListItemVM> Chats { get; } = new();
    /// <summary>Attachments in the composer, sent with the next question.</summary>
    public ObservableCollection<AttachmentChipVM> Pending { get; } = new();
    public bool IsBusy => Current.IsBusy;

    /// <summary>The model picked in the composer (null until the installed models are known: the Settings › AI one).</summary>
    public string? Model { get; set; }

    // The composer's switches (kept for the session; defaults from Settings › Ask Aqua, Think and Research per model).
    public bool Web { get; set; }
    public bool Think { get; set; }
    public bool Research { get; set; }
    public bool Computer { get; set; }

    /// <summary>Raised on the UI thread when an answer starts or finishes (in any chat).</summary>
    public event Action? BusyChanged;
    /// <summary>Raised on the UI thread as answer text streams in (for auto-scroll).</summary>
    public event Action? Progress;
    /// <summary>Raised when a question should be put back in the composer.</summary>
    public event Action<string>? EditRequested;
    /// <summary>Raised when another chat is shown (the page rebinds).</summary>
    public event Action? ChatChanged;
    /// <summary>Raised when the context meter changes.</summary>
    public event Action? ContextChanged;
    /// <summary>Raised on the UI thread when the notes of the chat on screen change (Aqua's or yours).</summary>
    public event Action? NotesChanged;
    /// <summary>Raised on the UI thread when a chat is being compressed, has been, or its compression was undone.</summary>
    public event Action? CompressionChanged;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (Hub.S.Privacy.KeepAskHistory)
        {
            try
            {
                Hub.Core.Chats.ImportLegacy(DateTimeOffset.Now);
                Hub.Core.Chats.Prune(DateTimeOffset.Now, Hub.S.Ask.ChatRetentionDays);
                // Pictures of chats that are gone — or all of them, if keeping pictures was switched off while Aqua was closed.
                if (Hub.S.Ask.KeepPictures) Hub.Core.Chats.PruneMedia();
                else Hub.Core.Chats.DeleteAllMedia();
                // The last chat comes back if it was used today (like the single conversation used to).
                if (Hub.Core.Chats.List().FirstOrDefault() is { } recent && DateTimeOffset.Now - recent.Updated < TimeSpan.FromHours(12))
                    Current = Load(recent);
            }
            catch (Exception ex) { Log.Warn("ask", "Couldn't load the chat history", ex); }
        }
        RefreshChats();
    }

    // ───────────────────────────── Chats ─────────────────────────────

    public string Filter
    {
        get => _filter;
        set { _filter = value ?? ""; RefreshChats(); }
    }

    private DateTimeOffset _lastPrune = DateTimeOffset.Now;

    /// <summary>Rebuilds the history list: starred first, then by age (Today, Yesterday, Previous 7 days…).</summary>
    public void RefreshChats()
    {
        var now = DateTimeOffset.Now;
        // Aqua can run for weeks: expired chats go at most an hour late (never the one on screen).
        if (now - _lastPrune > TimeSpan.FromHours(1) && Hub.S.Privacy.KeepAskHistory)
        {
            _lastPrune = now;
            try { Hub.Core.Chats.Prune(now, Hub.S.Ask.ChatRetentionDays, Current.Id); }
            catch (Exception ex) { Log.Warn("ask", "Couldn't prune chats", ex); }
        }
        List<(ChatSummary Chat, string Snippet)> chats;
        try
        {
            chats = _filter.Trim().Length > 0
                ? Hub.Core.Chats.Search(_filter)
                : Hub.Core.Chats.List().Select(c => (c, "")).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("ask", "Couldn't list chats", ex);
            chats = new();
        }
        Chats.Clear();
        string? lastGroup = null;
        foreach (var (chat, snippet) in chats.OrderByDescending(c => c.Chat.Starred).ThenByDescending(c => c.Chat.Updated))
        {
            var group = chat.Starred ? "Starred" : GroupOf(chat.Updated, now);
            if (group != lastGroup) { Chats.Add(new ChatListItemVM { IsHeader = true, Title = group }); lastGroup = group; }
            var item = new ChatListItemVM
            {
                Id = chat.Id, Title = chat.Title, Starred = chat.Starred, IsCurrent = chat.Id == Current.Id,
                Subtitle = snippet.Length > 0 ? snippet : TimeText.Ago(chat.Updated, now),
                Tooltip = chat.Preview.Length > 0 ? chat.Title + "\n" + chat.Preview : chat.Title,
                IsBusy = _open.TryGetValue(chat.Id, out var t) && t.IsBusy,
            };
            item.OpenCommand = new RelayCommand(() => Open(item.Id));
            item.StarCommand = new RelayCommand(() => SetStar(item.Id, !item.Starred));
            item.CopyCommand = new RelayCommand(() => CopyChat(item.Id));
            item.SaveCommand = new RelayCommand(() => SaveChat(item.Id));
            item.DeleteCommand = new RelayCommand(() => Delete(item.Id));
            item.RenameCommand = new RelayCommand(() => { item.RenameText = item.Title; item.IsRenaming = true; });
            item.CommitRenameCommand = new RelayCommand(() => { item.IsRenaming = false; Rename(item.Id, item.RenameText); });
            item.CancelRenameCommand = new RelayCommand(() => item.IsRenaming = false);
            Chats.Add(item);
        }
    }

    internal static string GroupOf(DateTimeOffset when, DateTimeOffset now)
    {
        var days = (now.Date - when.ToLocalTime().Date).TotalDays;
        return days <= 0 ? "Today" : days <= 1 ? "Yesterday" : days <= 7 ? "Previous 7 days" : days <= 30 ? "Previous 30 days" : "Older";
    }

    /// <summary>Shows a chat from the history (an answer still being written there keeps going).</summary>
    public void Open(string id)
    {
        if (Current.Id == id) return;
        if (_open.TryGetValue(id, out var thread)) Current = thread;
        else if (Hub.Core.Chats.Get(id) is { } summary) Current = Load(summary);
        else return;
        Pending.Clear();
        RefreshChats();
        ChatChanged?.Invoke();
        ContextChanged?.Invoke();
        NotesChanged?.Invoke();
        CompressionChanged?.Invoke();
    }

    private ChatThread Load(ChatSummary summary)
    {
        var thread = NewThread(summary, new ChatNotes(Hub.Core.Chats.LoadNotes(summary.Id)));
        thread.ContextUsed = summary.ContextUsed;
        thread.ContextWindow = summary.ContextWindow;
        thread.Compression = Hub.Core.Chats.LoadCompression(summary.Id);
        var folder = Hub.Core.Chats.MediaFolder(summary.Id);
        foreach (var m in Hub.Core.Chats.Load(summary.Id))
        {
            // Pictures still there to show: none once keeping them was switched off (their files went with it), and a
            // screenshot only while its file is (a picture on the PC can still be opened from its path).
            var pictures = Hub.S.Ask.KeepPictures
                ? (m.Pictures ?? new()).Where(p => p.Path is { Length: > 0 } || ChatMedia.PathOf(folder, p.File) is { } f && File.Exists(f))
                    .Select(p => KeptPicture(thread, p)).ToList()
                : new List<PictureVM>();
            var vm = new ChatMessageVM
            {
                IsUser = m.User, Text = m.Text, Citations = m.Citations ?? new(), Footer = m.Footer ?? "", IsChat = m.Chat,
                Reasoning = m.Reasoning ?? "", ReasoningLabel = m.Reasoning is { Length: > 0 } ? "Reasoning" : "", ModeLabel = m.Mode ?? "",
                Question = m.Question ?? "", Options = m.Options, IsDone = true, At = m.At ?? summary.Updated, Sources = m.Sources ?? new(),
                Attachments = (m.Attachments ?? new()).Select(a => new AttachmentChipVM
                {
                    Name = a, Icon = Documents.IsImage(a) || a.StartsWith("Screenshot", StringComparison.Ordinal) ? "image" : "file",
                    Picture = m.User ? pictures.FirstOrDefault(p => p.Name == a) : null,
                }).ToList(),
            };
            if (!m.User) foreach (var p in pictures) vm.Pictures.Add(p);
            // Thumbnails are read from the chat's folder in the background.
            foreach (var p in pictures)
                if (ChatMedia.PathOf(folder, p.File) is { } kept)
                {
                    var chip = vm.Attachments.FirstOrDefault(a => ReferenceEquals(a.Picture, p));
                    _ = LoadThumbnailAsync(kept, t => { p.Thumbnail = t; if (chip is not null) chip.Thumbnail = t; });
                }
            foreach (var step in m.Steps ?? new()) vm.Steps.Add(new StepVM { Text = step, State = "ok", Icon = "check" });
            if (vm.Steps.Count > 0) vm.FoldSteps();
            Wire(thread, vm);
            thread.Messages.Add(vm);
        }
        _open[summary.Id] = thread;
        MarkCompressed(thread);
        return thread;
    }

    /// <summary>
    /// A question from elsewhere in the app (a story's Ask Aqua, the palette, the tray): it gets its own chat, so the one
    /// you were in keeps its thread and context. An empty chat is simply used.
    /// </summary>
    public void StartFresh()
    {
        EnsureLoaded();
        if (Current.Messages.Count > 0 || Current.IsBusy) NewChat();
    }

    /// <summary>Starts a fresh chat (the previous one stays in the history; an answer still being written there carries on).</summary>
    public void NewChat()
    {
        if (Current.Messages.Count == 0 && Current.Summary is null) { Pending.Clear(); return; }
        Current = NewThread(null);
        Pending.Clear();
        RefreshChats();
        ChatChanged?.Invoke();
        ContextChanged?.Invoke();
        NotesChanged?.Invoke();
        CompressionChanged?.Invoke();
    }

    public void SetStar(string id, bool starred)
    {
        if (_open.TryGetValue(id, out var t) && t.Summary is { } ts) ts.Starred = starred;
        Hub.Core.Chats.Update(id, c => c.Starred = starred);
    }

    public void Rename(string id, string title)
    {
        title = HtmlText.Truncate(Regex.Replace(title ?? "", @"\s+", " ").Trim(), 80);
        if (title.Length == 0) return;
        if (_open.TryGetValue(id, out var t) && t.Summary is { } ts) { ts.Title = title; ts.Named = true; }
        Hub.Core.Chats.Update(id, c => { c.Title = title; c.Named = true; });
    }

    public void Delete(string id)
    {
        if (_open.TryGetValue(id, out var t))
        {
            t.Cts?.Cancel();
            _open.Remove(id);
        }
        var wasCurrent = Current.Id == id;
        if (wasCurrent) Current = NewThread(null);
        Hub.Core.Chats.Delete(id);
        if (wasCurrent)
        {
            ChatChanged?.Invoke();
            ContextChanged?.Invoke();
            NotesChanged?.Invoke();
            CompressionChanged?.Invoke();
        }
    }

    public void Stop() => Current.Cts?.Cancel();

    // ───────────────────────────── Asking ─────────────────────────────

    private AskOptions CurrentOptions(string? storyId) =>
        new() { Web = Web, Think = Think, Research = Research, Computer = Computer, StoryId = storyId, Model = Model };

    private static string ModeOf(AskOptions o) =>
        string.Join(" · ", new[] { o.Research ? "Research" : o.UsesWeb ? "Web" : null, o.Think ? "Think" : null, o.Computer ? "Use my PC" : null }.Where(x => x is not null));

    /// <summary>Asks about one story ("Tell me more"): its full coverage and articles go to the model.</summary>
    public Task AskAboutStoryAsync(string storyId)
    {
        var story = HubSearch.FindStory(Hub.State, storyId);
        var title = story?.Summary is { IsAi: true, Headline.Length: > 0 } s ? s.Headline : story?.Title ?? "this story";
        StartFresh();
        return SendAsync($"Tell me more about: {title}", story is null ? null : CurrentOptions(story.Id));
    }

    /// <summary>The chat's messages as the model sees them, in order: questions (with their attachments' names) and finished answers.</summary>
    private static List<(ChatMessageVM Message, ChatTurn Turn)> Turns(ChatThread thread)
    {
        var list = new List<(ChatMessageVM, ChatTurn)>();
        foreach (var m in thread.Messages)
        {
            if (!m.IsChat || m.Text.Length == 0) continue;
            if (m.IsUser) list.Add((m, new ChatTurn(true, m.Text + (m.Attachments.Count > 0 ? $" [attached: {string.Join(", ", m.Attachments.Select(a => a.Name))}]" : ""))));
            else if (m.IsDone && m.Footer != "Stopped.") list.Add((m, new ChatTurn(false, m.Text, m.Citations)));
        }
        return list;
    }

    /// <summary>The chat so far as the model sees it: after a compression, only the messages its summary doesn't stand in for.</summary>
    private static List<LlmMessage> HistoryOf(ChatThread thread, ChatCompression? compression) =>
        Turns(thread).Skip(compression?.Covers ?? 0).Select(t => new LlmMessage(t.Turn.User ? "user" : "assistant", t.Turn.Text)).ToList();

    public async Task SendAsync(string question, AskOptions? options = null)
    {
        EnsureLoaded();
        var thread = Current;
        thread.Cts?.Cancel();
        var cts = thread.Cts = new CancellationTokenSource();
        var opts = options ?? CurrentOptions(null);
        // Every question goes to the composer's model, however it was asked (a chip, a skill, "Look it up").
        if (opts.Model is null) opts = opts with { Model = Model };
        var attachments = Pending.Where(p => p.Attachment is not null).ToList();
        Pending.Clear();
        foreach (var a in attachments)
            if (a.Attachment is { Kind: AttachmentKind.Folder, Path: { } folder } && !thread.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                thread.Folders.Add(folder);
        if (thread.Folders.Count > 0) opts = opts with { Folders = thread.Folders.ToList() };
        var compression = ValidCompression(thread);
        var history = HistoryOf(thread, compression);
        var user = new ChatMessageVM
        {
            IsUser = true, Text = question, IsChat = true, ModeLabel = ModeOf(opts), Options = opts, IsDone = true,
            Attachments = attachments.Select(a => new AttachmentChipVM { Name = a.Name, Icon = a.Icon, Thumbnail = a.Thumbnail, Note = a.Note, Picture = AttachedPicture(thread, a) }).ToList(),
        };
        Wire(thread, user);
        thread.Messages.Add(user);
        var answer = new ChatMessageVM { IsUser = false, IsThinking = true, IsChat = true, Question = question, Options = opts };
        Wire(thread, answer);
        thread.Messages.Add(answer);
        SetBusy(thread, true);
        Persist(thread);

        var host = new ChatHost(this, thread, answer, cts.Token);
        try
        {
            // "Remember that …" / "Forget …": straight to the Workbench's memory, no model needed (with Undo and
            // "Answer it instead" in case it was meant as a question).
            if (Hub.S.Ask.UseMemory && !opts.AsQuestion && attachments.Count == 0 && Workbench.MemoryCommand(question) is { } memory &&
                HandleMemory(thread, user, answer, memory.Kind, memory.Fact))
                return;

            // No model: start a stopped local server if Settings allow it (never one you turned off yourself);
            // failing that, answer from what the agents collected rather than fail.
            var unavailable = Hub.Core.Llm.UserPaused ? "AI is paused"
                : Hub.Core.Llm.Health.Available ? null
                : Hub.Ollama.UserTurnedOff ? "you turned the local model off"
                : "the local model isn't available";
            if (unavailable is "the local model isn't available" && Hub.S.Ai.StartOllamaOnDemand && OllamaPolicy.Applies(Hub.S.Ai) && OllamaManager.Installed)
            {
                answer.Status = "Starting the local model…";
                if (await Hub.Ollama.StartAsync("you asked a question", cts.Token) && Hub.Core.Llm.Health.Available) unavailable = null;
                answer.Status = "";
            }
            if (unavailable is not null)
            {
                await AnswerOfflineAsync(answer, question, unavailable, opts);
                return;
            }

            var files = attachments.Select(a => a.Attachment!).ToList();
            var earlier = EarlierSources(thread, answer);
            var chat = new AskChatContext { Notes = thread.Notes, Summary = compression?.Summary ?? "", SummaryCovers = compression?.Covers ?? 0 };
            var result = await Task.Run(() => Hub.Core.Assistant.RunAsync(question, history, files, opts, host, Platform, thread.AllowedForChat, cts.Token, earlier, chat), cts.Token);
            host.Flush();
            host.Close();
            answer.Text = result.Text.Length > 0 ? result.Text
                : "I couldn't come up with an answer this time — " + (opts.UsesWeb || !Hub.S.Ask.Web ? "try asking it another way, or press Ask again." : "switching on Web below lets me look it up.");
            answer.Citations = result.Citations;
            answer.Sources = result.Sources;
            answer.IsThinking = false;
            if (result.Thinking.Length > 0)
            {
                answer.Reasoning = result.Thinking;
                answer.ReasoningLabel = (result.ReasoningCut ? "Reasoning (cut short)" : "Reasoning") + $" · {Plural.Of(result.Thinking.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, "word")}";
            }
            answer.ReasoningOpen = false;
            var used = answer.Citations.Count;
            var snippets = answer.Citations.Count(c => c.Kind == "result");
            var parts = new List<string> { result.Model, $"{result.Elapsed.TotalSeconds:0.0}s" };
            parts.Add(used == 0 ? "no sources cited" : Plural.Of(used, "source") + " cited" + (snippets > 0 ? $" ({snippets} only from search snippets)" : ""));
            if (result.UsedWeb) parts.Add("used the web");
            if (result.Skill.Length > 0) parts.Add("skill: " + result.Skill);
            if (compression is not null) parts.Add($"{Plural.Of(compression.Covers, "earlier message")} as a summary");
            if (result.TrimmedMessages > 0) parts.Add($"left out {Plural.Of(result.TrimmedMessages, "older message")}");
            parts.Add("on-device");
            answer.Footer = string.Join(" · ", parts);
            thread.ContextUsed = result.PromptTokens + result.OutputTokens;
            thread.ContextWindow = result.ContextTokens > 0 ? result.ContextTokens : Hub.S.Ai.ContextTokens;
            if (ReferenceEquals(thread, Current)) ContextChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            host.Flush();
            host.Close();
            answer.Footer = "Stopped.";
            if (answer.Text.Length == 0) answer.Text = "…";
        }
        catch (LlmUnavailableException) when (answer.Text.Length == 0)
        {
            await AnswerOfflineAsync(answer, question, "the local model isn't available", opts);
        }
        catch (Exception ex)
        {
            Log.Warn("ask", "Answer failed", ex);
            host.Flush();
            host.Close();
            answer.Text = ex is LlmUnavailableException
                ? "I can't reach the local model right now. " + ex.Message
                : "Sorry — something went wrong: " + ex.Message;
        }
        finally
        {
            host.Close();
            answer.IsThinking = false;
            answer.Status = "";
            answer.Approval = null;
            foreach (var step in answer.Steps.Where(s => s.IsRunning)) step.State = "failed";
            if (answer.Steps.Count > 0) answer.FoldSteps();
            answer.IsDone = true;
            if (ReferenceEquals(thread.Cts, cts)) SetBusy(thread, false);
            Persist(thread);
            // A "remember …"/"forget …" chat keeps its question as the title: it was answered without the model, so
            // there's nothing worth waking it for.
            if (thread.Summary is { Named: false } && thread.Messages.Count(m => m.IsUser) == 1 && answer.Footer.EndsWith("on-device", StringComparison.Ordinal)
                && !answer.HasMemoryActions)
                _ = NameChatAsync(thread, opts.Model);
            // A chat whose earlier messages no longer all go with a question is compressed (while the model is loaded).
            if (answer.Footer.EndsWith("on-device", StringComparison.Ordinal) && !answer.HasMemoryActions) _ = MaybeCompressAsync(thread);
        }
    }

    /// <summary>Carries out "remember …" / "forget …"; false when there's nothing to forget (then it's answered as a question).</summary>
    private bool HandleMemory(ChatThread thread, ChatMessageVM user, ChatMessageVM answer, string kind, string fact)
    {
        void AnswerInstead()
        {
            answer.HasMemoryActions = false;
            if (thread.IsBusy) return;
            _ = ResendAsync(thread, user, user.Text, (user.Options ?? CurrentOptions(null)) with { AsQuestion = true });
        }
        if (kind == "remember")
        {
            var memory = Hub.Core.Workbench.Remember(fact, "chat");
            answer.Text = $"Got it — I'll remember that: “{memory.Text}”.\n\nYou can see and change what I remember in Workbench › Memory.";
            answer.Footer = "Saved to memory · on-device";
            answer.UndoMemoryCommand = new RelayCommand(() =>
            {
                Hub.Core.Workbench.Forget(memory.Id);
                answer.HasMemoryActions = false;
                answer.Text = "OK — I won't remember that.";
                answer.Footer = "Memory unchanged · on-device";
                Persist(thread);
            });
            answer.AnswerInsteadCommand = new RelayCommand(() => { Hub.Core.Workbench.Forget(memory.Id); AnswerInstead(); });
        }
        else
        {
            if (Hub.Core.Workbench.Matching(fact).Count == 0) return false;
            var gone = Hub.Core.Workbench.Forget(fact);
            answer.Text = "Done — I've forgotten: " + string.Join("; ", gone.Select(m => "“" + m.Text + "”")) + ".";
            answer.Footer = "Memory updated · on-device";
            answer.UndoMemoryCommand = new RelayCommand(() =>
            {
                Hub.Core.Workbench.Restore(gone);
                answer.HasMemoryActions = false;
                answer.Text = "OK — I still remember: " + string.Join("; ", gone.Select(m => "“" + m.Text + "”")) + ".";
                answer.Footer = "Memory unchanged · on-device";
                Persist(thread);
            });
            answer.AnswerInsteadCommand = new RelayCommand(() => { Hub.Core.Workbench.Restore(gone); AnswerInstead(); });
        }
        answer.HasMemoryActions = true;
        answer.IsThinking = false;
        return true;
    }

    /// <summary>Gives a new chat a short title from its first exchange (in the background, when the model is idle).</summary>
    /// <param name="model">The model that answered (already loaded, so naming doesn't swap models).</param>
    private static async Task NameChatAsync(ChatThread thread, string? model)
    {
        var summary = thread.Summary;
        if (summary is null || !Hub.Core.Llm.Health.Available) return;
        var q = thread.Messages.FirstOrDefault(m => m.IsUser)?.Text ?? "";
        var a = thread.Messages.FirstOrDefault(m => !m.IsUser)?.Text ?? "";
        try
        {
            var result = await Task.Run(() => Hub.Core.Llm.CompleteAsync(new LlmRequest
            {
                Model = model,
                Purpose = "chat-title",
                Priority = LlmPriority.Background,
                System = "You name chats. Reply with the title only: 2 to 6 words, no quotes, no full stop.",
                Messages = { new LlmMessage("user", "Question: " + HtmlText.Truncate(q, 400) + "\nAnswer: " + HtmlText.Truncate(a, 400)) },
                Temperature = 0.2,
                MaxTokens = 24,
                Think = false,
            }));
            var title = Regex.Replace(result.Text.Trim().Trim('"', '“', '”', '\'').TrimEnd('.'), @"\s+", " ");
            if (title.Length is < 3 or > 70 || summary.Named) return;
            summary.Title = title;
            summary.Named = true;
            Hub.Core.Chats.Update(summary.Id, c => { c.Title = title; c.Named = true; });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Debug("ask", "Couldn't name the chat: " + ex.Message); }
    }

    /// <summary>
    /// No model: what the agents collected for this question, plus — when Web is on — the top web results with links
    /// (a search needs no model), so Ask still finds things.
    /// </summary>
    private static async Task AnswerOfflineAsync(ChatMessageVM answer, string question, string reason, AskOptions? options = null)
    {
        var ctx = await Task.Run(() => Hub.Core.Ask.BuildContext(question));
        var text = Hub.Core.Ask.OfflineAnswer(question, ctx, reason);
        var citations = ctx.Citations.Where(c => text.Contains($"[{c.Number}]")).ToList();
        var fromWeb = 0;
        if (options?.UsesWeb == true && Hub.S.Ask.Web)
        {
            try
            {
                answer.Status = "Searching the web…";
                var results = await Hub.Core.WebSearch.SearchAsync(AskAgent.SearchQuery(question), 5, WebSearch.LooksTimely(question));
                if (results.Count > 0)
                {
                    var sb = new StringBuilder("**From the web**\n");
                    var n = ctx.Citations.Count;
                    foreach (var r in results)
                    {
                        citations.Add(new Citation(++n, r.Title, r.Site, r.Url, "web"));
                        sb.Append("- ").Append(r.Title).Append(" — ").Append(r.Site).Append(" [").Append(n).Append("]\n");
                    }
                    fromWeb = results.Count;
                    // Keep the closing "ask again" line last; drop the "couldn't find anything" apology when the web found something.
                    text = text.StartsWith("I couldn't find anything", StringComparison.Ordinal)
                        ? char.ToUpperInvariant(reason[0]) + reason[1..] + ", so here is what a web search found:\n\n" + sb + "\nOpen a link to read more, or ask again when the model is back for a written answer."
                        : text.Replace("Ask again when the model is back", sb + "\nAsk again when the model is back");
                }
            }
            catch (WebSearchException ex) { Log.Info("ask", "Offline web search: " + ex.Message); }
            finally { answer.Status = ""; }
        }
        answer.Citations = citations;
        answer.Text = text;
        answer.IsThinking = false;
        answer.Footer = citations.Count == 0 ? "No model used"
            : fromWeb > 0 ? $"From your feeds and the web · {Plural.Of(citations.Count, "source")} · no model used"
            : $"From your feeds · {Plural.Of(citations.Count, "source")} · no model used";
    }

    public async Task SummariseClipboardAsync()
    {
        var text = ActionExecutor.ClipboardText();
        if (string.IsNullOrWhiteSpace(text)) return;
        StartFresh();
        var thread = Current;
        thread.Cts?.Cancel();
        var cts = thread.Cts = new CancellationTokenSource();
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var user = new ChatMessageVM { IsUser = true, Text = $"Summarise my clipboard ({Plural.Of(words, "word")})", IsDone = true };
        Wire(thread, user);
        thread.Messages.Add(user);
        var answer = new ChatMessageVM { IsThinking = true };
        Wire(thread, answer);
        thread.Messages.Add(answer);
        SetBusy(thread, true);
        var sb = new StringBuilder();
        try
        {
            await foreach (var piece in Hub.Core.Llm.StreamAsync(Prompts.SummarizeText(text, Hub.S), cts.Token))
            {
                sb.Append(piece);
                answer.Text = sb.ToString();
                answer.IsThinking = false;
                if (ReferenceEquals(thread, Current)) Progress?.Invoke();
            }
            answer.Footer = "Summarised on-device · nothing left this PC";
        }
        catch (OperationCanceledException)
        {
            answer.Footer = "Stopped.";
        }
        catch (Exception ex)
        {
            answer.Text = "Couldn't summarise: " + ex.Message;
        }
        finally
        {
            answer.IsThinking = false;
            answer.IsDone = true;
            if (ReferenceEquals(thread.Cts, cts)) SetBusy(thread, false);
            Persist(thread);
        }
    }

    // ───────────────────────────── Attachments ─────────────────────────────

    public async Task AttachFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Take(8))
        {
            if (Pending.Count >= 8) break;
            if (!Documents.IsSupported(path))
            {
                AddFailedChip(Path.GetFileName(path), "Can't read this kind of file — Aqua reads " + Documents.SupportedSummary);
                continue;
            }
            if (LocalFiles.IsSensitiveName(path))
            {
                AddFailedChip(Path.GetFileName(path), "Aqua doesn't read passwords, keys or app data");
                continue;
            }
            var chip = new AttachmentChipVM
            {
                Name = Path.GetFileName(path), Icon = Documents.IsImage(path) ? "image" : "file", IsLoading = true, Note = "Reading…",
                Thumbnail = Documents.IsImage(path) ? await Task.Run(() => SafeThumb(path)) : null,
            };
            chip.RemoveCommand = new RelayCommand(() => Pending.Remove(chip));
            Pending.Add(chip);
            try
            {
                var attachment = await Platform.AttachFileAsync(path);
                chip.Attachment = attachment;
                chip.Note = attachment.Kind == AttachmentKind.Image
                    ? (attachment.Text.Length > 0 ? "image · text found" : "image")
                    : attachment.Note;
                if (attachment.Kind == AttachmentKind.Document && attachment.Text.Trim().Length == 0)
                {
                    chip.Attachment = null;
                    chip.Note = "No readable text found";
                }
            }
            catch (Exception ex)
            {
                Log.Warn("ask", $"Couldn't attach {path}", ex);
                chip.Note = ex is NotSupportedException or InvalidOperationException ? ex.Message : "Couldn't read this file";
            }
            finally { chip.IsLoading = false; }
        }
    }

    /// <summary>
    /// Lets this chat search and read a folder, with the rules Use my PC's folders follow (never app data, system or key
    /// folders, nor password or key files inside), until the chat is closed.
    /// </summary>
    public async Task AttachFolderAsync(string path)
    {
        if (Pending.Count >= 8) return;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AddFailedChip(path, "That isn't a folder");
            return;
        }
        if (full.EndsWith(':')) full += Path.DirectorySeparatorChar;
        var name = Path.GetFileName(full) is { Length: > 0 } n ? n : full;
        if (!Directory.Exists(full))
        {
            AddFailedChip(name, "That folder isn't there");
            return;
        }
        if (LocalFiles.IsSensitiveFolder(full))
        {
            AddFailedChip(name, "Aqua doesn't read app data, system or key folders");
            return;
        }
        var chip = new AttachmentChipVM { Name = name, Icon = "folder", IsLoading = true, Note = "Looking inside…" };
        chip.RemoveCommand = new RelayCommand(() => Pending.Remove(chip));
        Pending.Add(chip);
        try
        {
            var (count, more, newest) = await Task.Run(() => LocalFiles.Survey(full));
            var listing = string.Join("\n", newest.Select(h => $"- {h.Name} ({LocalFiles.Size(h.Size)}, {h.Modified.ToString("d MMM yyyy", CultureInfo.CurrentCulture)})"));
            var note = (more ? $"{count.ToString("N0", CultureInfo.CurrentCulture)}+ files" : Plural.Of(count, "file")) + " · for this chat";
            chip.Attachment = new AskAttachment { Name = name, Kind = AttachmentKind.Folder, Path = full, Text = listing, Note = note };
            chip.Note = note;
        }
        catch (Exception ex)
        {
            Log.Warn("ask", $"Couldn't look inside {full}", ex);
            chip.Note = "Couldn't look inside this folder";
        }
        finally { chip.IsLoading = false; }
    }

    private static System.Windows.Media.ImageSource? SafeThumb(string path)
    {
        try { return Images.Thumbnail(File.ReadAllBytes(path), 96); }
        catch { return null; }
    }

    private void AddFailedChip(string name, string why)
    {
        var chip = new AttachmentChipVM { Name = name, Icon = "warning", Note = why };
        chip.RemoveCommand = new RelayCommand(() => Pending.Remove(chip));
        Pending.Add(chip);
    }

    /// <summary>Your own screenshot (the button, or "Ask about my screen"): never needs an extra OK.</summary>
    /// <summary>Attaches a capture of one screen, all screens or one app window (the composer's screenshot menu).</summary>
    public async Task<bool> AttachCaptureAsync(Func<Task<byte[]?>> capture, string label)
    {
        var png = await capture();
        if (png is null) return false;
        await AttachImageAsync(png, $"{label} {DateTime.Now:HH.mm.ss}.png");
        return true;
    }

    public async Task<bool> AttachScreenshotAsync(bool snip)
    {
        var png = snip ? await ScreenCapture.SnipAsync() : await ScreenCapture.CaptureScreenAsync();
        if (png is null) return false;
        await AttachImageAsync(png, snip ? $"Snip {DateTime.Now:HH.mm.ss}.png" : $"Screenshot {DateTime.Now:HH.mm.ss}.png");
        return true;
    }

    public async Task AttachImageAsync(byte[] bytes, string name)
    {
        var chip = new AttachmentChipVM { Name = name, Icon = "image", IsLoading = true, Note = "Reading…", Thumbnail = Images.Thumbnail(bytes, 96) };
        chip.RemoveCommand = new RelayCommand(() => Pending.Remove(chip));
        Pending.Add(chip);
        try
        {
            chip.Attachment = await Platform.AttachImageAsync(bytes, name);
            chip.Note = chip.Attachment.Text.Length > 0 ? "image · text found" : "image";
        }
        catch (Exception ex)
        {
            Log.Warn("ask", "Couldn't attach the image", ex);
            chip.Note = "Couldn't read this image";
        }
        finally { chip.IsLoading = false; }
    }

    public bool AttachmentsLoading => Pending.Any(p => p.IsLoading);

    /// <summary>
    /// From the palette or the tray: captures the screen first (while Aqua's own windows are hidden), then opens Ask with
    /// it attached and asks what's on it.
    /// </summary>
    public async Task AskAboutScreenAsync()
    {
        await Task.Delay(150); // let the menu or palette close
        var png = await ScreenCapture.CaptureScreenAsync();
        Hub.Windows.ShowMain("ask");
        if (png is null) return;
        StartFresh();
        await AttachImageAsync(png, $"Screenshot {DateTime.Now:HH.mm.ss}.png");
        await SendAsync("What's on my screen? Explain what I'm looking at and anything I should know.");
    }

    // ───────────────────────────── Message actions ─────────────────────────────

    private void Wire(ChatThread thread, ChatMessageVM vm)
    {
        vm.CopyCommand = new RelayCommand(() => Copy(vm.Text));
        vm.ReadAloudCommand = new RelayCommand(() => _ = ReadAloudAsync(vm));
        if (vm.IsUser)
        {
            vm.EditCommand = new RelayCommand(() =>
            {
                if (thread.IsBusy) { EditRequested?.Invoke(vm.Text); return; }
                vm.EditText = vm.Text;
                vm.IsEditing = true;
            });
            vm.CancelEditCommand = new RelayCommand(() => vm.IsEditing = false);
            vm.SaveEditCommand = new RelayCommand(() =>
            {
                var text = vm.EditText.Trim();
                vm.IsEditing = false;
                if (text.Length == 0 || thread.IsBusy) return;
                _ = ResendAsync(thread, vm, text);
            });
            return;
        }
        vm.RetryCommand = new RelayCommand(() =>
        {
            if (thread.IsBusy || vm.Question.Length == 0) return;
            // The last answer is written again in place; an older one is asked again at the end.
            var at = thread.Messages.IndexOf(vm);
            if (at == thread.Messages.Count - 1 && at >= 1 && thread.Messages[at - 1].IsUser)
                _ = ResendAsync(thread, thread.Messages[at - 1], vm.Question, vm.Options);
            else _ = SendAsync(vm.Question, vm.Options ?? CurrentOptions(null));
        });
        vm.DeeperCommand = new RelayCommand(() =>
        {
            if (thread.IsBusy || vm.Question.Length == 0) return;
            _ = SendAsync(vm.Question, (vm.Options ?? CurrentOptions(null)) with { Research = Hub.S.Ask.Web, Web = Hub.S.Ask.Web });
        });
    }

    /// <summary>Edit and ask again: the question and everything after it are replaced by the new exchange.</summary>
    private async Task ResendAsync(ChatThread thread, ChatMessageVM question, string text, AskOptions? options = null)
    {
        var at = thread.Messages.IndexOf(question);
        if (at < 0) return;
        var opts = options ?? (at + 1 < thread.Messages.Count ? thread.Messages[at + 1].Options : null) ?? question.Options;
        while (thread.Messages.Count > at) thread.Messages.RemoveAt(thread.Messages.Count - 1);
        if (!ReferenceEquals(thread, Current) && thread.Id.Length > 0) Open(thread.Id);
        await SendAsync(text, opts);
    }

    private static async Task ReadAloudAsync(ChatMessageVM vm)
    {
        if (vm.IsSpeaking) { Hub.Speech.Stop(); vm.IsSpeaking = false; return; }
        vm.IsSpeaking = true;
        void Done()
        {
            if (Hub.Speech.IsSpeaking) return;
            vm.IsSpeaking = false;
            Hub.Speech.StateChanged -= Done;
        }
        Hub.Speech.StateChanged += Done;
        await Hub.Speech.SpeakAsync(PlainText(vm.Text), Hub.S.Location.Language);
        if (!Hub.Speech.IsSpeaking) Done();
    }

    /// <summary>An answer without Markdown and citation marks, for reading aloud.</summary>
    internal static string PlainText(string markdown)
    {
        var t = Regex.Replace(markdown, @"\[(\d+)\]", "");
        t = Regex.Replace(t, @"\[([^\]]+)\]\((https?://[^)]+)\)", "$1");
        t = Regex.Replace(t, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        // Tables read row by row ("Basic, €5, monthly"); their divider rows, rules and quote marks aren't read.
        t = Regex.Replace(t, @"^\s*(?:\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?|[-*_](?:\s*[-*_]){2,})\s*$", "", RegexOptions.Multiline);
        t = Regex.Replace(t, @"^[ \t]*\|[ \t]*|[ \t]*\|[ \t]*$", "", RegexOptions.Multiline);
        t = Regex.Replace(t, @"[ \t]*\|[ \t]*", ", ");
        t = Regex.Replace(t, @"^\s*>+\s?", "", RegexOptions.Multiline);
        t = Regex.Replace(t, @"^\s*[-*•]\s+", "", RegexOptions.Multiline);
        t = t.Replace("**", "").Replace("__", "").Replace("`", "");
        return Regex.Replace(t, @"[ \t]+", " ").Trim();
    }

    public static void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Sandbox.Intercept("clipboard", HtmlText.Truncate(text, 80))) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Warn("ask", "Copy failed", ex); }
    }

    private void SetBusy(ChatThread thread, bool busy)
    {
        thread.IsBusy = busy;
        foreach (var item in Chats.Where(c => !c.IsHeader && c.Id == thread.Id)) item.IsBusy = busy;
        BusyChanged?.Invoke();
    }

    /// <summary>
    /// The chat's messages as saved. Sources are kept with the last six answers that have any (enough for follow-ups,
    /// and chats stay small); an export marks failed steps with ✗.
    /// </summary>
    private static List<SavedChatMessage> Snapshot(ChatThread thread, bool forExport)
    {
        var keepSources = thread.Messages.Where(m => !m.IsUser && m.Sources.Count > 0).TakeLast(6).ToHashSet();
        // Pictures go into the chat's own folder (Settings › Privacy › Keep pictures with chats); an export only names them.
        var folder = !forExport && Hub.S.Ask.KeepPictures && Hub.S.Privacy.KeepAskHistory && thread.Summary is { } summary
            ? Hub.Core.Chats.MediaFolder(summary.Id) : null;
        return thread.Messages.Select(m => new SavedChatMessage(m.IsUser, m.Text, m.Citations, m.Footer, m.IsChat,
            m.Steps.Select(s => forExport && s.IsFailed ? "✗ " + s.Text : s.Text).ToList(), m.Reasoning.Length > 0 ? HtmlText.Truncate(m.Reasoning, forExport ? 60000 : 20000) : null,
            m.Attachments.Select(a => a.Name).ToList(), m.ModeLabel, m.Question)
        {
            Options = m.Options, At = m.At, Sources = !forExport && keepSources.Contains(m) ? m.Sources : null,
            Pictures = forExport || folder is not null ? Keep(m.IsUser ? m.Attachments.Select(a => a.Picture).OfType<PictureVM>() : m.Pictures, folder) : null,
        }).ToList();
    }

    // ───────────────────────────── Pictures ─────────────────────────────

    /// <summary>
    /// Pictures as kept with the chat: a screenshot or pasted picture whole, a picture on the PC as a thumbnail beside its
    /// path (the file itself stays where it is). Without a folder nothing is written — an export, or keeping is off.
    /// </summary>
    private static List<SavedPicture>? Keep(IEnumerable<PictureVM> pictures, string? folder)
    {
        var list = new List<SavedPicture>();
        foreach (var p in pictures)
        {
            if (folder is not null && p.File is null && p.Bytes is { } bytes)
            {
                var linked = p.Path is { Length: > 0 };
                p.File = ChatMedia.Save(folder, linked ? Images.Prepare(bytes, 320) : bytes, thumbnail: linked);
                if (p.File is not null) p.Bytes = null;
            }
            list.Add(new SavedPicture(p.Name, p.Kind) { Path = p.Path, File = p.File, Match = p.Match, Note = p.Note });
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>A picture Ask looked at, for the answer to show.</summary>
    private PictureVM PictureFor(ChatThread thread, AskPicture picture, System.Windows.Media.ImageSource? thumbnail)
    {
        var vm = new PictureVM
        {
            Name = picture.Name, Kind = picture.Kind, Path = picture.Path, Match = picture.Match, Note = picture.Note, Bytes = picture.Image,
            Thumbnail = thumbnail,
        };
        vm.OpenCommand = new RelayCommand(() => OpenPicture(thread, vm));
        return vm;
    }

    /// <summary>A picture you attached to a question (a file, a screenshot, a paste), kept with the chat like the ones Ask looks at.</summary>
    private PictureVM? AttachedPicture(ChatThread thread, AttachmentChipVM chip)
    {
        if (chip.Attachment is not { Kind: AttachmentKind.Image, Image: { } image } attachment) return null;
        var vm = new PictureVM { Name = chip.Name, Kind = "attachment", Path = attachment.Path, Bytes = image, Thumbnail = chip.Thumbnail };
        vm.OpenCommand = new RelayCommand(() => OpenPicture(thread, vm));
        return vm;
    }

    /// <summary>A picture kept with a saved chat (its thumbnail is read afterwards).</summary>
    private PictureVM KeptPicture(ChatThread thread, SavedPicture saved)
    {
        var vm = new PictureVM { Name = saved.Name, Kind = saved.Kind, Path = saved.Path, Match = saved.Match, Note = saved.Note, File = saved.File };
        vm.OpenCommand = new RelayCommand(() => OpenPicture(thread, vm));
        return vm;
    }

    private static async Task LoadThumbnailAsync(string path, Action<System.Windows.Media.ImageSource> apply)
    {
        System.Windows.Media.ImageSource? thumbnail;
        try { thumbnail = await Task.Run(() => File.Exists(path) ? Images.Thumbnail(File.ReadAllBytes(path), 192) : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { thumbnail = null; }
        if (thumbnail is not null) apply(thumbnail);
    }

    /// <summary>
    /// Opens a picture: one on the PC with its app (as files in answers open); a screenshot or pasted picture — or a
    /// picture whose file has gone, as its thumbnail — in a window of its own.
    /// </summary>
    private static void OpenPicture(ChatThread thread, PictureVM picture)
    {
        if (picture.Path is { Length: > 0 } path && File.Exists(path))
        {
            UI.Controls.MarkdownView.OpenFile(path);
            return;
        }
        System.Windows.Media.Imaging.BitmapSource? image = null;
        try
        {
            if (picture.Bytes is { } bytes) image = Images.Decode(bytes);
            else if (ChatMedia.PathOf(Hub.Core.Chats.MediaFolder(thread.Id), picture.File) is { } kept && File.Exists(kept)) image = Images.Decode(File.ReadAllBytes(kept));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Debug("ask", "Couldn't open a chat picture: " + ex.Message); }
        // Its file gone too (keeping pictures was switched off since): the thumbnail on screen is all there is.
        image ??= picture.Thumbnail as System.Windows.Media.Imaging.BitmapSource;
        if (image is null) return;
        UI.Shell.PictureViewer.Show(picture.Path is { Length: > 0 } gone ? $"{picture.Name} — no longer at {gone}" : picture.Name, image);
    }

    /// <summary>A chat (this one by default) as Markdown: questions, answers, plans and steps, reasoning, sources.</summary>
    public string ExportMarkdown(string? chatId = null)
    {
        EnsureLoaded();
        if (chatId is null || Current.Summary?.Id == chatId)
            return ChatExport.ToMarkdown(Current.Summary?.Title ?? "Ask Aqua chat", Snapshot(Current, forExport: true), DateTimeOffset.Now, Current.Notes.Items);
        if (_open.TryGetValue(chatId, out var open))
            return ChatExport.ToMarkdown(open.Summary?.Title ?? "Ask Aqua chat", Snapshot(open, forExport: true), DateTimeOffset.Now, open.Notes.Items);
        return ChatExport.ToMarkdown(Hub.Core.Chats.Get(chatId)?.Title ?? "Ask Aqua chat", Hub.Core.Chats.Load(chatId), DateTimeOffset.Now, Hub.Core.Chats.LoadNotes(chatId));
    }

    /// <summary>Copies a chat as Markdown (to paste into a message or a bug report).</summary>
    public void CopyChat(string? chatId = null) => Copy(ExportMarkdown(chatId));

    /// <summary>Saves a chat as a Markdown file you choose.</summary>
    public void SaveChat(string? chatId = null)
    {
        var markdown = ExportMarkdown(chatId);
        var title = chatId is null ? Current.Summary?.Title : Hub.Core.Chats.Get(chatId)?.Title;
        var name = string.Concat((title ?? "Ask Aqua chat").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
        if (Sandbox.Intercept("save-file", name + ".md")) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = name + ".md", Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt", DefaultExt = ".md" };
        if (dialog.ShowDialog() != true) return;
        try { File.WriteAllText(dialog.FileName, markdown); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ask", "Couldn't save the chat", ex); }
    }

    /// <summary>What this chat's earlier answers read (newest first, one of each), for a follow-up to use again.</summary>
    private static List<ChatSource> EarlierSources(ChatThread thread, ChatMessageVM current) =>
        thread.Messages.Where(m => !m.IsUser && !ReferenceEquals(m, current) && m.Sources.Count > 0).Reverse()
            .SelectMany(m => m.Sources).DistinctBy(s => s.Url ?? s.Title).Take(10).ToList();

    /// <summary>Saves a chat (created in the history with its first message). Nothing is kept when Privacy says so.</summary>
    private void Persist(ChatThread thread)
    {
        if (!Hub.S.Privacy.KeepAskHistory || thread.Messages.Count == 0) return;
        try
        {
            var now = DateTimeOffset.Now;
            if (thread.Summary is null)
            {
                var first = thread.Messages.FirstOrDefault(m => m.IsUser)?.Text ?? "New chat";
                thread.Summary = new ChatSummary { Id = ChatStore.NewId(now), Title = ChatStore.TitleFrom(first), Created = now };
                _open[thread.Summary.Id] = thread;
            }
            thread.Summary.Updated = now;
            thread.Summary.ContextUsed = thread.ContextUsed;
            thread.Summary.ContextWindow = thread.ContextWindow;
            Hub.Core.Chats.Save(thread.Summary, Snapshot(thread, forExport: false));
            // Notes typed before the chat's first question are saved once it exists.
            if (thread.Notes.Count > 0) SaveNotes(thread);
        }
        catch (Exception ex) { Log.Warn("ask", "Couldn't save the chat", ex); }
    }

    // ───────────────────────────── Notes and compression ─────────────────────────────

    private static void SaveNotes(ChatThread thread)
    {
        if (thread.Summary is not { } summary || !Hub.S.Privacy.KeepAskHistory) return;
        try { Hub.Core.Chats.SaveNotes(summary.Id, thread.Notes.Items); }
        catch (Exception ex) { Log.Warn("ask", "Couldn't save the chat's notes", ex); }
    }

    private static void SaveCompression(ChatThread thread)
    {
        if (thread.Summary is not { } summary || !Hub.S.Privacy.KeepAskHistory) return;
        try { Hub.Core.Chats.SaveCompression(summary.Id, thread.Compression); }
        catch (Exception ex) { Log.Warn("ask", "Couldn't save the chat's compression", ex); }
    }

    /// <summary>The chat's compression if it still fits its messages; one that no longer does (an earlier question was edited) is dropped.</summary>
    private ChatCompression? ValidCompression(ChatThread thread)
    {
        if (thread.Compression is null) return null;
        var valid = ChatCompressor.Valid(thread.Compression, Turns(thread).Select(t => t.Turn).ToList());
        if (valid is not null) return valid;
        thread.Compression = null;
        SaveCompression(thread);
        MarkCompressed(thread);
        if (ReferenceEquals(thread, Current)) CompressionChanged?.Invoke();
        return null;
    }

    /// <summary>Fades the compressed messages and puts the line that says so above the first message after them.</summary>
    private void MarkCompressed(ChatThread thread)
    {
        var turns = Turns(thread);
        var compression = ChatCompressor.Valid(thread.Compression, turns.Select(t => t.Turn).ToList());
        var covered = turns.Take(compression?.Covers ?? 0).Select(t => t.Message).ToHashSet();
        var after = compression is not null && compression.Covers < turns.Count ? turns[compression.Covers].Message : null;
        foreach (var m in thread.Messages)
        {
            m.IsCompressed = covered.Contains(m);
            m.Compression = ReferenceEquals(m, after) ? CompressionLine(thread, compression!) : null;
        }
    }

    private CompressionVM CompressionLine(ChatThread thread, ChatCompression c)
    {
        CompressionVM? line = null;
        line = new CompressionVM
        {
            Label = $"{Plural.Of(c.Covers, "earlier message")} {(c.Covers == 1 ? "goes" : "go")} to the model as a summary{(c.Automatic ? " (compressed automatically)" : "")} · " +
                    $"about {TokenCount(c.TokensBefore)} → {TokenCount(c.TokensAfter)} tokens",
            Summary = c.Summary,
            Tooltip = "The messages above stay in the chat; the model gets the summary instead. Undo sends them in full again.",
            ToggleCommand = new RelayCommand(() => line!.SummaryOpen = !line.SummaryOpen),
            UndoCommand = new RelayCommand(() => UndoCompression(thread)),
        };
        return line;
    }

    /// <summary>"950", "7.9K", "12K".</summary>
    internal static string TokenCount(int n) =>
        n >= 1000 ? (n / 1000.0).ToString(n >= 10000 ? "0" : "0.#", CultureInfo.CurrentCulture) + "K" : n.ToString(CultureInfo.CurrentCulture);

    /// <summary>The chat's compression, if it still fits its messages.</summary>
    public ChatCompression? CompressionOf(ChatThread thread) => ChatCompressor.Valid(thread.Compression, Turns(thread).Select(t => t.Turn).ToList());

    /// <summary>What Compress would do now: how many messages it would summarise and about how many tokens they are (0 when there's nothing to compress).</summary>
    public (int Messages, int Tokens) CompressionPreview(ChatThread thread)
    {
        var turns = Turns(thread).Select(t => t.Turn).ToList();
        var from = ChatCompressor.Valid(thread.Compression, turns)?.Covers ?? 0;
        var covers = ChatCompressor.CoverCount(turns, from);
        return covers == 0 ? (0, 0) : (covers - from, turns.Skip(from).Take(covers - from).Sum(t => ChatCompressor.Tokens(t.Text)));
    }

    /// <summary>
    /// Compresses the start of a chat — all but its last two exchanges, on top of an earlier summary — into a summary the
    /// model gets instead. The messages stay in the chat, and Undo sends them in full again. Returns what happened.
    /// </summary>
    public async Task<string> CompressAsync(ChatThread thread, bool automatic = false)
    {
        if (thread.Compressing) return "Already compressing this chat…";
        var turns = Turns(thread).Select(t => t.Turn).ToList();
        var current = ValidCompression(thread);
        var covers = ChatCompressor.CoverCount(turns, current?.Covers ?? 0);
        if (covers == 0)
            return current is null ? "There isn't enough to compress yet: the last two exchanges are always sent in full."
                : "Everything but the last two exchanges is already compressed.";
        if (Hub.Core.Llm.UserPaused || !Hub.Core.Llm.Health.Available) return "The local model isn't available, so the chat can't be compressed now.";
        thread.Compressing = true;
        CompressionChanged?.Invoke();
        try
        {
            var model = Model;
            var window = thread.ContextWindow;
            var made = await Task.Run(() => ChatCompressor.CompressAsync(Hub.Core.Llm, Hub.S, model, current, turns, covers, automatic, window, DateTimeOffset.Now, CancellationToken.None));
            if (made is null)
            {
                if (automatic) thread.AutoFailedAt = turns.Count;
                return "The model's summary wasn't usable, so nothing changed. Try again in a moment.";
            }
            // The chat may have changed while the summary was written (an edit, Ask again): it's kept only if it still fits.
            if (ChatCompressor.Valid(made, Turns(thread).Select(t => t.Turn).ToList()) is null) return "The chat changed while it was being compressed, so nothing changed.";
            thread.Compression = made;
            if (!automatic && thread.Summary is { NoAutoCompress: true } summary)
            {
                summary.NoAutoCompress = false;
                Hub.Core.Chats.Update(summary.Id, c => c.NoAutoCompress = false);
            }
            SaveCompression(thread);
            MarkCompressed(thread);
            return $"Compressed {Plural.Of(made.Covers, "earlier message")}: about {TokenCount(made.TokensBefore)} → {TokenCount(made.TokensAfter)} tokens.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("ask", "Couldn't compress the chat", ex);
            if (automatic) thread.AutoFailedAt = turns.Count;
            return "Couldn't compress the chat: " + ex.Message;
        }
        finally
        {
            thread.Compressing = false;
            CompressionChanged?.Invoke();
        }
    }

    /// <summary>Sends the compressed messages in full again; the chat isn't compressed on its own after that (Compress still works).</summary>
    public void UndoCompression(ChatThread thread)
    {
        if (thread.Compression is null) return;
        thread.Compression = null;
        if (thread.Summary is { } summary)
        {
            summary.NoAutoCompress = true;
            Hub.Core.Chats.Update(summary.Id, c => c.NoAutoCompress = true);
        }
        SaveCompression(thread);
        MarkCompressed(thread);
        CompressionChanged?.Invoke();
    }

    /// <summary>After an answer: compresses the chat when its earlier messages no longer all go with a question (Settings › Ask Aqua).</summary>
    private async Task MaybeCompressAsync(ChatThread thread)
    {
        if (!Hub.S.Ask.AutoCompress || thread.Compressing || thread.Summary is { NoAutoCompress: true }) return;
        var turns = Turns(thread).Select(t => t.Turn).ToList();
        // A failed try waits until the chat has grown by two exchanges.
        if (thread.AutoFailedAt > 0 && turns.Count < thread.AutoFailedAt + 4) return;
        var covered = ChatCompressor.Valid(thread.Compression, turns)?.Covers ?? 0;
        var limit = AskAgent.ContextLimit(Hub.S, Hub.State.System?.Gpu?.VramTotalGb ?? 0);
        if (!ChatCompressor.ShouldCompress(turns, covered, Hub.S.Ask.HistoryMessages, thread.ContextUsed, limit)) return;
        Log.Info("ask", "Compressing a chat on its own: " + await CompressAsync(thread, automatic: true));
    }

    /// <summary>
    /// Bridges the agent (background thread) to the chat (UI thread): text and reasoning are buffered and flushed at
    /// most every 60 ms, steps and approvals are marshalled in order.
    /// </summary>
    private sealed class ChatHost : IAskHost
    {
        private readonly AskSession _session;
        private readonly ChatThread _thread;
        private readonly ChatMessageVM _answer;
        private readonly CancellationToken _ct;
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private readonly Stopwatch _thinkingClock = new();
        private bool _flushQueued;
        private volatile bool _closed;
        private int _nextStep;

        /// <summary>The answer is final: late status or text updates are ignored.</summary>
        public void Close() => _closed = true;

        public ChatHost(AskSession session, ChatThread thread, ChatMessageVM answer, CancellationToken ct)
        {
            _session = session; _thread = thread; _answer = answer; _ct = ct;
        }

        private static void Ui(Action a) => Hub.Ui.BeginInvoke(a, DispatcherPriority.Background);

        private void Progress() { if (ReferenceEquals(_thread, _session.Current)) _session.Progress?.Invoke(); }

        public void Status(string text) => Ui(() => { if (!_closed) _answer.Status = text; });

        // Not dropped once the answer is final: a picture can arrive just before it.
        public void Picture(AskPicture picture)
        {
            // The thumbnail is made here, off the UI thread (it's frozen, so the chat can show it).
            var thumbnail = picture.Image is { } bytes ? Images.Thumbnail(bytes, 192) : null;
            Ui(() =>
            {
                _answer.Pictures.Add(_session.PictureFor(_thread, picture, thumbnail));
                Progress();
            });
        }

        public int StepStarted(string icon, string text)
        {
            var id = Interlocked.Increment(ref _nextStep);
            Ui(() =>
            {
                _answer.Steps.Add(new StepVM { Id = id, Icon = icon, Text = text });
                Progress();
            });
            return id;
        }

        public void StepFinished(int id, string text, bool ok = true, string? url = null) => Ui(() =>
        {
            var step = _answer.Steps.FirstOrDefault(s => s.Id == id);
            if (step is null) return;
            step.Text = text;
            step.State = ok ? "ok" : "failed";
            if (url is not null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                step.Url = url;
                step.OpenCommand = new RelayCommand(() => AppLauncher.OpenUrl(url));
            }
        });

        public void Thinking(string delta)
        {
            lock (_gate)
            {
                if (!_thinkingClock.IsRunning) _thinkingClock.Start();
                _reasoning.Append(delta);
            }
            QueueFlush();
        }

        public void Text(string delta)
        {
            lock (_gate)
            {
                _thinkingClock.Stop();
                _text.Append(delta);
            }
            QueueFlush();
        }

        public void ResetText()
        {
            lock (_gate) _text.Clear();
            QueueFlush();
        }

        private void QueueFlush()
        {
            lock (_gate)
            {
                if (_flushQueued) return;
                _flushQueued = true;
            }
            Hub.Ui.BeginInvoke(async () =>
            {
                await Task.Delay(60);
                Flush();
            }, DispatcherPriority.Background);
        }

        /// <summary>Pushes buffered text to the message (UI thread).</summary>
        public void Flush()
        {
            if (_closed) return;
            string text, reasoning;
            double seconds;
            lock (_gate)
            {
                _flushQueued = false;
                text = _text.ToString();
                reasoning = _reasoning.ToString();
                seconds = _thinkingClock.Elapsed.TotalSeconds;
            }
            if (!Hub.Ui.CheckAccess()) { Hub.Ui.BeginInvoke(Flush); return; }
            if (reasoning.Length > 0 && reasoning != _answer.Reasoning)
            {
                _answer.Reasoning = reasoning;
                _answer.ReasoningLabel = text.Length == 0 ? $"Thinking… {seconds:0}s" : $"Thought for {Math.Max(1, seconds):0}s";
                if (text.Length == 0) _answer.ReasoningOpen = true;
            }
            if (text != _answer.Text)
            {
                _answer.Text = text;
                if (text.Length > 0)
                {
                    _answer.IsThinking = false;
                    if (_answer.ReasoningOpen) _answer.ReasoningOpen = false;
                    if (reasoning.Length > 0) _answer.ReasoningLabel = $"Thought for {Math.Max(1, seconds):0}s";
                }
            }
            Progress();
        }

        public async Task<bool> ApproveAsync(ToolApproval request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Hub.Ui.InvokeAsync(() =>
            {
                _answer.Approval = new ApprovalVM
                {
                    Title = request.Title, Detail = request.Detail, Icon = request.Icon,
                    AllowCommand = new RelayCommand(() => tcs.TrySetResult(true)),
                    AllowForChatCommand = new RelayCommand(() =>
                    {
                        if (request.AllowForChat) lock (_thread.AllowedForChat) _thread.AllowedForChat.Add(request.Tool);
                        tcs.TrySetResult(true);
                    }),
                    CanAllowForChat = request.AllowForChat,
                    DenyCommand = new RelayCommand(() => tcs.TrySetResult(false)),
                };
                Progress();
            });
            // Unanswered requests are declined after three minutes (the model is released meanwhile).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var reg = timeout.Token.Register(() => tcs.TrySetResult(false));
            var ok = await tcs.Task.ConfigureAwait(false);
            Ui(() => _answer.Approval = null);
            return ok;
        }
    }
}
