using AquaHub.Core;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using Xunit;

namespace AquaHub.Tests;

/// <summary>Voice input: the dictation language, the tidy-up's guard rails, the audio pipe and microphone names.</summary>
public class SpeechLanguageTests
{
    private static readonly string[] Online = { "en-US", "en-GB", "fr-FR", "de-DE" };

    [Fact]
    public void AMissingVarietyUsesTheNearestOne()
    {
        // The owner's error (30 Sep): English (Ireland) isn't a Windows speech language → "The requested language is not supported".
        Assert.Equal("en-GB", SpeechLanguage.Pick("en-IE", Online, null));
        Assert.Equal("de-DE", SpeechLanguage.Pick("de-AT", Online, null));
        Assert.Equal("en-GB", SpeechLanguage.Pick("en_IE", Online, null));
    }

    [Fact]
    public void TheExactLanguageWinsThenWindowsOwnSpeechLanguage()
    {
        Assert.Equal("en-GB", SpeechLanguage.Pick("en-GB", Online, "en-US"));
        Assert.Equal("en-US", SpeechLanguage.Pick("en-IE", Online, "en-US"));   // what Windows is set to speak
        Assert.Equal("EN-GB", SpeechLanguage.Pick("en-gb", new[] { "EN-GB" }, null));
    }

    [Fact]
    public void AnyVarietyOfTheSameLanguageBeforeGivingUp()
    {
        Assert.Equal("en-US", SpeechLanguage.Pick("en-AU", Online, null));
        Assert.Null(SpeechLanguage.Pick("ga-IE", Online, "en-GB"));   // never dictate Irish with an English recognizer
        Assert.Null(SpeechLanguage.Pick("en-GB", Array.Empty<string>(), "en-GB"));
        Assert.Null(SpeechLanguage.Pick(" ", Online, null));
    }
}

public class DictationTidyTests
{
    [Fact]
    public void ACorrectionOfWhatWasMisheardIsKept()
    {
        // The owner's test (30 Sep): "testing one two three number three" came out as this.
        Assert.Equal("Testing one, two, three, number three.", DictationTidy.Accept("The Doc One Two Tree Number tree", "Testing one, two, three, number three."));
        Assert.Equal("What's the weather in Dublin tomorrow?", DictationTidy.Accept("whats the whether in dublin tomorrow", "What's the weather in Dublin tomorrow?"));
        Assert.Equal("Testing 1, 2, 3.", DictationTidy.Accept("testing one two three", "\"Testing 1, 2, 3.\""));
    }

    [Fact]
    public void AnAnswerIsNeverACorrection()
    {
        Assert.Null(DictationTidy.Accept("what is two plus two", "Four."));
        Assert.Null(DictationTidy.Accept("whats the whether in dublin",
            "Sure! The weather in Dublin today is mild and cloudy, with highs of 17 degrees and a light westerly breeze through the afternoon."));
        Assert.Null(DictationTidy.Accept("open my documents folder", "I can't open folders, but here is how you can do it yourself."));
        Assert.Null(DictationTidy.Accept("hello there", ""));
        Assert.Null(DictationTidy.Accept("hello there", null));
    }

    [Fact]
    public void AReplyAboutSomethingElseIsThrownAway()
    {
        Assert.Null(DictationTidy.Accept("remind me to call the garage about the car on monday", "Schedule a dentist appointment for next week with Doctor Byrne."));
    }

    [Fact]
    public void LoneUnsureWordsAreNoiseButUnsureSentencesAreKept()
    {
        Assert.True(DictationTidy.IsNoise("the", 0.2));
        Assert.True(DictationTidy.IsNoise("anything at all", 0.05));
        Assert.False(DictationTidy.IsNoise("the", 0.7));
        Assert.False(DictationTidy.IsNoise("open notepad", 0.15));   // was dropped below 0.25 before — words were lost
    }

