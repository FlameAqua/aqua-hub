using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;

namespace AquaHub.UI.ViewModels;

public sealed class ChatMessageVM : ObservableObject
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => (IsUser ? "You: " : "Aqua: ") + AquaHub.Core.Util.HtmlText.OneLine(Text, 160);

    private string _text = "";
    private bool _thinking;
    private List<Citation> _citations = new();
    private string _footer = "";
    private string _reasoning = "";
    private string _reasoningLabel = "";
    private bool _reasoningOpen;
    private string _status = "";
    private ApprovalVM? _approval;
    private bool _done;

    public bool IsUser { get; init; }

    /// <summary>When it was written (kept with the chat; shown in exports).</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>What this answer read (excerpts), kept with the chat for follow-up questions.</summary>
    public List<ChatSource> Sources { get; set; } = new();

    /// <summary>Part of the model's multi-turn history (clipboard summaries are not).</summary>
    public bool IsChat { get; init; }

    public string Text { get => _text; set { if (Set(ref _text, value)) Raise(nameof(HasText)); } }
    public bool HasText => _text.Length > 0;
    /// <summary>Still waiting for the first words (shows the shimmer).</summary>
    public bool IsThinking { get => _thinking; set => Set(ref _thinking, value); }
    public List<Citation> Citations { get => _citations; set { Set(ref _citations, value); Raise(nameof(HasCitations)); } }
    public bool HasCitations => _citations.Count > 0;
    public string Footer { get => _footer; set => Set(ref _footer, value); }

    /// <summary>The model's reasoning (Think mode), shown collapsed under "Thought for …".</summary>
    public string Reasoning { get => _reasoning; set { if (Set(ref _reasoning, value)) Raise(nameof(HasReasoning)); } }
    public bool HasReasoning => _reasoning.Length > 0;
    public string ReasoningLabel { get => _reasoningLabel; set => Set(ref _reasoningLabel, value); }
    public bool ReasoningOpen { get => _reasoningOpen; set => Set(ref _reasoningOpen, value); }

    /// <summary>What Ask is doing right now ("Searching the web…"), shown while it works.</summary>
    public string Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => _status.Length > 0;

    /// <summary>The searches, pages and files Ask used, in order.</summary>
    public ObservableCollection<StepVM> Steps { get; } = new();

    private bool _stepsOpen = true;
    private string _activity = "Working…";

    /// <summary>The activity list is open while Ask works and folds into a one-line summary when a long answer is done.</summary>
    public bool StepsOpen { get => _stepsOpen; set => Set(ref _stepsOpen, value); }
    public string ActivityLabel { get => _activity; set => Set(ref _activity, value); }

    /// <summary>"Searched the web 3× · read 6 pages · 1 file" from the finished steps.</summary>
    public void FoldSteps()
    {
        int Count(string prefix) => Steps.Count(s => s.Text.StartsWith(prefix, StringComparison.Ordinal) && !s.IsFailed);
        var web = Count("Searched the web") + Count("Search the web");
        var files = Count("Searched your files");
        var feeds = Count("Searched your feeds");
        var read = Steps.Count(s => s.Text.StartsWith("Read ", StringComparison.Ordinal) && !s.IsFailed);
        var parts = new List<string>();
        if (feeds > 0) parts.Add("checked your feeds");
        if (web > 0) parts.Add(web == 1 ? "searched the web" : $"searched the web {web}×");
        if (files > 0) parts.Add("searched your files");
        if (read > 0) parts.Add("read " + Core.Util.Plural.Of(read, "source"));
        var summary = parts.Count > 0 ? string.Join(" · ", parts) : Core.Util.Plural.Of(Steps.Count, "step");
        ActivityLabel = char.ToUpperInvariant(summary[0]) + summary[1..];
        StepsOpen = Steps.Count <= 3;
    }

    /// <summary>A pending "Allow / Deny" request for an action.</summary>
    public ApprovalVM? Approval { get => _approval; set { Set(ref _approval, value); Raise(nameof(HasApproval)); } }
    public bool HasApproval => _approval is not null;

    /// <summary>Files and screenshots sent with a question.</summary>
    public List<AttachmentChipVM> Attachments { get; init; } = new();
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>"Research", "Think", "Web", "PC" — how the question was asked.</summary>
    public string ModeLabel { get; init; } = "";
    public bool HasMode => ModeLabel.Length > 0;

    /// <summary>For answers: the question and switches that produced it (Retry / Dig deeper).</summary>
    public string Question { get; init; } = "";
    public AskOptions? Options { get; init; }

    /// <summary>The answer is complete (shows Copy / Retry / Dig deeper).</summary>
    public bool IsDone { get => _done; set => Set(ref _done, value); }

    public ICommand? CopyCommand { get; set; }
    public ICommand? RetryCommand { get; set; }
    public ICommand? DeeperCommand { get; set; }
    public ICommand? EditCommand { get; set; }
    public ICommand? ReadAloudCommand { get; set; }

    private bool _editing;
    private string _editText = "";
    private bool _speaking;

    /// <summary>Your question is being edited in place (Save sends it again and replaces what followed).</summary>
    public bool IsEditing { get => _editing; set => Set(ref _editing, value); }
    public string EditText { get => _editText; set => Set(ref _editText, value); }
    public ICommand? SaveEditCommand { get; set; }
    public ICommand? CancelEditCommand { get; set; }

    /// <summary>This answer is being read aloud (the button then stops it).</summary>
    public bool IsSpeaking { get => _speaking; set { if (Set(ref _speaking, value)) Raise(nameof(ReadAloudTip)); } }
    public string ReadAloudTip => _speaking ? "Stop reading" : "Read aloud";

    private bool _memoryActions;

    /// <summary>After "remember …" / "forget …": Undo, or answer the message as an ordinary question instead.</summary>
    public bool HasMemoryActions { get => _memoryActions; set => Set(ref _memoryActions, value); }
    public ICommand? UndoMemoryCommand { get; set; }
    public ICommand? AnswerInsteadCommand { get; set; }
}

