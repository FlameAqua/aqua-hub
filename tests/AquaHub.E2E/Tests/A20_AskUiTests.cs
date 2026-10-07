using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace AquaHub.E2E.Tests;

/// <summary>Ask page interactions that do not need a local model or external services.</summary>
public sealed class A20_AskUiTests : E2ETestBase
{
    public A20_AskUiTests(AppFixture fixture, ITestOutputHelper output) : base(fixture, output) { }
    protected override SessionOptions Options => new()
    {
        EditSettings = root => root["ai"] = new JsonObject
        {
            ["enabled"] = false, ["provider"] = "ollama", ["model"] = "auto", ["manageOllama"] = false,
        },
    };

    private const uint MouseWheelFlag = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private AutomationElement History()
    {
        if (Ui.Find(PageRoot("ask"), Ui.Id("ask-history")) is { } open && !Ui.IsOffscreen(open)) return open;
        Ui.Invoke(Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-history-toggle"), "Show chats"));
        return Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-history"), "chat history panel");
    }

    [Fact]
    public void T01_RenameTheCurrentChatAndKeepItsTitle()
    {
        Run(() =>
        {
            GoTo("ask");
            if (Ui.Find(PageRoot("ask"), Ui.Id("Messages")) is { } messages &&
                Ui.FindAll(messages, Ui.Type(ControlType.DataItem), TreeScope.Children).Count > 0)
                Ui.Invoke(Ui.WaitButtonWithText(PageRoot("ask"), "New chat"));

            const string question = "Remember that my test colour is teal";
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "Ask input"), question);
            Ui.Invoke(Ui.WaitButtonWithText(PageRoot("ask"), "Send"));
            Wait.For(() => Ui.FindAll(Ui.WaitFind(PageRoot("ask"), Ui.Id("Messages"), "messages"),
                Ui.Type(ControlType.DataItem), TreeScope.Children).Count >= 2, "the quick local-memory exchange");

            var title = Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-chat-title"), "current chat title");
            App.EnsureForeground(Main);
            Input.ClickElement(Pid, title, right: true);
            var menu = Wait.For(() => AppWindows.FindInPopups(Pid, Ui.Name("Rename this chat…")), "rename chat menu");
            Ui.Invoke(menu);
            var edit = Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-chat-title-edit"), "chat title editor");
            Ui.SetValue(edit, "Aqua UI follow-up");
            Ui.Invoke(Ui.WaitButtonWithText(PageRoot("ask"), "Save"));
            Wait.For(() => Ui.NameOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-chat-title"), "renamed title")) == "Aqua UI follow-up",
                "the current title updates");

            // Leave the dry-run profile unchanged after exercising the name flow.
            Ui.Invoke(Ui.WaitFind(Ui.FindAll(Ui.WaitFind(PageRoot("ask"), Ui.Id("Messages"), "messages"),
                Ui.Type(ControlType.DataItem), TreeScope.Children).Last(), Ui.Id("ask-memory-undo"), "undo the test memory"));

            Ui.Invoke(Ui.WaitButtonWithText(PageRoot("ask"), "New chat"));
            var history = History();
            Ui.Invoke(Ui.WaitFind(history, Ui.Name("Aqua UI follow-up"), "renamed chat in history"));
            Wait.For(() => Ui.NameOf(Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-chat-title"), "reopened title")) == "Aqua UI follow-up",
                "the renamed title survives reopening the chat");
        });
    }

    [Fact]
    public void T02_ContextAndScreenshotPopupsToggleWithPointerClicks()
    {
        Run(() =>
        {
            GoTo("ask");
            var context = Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-context"), "context meter");
            Input.ClickElement(Pid, context);
            Wait.For(() => AppWindows.FindInPopups(Pid, Ui.Name("Context window")) is not null, "context popup opens");
            Input.ClickElement(Pid, context);
            Wait.For(() => AppWindows.FindInPopups(Pid, Ui.Name("Context window")) is null, "second context click closes it");

            var screenshot = Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-screenshot"), "screenshot menu button");
            Input.ClickElement(Pid, screenshot);
            Wait.For(() => AppWindows.FindInPopups(Pid, Ui.Name("Capture the screen Aqua is on")) is not null, "screenshot menu opens");
            Input.ClickElement(Pid, screenshot);
            Wait.For(() => AppWindows.FindInPopups(Pid, Ui.Name("Capture the screen Aqua is on")) is null, "second screenshot click closes it");
            Input.ClickElement(Pid, screenshot);
            Wait.For(() => AppWindows.FindInPopups(Pid, Ui.Name("Capture the screen Aqua is on")) is not null, "screenshot menu can reopen");
            Input.Press(Pid, VK.Escape);
            Wait.For(() => AppWindows.FindInPopups(Pid, Ui.Name("Capture the screen Aqua is on")) is null, "Escape dismisses screenshot menu");
        });
    }