    [Fact]
    public void ThePromptCarriesTheOtherGuessesAndTheUnsureParts()
    {
        var heard = new[]
        {
            new HeardPhrase("The Doc", 0.2, new[] { "the dock", "testing" }),
            new HeardPhrase("One Two Tree Number tree", 0.6, Array.Empty<string>()),
        };
        // Each phrase with its guesses side by side, best first (as footnotes, the model kept the first guess).
        var prompt = DictationTidy.Prompt(heard);
        Assert.Contains("1. \"The Doc\"  or  \"the dock\"  or  \"testing\"\n", prompt);
        Assert.Contains("2. \"One Two Tree Number tree\"\n", prompt);
        Assert.Contains("unsure of: \"The Doc\"", prompt);
        Assert.Equal("The Doc One Two Tree Number tree", DictationTidy.Joined(heard));
        // No other guesses at all: just the words.
        var plain = DictationTidy.Prompt(new[] { new HeardPhrase("whats the whether", 0.8, Array.Empty<string>()) });
        Assert.Equal("Heard: \"whats the whether\"\n", plain);
    }

    [Fact]
    public void SimilarityIsOneMinusTheEditDistanceShare()
    {
        Assert.Equal(1 - 3 / 7.0, DictationTidy.Similarity("kitten", "sitting"), 3);
        Assert.Equal(1, DictationTidy.Similarity("same", "same"));
        Assert.Equal(0, DictationTidy.Similarity("", "x"));
    }

    // Review 30 Sep (M1): the instructions quoted the owner's own mishearings, and the model then "fixed" correct speech
    // ("The doc said I should rest" → "Testing! Said I should rest"). Only unsure dictation is tidied now, and the
    // instructions give no example substitutions.
    [Fact]
    public void OnlyUnsureDictationIsTidied()
    {
        Assert.Null(DictationTidy.Skip(new[] { new HeardPhrase("check whether it will rain", 0.55, Array.Empty<string>()) }));
        Assert.Null(DictationTidy.Skip(new[] { new HeardPhrase("sure", 0.9, Array.Empty<string>()), new HeardPhrase("not sure", 0.6, Array.Empty<string>()) }));
        Assert.Equal("the recognizer was sure of every word", DictationTidy.Skip(new[] { new HeardPhrase("check whether it will rain", 0.9, Array.Empty<string>()) }));
        Assert.Equal("nothing was heard", DictationTidy.Skip(Array.Empty<HeardPhrase>()));
        Assert.DoesNotContain("whether", DictationTidy.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the doc", DictationTidy.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tree", DictationTidy.Instructions, StringComparison.OrdinalIgnoreCase);
    }

    // Review 30 Sep (L2): only the first 1,500 characters were sent, and a reply less than half as long was accepted.
    [Fact]
    public void LongDictationsAreLeftWholeAndCutRepliesRefused()
    {
        var sentence = "Please add milk, eggs, bread and coffee to the shopping list for the weekend. ";
        var long1 = new[] { new HeardPhrase(string.Concat(Enumerable.Repeat(sentence, 25)), 0.4, Array.Empty<string>()) };
        Assert.Equal("long dictations are left as heard", DictationTidy.Skip(long1));
        var many = Enumerable.Range(0, 13).Select(i => new HeardPhrase("phrase " + i, 0.4, Array.Empty<string>())).ToArray();
        Assert.Equal("long dictations are left as heard", DictationTidy.Skip(many));
        var heard = string.Concat(Enumerable.Repeat(sentence, 4)).Trim();
        Assert.Null(DictationTidy.Accept(heard, heard[..(heard.Length / 2)]));
        Assert.NotNull(DictationTidy.Accept("um so what's the weather like", "So what's the weather like?"));
    }
}

public class PcmPipeTests
{
    private static byte[] Bytes(int from, int count) => Enumerable.Range(from, count).Select(i => (byte)i).ToArray();

    [Fact]
    public void ReadsComeOutInOrder()
    {
        using var pipe = new PcmPipe(64);
        pipe.Write(Bytes(0, 10), 0, 10);
        var buffer = new byte[4];
        Assert.Equal(4, pipe.Read(buffer, 0, 4));
        Assert.Equal(Bytes(0, 4), buffer);
        Assert.Equal(6, pipe.Available);
        Assert.Equal(4, pipe.Position);
    }