/// <summary>A chat in the history list, or a group header ("Today", "Starred").</summary>
public sealed class ChatListItemVM : ObservableObject
{
    private bool _renaming;
    private string _renameText = "";
    private bool _busy;

    public bool IsHeader { get; init; }
    public bool IsChat => !IsHeader;
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public bool Starred { get; init; }
    public bool IsCurrent { get; init; }
    /// <summary>An answer is still being written in this chat.</summary>
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
    public bool IsRenaming { get => _renaming; set => Set(ref _renaming, value); }
    public string RenameText { get => _renameText; set => Set(ref _renameText, value); }
    public string StarLabel => Starred ? "Unstar (let it expire)" : "Star (keep it)";

    public ICommand? OpenCommand { get; set; }
    public ICommand? StarCommand { get; set; }
    /// <summary>Copy the chat as Markdown / save it as a .md file.</summary>
    public ICommand? CopyCommand { get; set; }
    public ICommand? SaveCommand { get; set; }
    public ICommand? RenameCommand { get; set; }
    public ICommand? CommitRenameCommand { get; set; }
    public ICommand? CancelRenameCommand { get; set; }
    public ICommand? DeleteCommand { get; set; }
}

public sealed class StepVM : ObservableObject
{
    private string _text = "";
    private string _state = "running";

    public int Id { get; init; }
    public string Icon { get; init; } = "search";
    public string Text { get => _text; set => Set(ref _text, value); }
    /// <summary>running | ok | failed</summary>
    public string State { get => _state; set { if (Set(ref _state, value)) { Raise(nameof(IsRunning)); Raise(nameof(IsFailed)); } } }
    public bool IsRunning => _state == "running";
    public bool IsFailed => _state == "failed";
    public string? Url { get; set; }
    public ICommand? OpenCommand { get; set; }
}

public sealed class ApprovalVM : ObservableObject
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Icon { get; init; } = "shield";
    public ICommand AllowCommand { get; init; } = null!;
    public ICommand AllowForChatCommand { get; init; } = null!;
    /// <summary>False for actions that need an OK every time (the button is hidden).</summary>
    public bool CanAllowForChat { get; init; } = true;
    public ICommand DenyCommand { get; init; } = null!;
}

/// <summary>An attachment shown as a chip (in the composer before sending, and on the sent question).</summary>
public sealed class AttachmentChipVM : ObservableObject
{
    private bool _loading;
    private string _note = "";

    public string Name { get; init; } = "";
    public string Icon { get; init; } = "file";
    public ImageSource? Thumbnail { get; init; }
    public bool HasThumbnail => Thumbnail is not null;
    public string Note { get => _note; set => Set(ref _note, value); }
    public bool IsLoading { get => _loading; set => Set(ref _loading, value); }
    public ICommand? RemoveCommand { get; set; }
    public AskAttachment? Attachment { get; set; }
    public string Tooltip => Note.Length > 0 ? $"{Name} · {Note}" : Name;
}
