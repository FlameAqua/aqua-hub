namespace AquaHub.E2E.Tests;

/// <summary>
/// Ask Aqua (local model): every suggestion chip streams an answer, citations open sources, typed question + Enter,
/// Stop while streaming, New chat. Answers can take up to AQUAHUB_E2E_AI_TIMEOUT (150 s) each.
/// </summary>
public sealed class A09_AskTests : E2ETestBase
{
    public A09_AskTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    private static readonly string[] Suggestions =
    {
        "What's happening near me today?", "Summarise the markets and my watchlist", "What's on my agenda this week?",
        "What are people talking about locally?", "Explain the biggest story in simple terms", "What do prediction markets expect next?",
    };

    private AutomationElement Ask() => GoTo("ask");
    private AutomationElement? Messages() => Ui.Find(PageRoot("ask"), Ui.Id("Messages"));
    private List<AutomationElement> Items() => Messages() is { } m ? Ui.FindAll(m, Ui.Type(ControlType.DataItem), TreeScope.Children) : new();
    private bool Busy() => Ui.Find(PageRoot("ask"), Ui.Id("StopButton")) is not null;

    private void NewChat()
    {
        if (Items().Count == 0) return;
        var button = Ui.WaitButtonWithText(PageRoot("ask"), "New chat");
        Ui.Invoke(button);
        Wait.For(() => Items().Count == 0, "chat cleared");
    }

    /// <summary>Waits for the question bubble and the finished answer; returns (answer, footer).</summary>
    private (string Answer, string Footer, AutomationElement Item) WaitForAnswer(string question)
    {
        Wait.For(() => Items().Any(i => Ui.AllTexts(i).Contains(question)), $"question bubble '{question}'");
        Wait.For(() => !Busy(), "answer finished (Stop button gone)", E2EConfig.AiTimeout, 500);
        var last = Items().Last();
        var texts = Ui.AllTexts(last);
        var footer = texts.LastOrDefault(t => t.EndsWith("on-device", StringComparison.Ordinal) || t == "Stopped.") ?? "";
        // The answer is the message's rich text (a Document). Plan lines, captions and buttons around it are not, so
        // they can't stand in for an answer that is missing or unreadable.
        var answer = Ui.DocumentTexts(last).OrderByDescending(t => t.Length).FirstOrDefault() ?? "";
        Step($"   answer ({answer.Length} chars): {(answer.Length > 140 ? answer[..140] + "…" : answer)}");
        Step($"   footer: {footer}");
        return (answer, footer, last);
    }

    private static void ExpectRealAnswer(string answer)
    {
        Expect(answer.Trim().Length > 20, $"assistant text is empty or too short: '{answer}'");
        Expect(!answer.StartsWith("Sorry — something went wrong", StringComparison.Ordinal) && !answer.StartsWith("I can't reach the local model", StringComparison.Ordinal),
            "model error: " + answer);
    }

