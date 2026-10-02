using System.Text.Json;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Data;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;

namespace AquaHub.Tests;

/// <summary>Phase 3.4: the pictures Ask looked at show in its answer and are kept with the chat.</summary>
public class ChatPictureTests : IDisposable
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void APictureIsKeptOnceUnderANameMadeFromItsContent()
    {
        var folder = Path.Combine(_dir.Path, "c1");
        var name = ChatMedia.Save(folder, Png, thumbnail: false);
        Assert.Matches(@"^s-[0-9a-f]{16}\.png$", name);
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(folder, name!)));
        Assert.Equal(name, ChatMedia.Save(folder, Png, thumbnail: false)); // the same picture again: the same file
        Assert.Matches(@"^t-[0-9a-f]{16}\.png$", ChatMedia.Save(folder, Png, thumbnail: true));
        Assert.Equal(2, Directory.GetFiles(folder).Length);                 // and nothing half-written left behind
        Assert.Null(ChatMedia.Save(folder, Array.Empty<byte>(), thumbnail: false));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8' }, ".gif")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, ".bmp")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, ".webp")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ".png")]
    [InlineData(new byte[] { 1, 2, 3 }, ".png")]
    public void TheFileIsNamedForItsKind(byte[] bytes, string extension) => Assert.Equal(extension, ChatMedia.Extension(bytes));

    [Fact]
    public void OnlyANameAquaMadeLeadsToAFile()
    {
        var folder = Path.Combine(_dir.Path, "c1");
        Assert.Equal(Path.Combine(folder, "s-0123456789abcdef.png"), ChatMedia.PathOf(folder, "s-0123456789abcdef.png"));
        Assert.Null(ChatMedia.PathOf(folder, @"..\..\secrets.png"));
        Assert.Null(ChatMedia.PathOf(folder, "../s-0123456789abcdef.png"));
        Assert.Null(ChatMedia.PathOf(folder, @"C:\Windows\s-0123456789abcdef.png"));
        Assert.Null(ChatMedia.PathOf(folder, "s-0123456789abcdef.exe"));
        Assert.Null(ChatMedia.PathOf(folder, "S-0123456789ABCDEF.png"));
        Assert.Null(ChatMedia.PathOf(folder, "s-0123456789abcdef.png\n"));
        Assert.Null(ChatMedia.PathOf(folder, null));
        Assert.Null(ChatMedia.PathOf(null, "s-0123456789abcdef.png"));
    }

    [Fact]
    public void APictureThatCantBeWrittenIsSimplyNotKept()
    {
        var notAFolder = Path.Combine(_dir.Path, "a file");
        File.WriteAllText(notAFolder, "x");
        Assert.Null(ChatMedia.Save(notAFolder, Png, thumbnail: false));
    }

    private static AskRun Run(NullHost host, LocalFiles? files = null) => new()
    {
        State = new HubState(null), Settings = new HubSettings(), Book = new SourceBook(), Options = new AskOptions { Computer = true }, Host = host,
        AllowedForChat = new HashSet<string>(), Files = files, Platform = new PicturePlatform(), Vision = true,
    };

    [Fact]
    public void AnAnswerShowsEachPictureOnceAndOnlySoMany()
    {
        var host = new NullHost();
        var run = Run(host);
        run.Saw(new AskPicture { Name = "logo.png", Path = @"C:\Users\me\Pictures\logo.png", Image = Png });
        run.Saw(new AskPicture { Name = "logo.png", Path = @"c:\users\me\pictures\LOGO.png", Image = Png });            // the same file
        run.Saw(new AskPicture { Name = "Screenshot of the screen", Kind = "screen", Image = new byte[] { 1, 2, 3 } });
        run.Saw(new AskPicture { Name = "Screenshot of the screen", Kind = "screen", Image = new byte[] { 1, 2, 3 } }); // the same capture
        run.Saw(new AskPicture { Name = "Screenshot of the screen", Kind = "screen", Image = new byte[] { 4, 5, 6 } }); // a new one
        Assert.Equal(new[] { "logo.png", "Screenshot of the screen", "Screenshot of the screen" }, host.Pictures.Select(p => p.Name));

        for (var i = 0; i < 20; i++) run.Saw(new AskPicture { Name = $"photo {i}.jpg", Path = $@"C:\Photos\photo {i}.jpg" });
        Assert.Equal(AskRun.MaxPictures, host.Pictures.Count);
    }

    [Fact]
    public async Task APictureAquaReadsShowsInTheAnswer()
    {
        var pictures = Directory.CreateDirectory(Path.Combine(_dir.Path, "Pictures")).FullName;
        var path = Path.Combine(pictures, "boiler label.png");
        File.WriteAllBytes(path, Png);
        File.WriteAllText(Path.Combine(pictures, "notes.txt"), "Boiler serviced in March");
        var host = new NullHost();
        var run = Run(host, new LocalFiles(() => new[] { pictures }));

        var result = await new ReadFileTool().RunAsync(JsonSerializer.SerializeToElement(new { path }), run, default);
        Assert.True(result.Ok);
        var seen = Assert.Single(host.Pictures);
        Assert.Equal(("boiler label.png", "file", path), (seen.Name, seen.Kind, seen.Path));
        Assert.Equal(Png, seen.Image);
        // A document isn't a picture.
        Assert.True((await new ReadFileTool().RunAsync(JsonSerializer.SerializeToElement(new { path = Path.Combine(pictures, "notes.txt") }), run, default)).Ok);
        Assert.Single(host.Pictures);
    }

    [Fact]
    public async Task TheAnswerShowsThePicturesAquaLookedAtTheMatchFirst()
    {
        var pictures = Directory.CreateDirectory(Path.Combine(_dir.Path, "Pictures")).FullName;
        File.WriteAllBytes(Path.Combine(pictures, "logo draft.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2 });
        File.WriteAllBytes(Path.Combine(pictures, "logo final.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 3, 4 });
        using var server = new FakeOllama
        {
            Json = req => req["messages"]!.AsArray().Any(m => m!["content"]!.GetValue<string>().Contains("Plan how Aqua"))
                ? """{"intent":"files","web_queries":[],"file_terms":["logo"],"file_kind":"image","looks":"a blue flame","format":"direct"}"""
                : """{"images":[{"n":1,"shows":"a red square","match":false},{"n":2,"shows":"a blue flame logo","match":true}]}""",
            Script = (_, _) => new[] { FakeOllama.Chunk("It's one of your logos [1].", done: true) },
        };
        var s = new HubSettings();
        s.Ai.Endpoint = server.Endpoint;
        s.Ask.ReadStoryArticles = false;
        s.Ask.Folders = new() { _dir.Path };
        var llm = new LlmClient(() => s.Ai, new InMemorySecretStore());
        var agent = new AskAgent(new HubState(null), null, llm, () => s, new WebSearch(new HttpFetcher(null), () => s, new InMemorySecretStore()), new WebReader());
        var host = new NullHost();

        await agent.RunAsync("Find my logo of a blue flame", Array.Empty<LlmMessage>(), Array.Empty<AskAttachment>(), new AskOptions { Computer = true },
            host, new PicturePlatform(), new HashSet<string>(), CancellationToken.None);

        Assert.Equal(2, host.Pictures.Count);
        Assert.Equal((true, "a blue flame logo"), (host.Pictures[0].Match, host.Pictures[0].Note));
        Assert.Equal((false, "a red square"), (host.Pictures[1].Match, host.Pictures[1].Note));
        Assert.Equal(new[] { "logo draft.png", "logo final.png" }, host.Pictures.Select(p => p.Name).Order());
        Assert.All(host.Pictures, p => Assert.Equal(Path.Combine(pictures, p.Name), p.Path));
    }

    [Fact]
    public void PicturesAreSavedWithTheirAnswersAndNamedInAnExport()
    {
        var db = new HubDatabase(Path.Combine(_dir.Path, "hub.db"));
        var media = Path.Combine(_dir.Path, "chat-media");
        var summary = new ChatSummary { Id = "c1", Title = "Logos", Created = DateTimeOffset.Now, Updated = DateTimeOffset.Now };
        var pictures = new List<SavedPicture>
        {
            new("logo final.png", "file") { Path = @"C:\Users\me\Pictures\logo final.png", File = "t-0123456789abcdef.jpg", Match = true, Note = "a blue flame logo" },
            new("Screenshot of the screen", "screen") { File = "s-fedcba9876543210.png" },
        };
        new ChatStore(db, media).Save(summary, new[]
        {
            new SavedChatMessage(true, "Find my logo", null, null, true),
            new SavedChatMessage(false, "Here it is.", null, null, true) { Pictures = pictures },
        });

        var loaded = new ChatStore(db, media).Load("c1");
        Assert.Null(loaded[0].Pictures);
        Assert.Equal(pictures, loaded[1].Pictures);
        var markdown = ChatExport.ToMarkdown("Logos", loaded, DateTimeOffset.Now);
        Assert.Contains(@"_Pictures Aqua looked at: C:\Users\me\Pictures\logo final.png, Screenshot of the screen_", markdown);
    }

    private sealed class PicturePlatform : IAskPlatform
    {
        public IEnumerable<AskTool> ComputerTools() => Array.Empty<AskTool>();
        public Task<(string Text, int Pages, int Total)> ReadPdfAsync(byte[] pdf, int maxPages, CancellationToken ct) => Task.FromResult(("", 0, 0));
        public Task<string> ReadImageTextAsync(byte[] image, CancellationToken ct) => Task.FromResult("");
        public byte[] PrepareImage(byte[] image, int maxEdge = 1600) => image;
        public string? KnownFolder(string token) => null;
        public Task<IReadOnlyList<IndexedFile>> SearchIndexAsync(IReadOnlyList<string> terms, IReadOnlyList<string> scopes, bool content, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IndexedFile>>(Array.Empty<IndexedFile>());
    }
}
