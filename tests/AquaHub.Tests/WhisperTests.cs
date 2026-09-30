using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AquaHub.Core.Speech;
using Xunit;

namespace AquaHub.Tests;

/// <summary>A download server for the installer: each address serves fixed bytes, a status, or a stream.</summary>
internal sealed class FakeDownloads : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();
    public List<string> Requested { get; } = new();

    public void Serve(string url, byte[] body) => _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    public void Serve(string url, HttpStatusCode status) => _routes[url] = () => new HttpResponseMessage(status);
    public void Serve(string url, Func<Stream> stream) => _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream()) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);
        var response = _routes.TryGetValue(url, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>Gives some bytes, then nothing until cancelled (a stalled or slow download).</summary>
internal sealed class StallingStream(byte[] first) : Stream
{
    private int _given;
    public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _given; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_given < first.Length)
        {
            var n = Math.Min(buffer.Length, first.Length - _given);
            first.AsMemory(_given, n).CopyTo(buffer);
            _given += n;
            return n;
        }
        Reading.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }
}

public class WhisperSetupTests
{
    private const string RuntimeUrl = "https://downloads.example/whisper-bin-x64.zip";
    private const string ModelUrl = "https://models.example/ggml-small.en-q5_1.bin";
    private const string OtherModelUrl = "https://models.example/ggml-base.en-q5_1.bin";

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    /// <summary>An archive like whisper.cpp's: the program and libraries in a folder, plus things Aqua doesn't keep.</summary>
    private static byte[] Archive(bool withCli = true)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            if (withCli) Add("Release/whisper-cli.exe", "MZ fake program");
            Add("Release/whisper.dll", "library");
            Add("Release/ggml-cpu.dll", "library");
            Add("Release/whisper-server.exe", "another program");
            Add("Release/extras/evil.dll", "in a subfolder");
            Add("README.md", "readme");
        }
        return ms.ToArray();
    }

    private static (WhisperSetup Setup, FakeDownloads Server, WhisperDownload Runtime, WhisperModelFile Model, byte[] ModelBytes) Arrange(TempDir dir, bool withCli = true)
    {
        var server = new FakeDownloads();
        var zip = Archive(withCli);
        var modelBytes = RandomNumberGenerator.GetBytes(300_000);
        server.Serve(RuntimeUrl, zip);
        server.Serve(ModelUrl, modelBytes);
        var setup = new WhisperSetup(Path.Combine(dir.Path, "whisper"), new HttpClient(server), Architecture.X64);
        return (setup, server, new WhisperDownload(RuntimeUrl, zip.Length, Sha(zip)),
                new WhisperModelFile("ggml-small.en-q5_1.bin", true, new WhisperDownload(ModelUrl, modelBytes.Length, Sha(modelBytes))), modelBytes);
    }

    [Fact]
    public async Task InstallsTheProgramItsLibrariesAndTheModel()
    {
        using var dir = new TempDir();
        var (setup, _, runtime, model, modelBytes) = Arrange(dir);
        var progress = new List<WhisperProgress>();
        setup.Progress += progress.Add;

        var install = await setup.InstallAsync(runtime, model, "small", CancellationToken.None);

        Assert.Equal("Whisper Small (English)", install.Name);
        Assert.True(install.EnglishOnly);
        Assert.Equal(modelBytes, File.ReadAllBytes(install.Model));
        var kept = Directory.GetFiles(Path.GetDirectoryName(install.Cli)!).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "ggml-cpu.dll", "whisper-cli.exe", "whisper.dll" }, kept);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(install.Cli)!));
        Assert.Empty(Directory.GetFiles(setup.Folder, "*.part", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(setup.Folder, "*.zip"));
        // Progress only goes up, and ends at everything.
        Assert.True(progress.Zip(progress.Skip(1)).All(p => p.Second.Done >= p.First.Done));
        Assert.Equal(runtime.Size + model.Download.Size, progress[^1].Done);
        Assert.Equal(install, setup.Installed());
        Assert.False(setup.Busy);
    }

    [Fact]
    public async Task AFileThatDoesntMatchItsFingerprintIsThrownAway()
    {
        using var dir = new TempDir();
        var (setup, _, runtime, model, _) = Arrange(dir);
        var forged = model with { Download = model.Download with { Sha256 = new string('0', 64) } };

        var ex = await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, forged, "small", CancellationToken.None));

        Assert.Contains("fingerprint", ex.Message);
        Assert.Null(setup.Installed());
        Assert.False(File.Exists(Path.Combine(setup.Folder, "models", "ggml-small.en-q5_1.bin")));
        Assert.Empty(Directory.GetFiles(setup.Folder, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ADownloadBiggerOrSmallerThanExpectedIsThrownAway()
    {
        using var dir = new TempDir();
        var (setup, _, runtime, model, _) = Arrange(dir);
        var smaller = model with { Download = model.Download with { Size = model.Download.Size - 1000 } };
        var bigger = model with { Download = model.Download with { Size = model.Download.Size + 1000 } };

        Assert.Contains("bigger", (await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, smaller, "small", CancellationToken.None))).Message);
        Assert.Contains("ended early", (await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, bigger, "small", CancellationToken.None))).Message);
        Assert.Null(setup.Installed());
    }

    [Fact]
    public async Task AnArchiveWithoutTheProgramIsRefused()
    {
        using var dir = new TempDir();
        var (setup, _, runtime, model, _) = Arrange(dir, withCli: false);
        var ex = await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, model, "small", CancellationToken.None));
        Assert.Contains("whisper-cli.exe", ex.Message);
        Assert.Null(setup.Installed());
    }

    [Fact]
    public async Task ServerErrorsAndPlainHttpAreExplained()
    {
        using var dir = new TempDir();
        var (setup, server, runtime, model, _) = Arrange(dir);
        server.Serve(ModelUrl, HttpStatusCode.ServiceUnavailable);
        Assert.Contains("503", (await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, model, "small", CancellationToken.None))).Message);

        var plain = model with { Download = model.Download with { Url = "http://models.example/ggml-small.en-q5_1.bin" } };
        Assert.Contains("HTTPS", (await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, plain, "small", CancellationToken.None))).Message);
    }

    [Fact]
    public async Task NotEnoughRoomIsSaidBeforeDownloading()
    {
        using var dir = new TempDir();
        var (setup, server, runtime, model, _) = Arrange(dir);
        setup.FreeSpace = () => 10_000;
        var ex = await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, model, "small", CancellationToken.None));
        Assert.Contains("free space", ex.Message);
        Assert.Empty(server.Requested);
    }

    [Fact]
    public async Task AStalledDownloadGivesUp()
    {
        using var dir = new TempDir();
        var (setup, server, runtime, model, _) = Arrange(dir);
        server.Serve(ModelUrl, () => new StallingStream(new byte[1000]));
        setup.StallAfter = TimeSpan.FromMilliseconds(300);
        var ex = await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, model, "small", CancellationToken.None));
        Assert.Contains("stalled", ex.Message);
        Assert.Empty(Directory.GetFiles(setup.Folder, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellingStopsAndCleansUp()
    {
        using var dir = new TempDir();
        var (setup, server, runtime, model, _) = Arrange(dir);
        var stalling = new StallingStream(new byte[1000]);
        server.Serve(ModelUrl, () => stalling);

        var install = setup.InstallAsync(runtime, model, "small", CancellationToken.None);
        await stalling.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(setup.Busy);
        Assert.NotNull(setup.Current);
        setup.Cancel();

        Assert.Equal("Cancelled.", (await Assert.ThrowsAsync<WhisperSetupException>(() => install)).Message);
        Assert.False(setup.Busy);
        Assert.Null(setup.Current);
        Assert.Null(setup.Installed());
        Assert.Empty(Directory.GetFiles(setup.Folder, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SwitchingModelsKeepsTheProgramAndDropsTheOldModel()
    {
        using var dir = new TempDir();
        var (setup, server, runtime, model, _) = Arrange(dir);
        await setup.InstallAsync(runtime, model, "small", CancellationToken.None);
        var otherBytes = RandomNumberGenerator.GetBytes(100_000);
        server.Serve(OtherModelUrl, otherBytes);
        server.Requested.Clear();

        var install = await setup.InstallAsync(runtime, new WhisperModelFile("ggml-base.en-q5_1.bin", true, new WhisperDownload(OtherModelUrl, otherBytes.Length, Sha(otherBytes))), "base", CancellationToken.None);

        Assert.Equal(new[] { OtherModelUrl }, server.Requested);
        Assert.Equal("Whisper Base (English)", install.Name);
        Assert.Equal(new[] { "ggml-base.en-q5_1.bin" }, Directory.GetFiles(Path.Combine(setup.Folder, "models")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RemovingDeletesEverything()
    {
        using var dir = new TempDir();
        var (setup, _, runtime, model, _) = Arrange(dir);
        await setup.InstallAsync(runtime, model, "small", CancellationToken.None);
        var changed = 0;
        setup.Changed += () => changed++;

        setup.Remove();

        Assert.False(Directory.Exists(setup.Folder));
        Assert.Null(setup.Installed());
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData(@"..\..\elsewhere\whisper-cli.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"whisper.cpp-1.9.4-x64\whisper-server.exe")]
    public async Task AnInstallRecordPointingElsewhereIsIgnored(string cli)
    {
        using var dir = new TempDir();
        var (setup, _, runtime, model, _) = Arrange(dir);
        await setup.InstallAsync(runtime, model, "small", CancellationToken.None);
        var record = Path.Combine(setup.Folder, "whisper.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(record))!;
        json["Cli"] = cli;
        File.WriteAllText(record, json.ToJsonString());
        Assert.Null(setup.Installed());
    }

    [Fact]
    public async Task OneInstallAtATime()
    {
        using var dir = new TempDir();
        var (setup, server, runtime, model, _) = Arrange(dir);
        var stalling = new StallingStream(new byte[10]);
        server.Serve(ModelUrl, () => stalling);
        var first = setup.InstallAsync(runtime, model, "small", CancellationToken.None);
        await stalling.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("already", (await Assert.ThrowsAsync<WhisperSetupException>(() => setup.InstallAsync(runtime, model, "small", CancellationToken.None))).Message);
        Assert.Throws<WhisperSetupException>(setup.Remove);
        setup.Cancel();
        await Assert.ThrowsAsync<WhisperSetupException>(() => first);
    }
}

public class WhisperCatalogTests
{
    [Fact]
    public void EveryDownloadIsPinnedToOneReleaseOverHttps()
    {
        var files = new[] { WhisperCatalog.Runtime(Architecture.X64)!, WhisperCatalog.Runtime(Architecture.Arm64)! }
            .Concat(WhisperCatalog.Choices.SelectMany(c => new[] { c.English.Download, c.Multilingual.Download }));
        foreach (var f in files)
        {
            Assert.StartsWith("https://", f.Url);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            Assert.True(f.Size > 1_000_000);
            // A fixed build or commit, never "latest" or a branch.
            Assert.True(f.Url.Contains("/releases/download/b5130/") || f.Url.Contains("/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/"), f.Url);
        }
        Assert.Null(WhisperCatalog.Runtime(Architecture.X86));
    }

    [Fact]
    public void EnglishSpeakersGetTheEnglishModel()
    {
        var small = WhisperCatalog.Choice("small");
        Assert.Equal("ggml-small.en-q5_1.bin", small.For("en-IE").FileName);
        Assert.Equal("ggml-small.en-q5_1.bin", small.For("").FileName);
        Assert.Equal("ggml-small-q5_1.bin", small.For("fr-FR").FileName);
        Assert.False(WhisperCatalog.Choice("turbo").For("en-GB").EnglishOnly);
        Assert.Equal("small", WhisperCatalog.Choice("nonsense").Id);
        Assert.Equal("small", WhisperCatalog.Choice(null).Id);
        Assert.False(WhisperCatalog.IsEnglish("eng"));
        Assert.StartsWith("Small — recommended, 181 MB", small.Label("en-IE"));
    }

    [Fact]
    public void TheSettingIsKeptToAKnownModel()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, """{ "ask": { "voice": "whisper", "whisperModel": "../../evil" } }""");
        var store = new AquaHub.Core.Settings.SettingsStore(path);
        Assert.Equal("whisper", store.Current.Ask.Voice);
        Assert.Equal("small", store.Current.Ask.WhisperModel);
        Assert.True(store.Current.Ask.WhisperTip);
    }
}

public class SpeechAudioTests
{
    private const int Rate = SpeechAudio.SampleRate;

    /// <summary>16-bit PCM: stretches of a tone at some amplitude (0 = room noise only).</summary>
    private static byte[] Pcm(params (double Seconds, int Amplitude)[] parts)
    {
        var random = new Random(7);
        var samples = new List<short>();
        foreach (var (seconds, amplitude) in parts)
            for (var i = 0; i < (int)(seconds * Rate); i++)
                samples.Add((short)(random.Next(-30, 31) + amplitude * Math.Sin(2 * Math.PI * 220 * i / Rate)));
        var bytes = new byte[samples.Count * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static int Peak(byte[] pcm) => Enumerable.Range(0, pcm.Length / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(pcm, i * 2)));

    [Fact]
    public void AWavHeaderWhisperCanRead()
    {
        var wav = SpeechAudio.Wav(new byte[3200]);
        Assert.Equal(44 + 3200, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + 3200, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));        // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));        // mono
        Assert.Equal(Rate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(3200, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void RoomNoiseAndClicksAreNotSpeech()
    {
        Assert.Null(SpeechAudio.SpeechPart(Pcm((4, 0)), out _));
        Assert.Null(SpeechAudio.SpeechPart(Pcm((1, 0), (0.06, 8000), (1, 0)), out _));
        Assert.Null(SpeechAudio.SpeechPart(new byte[100], out _));
    }

    [Fact]
    public void SpeechIsCutFromTheQuietAroundItAndTurnedUp()
    {
        var clip = SpeechAudio.SpeechPart(Pcm((2, 0), (1, 3000), (2, 0)), out var speech);
        Assert.NotNull(clip);
        Assert.InRange(speech.TotalSeconds, 0.9, 1.1);
        Assert.InRange(clip!.Length / (Rate * 2.0), 1.9, 2.3);
        Assert.InRange(Peak(clip), 20_000, 24_000);
    }

    [Fact]
    public void LoudSpeechIsLeftAsItIs()
    {
        var clip = SpeechAudio.SpeechPart(Pcm((0.5, 0), (1, 28_000), (0.5, 0)), out _);
        Assert.InRange(Peak(clip!), 27_900, 28_100);
    }
}

public class WhisperCliTests
{
    [Fact]
    public void TheRecordingGoesInThroughAPipeAndTheWordsComeOut()
    {
        var args = WhisperCli.Arguments(@"C:\m\model.bin", "en-IE", englishOnly: false, threads: 4);
        var joined = string.Join(" ", args);
        Assert.Contains("-f -", joined);
        Assert.Contains("-of ", joined);
        Assert.Contains("-l en", joined);
        Assert.Contains("-nt", args);
        Assert.Contains("-np", args);
        // No output files.
        Assert.DoesNotContain(args, a => a is "-otxt" or "-oj" or "-ojf" or "-osrt" or "-ovtt" or "-ocsv" or "-olrc");
        Assert.Equal(@"C:\m\model.bin", args[args.ToList().IndexOf("-m") + 1]);
    }

    [Theory]
    [InlineData("en-IE", false, "en")]
    [InlineData("fr-FR", false, "fr")]
    [InlineData("pt-BR", false, "pt")]
    [InlineData("de-DE", true, "en")]
    [InlineData("", false, "auto")]
    public void Languages(string language, bool englishOnly, string expected) => Assert.Equal(expected, WhisperCli.Language(language, englishOnly));

    [Theory]
    [InlineData(16, 8)]
    [InlineData(32, 8)]
    [InlineData(8, 4)]
    [InlineData(2, 1)]
    [InlineData(1, 1)]
    public void Threads(int processors, int expected) => Assert.Equal(expected, WhisperCli.Threads(processors));

    [Theory]
    [InlineData("\n Testing 1, 2, 3, number 3.", "Testing 1, 2, 3, number 3.")]
    [InlineData(" [BLANK_AUDIO]", "")]
    [InlineData(" Hello there. [Music] How are you?", "Hello there. How are you?")]
    [InlineData(" (upbeat music) What's the weather like?", "What's the weather like?")]
    [InlineData(" *laughs* That's funny.", "That's funny.")]
    [InlineData(" ♪ ♪", "")]
    [InlineData(" Subtitles by the Amara.org community", "")]
    [InlineData(" Turn on subtitles by default.", "Turn on subtitles by default.")]
    [InlineData(" Remind me (if you can) to call Mum.", "Remind me (if you can) to call Mum.")]
    public void WhatIsntSpeechGoes(string output, string expected) => Assert.Equal(expected, WhisperCli.Clean(output, TimeSpan.FromSeconds(3)));

    [Fact]
    public void WordsMadeUpFromALittleNoiseGoButRealThanksStay()
    {
        Assert.Equal("", WhisperCli.Clean(" Thank you.", TimeSpan.FromSeconds(0.5)));
        Assert.Equal("", WhisperCli.Clean(" you", TimeSpan.FromSeconds(0.3)));
        Assert.Equal("Thank you.", WhisperCli.Clean(" Thank you.", TimeSpan.FromSeconds(2)));
        Assert.Equal("Thank you for the help.", WhisperCli.Clean(" Thank you for the help.", TimeSpan.FromSeconds(0.9)));
    }

    [Fact]
    public void FailuresAreExplained()
    {
        Assert.Contains("reinstall", WhisperCli.Explain(unchecked((int)0xC0000135), ""));
        Assert.Contains("model", WhisperCli.Explain(2, "whisper_init_from_file_with_params_no_state: failed to load model\n"));
        Assert.Equal("Whisper stopped (code 7): something odd", WhisperCli.Explain(7, "line one\nsomething odd\n"));
    }
}

/// <summary>A live Whisper test: AQUAHUB_LIVE=1 and AQUAHUB_WHISPER set to an installed whisper folder.</summary>
public sealed class WhisperFactAttribute : FactAttribute
{
    public WhisperFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AQUAHUB_LIVE") != "1" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AQUAHUB_WHISPER")))
            Skip = "Whisper test: set AQUAHUB_LIVE=1 and AQUAHUB_WHISPER to a profile's whisper folder (Settings › Ask Aqua › Voice › Install)";
    }
}

public class LiveWhisperTests
{
    /// <summary>Windows' own voice (SAPI) saying something, as 16 kHz 16-bit mono PCM — no microphone involved.</summary>
    private static byte[] Say(string text)
    {
        static object Get(object o, string name) => o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null)!;
        var voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("No Windows speech synthesizer"))!;
        var stream = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpMemoryStream")!)!;
        var format = Get(stream, "Format");
        format.GetType().InvokeMember("Type", BindingFlags.SetProperty, null, format, new object[] { 18 });   // SAFT16kHz16BitMono
        voice.GetType().InvokeMember("AudioOutputStream", BindingFlags.PutRefDispProperty, null, voice, new[] { stream });
        voice.GetType().InvokeMember("Speak", BindingFlags.InvokeMethod, null, voice, new object[] { text, 0 });
        return (byte[])stream.GetType().InvokeMember("GetData", BindingFlags.InvokeMethod, null, stream, null)!;
    }

    private static async Task<string?> HearAsync(byte[] pcm)
    {
        var install = new WhisperSetup(Environment.GetEnvironmentVariable("AQUAHUB_WHISPER")!, new HttpClient()).Installed()
                      ?? throw new InvalidOperationException("No complete Whisper install in AQUAHUB_WHISPER");
        // Half a second of quiet either side, as when someone clicks the microphone and then speaks.
        var padded = new byte[pcm.Length + SpeechAudio.SampleRate * 2];
        Buffer.BlockCopy(pcm, 0, padded, SpeechAudio.SampleRate, pcm.Length);
        if (SpeechAudio.SpeechPart(padded, out var speech) is not { } clip) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var text = await WhisperRunner.TranscribeAsync(install, clip, speech, "en-IE", cts.Token);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "aquahub-live-whisper.md"), $"- {install.Name}: {text}\n");
        return text;
    }

    [WhisperFact]
    public async Task HearsTheOwnersTestPhrase()
    {
        var text = await HearAsync(Say("Testing, one, two, three. Number three."));
        Assert.NotNull(text);
        Assert.Contains("test", text!, StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Contains("three", StringComparison.OrdinalIgnoreCase) || text.Contains('3'), text);
        Assert.DoesNotContain("tree", text, StringComparison.OrdinalIgnoreCase);
    }

    [WhisperFact]
    public async Task HearsAQuestion()
    {
        var text = await HearAsync(Say("What's the weather going to be like in Dublin tomorrow?"));
        Assert.Contains("weather", text!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dublin", text);
        Assert.Contains("tomorrow", text, StringComparison.OrdinalIgnoreCase);
    }

    [WhisperFact]
    public async Task SilenceIsNotSentToWhisper() => Assert.Null(await HearAsync(new byte[SpeechAudio.SampleRate * 4]));
}