    [Fact]
    public void T01_EverySuggestionChipStreamsAnAnswer() => Run(() =>
    {
        Ask();
        foreach (var s in Suggestions)
        {
            Check("Ask", $"suggestion '{s}' → answer", () =>
            {
                NewChat();
                Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Button(s), "suggestion chip"));
                Wait.For(Busy, "streaming started (Stop visible)", TimeSpan.FromSeconds(10), 100);
                var (answer, footer, item) = WaitForAnswer(s);
                ExpectRealAnswer(answer);
                Expect(footer.EndsWith("on-device", StringComparison.Ordinal), $"footer missing: '{footer}'");
                var cites = Ui.FindAll(item, Ui.Id("ask-citation")).Count;
                Step($"   citation chips: {cites}");
            });
        }
    });

    [Fact]
    public void T02_CitationChipsAndInlineLinksOpenSources() => Run(() =>
    {
        Ask();
        AutomationElement? chip = null;
        Check("Ask", "an answer with citation chips", () =>
        {
            chip = Items().SelectMany(i => Ui.FindAll(i, Ui.Id("ask-citation"))).FirstOrDefault();
            if (chip is null)
            {
                NewChat();
                const string q = "What are the three biggest news stories today? Cite your sources with [n].";
                Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "input"), q);
                Ui.Invoke(Button(PageRoot("ask"), "Send"));
                var (answer, _, item) = WaitForAnswer(q);
                ExpectRealAnswer(answer);
                chip = Ui.FindAll(item, Ui.Id("ask-citation")).FirstOrDefault();
            }
            Expect(chip is not null, "the model's answer cited no sources, so no citation chips were shown");
        });
        if (chip is not null)
        {
            Check("Ask", $"citation chip '{Ui.NameOf(chip)}' → journal open-url", () =>
                ExpectJournal("open-url", () => Ui.Invoke(chip), d => d.StartsWith("http", StringComparison.Ordinal)));
            Check("Ask", "inline [n] link → journal open-url", () =>
            {
                var link = Items().Select(i => Ui.Find(i, Ui.Type(ControlType.Hyperlink))).FirstOrDefault(l => l is not null);
                Expect(link is not null, "no inline [n] hyperlinks in the answers");
                ExpectJournal("open-url", () => Ui.Invoke(link!), d => d.StartsWith("http", StringComparison.Ordinal));
            });
        }
    });

    [Fact]
    public void T03_TypedQuestionWithEnter() => Run(() =>
    {
        Ask();
        Check("Ask", "typed question + Enter → answer", () =>
        {
            NewChat();
            const string q = "In one sentence, what is the weather like in Dublin today?";
            var input = Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "input");
            Ui.SetValue(input, q);
            FocusAndPress(Main, input, VK.Enter);
            var (answer, _, _) = WaitForAnswer(q);
            ExpectRealAnswer(answer);
            Expect(Ui.ValueOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "input")) == "", "input not cleared after sending");
        });
    });

    [Fact]
    public void T04_StopWhileStreaming() => Run(() =>
    {
        Ask();
        Check("Ask", "Stop during streaming → 'Stopped.' and Send re-enabled", () =>
        {
            NewChat();
            const string q = "Write a long, detailed overview of every news story you know about today, one paragraph each.";
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "input"), q);
            Ui.Invoke(Button(PageRoot("ask"), "Send"));
            Wait.For(Busy, "streaming started", TimeSpan.FromSeconds(10), 100);
            // Let a few tokens arrive (or at least the request start) before stopping.
            Wait.Until(() => Items().Count >= 2 && Ui.AllTexts(Items().Last()).Any(t => t.Length > 30), TimeSpan.FromSeconds(60), 300);
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("StopButton"), "Stop button"));
            Wait.For(() => !Busy(), "streaming stopped", TimeSpan.FromSeconds(15));
            Wait.For(() => Ui.Texts(Items().Last()).Contains("Stopped."), "'Stopped.' footer");
            Expect(Ui.IsEnabled(Button(PageRoot("ask"), "Send")), "Send button still disabled");
        });
    });

    [Fact]
    public void T05_NewChatClearsTheConversation() => Run(() =>
    {
        Ask();
        Check("Ask", "New chat clears messages and shows suggestions", () =>
        {
            if (Items().Count == 0)
            {
                Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Button(Suggestions[0]), "suggestion"));
                Wait.For(() => Items().Count > 0, "a message");
                Wait.Until(() => !Busy(), E2EConfig.AiTimeout, 500);
            }
            Ui.Invoke(Ui.WaitButtonWithText(PageRoot("ask"), "New chat"));
            Wait.For(() => Items().Count == 0, "no messages");
            Ui.WaitFind(PageRoot("ask"), Ui.Button(Suggestions[0]), "suggestion chips visible again");
        });
    });

    /// <summary>The history panel (opened with its toggle when the window is narrow).</summary>
    private AutomationElement History()
    {
        if (Ui.Find(PageRoot("ask"), Ui.Id("ask-history")) is { } open && !Ui.IsOffscreen(open)) return open;
        Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-history-toggle"), "Show chats"));
        return Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-history"), "chat history panel");
    }

    private List<AutomationElement> ChatRows() => Ui.FindAll(History(), Ui.Id("ask-chat"));

    /// <summary>"Remember that …" is answered from Aqua's memory without the model, so these checks are quick.</summary>
    private void SendQuick(string text)
    {
        Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "input"), text);
        Ui.Invoke(Button(PageRoot("ask"), "Send"));
        WaitForAnswer(text);
    }

    [Fact]
    public void T06_ChatsAreKeptStarredAndDeleted() => Run(() =>
    {
        Ask();
        const string q = "Remember that my favourite colour is teal";
        Check("Ask", "a sent question starts a chat in the history, titled from the question", () =>
        {
            NewChat();
            SendQuick(q);
            Wait.For(() => ChatRows().Any(c => Ui.NameOf(c).StartsWith("Remember that my favourite colour", StringComparison.Ordinal)), "the chat in the history list");
        });
        Check("Ask", "star → the chat is listed under Starred", () =>
        {
            var star = Ui.FindAll(History(), Ui.Id("ask-chat-star")).First();
            Ui.Invoke(star);
            Wait.For(() => Ui.Texts(History()).Contains("STARRED") || Ui.Texts(History()).Contains("Starred"), "a Starred group");
        });
        Check("Ask", "New chat → empty; clicking the chat in the history reopens it", () =>
        {
            Ui.Invoke(Ui.WaitFind(History(), Ui.Id("ask-history-new"), "new chat in the history panel"));
            Wait.For(() => Items().Count == 0, "an empty chat");
            Ui.Invoke(ChatRows().First(c => Ui.NameOf(c).StartsWith("Remember that my favourite colour", StringComparison.Ordinal)));
            Wait.For(() => Items().Any(i => Ui.AllTexts(i).Contains(q)), "the chat's messages back");
        });
        Check("Ask", "delete → the chat leaves the history", () =>
        {
            var before = ChatRows().Count;
            Ui.Invoke(Ui.FindAll(History(), Ui.Id("ask-chat-delete")).First());
            Wait.For(() => ChatRows().Count == before - 1, "one chat fewer");
        });
        Check("Ask", "search box filters the history", () =>
        {
            NewChat();
            SendQuick("Remember that my bike is a Brompton");
            var search = Ui.WaitFind(History(), Ui.Id("ask-history-search"), "search chats");
            Ui.SetValue(search, "Brompton");
            Wait.For(() => ChatRows().Count >= 1 && ChatRows().All(c => Ui.NameOf(c).Contains("Brompton", StringComparison.OrdinalIgnoreCase)), "only matching chats");
            Ui.SetValue(search, "");
        });
    });

    [Fact]
    public void T07_YourQuestionCanBeCopiedAndEdited() => Run(() =>
    {
        Ask();
        Check("Ask", "Copy on a question → journal clipboard with its text", () =>
        {
            NewChat();
            SendQuick("Remember that my tea is Barry's");
            var question = Items().First();
            ExpectJournal("clipboard", () => Ui.Invoke(Ui.WaitFind(question, Ui.Id("ask-question-copy"), "Copy on the question")), d => d.Contains("Barry"));
        });
        Check("Ask", "the question text is selectable (read-only text box with its words)", () =>
        {
            var box = Ui.WaitFind(Items().First(), Ui.Id("ask-question"), "question text");
            Expect(Ui.ValueOf(box).Contains("Barry's", StringComparison.Ordinal), "question box doesn't hold the question");
        });
        Check("Ask", "Edit → edit box; Save and ask → the question is replaced and answered again", () =>
        {
            Ui.Invoke(Ui.WaitFind(Items().First(), Ui.Id("ask-question-edit"), "Edit on the question"));
            var box = Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-edit-box"), "edit box");
            Ui.SetValue(box, "Remember that my tea is Lyons");
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-edit-save"), "Save and ask"));
            WaitForAnswer("Remember that my tea is Lyons");
            Expect(!Items().Any(i => Ui.AllTexts(i).Any(t => t.Contains("Barry's", StringComparison.Ordinal))), "the old question is still there");
            Expect(Items().Count == 2, $"expected 2 messages after editing, found {Items().Count}");
        });
    });

    [Fact]
    public void T08_ContextMeterVoiceAndReadAloud() => Run(() =>
    {
        Ask();
        Check("Ask", "context meter opens its settings (window size, earlier messages)", () =>
        {
            Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-context"), "context meter"));
            Wait.For(() => AppWindows.FindInPopups(Pid, Ui.Name("Context window")), "the context popup with its window-size setting");
            App.EnsureForeground(App.Main);
            Input.Press(Pid, VK.Escape);
            DismissTransients();
        });
        Check("Ask", "microphone → journal voice-input (simulated in tests)", () =>
            ExpectJournal("voice-input", () => Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-mic"), "microphone"))));
        Check("Ask", "Read aloud on an answer → journal speak", () =>
        {
            if (Items().Count == 0) SendQuick("Remember that my dog is called Bran");
            var read = Ui.FindAll(Items().Last(), Ui.Id("ask-read-aloud")).FirstOrDefault();
            Expect(read is not null, "no Read aloud button on the answer");
            ExpectJournal("speak", () => Ui.Invoke(read!));
        });
    });

    [Fact]
    public void T09_ARememberedFactCanBeUndone() => Run(() =>
    {
        Ask();
        Check("Ask", "Remember that … → saved with Undo and Answer it instead; Undo takes it back", () =>
        {
            NewChat();
            SendQuick("Remember that my bike is a blue Brompton");
            var answer = Items().Last();
            Expect(Ui.AllTexts(answer).Any(t => t.Contains("I'll remember", StringComparison.Ordinal)), "no confirmation that it was remembered");
            Ui.WaitFind(answer, Ui.Id("ask-memory-answer"), "Answer it instead");
            Ui.Invoke(Ui.WaitFind(answer, Ui.Id("ask-memory-undo"), "Undo"));
            Wait.For(() => Ui.AllTexts(Items().Last()).Any(t => t.Contains("won't remember", StringComparison.Ordinal)), "the answer saying it won't remember");
        });
    });

    [Fact]
    public void T10_TheModelPickerNamesTheModelThatAnswers() => Run(() =>
    {
        Ask();
        Check("Ask", "the composer's model picker shows a model, and the answer's footer names the same one", () =>
        {
            var picker = Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-model"), "model picker (shown once the model server lists its models)", E2EConfig.AiTimeout);
            var model = Ui.SelectedComboItem(picker);
            Expect(model.Length > 0, "the model picker has nothing selected");
            NewChat();
            const string q = "In five words, what is a haiku?";
            var input = Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "input");
            Ui.SetValue(input, q);
            FocusAndPress(Main, input, VK.Enter);
            var (_, footer, _) = WaitForAnswer(q);
            Expect(footer.StartsWith(model + " ·", StringComparison.Ordinal), $"picked {model}, but the answer says '{footer}'");
        });
    });
}
