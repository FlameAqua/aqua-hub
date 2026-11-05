using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Data;

namespace AquaHub.E2E.Tests;

/// <summary>Real WPF paging/rendering over synthetic persisted history. No model or web access.</summary>
[Trait("Execution", "Offline")]
[Trait("Component", "Ask")]
public sealed class A23_AskExperienceTests(AppFixture fixture, ITestOutputHelper output) : E2ETestBase(fixture, output)
{
    private const string ChatId = "phase4-paging-fixture";
    private const string Tail = "UNTRUNCATED_HISTORY_TAIL";
    private static string LongAnswer => "Fixture answer 300\n\n" + new string('x', 40000) + "\n\n" + Tail;
    protected override SessionOptions Options => new()
    {
        Offline = true,
        SeedProfile = profile =>
        {
            var path = Path.Combine(profile, "hub.db");
            try
            {
                var store = new ChatStore(new HubDatabase(path));
                var now = DateTimeOffset.Now;
                var messages = Enumerable.Range(1, 300).Select(i => new SavedChatMessage(i % 2 == 1,
                    i == 300 ? LongAnswer : $"Fixture {(i % 2 == 1 ? "question" : "answer")} {i:000}", null, null, true)).ToList();
                store.Save(new ChatSummary { Id = ChatId, Title = "Phase 4 paging fixture", Created = now, Updated = now, Named = true }, messages);
            }
            finally { HubDatabase.ReleaseFile(path); }
        },
    };
    private AutomationElement Field(string id) => Ui.WaitFind(PageRoot("ask"), Ui.Id(id), id);
    private void Range(string value) => Wait.For(() => Ui.NameOf(Field("ask-message-range")) == value, value);
    private int RenderedMessages => Ui.FindAll(Field("Messages"), Ui.Or(Ui.Id("ask-question"), Ui.Id("ask-answer"))).Count;

    [Fact]
    [Trait("Category", "Smoke")]
    public void T01_PagesBoundControlsAndKeepFullHistoryAndExport() => Run(() =>
    {
        GoTo("ask");
        Range("241–300 of 300 messages");
        Wait.For(() => RenderedMessages == 60, "exactly 60 message Markdown controls");
        Assert.False(Field("ask-messages-later").Current.IsEnabled);
        Wait.For(() => Ui.DocumentTexts(Field("Messages")).Any(t => t.Contains("Long answer preview limited.")), "long Markdown preview explanation");
        Assert.DoesNotContain(Tail, string.Join(" ", Ui.DocumentTexts(Field("Messages"))));
        Ui.Invoke(Field("ask-messages-earlier")); Range("181–240 of 300 messages");
        Assert.Equal(60, RenderedMessages);
        Ui.Invoke(Field("ask-messages-later")); Range("241–300 of 300 messages");
        for (var i = 0; i < 4; i++) Ui.Invoke(Field("ask-messages-earlier"));
        Range("1–60 of 300 messages");
        Assert.False(Field("ask-messages-earlier").Current.IsEnabled);
        Assert.Equal(60, RenderedMessages);
        Ui.Invoke(Field("ask-messages-latest")); Range("241–300 of 300 messages");
        ExpectJournal("clipboard", () => Ui.Invoke(Field("ask-copy-chat"))); // OS clipboard remains untouched.
        var path = Path.Combine(App.ProfileDir, "hub.db");
        try
        {
            var saved = new ChatStore(new HubDatabase(path)).Load(ChatId);
            Assert.Equal(300, saved.Count);
            Assert.Equal(LongAnswer, saved[^1].Text);
            var markdown = ChatExport.ToMarkdown("fixture", saved, DateTimeOffset.Now);
            Assert.Contains("Fixture question 001", markdown);
            Assert.Contains(Tail, markdown);
        }
        finally { HubDatabase.ReleaseFile(path); }
    });

    [Fact]
    public void T02_SendingFromEarlierPageReturnsToLatest() => Run(() =>
    {
        GoTo("ask");
        Ui.Invoke(Field("ask-messages-earlier"));
        const string question = "Offline paging follow-up";
        Ui.SetValue(Field("Input"), question);
        Ui.Invoke(Field("SendButton"));
        Range("243–302 of 302 messages");
        Wait.For(() => RenderedMessages == 60, "new answer remains within the 60-control page");
        Wait.For(() => Ui.DocumentTexts(Field("Messages")).Any(t => t.Contains(question)), "new question visible on Latest");
        Assert.False(Field("ask-messages-latest").Current.IsEnabled);
        Wait.For(() => Field("SendButton").Current.IsEnabled, "offline answer finished");
    });
}