    [Fact]
    public void T03_MarkdownQuestionStaysRawForCopyAndEditAndWheelScrollsOverIt()
    {
        Run(() =>
        {
            GoTo("ask");
            var messages = Ui.WaitFind(PageRoot("ask"), Ui.Id("Messages"), "messages");
            if (Ui.FindAll(messages, Ui.Type(ControlType.DataItem), TreeScope.Children).Count > 0)
                Ui.Invoke(Ui.WaitButtonWithText(PageRoot("ask"), "New chat"));

            var raw = "Please format this check: *italic phrase* and **bold phrase**.\n\n" +
                      string.Join("\n", Enumerable.Range(1, 45).Select(i => $"Scroll fixture line {i:00}."));
            Ui.SetValue(Ui.WaitFind(PageRoot("ask"), Ui.Id("Input"), "Ask input"), raw);
            Ui.Invoke(Ui.WaitButtonWithText(PageRoot("ask"), "Send"));
            Wait.For(() => Ui.FindAll(messages, Ui.Type(ControlType.DataItem), TreeScope.Children).Count >= 2, "quick memory reply");
            Wait.For(() => Ui.Find(PageRoot("ask"), Ui.Id("StopButton")) is not { } stop || Ui.IsOffscreen(stop),
                "offline answer finishes without inference");

            var userItem = Ui.FindAll(messages, Ui.Type(ControlType.DataItem), TreeScope.Children)
                .First(item => Ui.Find(item, Ui.Id("ask-question")) is not null);
            var user = Ui.WaitFind(userItem, Ui.Id("ask-question"), "rendered user question");
            TextPattern? textPattern = null;
            if (user.TryGetCurrentPattern(TextPattern.Pattern, out var ownText)) textPattern = (TextPattern)ownText;
            if (textPattern is null && Ui.Find(user, Ui.Type(ControlType.Document)) is { } document &&
                document.TryGetCurrentPattern(TextPattern.Pattern, out var documentText)) textPattern = (TextPattern)documentText;
            var rendered = textPattern?.DocumentRange.GetText(-1) ?? Ui.DocumentTexts(userItem).FirstOrDefault();
            Expect(rendered?.Contains("italic phrase", StringComparison.Ordinal) == true &&
                   rendered.Contains("bold phrase", StringComparison.Ordinal), "the prompt's Markdown text is missing from its rendered document");
            Expect(rendered?.Contains("*italic phrase*", StringComparison.Ordinal) != true &&
                   rendered?.Contains("**bold phrase**", StringComparison.Ordinal) != true, "Markdown delimiters are shown as literal prompt text");

            if (textPattern is not null)
            {
                var italic = textPattern.DocumentRange.FindText("italic phrase", false, true);
                Expect(italic is not null, "the italic phrase is not selectable in the rendered document");
                if (italic is not null)
                    Expect(italic.GetAttributeValue(TextPattern.IsItalicAttribute) is bool isItalic && isItalic,
                        "the prompt's emphasis is not exposed as italic text");
            }
            else Expect(false, "the rendered prompt does not expose its text through UI Automation");

            ExpectJournal("clipboard", () => Ui.Invoke(Ui.WaitFind(userItem, Ui.Id("ask-question-copy"), "copy raw question")),
                detail => detail.Contains("*italic phrase*", StringComparison.Ordinal) && detail.Contains("**bold phrase**", StringComparison.Ordinal));
            var itemsBeforeEdit = Ui.FindAll(messages, Ui.Type(ControlType.DataItem), TreeScope.Children).Count;
            Ui.Invoke(Ui.WaitFind(userItem, Ui.Id("ask-question-edit"), "edit raw question"));
            var edit = Ui.WaitFind(PageRoot("ask"), Ui.Id("ask-edit-box"), "question editor");
            Expect(Ui.ValueOf(edit) == raw, "editing changed or stripped the raw Markdown source");
            Ui.Invoke(Ui.WaitButtonWithText(userItem, "Cancel"));
            Expect(Ui.FindAll(messages, Ui.Type(ControlType.DataItem), TreeScope.Children).Count == itemsBeforeEdit,
                "canceling the question edit changed the transcript");

            var answer = Ui.FindAll(messages, Ui.Type(ControlType.DataItem), TreeScope.Children).Last();
            var answerText = Ui.DocumentTexts(answer).FirstOrDefault() ?? "";
            var guidanceButton = Ui.FindAll(answer, Ui.Type(ControlType.Button)).FirstOrDefault(button =>
            {
                try { return string.Equals(button.Current.HelpText, "Regenerate with extra guidance", StringComparison.Ordinal); }
                catch (ElementNotAvailableException) { return false; }
            });
            Expect(guidanceButton is not null, "the answer has no regenerate-with-guidance button");
            if (guidanceButton is not null)
            {
                Ui.Invoke(guidanceButton);
                var guidance = Ui.WaitFind(PageRoot("ask"), Ui.Name("Extra guidance for regenerating the answer"), "guided retry editor");
                Ui.SetValue(guidance, "Use three short bullets.");
                Ui.Invoke(Ui.WaitButtonWithText(answer, "Cancel"));
                Wait.For(() => Ui.Find(PageRoot("ask"), Ui.Name("Extra guidance for regenerating the answer")) is null,
                    "guided retry editor closes");
                Expect(Ui.DocumentTexts(answer).FirstOrDefault() == answerText, "canceling guided regeneration changed the answer");
                Expect(Ui.FindAll(messages, Ui.Type(ControlType.DataItem), TreeScope.Children).Count == itemsBeforeEdit,
                    "canceling guided regeneration changed the transcript");
            }

            // The long prompt is a rendered MarkdownView with a real Run in its FlowDocument. Wheel over
            // its visible area and verify the containing chat ScrollViewer moves.
            var scroller = Ui.WaitFind(PageRoot("ask"), Ui.Id("Scroller"), "chat scroller");
            Expect(Ui.Supports(scroller, ScrollPattern.Pattern), "the chat scroller has no UIA ScrollPattern");
            if (scroller.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollPattern))
            {
                var scroll = (ScrollPattern)scrollPattern;
                Ui.BringIntoView(user);
                var before = scroll.Current.VerticalScrollPercent;
                var delta = before > 50 ? 120 : -120;
                WheelOver(Pid, user, scroller, delta);
                Wait.For(() => delta > 0
                        ? scroll.Current.VerticalScrollPercent < before - 0.5
                        : scroll.Current.VerticalScrollPercent > before + 0.5,
                    "wheel over prompt scrolls the chat");
            }
        });
    }

    private static void WheelOver(int pid, AutomationElement target, AutomationElement viewport, int delta)
    {
        Input.Guard(pid);
        Ui.BringIntoView(target);
        var targetBounds = target.Current.BoundingRectangle;
        var viewBounds = viewport.Current.BoundingRectangle;
        var left = Math.Max(targetBounds.Left, viewBounds.Left);
        var top = Math.Max(targetBounds.Top, viewBounds.Top);
        var right = Math.Min(targetBounds.Right, viewBounds.Right);
        var bottom = Math.Min(targetBounds.Bottom, viewBounds.Bottom);
        if (right <= left || bottom <= top) throw new InvalidOperationException("The question has no visible area inside the chat scroller.");
        var point = new NativePoint { X = (int)((left + right) / 2), Y = (int)((top + bottom) / 2) };
        var hwnd = WindowFromPoint(point);
        _ = GetWindowThreadProcessId(hwnd, out var ownerPid);
        if (ownerPid != (uint)pid) throw new InputRefusedException("Refusing to wheel over a window outside the E2E app.");
        if (!GetCursorPos(out var old)) throw new InvalidOperationException("Couldn't read the cursor position.");
        try
        {
            if (!SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Couldn't position the cursor over the question.");
            mouse_event(MouseWheelFlag, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
            Thread.Sleep(120);
        }
        finally { SetCursorPos(old.X, old.Y); }
    }
}
