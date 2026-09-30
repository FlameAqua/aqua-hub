using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.Controls;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

/// <summary>A view over <see cref="AskSession"/> — the chats themselves outlive this page.</summary>
public partial class AskPage : UserControl, IPage
{
    private static readonly string[] SuggestionTexts =
    {
        "What's happening near me today?",
        "Summarise the markets and my watchlist",
        "What's on my agenda this week?",
        "What are people talking about locally?",
        "Explain the biggest story in simple terms",
        "What do prediction markets expect next?",
    };

    /// <summary>The history panel's state for this session (null: decide by window width).</summary>
    private static bool? _historyOpen;
    private readonly UiThrottle _scroll;
    private bool _syncing;
    private bool _follow = true;
    private string _dictationBase = "";
    private string _beforeDictation = "";
    private string? _untidied;
    private CancellationTokenSource? _tidyCts;

    public AskPage()
    {
        InitializeComponent();
        _scroll = new UiThrottle(() => { if (_follow) Scroller.ScrollToEnd(); }, 60);
        Scroller.ScrollChanged += (_, e) =>
        {
            // Follow the answer while the reader is at the bottom; stop following once they scroll up to read.
            if (e.ExtentHeightChange == 0) _follow = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 24;
        };
        foreach (var s in SuggestionTexts)
        {
            var b = new Button { Style = (Style)FindResource("Btn.Standard"), Content = s, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(14, 8, 14, 8) };
            UiProps.SetCornerRadius(b, new CornerRadius(18));
            b.Click += (_, _) => Send(s);
            Suggestions.Children.Add(b);
        }
        PendingList.ItemsSource = Session.Pending;
        ChatList.ItemsSource = Session.Chats;
        CommandManager.AddPreviewExecutedHandler(Input, OnInputCommand);
        SizeChanged += (_, _) => { if (_historyOpen is null) ApplyHistoryVisibility(); };
        Session.Voice.Partial += OnVoicePartial;
        Session.Voice.Phrase += OnVoicePhrase;
        Session.Voice.Ended += OnVoiceEnded;
        Session.Voice.Problem += OnVoiceProblem;
        Session.Voice.Level += OnVoiceLevel;
        Session.Voice.Transcribing += OnVoiceTranscribing;
    }

    private static AskSession Session => Hub.Ask;