    [Fact]
    public async Task AReadWaitsUntilItCanBeFilled()
    {
        using var pipe = new PcmPipe(64);
        var buffer = new byte[8];
        var read = Task.Run(() => pipe.Read(buffer, 0, 8));
        pipe.Write(Bytes(0, 4), 0, 4);
        await Task.Delay(150);
        Assert.False(read.IsCompleted);   // a short read would end the recognizer's audio
        pipe.Write(Bytes(4, 4), 0, 4);
        Assert.Equal(8, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(Bytes(0, 8), buffer);
    }

    [Fact]
    public async Task ClosingReturnsWhatIsLeftThenNothing()
    {
        using var pipe = new PcmPipe(64);
        pipe.Write(Bytes(0, 3), 0, 3);
        var buffer = new byte[10];
        var read = Task.Run(() => pipe.Read(buffer, 0, 10));
        await Task.Delay(100);
        pipe.Complete();
        Assert.Equal(3, await read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, pipe.Read(buffer, 0, 10));
        pipe.Write(Bytes(0, 3), 0, 3);   // ignored once closed
        Assert.Equal(0, pipe.Available);
    }

    [Fact]
    public void WhenFullTheOldestAudioGoes()
    {
        using var pipe = new PcmPipe(8);
        pipe.Write(Bytes(0, 6), 0, 6);
        pipe.Write(Bytes(6, 6), 0, 6);
        Assert.Equal(8, pipe.Available);
        var buffer = new byte[8];
        Assert.Equal(8, pipe.Read(buffer, 0, 8));
        Assert.Equal(Bytes(4, 8), buffer);
    }

    [Fact]
    public void TheRecognizerCanAskForLengthAndPosition()
    {
        using var pipe = new PcmPipe(8);
        Assert.Equal(-1, pipe.Length);
        Assert.Equal(0, pipe.Seek(0, SeekOrigin.Current));
        pipe.Position = 5;   // ignored, never throws
        Assert.Equal(0, pipe.Position);
    }

    [Fact]
    public void PeakLevelIsOnADecibelScale()
    {
        static byte[] Sample(short value) => new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
        Assert.Equal(0, PcmPipe.PeakLevel(new byte[16], 16));
        Assert.Equal(1, PcmPipe.PeakLevel(Sample(short.MinValue), 2), 3);
        Assert.Equal(2 / 3.0, PcmPipe.PeakLevel(Sample(3277), 2), 2);   // −20 dB
        Assert.Equal(2 / 3.0, PcmPipe.PeakLevel(Sample(-3277), 2), 2);
    }
}

public class MicNameTests
{
    private static readonly string[] Full = { "Headset Microphone (Arctis 7 Chat)", "Microphone (Realtek(R) Audio)", "Microphone (USB Audio Device)", "Microphone (USB Audio Device) 2" };

    [Fact]
    public void CutOffNamesGetTheirFullName()
    {
        Assert.Equal("Headset Microphone (Arctis 7 Chat)", MicNames.Expand("Headset Microphone (Arctis 7 Ch", Full));
        Assert.Equal("Microphone (Realtek(R) Audio)", MicNames.Expand("Microphone (Realtek(R) Audio)", Full));
        Assert.Equal("Microphone (USB Audio Device)", MicNames.Expand("Microphone (USB Audio Device)", Full));   // ambiguous: kept as is
    }

