using AquaHub.Core.Speech;
using AquaHub.Core.Settings;

namespace AquaHub.Tests;

public class ReadAloudTests
{
    private sealed class Audio : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static ReadAloudRequest Request(object owner) => new(owner, "One short sentence.", "en-IE", "windows", "");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Guard(Task signal) => signal.WaitAsync(TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrReplacementPreventsLateAudio(bool replace)
    {
        var entered = Signal();
        var release = Signal();
        var played = Signal();
        var original = new object();
        var successor = new object();
        var late = new Audio();
        var current = new Audio();
        var playerOrder = new List<IDisposable>();
        var reader = new ReadAloud(async (request, _, _) =>
        {
            if (request.Owner == successor) return current;
            entered.SetResult();
            await release.Task; // The native synthesizer completes after cancellation.
            return late;
        }, (audio, _) => { playerOrder.Add(audio); played.TrySetResult(); return Task.CompletedTask; });
        reader.Toggle(Request(original));
        await Guard(entered.Task);
        Assert.Equal(ReadAloudStage.Preparing, reader.State.Stage);
        if (replace) reader.Toggle(Request(successor));
        else reader.Stop();
        release.SetResult();
        if (replace) await Guard(played.Task);
        await Guard(reader.ShutdownAsync());
        Assert.True(late.Disposed);
        Assert.Equal(replace ? new[] { current } : [], playerOrder);
        Assert.False(reader.State.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynthesisAndPlaybackFailuresCanBothRecover(bool playback)
    {
        var failed = Signal();
        var recovered = Signal();
        var original = new object();
        var successor = new object();
        var first = new Audio();
        var second = new Audio();
        var reader = new ReadAloud((request, _, _) =>
        {
            if (request.Owner == successor) return Task.FromResult<IDisposable>(second);
            return playback ? Task.FromResult<IDisposable>(first) : Task.FromException<IDisposable>(new IOException("private input"));
        }, (audio, _) => audio == first ? Task.FromException(new IOException("private input")) : Task.CompletedTask);
        reader.Changed += () =>
        {
            if (reader.State.Stage == ReadAloudStage.Failed) failed.TrySetResult();
            if (reader.State is { Stage: ReadAloudStage.Playing, Owner: { } owner } && owner == successor) recovered.TrySetResult();
        };
        reader.Toggle(Request(original));
        await Guard(failed.Task);
        Assert.DoesNotContain("private input", reader.State.Message);
        Assert.Equal(playback, first.Disposed);
        reader.Toggle(Request(successor));
        await Guard(recovered.Task);
        await Guard(reader.ShutdownAsync());
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task ShutdownJoinsPlaybackAndAnInFlightFollowingChunk()
    {
        var started = Signal();
        var nextStarted = Signal();
        var release = Signal();
        var count = 0;
        var reader = new ReadAloud(async (_, _, _) =>
        {
            if (++count > 1) { nextStarted.SetResult(); await release.Task; }
            return new Audio();
        }, async (_, token) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        reader.Toggle(Request(new()) with { Text = string.Concat(Enumerable.Repeat("A sentence with several words. ", 20)) });
        await Guard(started.Task);
        await Guard(nextStarted.Task);
        var closing = reader.ShutdownAsync();
        Assert.False(closing.IsCompleted);
        release.SetResult();
        await Guard(closing);
        Assert.Equal(2, count);
    }

    [Fact]
    public void SpokenPreparationOmitsCodeAndCitationsAndBoundsLongText()
    {
        var input = "## Heading\n```cs\nprivate code [4]\n```\n| Price | Feature |\n|---|---|\n| €5 | Export |\n" +
                    "The result is $x^2$ [1].\n" + string.Concat(Enumerable.Repeat("More words and details. ", 2000));
        var (chunks, truncated) = ReadAloud.Prepare(input, markdown: true);
        var spoken = string.Join(' ', chunks);
        Assert.True(truncated);
        Assert.DoesNotContain("private code", spoken);
        Assert.DoesNotContain("[1]", spoken);
        Assert.Contains("Code block omitted.", spoken);
        Assert.Contains("Price, Feature", spoken);
        Assert.Contains("x squared", spoken);
        Assert.InRange(chunks.Count, 2, 100);
        Assert.All(chunks, s => Assert.InRange(s.Length, 1, ReadAloud.MaxChunk));
        Assert.True(chunks.Sum(s => s.Length) <= ReadAloud.MaxSpoken);
    }

    [Fact]
    public void WindowsRemainsDefaultForAnExistingProfileAndKokoroChoiceCanPersist()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{"ask":{"voice":"whisper","whisperModel":"small"}}""");
        var store = new SettingsStore(path);
        Assert.Equal("windows", store.Current.Ask.ReadAloudBackend);
        store.Update(s =>
        {
            s.Ask.ReadAloudBackend = "kokoro";
            s.Ask.ReadAloudVoice = "am_michael";
        });
        var reopened = new SettingsStore(path);
        Assert.Equal("kokoro", reopened.Current.Ask.ReadAloudBackend);
        Assert.Equal("am_michael", reopened.Current.Ask.ReadAloudVoice);
        Assert.Equal("whisper", reopened.Current.Ask.Voice);
    }

    [Fact]
    public void SpeechCleanupRemovesWorkAndKeepsAdjacentFiles()
    {
        using var dir = new TempDir();
        using var client = new HttpClient();
        var setup = new KokoroSetup(dir.Path, client);
        var normal = setup.CreateWorkFolder();
        var abandoned = setup.CreateWorkFolder();
        var unrelated = Directory.CreateDirectory(Path.Combine(dir.Path, Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(normal, "text.txt"), "Fixture phrase.");
        File.WriteAllText(Path.Combine(abandoned, "text.txt"), "Another fixture.");
        setup.ReleaseWorkFolder(unrelated);
        setup.ReleaseWorkFolder(normal);
        Assert.False(Directory.Exists(normal));
        Assert.True(Directory.Exists(abandoned));
        setup.ClearAbandonedSpeech();
        Assert.False(Directory.Exists(abandoned));
        Assert.True(Directory.Exists(unrelated));
    }
}