    public void OnNavigatedTo(string? arg)
    {
        Session.EnsureLoaded();
        Bind();
        Session.ChatChanged -= OnChatChanged;
        Session.ChatChanged += OnChatChanged;
        Session.BusyChanged -= OnBusyChanged;
        Session.BusyChanged += OnBusyChanged;
        Session.Progress -= _scroll.Request;
        Session.Progress += _scroll.Request;
        Session.EditRequested -= OnEditRequested;
        Session.EditRequested += OnEditRequested;
        Session.ContextChanged -= UpdateContext;
        Session.ContextChanged += UpdateContext;
        Session.Pending.CollectionChanged -= OnPendingChanged;
        Session.Pending.CollectionChanged += OnPendingChanged;
        Session.Chats.CollectionChanged -= OnChatsChanged;
        Session.Chats.CollectionChanged += OnChatsChanged;
        OnPendingChanged(null, null);
        OnChatsChanged(null, null);
        MarkdownView.AskAboutSelection -= OnAskAboutSelection;
        MarkdownView.AskAboutSelection += OnAskAboutSelection;
        MarkdownView.SearchSelection -= OnSearchSelection;
        MarkdownView.SearchSelection += OnSearchSelection;
        Hub.Core.Settings.Changed -= OnSettings;
        Hub.Core.Settings.Changed += OnSettings;
        OnBusyChanged();
        SyncModes();
        UpdateContext();
        UpdateRetention();
        ApplyHistoryVisibility();
        Hub.State.Changed -= OnState;
        Hub.State.Changed += OnState;
        UpdateSubtitle();
        _ = HandleArgAsync(arg);
        _follow = true;
        Dispatcher.BeginInvoke(() => { Scroller.ScrollToEnd(); Input.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    public void OnNavigatedFrom()
    {
        Hub.State.Changed -= OnState;
        // Answers keep streaming in the session; this page just stops listening.
        Session.Messages.CollectionChanged -= OnMessagesChanged;
        Session.ChatChanged -= OnChatChanged;
        Session.BusyChanged -= OnBusyChanged;
        Session.Progress -= _scroll.Request;
        Session.EditRequested -= OnEditRequested;
        Session.ContextChanged -= UpdateContext;
        Session.Pending.CollectionChanged -= OnPendingChanged;
        Session.Chats.CollectionChanged -= OnChatsChanged;
        Hub.Core.Settings.Changed -= OnSettings;
        MarkdownView.AskAboutSelection -= OnAskAboutSelection;
        MarkdownView.SearchSelection -= OnSearchSelection;
        if (Session.Voice.IsListening) Session.Voice.Stop();
    }

    /// <summary>Shows the current chat (called again when another chat is opened).</summary>
    private void Bind()
    {
        if (Messages.ItemsSource is INotifyCollectionChanged old) old.CollectionChanged -= OnMessagesChanged;
        Messages.ItemsSource = Session.Messages;
        Session.Messages.CollectionChanged -= OnMessagesChanged;
        Session.Messages.CollectionChanged += OnMessagesChanged;
        UpdateEmpty();
        UpdateTitle();
    }

    private void OnChatChanged() => Hub.OnUi(() =>
    {
        Bind();
        OnBusyChanged();
        _follow = true;
        Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    });

    /// <summary>
    /// Page arguments from other surfaces: "@clipboard", "@story:&lt;id&gt;", "@screen", "@attach", "@new", "@history",
    /// "@chat:&lt;id&gt;", "@skill:&lt;id&gt;:&lt;question&gt;" and "@research:", "@web:", "@pc:", "@think:" followed by a
    /// question; anything else is asked as is.
    /// </summary>
    private async Task HandleArgAsync(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;
        if (arg == "@clipboard") { _ = Session.SummariseClipboardAsync(); return; }
        if (arg.StartsWith("@story:", StringComparison.Ordinal)) { _ = Session.AskAboutStoryAsync(arg[7..]); return; }
        if (arg == "@attach") { Session.StartFresh(); OnAttach(this, new RoutedEventArgs()); return; }
        if (arg == "@new") { Session.NewChat(); Input.Focus(); return; }
        if (arg == "@history") { _historyOpen = true; ApplyHistoryVisibility(); ChatSearch.Focus(); return; }
        if (arg == "@voice") { Session.StartFresh(); OnMic(this, new RoutedEventArgs()); return; }
        if (arg.StartsWith("@chat:", StringComparison.Ordinal)) { Session.Open(arg[6..]); return; }
        if (arg.StartsWith("@skill:", StringComparison.Ordinal))
        {
            // Workbench › Try it: "@skill:<id>:<question>" — a new chat that uses that skill.
            var rest = arg[7..];
            var colon = rest.IndexOf(':');
            var id = colon > 0 ? rest[..colon] : rest;
            var q = colon > 0 ? rest[(colon + 1)..].Trim() : "";
            Session.NewChat();
            if (q.Length > 0)
                _ = Session.SendAsync(q, new AskOptions { Web = Session.Web || Hub.S.Ask.Web, Computer = Hub.S.Ask.Computer, Think = Session.Think, SkillId = id });
            return;
        }
        // Everything below is a question from elsewhere in the app: it gets its own chat.
        Session.StartFresh();
        if (arg == "@screen")
        {
            if (await Session.AttachScreenshotAsync(snip: false)) Send("What's on my screen? Explain what I'm looking at and anything I should know.");
            return;
        }
        foreach (var (prefix, apply) in new (string, Action)[]
        {
            ("@research:", () => { Session.Research = true; Session.Web = true; }),
            ("@web:", () => Session.Web = true),
            ("@pc:", () => Session.Computer = Hub.S.Ask.Computer),
            ("@think:", () => Session.Think = true),
        })
        {
            if (!arg.StartsWith(prefix, StringComparison.Ordinal)) continue;
            apply();
            SyncModes();
            var q = arg[prefix.Length..].Trim();
            if (q.Length > 0) Send(q);
            else Input.Focus();
            return;
        }
        Send(arg);
    }

    private void OnState(string topic)
    {
        if (topic == Core.Agents.Topics.Ai) Hub.OnUi(UpdateSubtitle);
    }

    private void OnSettings(Core.Settings.HubSettings s) => Hub.OnUi(() => { SyncModes(); UpdateRetention(); UpdateContext(); });

    /// <summary>Follows the model's status live (it can come and go while the page is open).</summary>
    private void UpdateSubtitle()
    {
        var ai = Hub.State.Ai;
        TurnOnAi.Visibility = ai?.Available != true && !Hub.Core.Llm.UserPaused && OllamaManager.IsLocalOllama && OllamaManager.Installed && !Hub.Ollama.Transitioning
            ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = Hub.Core.Llm.UserPaused ? "AI is paused — answers list what your agents collected, with sources. Resume AI from the tray."
            : ai?.Available == true ? $"Private answers from your agents, the web and your PC when you allow it · {ai.ActiveModel} on this PC"
            : Hub.Ollama.UserTurnedOff ? "You turned the local model off — answers list what your agents collected, with sources."
            : "The local model is offline — answers list what your agents collected, with sources. Start Ollama for written answers.";
    }

    private async void OnTurnOnAi(object sender, RoutedEventArgs e)
    {
        TurnOnAi.IsEnabled = false;
        Subtitle.Text = "Starting the local model…";
        await Hub.Ollama.TurnOnAsync();
        TurnOnAi.IsEnabled = true;
        UpdateSubtitle();
    }

    private void OnPendingChanged(object? sender, NotifyCollectionChangedEventArgs? e) =>
        PendingList.Visibility = Session.Pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmpty();
        if (e.Action == NotifyCollectionChangedAction.Add) { _follow = true; _scroll.Request(); }
    }

    // ───────────────────────────── Chat history ─────────────────────────────

    private void OnChatsChanged(object? sender, NotifyCollectionChangedEventArgs? e)
    {
        HistoryEmpty.Visibility = Session.Chats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Text = Session.Filter.Trim().Length > 0 ? "No chats match." : Hub.S.Privacy.KeepAskHistory ? "Your chats will appear here." : "Chat history is off (Settings › Privacy).";
        UpdateTitle();
    }

    private void ApplyHistoryVisibility()
    {
        var open = _historyOpen ?? ActualWidth is 0 or >= 1000;
        HistoryPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        HistoryToggle.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnToggleHistory(object sender, RoutedEventArgs e)
    {
        _historyOpen = HistoryPanel.Visibility != Visibility.Visible;
        ApplyHistoryVisibility();
    }

    private void OnChatSearch(object sender, TextChangedEventArgs e) => Session.Filter = ChatSearch.Text;

    private void OnRenameBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box) Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void OnCopyChat(object sender, RoutedEventArgs e)
    {
        Session.CopyChat();
        CopyChatIcon.Visibility = Visibility.Collapsed;
        CopiedChatIcon.Visibility = Visibility.Visible;
        await Task.Delay(1500);
        CopyChatIcon.Visibility = Visibility.Visible;
        CopiedChatIcon.Visibility = Visibility.Collapsed;
    }

    private void OnSaveChat(object sender, RoutedEventArgs e) => Session.SaveChat();

    private void OnStarCurrent(object sender, RoutedEventArgs e)
    {
        if (Session.Current.Summary is not { } s) return;
        Session.SetStar(s.Id, !s.Starred);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var summary = Session.Current.Summary;
        PageTitle.Text = summary is null ? "Ask Aqua" : summary.Title;
        StarChatButton.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;
        var starred = summary?.Starred == true;
        StarOutline.Visibility = starred ? Visibility.Collapsed : Visibility.Visible;
        StarFilled.Visibility = starred ? Visibility.Visible : Visibility.Collapsed;
        StarChatButton.ToolTip = starred ? "Starred — this chat is kept until you delete it" : "Star this chat (starred chats never expire)";
    }

    private void UpdateRetention()
    {
        var days = Hub.S.Ask.ChatRetentionDays;
        RetentionNote.Text = !Hub.S.Privacy.KeepAskHistory ? "Chats aren't kept (Settings › Privacy)."
            : days <= 0 ? "Chats are kept until you delete them."
            : $"Unstarred chats are deleted after {days} days without use · Settings › Ask Aqua";
    }

    // ───────────────────────────── Composer ─────────────────────────────

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            OnSend(sender, e);
        }
        else if (e.Key == Key.Up && Input.Text.Length == 0)
        {
            // Up in an empty box edits your last question, like most chat apps.
            if (EditLast()) e.Handled = true;
        }
    }