    [Fact]
    public void ASavedNameMatchesItsCutOffForm()
    {
        Assert.True(MicNames.Same("Headset Microphone (Arctis 7 Chat)", "Headset Microphone (Arctis 7 Ch"));
        Assert.True(MicNames.Same("microphone (realtek(r) audio)", "Microphone (Realtek(R) Audio)"));
        Assert.False(MicNames.Same("Mic", "Microphone (Realtek(R) Audio)"));
    }
}

/// <summary>The tidy-up against the local model. Run with AQUAHUB_LIVE=1 and AQUAHUB_ASK_PROFILE.</summary>
public class LiveDictationTests
{
    private static async Task<string?> TidyAsync(string heard, params string[] alternates)
    {
        var source = Environment.GetEnvironmentVariable("AQUAHUB_ASK_PROFILE") ?? throw new InvalidOperationException("Set AQUAHUB_ASK_PROFILE");
        using var dir = new TempDir();
        foreach (var f in new[] { "settings.json", "hub.db" }) File.Copy(Path.Combine(source, f), Path.Combine(dir.Path, f));
        using var core = new HubCore(HubPaths.Resolve(dir.Path), new InMemorySecretStore(), new NullPlatform());
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await DictationTidy.TidyAsync(core.Llm, new[] { new HeardPhrase(heard, 0.4, alternates) }, cts.Token);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "aquahub-live-dictation.md"), $"- heard: {heard} → {result ?? "(kept as heard)"}\n");
        return result;
    }

    // The owner's test ("testing one two three number three", heard as below) is too garbled for the tidy-up to rebuild
    // without guessing — the small model's attempts drop or reorder words, and Accept refuses them. It must never make it
    // worse; Whisper is what gets it right (LiveWhisperTests.HearsTheOwnersTestPhrase).
    [LiveFact]
    public async Task TheOwnersExampleIsNeverMadeWorse()
    {
        const string heard = "The Doc One Two Tree Number tree";
        var tidied = await TidyAsync(heard);
        if (tidied is null) return;
        Assert.DoesNotContain("tree", tidied.Replace("three", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"(?i)\bone\b.*\btwo\b.*\bthree\b", tidied);
    }

    [LiveFact]
    public async Task FixesSoundAlikesWithoutAnswering()
    {
        var tidied = await TidyAsync("whats the whether going to be like in dublin tomorrow");
        Assert.NotNull(tidied);
        Assert.Contains("weather", tidied!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dublin", tidied);
        Assert.DoesNotContain("°", tidied);
    }

    [LiveFact]
    public async Task NonsenseEveryGuessSharesIsFixedOrLeftAlone()
    {
        // What Windows' offline recognizer (en-GB) made of synthetic speech through Aqua's audio pipe (30 Sep), with its
        // other guesses. Every guess has "the way the light", so the model has only the sense to go on: it usually writes
        // "the weather like" (3 of 3 probes) but once kept it — either is fine; anything else is not.
        const string heard = "Testing 123903 what is the way the light in Dublin tomorrow";
        var tidied = await TidyAsync(heard,
            "testing one two three nine oh three what is the way the light in Dublin tomorrow",
            "testing one two three another three what is the way the light in Dublin tomorrow",
            "testing one two three number three what is the way the light in Dublin tomorrow");
        if (tidied is null || tidied.Contains("the way the light", StringComparison.OrdinalIgnoreCase)) return;
        Assert.Contains("weather", tidied, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dublin", tidied);
        Assert.Contains("tomorrow", tidied, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task LeavesWhatIsRightAlone()
    {
        const string said = "Remind me what time the match starts on Saturday";
        var tidied = await TidyAsync(said) ?? said;
        Assert.True(DictationTidy.Similarity(said.ToLowerInvariant(), tidied.ToLowerInvariant().TrimEnd('.', '?')) > 0.85, tidied);
    }

    // Review 30 Sep (M1): correct sentences with words that have sound-alikes must keep every word (punctuation and
    // capitals may change). Marked unsure (0.4) so the model is asked, which is the hard case.
    [LiveTheory]
    [InlineData("The doc said I should rest for a week")]
    [InlineData("Check whether it will rain in Dublin tomorrow")]
    [InlineData("Their new house is right next to the bus stop")]
    [InlineData("I want to buy four tickets for the late show")]
    public async Task LeavesCorrectSentencesWithSoundAlikesAlone(string said)
    {
        static string Words(string s) => string.Join(" ", System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}']+").Select(m => m.Value));
        var tidied = await TidyAsync(said) ?? said;
        Assert.Equal(Words(said), Words(tidied));
    }
}
