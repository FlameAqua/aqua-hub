using System.Text.Json;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Data;

namespace AquaHub.Tests;

/// <summary>
/// What a chat keeps, in the store the app uses (a real database): a deleted chat is gone for good, attached folders
/// aren't saved, pictures go with what the messages said about them, a long chat keeps its summary, and a chat that read
/// private data stays marked. The Phase 3 review's M1 to M4 and L6; the audit's TM3 and TM4.
/// </summary>
public class ChatPrivacyTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly HubDatabase _db;
    private readonly string _media;

    public ChatPrivacyTests()
    {
        _db = new HubDatabase(Path.Combine(_dir.Path, "hub.db"));
        _media = Path.Combine(_dir.Path, "chat-media");
    }

    public void Dispose() => _dir.Dispose();

    private static ChatSummary Summary(string id) => new() { Id = id, Title = "Heat pumps", Created = DateTimeOffset.Now, Updated = DateTimeOffset.Now };
    private static SavedChatMessage Q(string text) => new(true, text, null, null, true);
    private static SavedChatMessage A(string text) => new(false, text, null, "qwen3.5:9b · 2.0s · on-device", true);
    private string Raw(string key) => JsonSerializer.Serialize(_db.GetJson<JsonElement>(key));

    [Fact]
    public void DeletingAChatRemovesItsNotesAndCompressionFromTheDatabase()
    {
        var store = new ChatStore(_db, _media);
        store.Save(Summary("c1"), new[] { Q("Which heat pump?"), A("The Daikin.") });
        store.Save(Summary("c2"), new[] { Q("And the grant?"), A("€6,500.") });
        store.SaveNotes("c1", new[] { new ChatNote(ChatNotes.Fact, "Budget €9,000") });
        store.SaveCompression("c1", new ChatCompression { Summary = "- Earlier", Covers = 2, Fingerprint = "f" });
        store.SaveNotes("c2", new[] { new ChatNote(ChatNotes.Fact, "Grant €6,500") });

        store.Delete("c1");
        Assert.Null(_db.GetJson<List<SavedChatMessage>>("ask:chat:c1"));
        Assert.Null(_db.GetJson<List<ChatNote>>("ask:chat-notes:c1"));
        Assert.Null(_db.GetJson<ChatCompression>("ask:chat-compressed:c1"));
        Assert.NotNull(_db.GetJson<List<ChatNote>>("ask:chat-notes:c2"));

        store.DeleteAll();
        Assert.Null(_db.GetJson<List<ChatNote>>("ask:chat-notes:c2"));
        Assert.Empty(new ChatStore(_db, _media).List());
    }

    [Fact]
    public void UndoingACompressionSurvivesARestartAndTheNextSave()
    {
        var store = new ChatStore(_db, _media);
        store.Save(Summary("c1"), new[] { Q("Which heat pump?"), A("The Daikin.") });
        store.Update("c1", c => c.NoAutoCompress = true);

        var reloaded = new ChatStore(_db, _media);
        var again = reloaded.Get("c1")!;
        Assert.True(again.NoAutoCompress);
        reloaded.Save(again, new[] { Q("Which heat pump?"), A("The Daikin."), Q("Is it quiet?"), A("Yes.") });
        Assert.True(new ChatStore(_db, _media).List().Single().NoAutoCompress);
    }

    [Fact]
    public void AChatDeletedWhileItsAnswerStreamsDoesntComeBack()
    {
        var store = new ChatStore(_db, _media);
        var summary = Summary("c1");
        store.Save(summary, new[] { Q("Which heat pump?"), A("…") });
        store.Delete("c1");

        // The answer stops, and the chat would be saved again with its notes, compression and pictures.
        store.Save(summary, new[] { Q("Which heat pump?"), A("The Daikin.") });
        store.SaveNotes("c1", new[] { new ChatNote(ChatNotes.Fact, "Budget €9,000") });
        store.SaveCompression("c1", new ChatCompression { Summary = "- Earlier", Covers = 2, Fingerprint = "f" });
        Assert.Null(store.MediaFolder("c1"));
        Assert.Empty(store.List());
        Assert.Empty(store.Load("c1"));
        Assert.Empty(store.LoadNotes("c1"));
        Assert.Null(store.LoadCompression("c1"));

        // Other chats are saved as ever.
        store.Save(Summary("c2"), new[] { Q("A new question") });
        Assert.Equal("c2", store.List().Single().Id);
        Assert.NotNull(store.MediaFolder("c2"));
    }

    [Fact]
    public void AttachedFoldersArentSavedWithAMessage()
    {
        var store = new ChatStore(_db, _media);
        var options = new AskOptions { Computer = true, Folders = new[] { @"C:\Users\me\Projects\private-plans" } };
        store.Save(Summary("c1"), new[] { Q("What's in here?") with { Options = options } });
        Assert.DoesNotContain("private-plans", Raw("ask:chat:c1"));
        var loaded = new ChatStore(_db, _media).Load("c1").Single().Options!;
        Assert.True(loaded.Computer);
        Assert.Empty(loaded.Folders);
    }

    [Fact]
    public void ChatsSavedBeforeLoseTheFolderPathsTheyKept()
    {
        // As 1.2 saved it: the folder's path inside each message's options.
        _db.PutJson("ask:chats", new[] { Summary("c1"), Summary("c2") });
        _db.PutJson("ask:chat:c1", JsonDocument.Parse("""[{"user":true,"text":"What's in here?","chat":true,"options":{"computer":false,"folders":["C:\\Users\\me\\Taxes 2025"]}}]""").RootElement);
        _db.PutJson("ask:chat:c2", JsonDocument.Parse("""[{"user":true,"text":"Hello","chat":true,"options":{"computer":false,"folders":[]}}]""").RootElement);
        var store = new ChatStore(_db, _media);
        Assert.Equal(1, store.ForgetFolders());
        Assert.DoesNotContain("Taxes", Raw("ask:chat:c1"));
        Assert.Equal("What's in here?", store.Load("c1").Single().Text);
        Assert.Equal(0, new ChatStore(_db, _media).ForgetFolders()); // once
    }

    [Fact]
    public void SwitchingPicturesOffForgetsWhatTheMessagesSaidAboutThem()
    {
        var store = new ChatStore(_db, _media);
        var file = ChatMedia.Save(store.MediaFolder("c1")!, new byte[] { 1, 2, 3 }, thumbnail: true)!;
        var picture = new SavedPicture("holiday.jpg", "file") { Path = @"C:\Users\me\Pictures\holiday.jpg", File = file, Note = "a beach at sunset" };
        store.Save(Summary("c1"), new[] { Q("Find my photo"), A("Here it is.") with { Pictures = new() { picture } } });
        store.Save(Summary("c2"), new[] { Q("Hello"), A("Hi.") });

        Assert.Equal(1, store.ForgetPictures());
        Assert.False(Directory.Exists(_media));
        Assert.DoesNotContain("holiday", Raw("ask:chat:c1"));
        Assert.DoesNotContain("sunset", Raw("ask:chat:c1"));
        Assert.Equal("Here it is.", store.Load("c1")[1].Text);
        Assert.Equal(0, store.ForgetPictures());
    }

    [Fact]
    public void AChatOverTheLimitKeepsItsSummaryForTheMessagesThatRemain()
    {
        var store = new ChatStore(_db, _media);
        var messages = Enumerable.Range(0, 175).SelectMany(i => new[] { Q($"Question {i}"), A($"Answer {i}") }).ToList();
        var turns = messages.Select(ChatCompressor.TurnOf).OfType<ChatTurn>().ToList();
        var summary = Summary("c1");
        store.Save(summary, messages.Take(2).ToList());
        store.SaveCompression("c1", new ChatCompression { Summary = "- The first 340 messages", Covers = 340, Fingerprint = ChatCompressor.Fingerprint(turns, 340) });

        store.Save(summary, messages); // 350: the oldest 50 go
        var kept = store.Load("c1");
        Assert.Equal(ChatStore.MaxMessagesPerChat, kept.Count);
        Assert.Equal("Question 25", kept[0].Text);
        var compression = ChatCompressor.Valid(store.LoadCompression("c1"), kept.Select(ChatCompressor.TurnOf).OfType<ChatTurn>().ToList());
        Assert.NotNull(compression);
        Assert.Equal(290, compression!.Covers);
        Assert.Equal("- The first 340 messages", compression.Summary);
    }

    [Fact]
    public void ASummaryOfFewerMessagesThanGoGoesToo()
    {
        var store = new ChatStore(_db, _media);
        var messages = Enumerable.Range(0, 175).SelectMany(i => new[] { Q($"Question {i}"), A($"Answer {i}") }).ToList();
        var turns = messages.Select(ChatCompressor.TurnOf).OfType<ChatTurn>().ToList();
        store.Save(Summary("c1"), messages.Take(2).ToList());
        store.SaveCompression("c1", new ChatCompression { Summary = "- The first 20 messages", Covers = 20, Fingerprint = ChatCompressor.Fingerprint(turns, 20) });
        store.Save(Summary("c1"), messages);
        Assert.Null(store.LoadCompression("c1"));
    }

    [Fact]
    public void PicturesOnlyTheDroppedMessagesUsedGoWithThem()
    {
        var store = new ChatStore(_db, _media);
        var folder = store.MediaFolder("c1")!;
        var old = ChatMedia.Save(folder, new byte[] { 1, 2, 3 }, thumbnail: false)!;
        var recent = ChatMedia.Save(folder, new byte[] { 4, 5, 6 }, thumbnail: false)!;
        var messages = Enumerable.Range(0, 160).SelectMany(i => new[] { Q($"Question {i}"), A($"Answer {i}") }).ToList();
        messages[1] = messages[1] with { Pictures = new() { new SavedPicture("Screenshot", "screen") { File = old } } };
        messages[^1] = messages[^1] with { Pictures = new() { new SavedPicture("Screenshot", "screen") { File = recent } } };
        store.Save(Summary("c1"), messages);
        Assert.False(File.Exists(Path.Combine(folder, old)));
        Assert.True(File.Exists(Path.Combine(folder, recent)));
    }

    [Fact]
    public void AChatThatReadPrivateDataStaysMarked()
    {
        var store = new ChatStore(_db, _media);
        var summary = Summary("c1");
        summary.ReadPrivate = true;
        store.Save(summary, new[] { Q("Summarise my payslip"), A("…") });
        store.Save(Summary("c1"), new[] { Q("Summarise my payslip"), A("Done.") }); // an older copy, without the mark
        Assert.True(new ChatStore(_db, _media).Get("c1")!.ReadPrivate);
    }

    [Fact]
    public void ChatsSavedBeforeTheMarkShowItInTheirMessages()
    {
        Assert.True(ChatStore.ShowsPrivate(new[] { Q("Summarise this") with { Attachments = new() { "payslip.pdf" } } }));
        Assert.True(ChatStore.ShowsPrivate(new[] { A("From your files") with { Citations = new() { new Citation(1, "notes.txt", "Your files", @"C:\notes.txt", "file") } } }));
        Assert.True(ChatStore.ShowsPrivate(new[] { A("On your screen") with { Pictures = new() { new SavedPicture("Screenshot", "screen") } } }));
        Assert.False(ChatStore.ShowsPrivate(new[] { Q("What's new?"), A("From the web") with { Citations = new() { new Citation(1, "RTÉ", "RTÉ", "https://www.rte.ie", "web") } } }));
    }

    [Fact]
    public void APictureFolderNeedsAPlainChatId()
    {
        var store = new ChatStore(_db, _media);
        Assert.Null(store.MediaFolder(".."));
        Assert.Null(store.MediaFolder("c1\n"));
        Assert.Equal(Path.Combine(_media, "c1"), store.MediaFolder("c1"));
    }

    [Fact]
    public void TheChatAndTheStoreReadMessagesTheSameWay()
    {
        Assert.Equal("Look at this [attached: a.png, b.pdf]", ChatCompressor.TurnOf(true, "Look at this", true, true, null, new[] { "a.png", "b.pdf" }, null)!.Text);
        Assert.Null(ChatCompressor.TurnOf(false, "Half an ans", true, true, "Stopped.", null, null));
        Assert.Null(ChatCompressor.TurnOf(false, "Still writing", true, false, "", null, null));
        Assert.Null(ChatCompressor.TurnOf(true, "Remember that I like tea", false, true, null, null, null));
        Assert.Null(ChatCompressor.TurnOf(new SavedChatMessage(false, "", null, null, true)));
        Assert.Equal("The Daikin.", ChatCompressor.TurnOf(A("The Daikin."))!.Text);
    }

    [Fact]
    public void ASummarysOpeningLineIsntKept()
    {
        const string body = "- Chose the Daikin Altherma 3 heat pump for its quiet outdoor unit.\n- Budget: €9,000; SEAI grant €6,500.";
        Assert.Equal(body, ChatCompressor.Clean("Sure! Here's a summary of the chat:\n" + body, 1000));
        Assert.Equal(body, ChatCompressor.Clean("**Here is a concise summary of our conversation so far:**\n" + body, 1000));
        Assert.Equal(body, ChatCompressor.Clean("Summary:\n" + body, 1000));
    }
}