    private bool EditLast()
    {
        if (Session.IsBusy) return false;
        var last = Session.Messages.LastOrDefault(m => m.IsUser && m.IsChat);
        if (last?.EditCommand is null) return false;
        last.EditCommand.Execute(null);
        return true;
    }

    private void OnEditLast(object sender, RoutedEventArgs e) => EditLast();

    private void OnEditBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; box.BringIntoView(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Ctrl+V with an image or files on the clipboard attaches them (text pastes as usual).</summary>
    private void OnInputCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                if (files.Count > 0) { e.Handled = true; _ = Session.AttachFilesAsync(files); }
            }
            else if (!Clipboard.ContainsText() && ScreenCapture.ClipboardImage() is { } png)
            {
                e.Handled = true;
                _ = Session.AttachImageAsync(png, $"Pasted image {DateTime.Now:HH.mm.ss}.png");
            }
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (Session.Voice.IsListening) Session.Voice.Stop();
        var text = Input.Text.Trim();
        if (Session.AttachmentsLoading)
        {
            ModeHint.Text = "Still reading your attachments…";
            return;
        }
        var hasFiles = Session.Pending.Any(p => p.Attachment is not null);
        if (text.Length == 0 && !hasFiles) return;
        if (text.Length == 0)
            text = Session.Pending.Any(p => p.Attachment?.Kind == AttachmentKind.Image) && Session.Pending.All(p => p.Attachment is null || p.Attachment.Kind == AttachmentKind.Image)
                ? "What's in this image?" : "Summarise what I attached.";
        Input.Text = "";
        Send(text);
    }

    private static void Send(string text) => _ = Session.SendAsync(text);

    private void OnStop(object sender, RoutedEventArgs e) => Session.Stop();

    private void OnNewChat(object sender, RoutedEventArgs e)
    {
        ContextPopup.IsOpen = false;
        Session.NewChat();
        UpdateEmpty();
        Input.Focus();
    }

    private void OnCitation(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Citation cite }) MarkdownView.OpenCitation(cite);
    }

    private void OnEditRequested(string text)
    {
        Input.Text = text;
        Input.CaretIndex = Input.Text.Length;
        Input.Focus();
    }

    private void OnAskAboutSelection(string selection)
    {
        var quote = selection.Length > 400 ? selection[..400] + "…" : selection;
        Input.Text = $"About “{quote.Replace('\n', ' ')}”: ";
        Input.CaretIndex = Input.Text.Length;
        Input.Focus();
    }

    private void OnSearchSelection(string selection)
    {
        var q = selection.Length > 200 ? selection[..200] : selection;
        _ = Session.SendAsync($"Look this up on the web: {q.Replace('\n', ' ')}", new AskOptions { Web = true, Think = Session.Think });
    }

    /// <summary>Read-only text boxes (your questions, the reasoning) would swallow the wheel; the chat scrolls instead unless they can scroll themselves.</summary>
    private void OnScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        var inner = e.OriginalSource as DependencyObject;
        while (inner is not null && inner != Scroller && inner is not ScrollViewer) inner = VisualTreeHelper.GetParent(inner) ?? LogicalTreeHelper.GetParent(inner);
        if (inner is not ScrollViewer sv || sv == Scroller) return;
        var canScroll = e.Delta < 0 ? sv.VerticalOffset < sv.ScrollableHeight - 0.5 : sv.VerticalOffset > 0.5;
        if (canScroll && sv.ScrollableHeight > 0) return;
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    // ───────────────────────────── Voice ─────────────────────────────

    private async void OnMic(object sender, RoutedEventArgs e)
    {
        if (Session.Voice.IsListening) { Session.Voice.Stop(); return; }
        if (Hub.S.Ask.Voice == "off")
        {
            ModeHint.Text = "Voice input is off — switch it on in Settings › Ask Aqua.";
            return;
        }
        _tidyCts?.Cancel();
        HideTidyUndo();
        _dictationBase = Input.Text.TrimEnd();
        _beforeDictation = _dictationBase;
        MicButton.IsEnabled = false;
        var error = await Session.Voice.StartAsync(Hub.S.Ask.Voice, Hub.S.Location.Language, Hub.S.Ask.Microphone);
        MicButton.IsEnabled = true;
        if (error is not null)
        {
            ModeHint.Text = error;
            return;
        }
        SetListening(true);
    }

    private void SetListening(bool on)
    {
        MicButton.Style = (Style)FindResource(on ? "Btn.Accent" : "Btn.Icon");
        MicButton.Width = 36;
        MicButton.Height = 36;
        MicButton.ToolTip = on ? "Listening — click to stop" : "Speak your question";
        ShowVoiceBar(on);
        if (!on)
        {
            UpdateModeHint();
            return;
        }
        var with = Session.Voice.Describe is { Length: > 0 } d ? " · " + d : "";
        ModeHint.Text = Hub.S.Ask.Voice switch
        {
            "online" => "Listening (Windows online speech" + with + ")…",
            "whisper" => "Listening" + with + "…",
            _ => "Listening on this PC" + with + "…",
        } + (Session.Voice.Note is { } note ? " " + note : "");
    }

    /// <summary>
    /// Under the Ask box while dictating: the microphone level and, with Windows' recognizers, a note that they often
    /// mishear and Whisper does far better (until you hide it). With Whisper, how it works: it writes when you stop.
    /// </summary>
    private void ShowVoiceBar(bool on)
    {
        VoiceBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        VoiceMeterFill.Width = 0;
        if (!on) return;
        var engine = Hub.S.Ask.Voice;
        // Windows online speech doesn't report a level.
        VoiceMeter.Visibility = engine == "online" ? Visibility.Collapsed : Visibility.Visible;
        var suggest = engine != "whisper" && Hub.S.Ask.WhisperTip;
        WhisperTipLinks.Visibility = suggest ? Visibility.Visible : Visibility.Collapsed;
        VoiceTip.Text = engine == "whisper" ? "Whisper writes down what you said when you stop — click the microphone, or just pause."
            : !suggest ? ""
            : Hub.Core.Whisper.Installed() is not null
                ? "Windows' speech recognition often mishears. Whisper is installed — switch voice input to it for much better results."
                : "Windows' speech recognition isn't perfect and often mishears. For much better results, install Whisper — free, and it runs on this PC.";
    }

    private void OnVoiceLevel(double level)
    {
        if (IsLoaded && VoiceBar.Visibility == Visibility.Visible) VoiceMeterFill.Width = 54 * Math.Clamp(level, 0, 1);
    }

    private void OnVoiceTranscribing()
    {
        if (!IsLoaded) return;
        VoiceMeterFill.Width = 0;
        MicButton.ToolTip = "Whisper is writing down what you said";
        ModeHint.Text = "Writing down what you said…";
    }

    private void OnWhisperSetup(object sender, RoutedEventArgs e)
    {
        if (Session.Voice.IsListening) Session.Voice.Stop();
        Hub.Windows.ShowMain("settings", "ask:whisper");
    }

    private void OnHideWhisperTip(object sender, RoutedEventArgs e)
    {
        Hub.Core.Settings.Update(s => s.Ask.WhisperTip = false);
        WhisperTipLinks.Visibility = Visibility.Collapsed;
        VoiceTip.Text = "Hidden. Whisper is in Settings › Ask Aqua › Voice if you change your mind.";
    }

    private void OnVoiceProblem(string advice)
    {
        if (IsLoaded && Session.Voice.IsListening) ModeHint.Text = advice;
    }

    private void OnVoicePartial(string text)
    {
        if (!IsLoaded) return;
        Input.Text = (_dictationBase.Length > 0 ? _dictationBase + " " : "") + text;
        Input.CaretIndex = Input.Text.Length;
    }

    private void OnVoicePhrase(string text)
    {
        if (!IsLoaded) return;
        var phrase = text.Trim();
        if (phrase.Length > 0) phrase = char.ToUpper(phrase[0], CultureInfo.CurrentCulture) + phrase[1..];
        _dictationBase = (_dictationBase.Length > 0 ? _dictationBase + " " : "") + phrase;
        Input.Text = _dictationBase;
        Input.CaretIndex = Input.Text.Length;
    }

    private async void OnVoiceEnded(string? error)
    {
        SetListening(false);
        if (error is not null) ModeHint.Text = error;
        if (!IsLoaded) return;
        Input.Text = _dictationBase;
        Input.CaretIndex = Input.Text.Length;
        Input.Focus();
        if (error is not null) return;
        await TidyAsync();
        if (IsLoaded && !Session.Voice.IsListening && Hub.S.Ask.VoiceAutoSend && Input.Text.Trim().Length > 0) OnSend(this, new RoutedEventArgs());
    }

    /// <summary>
    /// "Tidy up what I say": the local model corrects what the recognizer misheard, using the other wordings it
    /// considered. Skipped when the model isn't running; the words stay as heard if it is slow or its reply isn't a
    /// correction. Undo puts them back.
    /// </summary>
    private async Task TidyAsync()
    {
        var heard = Session.Voice.Heard.ToList();
        if (!Hub.S.Ask.VoiceTidy || DictationTidy.Skip(heard) is not null || !Hub.Core.Llm.Health.Available || Hub.Core.Llm.UserPaused) return;
        var dictated = Input.Text;
        _tidyCts?.Cancel();
        var cts = _tidyCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        ModeHint.Text = "Tidying up what you said…";
        var tidied = await Task.Run(() => DictationTidy.TidyAsync(Hub.Core.Llm, heard, cts.Token));
        if (cts != _tidyCts || Session.Voice.IsListening) return;
        // Typed over it or left the page meanwhile: leave the words alone.
        if (!IsLoaded || Input.Text != dictated || tidied is null || tidied == DictationTidy.Joined(heard))
        {
            UpdateModeHint();
            return;
        }
        Input.Text = (_beforeDictation.Length > 0 ? _beforeDictation + " " : "") + tidied;
        Input.CaretIndex = Input.Text.Length;
        _dictationBase = Input.Text;
        _untidied = dictated;
        ModeHint.Text = "Tidied up by the local model.";
        TidyUndo.Visibility = Visibility.Visible;
    }

    private void OnUndoTidy(object sender, RoutedEventArgs e)
    {
        if (_untidied is { } heard)
        {
            Input.Text = heard;
            Input.CaretIndex = Input.Text.Length;
            Input.Focus();
        }
        HideTidyUndo();
        UpdateModeHint();
    }

    private void HideTidyUndo()
    {
        _untidied = null;
        TidyUndo.Visibility = Visibility.Collapsed;
    }

    // Once you edit the tidied words, Undo would lose your edits.
    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_untidied is not null && Input.Text != _dictationBase) HideTidyUndo();
    }

    // ───────────────────────────── Context meter ─────────────────────────────

    private void UpdateContext()
    {
        var used = Session.Current.ContextUsed;
        var window = Session.Current.ContextWindow > 0 ? Session.Current.ContextWindow : Hub.S.Ask.ContextWindow > 0 ? Hub.S.Ask.ContextWindow : Hub.S.Ai.ContextTokens;
        var share = window > 0 ? Math.Clamp(used / (double)window, 0, 1) : 0;
        ContextFill.Width = 54 * share;
        ContextFill.Background = (Brush)FindResource(share >= 0.85 ? "B.Warn" : "B.Accent");
        ContextLabel.Text = used == 0 ? "Context" : $"{Tokens(used)} / {Tokens(window)}";
        var mode = Hub.S.Ask.ContextWindow > 0 ? $"fixed at {Tokens(Hub.S.Ask.ContextWindow)} tokens" : "automatic (it grows when an answer needs more)";
        ContextDetail.Text = used == 0
            ? $"Nothing used in this chat yet. The window is {mode}."
            : $"The last answer used about {used.ToString("N0", CultureInfo.CurrentCulture)} of {window.ToString("N0", CultureInfo.CurrentCulture)} tokens ({share:P0}) — your question, the earlier messages and everything Aqua gathered. The window is {mode}.";
        _syncing = true;
        ContextWindowCombo.SelectedItem = ContextWindowCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Hub.S.Ask.ContextWindow.ToString(CultureInfo.InvariantCulture));
        HistoryCombo.SelectedItem = HistoryCombo.Items.Cast<ComboBoxItem>().OrderBy(i => Math.Abs(int.Parse((string)i.Tag, CultureInfo.InvariantCulture) - Hub.S.Ask.HistoryMessages)).First();
        _syncing = false;
    }

    private static string Tokens(int n) => n >= 1000 ? (n / 1000.0).ToString(n >= 10000 ? "0" : "0.#", CultureInfo.CurrentCulture) + "K" : n.ToString(CultureInfo.CurrentCulture);

    private void OnContext(object sender, RoutedEventArgs e)
    {
        UpdateContext();
        ContextPopup.IsOpen = !ContextPopup.IsOpen;
    }

    private void OnContextWindowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ContextWindowCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var size = int.Parse(tag, CultureInfo.InvariantCulture);
        Hub.Core.Settings.Update(s => s.Ask.ContextWindow = size);
    }

    private void OnHistoryLengthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || HistoryCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var n = int.Parse(tag, CultureInfo.InvariantCulture);
        Hub.Core.Settings.Update(s => s.Ask.HistoryMessages = n);
    }

    // ───────────────────────────── Attachments ─────────────────────────────

    private void OnAttach(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach files to your question",
            Multiselect = true,
            Filter = "Documents and images|*.txt;*.md;*.csv;*.tsv;*.json;*.xml;*.yaml;*.yml;*.log;*.html;*.htm;*.pdf;*.docx;*.pptx;*.xlsx;*.odt;*.ods;*.odp;*.rtf;" +
                     "*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.cs;*.js;*.ts;*.py;*.java;*.cpp;*.c;*.h;*.go;*.rs;*.sql;*.ps1;*.sh|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _ = Session.AttachFilesAsync(dialog.FileNames);
    }

    private void OnScreenshotMenu(object sender, RoutedEventArgs e)
    {
        if (ScreenshotButton.ContextMenu is not { } menu) return;
        bool hasImage;
        try { hasImage = Clipboard.ContainsImage(); } catch { hasImage = false; }
        PasteImageItem.IsEnabled = hasImage;
        BuildCaptureItems(menu);
        menu.PlacementTarget = ScreenshotButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>With several screens: one item per screen and "all screens"; and a window picker (not password managers or sign-in prompts).</summary>
    private void BuildCaptureItems(ContextMenu menu)
    {
        foreach (var old in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, "capture")).ToList()) menu.Items.Remove(old);
        var at = 1;
        void Add(FrameworkElement item) { item.Tag = "capture"; menu.Items.Insert(at++, item); }
        var monitors = ScreenCapture.Monitors();
        if (monitors.Count > 1)
        {
            foreach (var m in monitors)
            {
                var item = new MenuItem { Header = "Capture " + m.Label };
                item.Click += async (_, _) => { if (!await Session.AttachCaptureAsync(() => ScreenCapture.CaptureMonitorAsync(m), "Screen " + m.Number)) ModeHint.Text = "Couldn't capture that screen."; Input.Focus(); };
                Add(item);
            }
            var all = new MenuItem { Header = "Capture all screens" };
            all.Click += async (_, _) => { if (!await Session.AttachCaptureAsync(ScreenCapture.CaptureAllAsync, "All screens")) ModeHint.Text = "Couldn't capture the screens."; Input.Focus(); };
            Add(all);
        }
        var windows = DesktopWindows.List().Where(w => !w.Minimized && !Core.Ai.Assistant.DesktopRules.IsPrivateWindow(w.Process)).Take(14).ToList();
        if (windows.Count > 0)
        {
            var pick = new MenuItem { Header = "Capture a window" };
            foreach (var w in windows)
            {
                var item = new MenuItem { Header = Core.Util.HtmlText.Truncate(w.Title, 60) + "  ·  " + w.Process };
                item.Click += async (_, _) =>
                {
                    if (!await Session.AttachCaptureAsync(() => Task.Run(() => ScreenCapture.CaptureWindow(w.Handle, w.Title)), Core.Util.HtmlText.Truncate(w.Process, 30)))
                        ModeHint.Text = "Couldn't capture that window.";
                    Input.Focus();
                };
                pick.Items.Add(item);
            }
            Add(pick);
        }
    }

    private async void OnCaptureScreen(object sender, RoutedEventArgs e)
    {
        if (!await Session.AttachScreenshotAsync(snip: false)) ModeHint.Text = "Couldn't capture the screen.";
        Input.Focus();
    }

    private async void OnSnip(object sender, RoutedEventArgs e)
    {
        ModeHint.Text = "Drag over the area to capture (Esc cancels)…";
        var ok = await Session.AttachScreenshotAsync(snip: true);
        ModeHint.Text = ok ? "" : "No snip arrived.";
        UpdateModeHint();
        Input.Focus();
    }

    private void OnPasteImage(object sender, RoutedEventArgs e)
    {
        if (ScreenCapture.ClipboardImage() is { } png) _ = Session.AttachImageAsync(png, $"Pasted image {DateTime.Now:HH.mm.ss}.png");
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            _ = Session.AttachFilesAsync(files.Where(File.Exists));
        }
    }

    // ───────────────────────────── Switches ─────────────────────────────

    private void SyncModes()
    {
        _syncing = true;
        var s = Hub.S.Ask;
        WebToggle.IsEnabled = ResearchToggle.IsEnabled = s.Web;
        if (!s.Web) { Session.Web = false; Session.Research = false; }
        ComputerToggle.Visibility = s.Computer ? Visibility.Visible : Visibility.Collapsed;
        if (!s.Computer) Session.Computer = false;
        WebToggle.IsChecked = Session.Web || Session.Research;
        ResearchToggle.IsChecked = Session.Research;
        ThinkToggle.IsChecked = Session.Think;
        ComputerToggle.IsChecked = Session.Computer;
        WebToggle.ToolTip = s.Web ? "Web: Aqua may search the internet and read pages when your feeds don't have the answer"
                                  : "The web is turned off for Ask in Settings › Ask Aqua";
        MicButton.Visibility = s.Voice == "off" ? Visibility.Collapsed : Visibility.Visible;
        _syncing = false;
        UpdateModeHint();
    }

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (sender == ResearchToggle && ResearchToggle.IsChecked == true) WebToggle.IsChecked = true;
        if (sender == WebToggle && WebToggle.IsChecked != true) ResearchToggle.IsChecked = false;
        Session.Web = WebToggle.IsChecked == true;
        Session.Research = ResearchToggle.IsChecked == true;
        Session.Think = ThinkToggle.IsChecked == true;
        Session.Computer = ComputerToggle.IsChecked == true;
        UpdateModeHint();
    }

    private void UpdateModeHint()
    {
        if (Session.Voice.IsListening) return;
        if (_untidied is not null) HideTidyUndo();
        var s = Hub.S.Ask;
        ModeHint.Text = Session.Research ? $"Reads up to {s.ResearchPages} pages and writes a cited report"
            : Session.Computer ? "Files in " + string.Join(", ", s.Folders.Take(3).Select(FolderLabel)) + (s.Folders.Count > 3 ? "…" : "") + (s.ConfirmActions ? " · actions ask first" : "")
            : Session.Think ? "Slower, more careful answers"
            : "";
    }

    internal static string FolderLabel(string folder) => folder.ToUpperInvariant() switch
    {
        "%DOCUMENTS%" => "Documents", "%DESKTOP%" => "Desktop", "%DOWNLOADS%" => "Downloads", "%PICTURES%" => "Pictures",
        "%MUSIC%" => "Music", "%VIDEOS%" => "Videos", "%USERPROFILE%" => "your user folder",
        _ => Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : folder,
    };

    private void UpdateEmpty()
    {
        var has = Session.Messages.Count > 0;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        Scroller.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NewChatButton.IsEnabled = has;
        CopyChatButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBusyChanged()
    {
        StopButton.Visibility = Session.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        SendButton.IsEnabled = !Session.IsBusy;
    }
}
