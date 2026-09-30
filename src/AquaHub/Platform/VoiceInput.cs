using System.Globalization;
using System.Runtime.InteropServices;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Speech;
using AquaHub.Core.Util;
using AquaHub.Services;
using OfflineSpeech = System.Speech.Recognition;
using SpeechFormat = System.Speech.AudioFormat;
using WinSpeech = Windows.Media.SpeechRecognition;

namespace AquaHub.Platform;

/// <summary>
/// Dictation for the Ask box. Three engines: "offline" — Windows' own desktop speech recognizer, entirely on this PC,
/// listening on the microphone chosen in Settings (or Windows' default); "online" — Windows online speech recognition
/// (more accurate; audio goes to Microsoft's speech service, Windows only allows it when "Online speech recognition" is
/// on in Settings › Privacy &amp; security › Speech, and it always uses the default microphone); and "whisper" —
/// Whisper on this PC (installed on request, <see cref="WhisperSetup"/>): Aqua records the chosen microphone into
/// memory and, when you stop, hands the recording to whisper-cli through a pipe. Windows' engines dictate in the
/// nearest language Windows offers (English (Ireland) → English (UK)); each phrase is kept with the other wordings the
/// recognizer considered, for the local model's tidy-up (<see cref="DictationTidy"/>). Whisper's words go in as written.
/// Listening stops after a few seconds of silence, or when you click the microphone again.
/// </summary>
public sealed class VoiceInput : IDisposable
{
    private OfflineSpeech.SpeechRecognitionEngine? _offline;
    private WinSpeech.SpeechRecognizer? _online;
    private MicCapture? _capture;
    private PcmPipe? _pipe;
    private System.Windows.Threading.DispatcherTimer? _silence;
    private DateTime _lastSpeech, _lastProblem;
    private bool _heardSomething;
    private readonly List<HeardPhrase> _heard = new();
    private WhisperInstall? _whisper;
    private string _whisperLanguage = "";
    private double _floor = 1;
    private bool _transcribing;
    private CancellationTokenSource? _transcribeCts;

    /// <summary>Words recognised so far in the current phrase (UI thread).</summary>
    public event Action<string>? Partial;
    /// <summary>A finished phrase (UI thread).</summary>
    public event Action<string>? Phrase;
    /// <summary>How loud the microphone is, 0–1, when the engine can tell (UI thread).</summary>
    public event Action<double>? Level;
    /// <summary>Advice about the sound, e.g. "Too quiet — …" (UI thread; at most every few seconds).</summary>
    public event Action<string>? Problem;
    /// <summary>Listening ended: null normally, or why it couldn't go on (UI thread).</summary>
    public event Action<string?>? Ended;
    /// <summary>Whisper: the recording has stopped and Whisper is writing down what was said (UI thread).</summary>
    public event Action? Transcribing;

    public bool IsListening { get; private set; }
    /// <summary>Whisper is writing down a recording (still <see cref="IsListening"/> until it's done).</summary>
    public bool IsTranscribing => _transcribing;
    /// <summary>The phrases of the current or last dictation, with the recognizer's other guesses.</summary>
    public IReadOnlyList<HeardPhrase> Heard => _heard;
    /// <summary>What it is listening with, e.g. "English (United Kingdom) · Microphone (USB Audio)".</summary>
    public string Describe { get; private set; } = "";
    /// <summary>Something worth saying about this session (the chosen microphone isn't connected…), or null.</summary>
    public string? Note { get; private set; }

    /// <summary>Starts listening. Returns null, or why it can't (no microphone, no recognizer, privacy setting off).</summary>
    public async Task<string?> StartAsync(string engine, string language, string microphone = "")
    {
        if (IsListening) return null;
        if (Sandbox.Intercept("voice-input", engine)) return "Voice input is simulated in this test session.";
        _heard.Clear();
        Note = null;
        try
        {
            var error = engine switch
            {
                "online" => await StartOnlineAsync(language),
                "whisper" => StartWhisper(language, microphone),
                _ => StartOffline(language, microphone),
            };
            if (error is not null) { Cleanup(); return error; }
        }
        catch (MicException ex)
        {
            Log.Warn("voice", "Couldn't open the microphone: " + ex.Message);
            Cleanup();
            return ex.Message;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            Log.Warn("voice", "Couldn't start listening", ex);
            Cleanup();
            return Explain(ex);
        }
        IsListening = true;
        _heardSomething = false;
        _lastSpeech = DateTime.UtcNow;
        _silence = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _silence.Tick += (_, _) =>
        {
            var quiet = DateTime.UtcNow - _lastSpeech;
            if (_heardSomething ? quiet > TimeSpan.FromSeconds(2.8) : quiet > TimeSpan.FromSeconds(9)) Stop();
            else if (_whisper is not null && _pipe is { } recording && recording.Available >= SpeechAudio.MaxBytes - MicCapture.SampleRate * 2)
            {
                ReportProblem("TooLong");
                Stop();
            }
        };
        _silence.Start();
        Log.Info("voice", $"Listening ({engine}): {Describe}");
        return null;
    }

    private string? StartOffline(string language, string microphone)
    {
        var installed = OfflineSpeech.SpeechRecognitionEngine.InstalledRecognizers();
        var tag = SpeechLanguage.Pick(language, installed.Select(r => r.Culture.Name), CultureInfo.CurrentUICulture.Name);
        var info = installed.FirstOrDefault(r => r.Culture.Name.Equals(tag, StringComparison.OrdinalIgnoreCase))
                   ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en")
                   ?? installed.FirstOrDefault();
        if (info is null)
            return "No offline speech recognizer is installed. Add a speech pack in Windows Settings › Time & language › Speech, or switch Ask's voice input to Whisper (install it there) or Windows online speech in Settings › Ask Aqua.";
        _offline = new OfflineSpeech.SpeechRecognitionEngine(info);
        _offline.LoadGrammar(new OfflineSpeech.DictationGrammar { Name = "dictation" });
        // Whole sentences rather than word-by-word fragments: the recognizer guesses far better with the words around them.
        _offline.EndSilenceTimeout = TimeSpan.FromMilliseconds(500);
        _offline.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(900);
        _offline.MaxAlternates = 4;

        var mic = Microphones.Find(Microphones.List(), microphone);
        if (microphone.Trim().Length > 0 && mic is null) Note = "Your chosen microphone isn't connected, so this uses Windows' default.";
        if (mic is not null)
        {
            _pipe = new PcmPipe();
            _capture = new MicCapture(mic.Index, _pipe, level => OnUi(() => Level?.Invoke(level)));
            _offline.SetInputToAudioStream(_pipe, new SpeechFormat.SpeechAudioFormatInfo(MicCapture.SampleRate, SpeechFormat.AudioBitsPerSample.Sixteen, SpeechFormat.AudioChannel.Mono));
        }
        else
        {
            _offline.SetInputToDefaultAudioDevice();
            _offline.AudioLevelUpdated += (_, e) => OnUi(() => Level?.Invoke(e.AudioLevel / 100.0));
        }
        Describe = info.Culture.DisplayName + " · " + (mic?.Name ?? "Windows' default microphone");

        _offline.SpeechHypothesized += (_, e) => OnUi(() => { _lastSpeech = DateTime.UtcNow; Partial?.Invoke(e.Result.Text); });
        _offline.SpeechDetected += (_, _) => OnUi(() => _lastSpeech = DateTime.UtcNow);
        _offline.AudioSignalProblemOccurred += (_, e) => OnUi(() => ReportProblem(e.AudioSignalProblem.ToString()));
        _offline.SpeechRecognized += (_, e) =>
        {
            var result = e.Result;
            if (string.IsNullOrWhiteSpace(result.Text) || DictationTidy.IsNoise(result.Text, result.Confidence)) return;
            // Read the other guesses here: the result belongs to the recognizer's thread.
            var alternates = result.Alternates.Select(a => a.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !t.Equals(result.Text, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            var phrase = new HeardPhrase(result.Text, result.Confidence, alternates);
            OnUi(() => { _heardSomething = true; _lastSpeech = DateTime.UtcNow; _heard.Add(phrase); Phrase?.Invoke(phrase.Text); });
        };
        _offline.RecognizeCompleted += (_, e) => OnUi(() => Finish(
            e.Error is not null ? Explain(e.Error) : _capture?.Failed == true ? "The microphone stopped — was it unplugged?" : null));
        _offline.RecognizeAsync(OfflineSpeech.RecognizeMode.Multiple);
        return null;
    }

    private string? StartWhisper(string language, string microphone)
    {
        var install = Hub.Core.Whisper.Installed();
        if (install is null)
            return "Whisper isn't installed yet — install it in Settings › Ask Aqua › Voice, or choose another voice input there.";
        var mic = Microphones.Find(Microphones.List(), microphone);
        if (microphone.Trim().Length > 0 && mic is null) Note = "Your chosen microphone isn't connected, so this uses Windows' default.";
        if (install.EnglishOnly && !WhisperCatalog.IsEnglish(language))
            Note = (Note is null ? "" : Note + " ") + "The Whisper model installed only knows English — Settings › Ask Aqua › Voice has the multilingual one.";
        _whisper = install;
        _whisperLanguage = language;
        _floor = 1;
        _pipe = new PcmPipe(SpeechAudio.MaxBytes);
        _capture = new MicCapture(mic?.Index ?? MicCapture.DefaultDevice, _pipe, level => OnUi(() => OnWhisperLevel(level)));
        Describe = install.Name + " · " + (mic?.Name ?? "Windows' default microphone");
        return null;
    }

    /// <summary>
    /// Whisper only writes once you stop, so the microphone level tells when you're speaking: well above the room's own
    /// noise (the quietest it has been lately, drifting up slowly in case the room gets louder).
    /// </summary>
    private void OnWhisperLevel(double level)
    {
        if (!IsListening || _transcribing) return;
        Level?.Invoke(level);
        _floor = level < _floor ? level : _floor + 0.001;
        if (level <= Math.Max(0.35, _floor + 0.2)) return;
        _lastSpeech = DateTime.UtcNow;
        _heardSomething = true;
    }

    /// <summary>Stops recording and has Whisper write down what was said; listening ends once it has.</summary>
    private async Task TranscribeAsync()
    {
        _transcribing = true;
        _silence?.Stop();
        try { _capture?.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        _capture = null;
        var pipe = _pipe!;
        pipe.Complete();
        var pcm = new byte[pipe.Available];
        pcm = pcm[..pipe.Read(pcm, 0, pcm.Length)];
        Transcribing?.Invoke();
        string? error = null;
        try
        {
            var cts = _transcribeCts = new CancellationTokenSource();
            var (install, language) = (_whisper!, _whisperLanguage);
            var text = await Task.Run(async () =>
            {
                if (SpeechAudio.SpeechPart(pcm, out var speech) is { } clip) return await WhisperRunner.TranscribeAsync(install, clip, speech, language, cts.Token);
                Log.Debug("voice", $"Whisper: no speech in {pcm.Length / (MicCapture.SampleRate * 2.0):0.0} s of recording");
                return "";
            });
            if (text.Length > 0 && IsListening)
            {
                var phrase = new HeardPhrase(text, 0.9, Array.Empty<string>());
                _heard.Add(phrase);
                Phrase?.Invoke(phrase.Text);
            }
        }
        catch (WhisperException ex) { error = ex.Message; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Nothing else is expected, but listening must end whatever happens, or the microphone button stays on.
            Log.Error("voice", "Whisper dictation failed", ex);
            error = "Whisper couldn't write it down: " + ex.Message;
        }
        Finish(error);
    }

    private async Task<string?> StartOnlineAsync(string language)
    {
        var offered = WinSpeech.SpeechRecognizer.SupportedTopicLanguages.Select(l => l.LanguageTag).ToList();
        var system = WinSpeech.SpeechRecognizer.SystemSpeechLanguage?.LanguageTag;
        // Only the languages Windows offers for dictation work; any other fails with "The requested language is not supported".
        var tag = SpeechLanguage.Pick(language, offered, system) ?? (offered.Count == 0 ? system : null);
        if (tag is null)
            return $"Windows online speech doesn't offer {LanguageName(language)} on this PC (it has {string.Join(", ", offered.Take(4).Select(LanguageName))}). " +
                   "Add the speech language in Windows Settings › Time & language › Speech, or use Whisper or “Windows, on this PC” (Settings › Ask Aqua › Voice input).";
        _online = new WinSpeech.SpeechRecognizer(new Windows.Globalization.Language(tag));
        var compiled = await _online.CompileConstraintsAsync();
        if (compiled.Status != WinSpeech.SpeechRecognitionResultStatus.Success)
            return "Windows online speech recognition isn't available for " + LanguageName(tag) + " (" + compiled.Status + ").";
        Describe = LanguageName(tag) + " · Windows' default microphone";
        _online.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromSeconds(4);
        _online.HypothesisGenerated += (_, e) => OnUi(() => { _lastSpeech = DateTime.UtcNow; Partial?.Invoke(e.Hypothesis.Text); });
        _online.RecognitionQualityDegrading += (_, e) => OnUi(() => ReportProblem(e.Problem.ToString()));
        _online.ContinuousRecognitionSession.ResultGenerated += (_, e) =>
        {
            var result = e.Result;
            if (result.Status != WinSpeech.SpeechRecognitionResultStatus.Success || string.IsNullOrWhiteSpace(result.Text)
                || result.Confidence == WinSpeech.SpeechRecognitionConfidence.Rejected) return;
            var alternates = new List<string>();
            try
            {
                alternates = result.GetAlternates(4).Select(a => a.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t) && !t.Equals(result.Text, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            }
            catch (COMException) { }
            var confidence = result.Confidence switch
            {
                WinSpeech.SpeechRecognitionConfidence.High => 0.9,
                WinSpeech.SpeechRecognitionConfidence.Medium => 0.6,
                _ => 0.3,
            };
            var phrase = new HeardPhrase(result.Text, confidence, alternates);
            OnUi(() => { _heardSomething = true; _lastSpeech = DateTime.UtcNow; _heard.Add(phrase); Phrase?.Invoke(phrase.Text); });
        };
        _online.ContinuousRecognitionSession.Completed += (_, e) => OnUi(() => Finish(
            e.Status is WinSpeech.SpeechRecognitionResultStatus.Success or WinSpeech.SpeechRecognitionResultStatus.TimeoutExceeded or WinSpeech.SpeechRecognitionResultStatus.UserCanceled
                ? null : "Speech recognition stopped: " + e.Status));
        await _online.ContinuousRecognitionSession.StartAsync();
        return null;
    }

    public void Stop()
    {
        if (!IsListening) return;
        if (_whisper is not null)
        {
            if (!_transcribing) _ = TranscribeAsync();
            return;
        }
        try
        {
            // A chosen microphone: end its audio, and the recognizer finishes what it has. (RecognizeAsyncStop would wait
            // for the audio stream itself to end — on the UI thread, for good.)
            if (_pipe is not null)
            {
                _capture?.Dispose();
                _capture = null;
                _pipe.Complete();
            }
            else if (_offline is not null) _offline.RecognizeAsyncStop();
            else if (_online is not null) _ = _online.ContinuousRecognitionSession.StopAsync().AsTask().ContinueWith(_ => OnUi(() => Finish(null)), TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException) { Finish(null); }
        // The offline engine reports RecognizeCompleted; make sure the UI never waits forever.
        _ = Task.Delay(1500).ContinueWith(_ => OnUi(() => Finish(null)), TaskScheduler.Default);
    }

    private void Finish(string? error)
    {
        if (!IsListening) return;
        IsListening = false;
        Cleanup();
        Ended?.Invoke(error);
    }

    private void Cleanup()
    {
        _silence?.Stop();
        _silence = null;
        try { _transcribeCts?.Cancel(); } catch (ObjectDisposedException) { }
        _transcribeCts?.Dispose();
        _transcribeCts = null;
        _whisper = null;
        _transcribing = false;
        // First end the audio, so a read the recognizer is waiting on returns.
        _pipe?.Complete();
        try { _offline?.Dispose(); } catch { }
        _offline = null;
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _pipe?.Dispose();
        _pipe = null;
        try { _online?.Dispose(); } catch { }
        _online = null;
    }

    private void ReportProblem(string problem)
    {
        if (ProblemText(problem) is not { } text || DateTime.UtcNow - _lastProblem < TimeSpan.FromSeconds(4)) return;
        _lastProblem = DateTime.UtcNow;
        Problem?.Invoke(text);
    }

    /// <summary>What to do about a sound problem either engine reports.</summary>
    internal static string? ProblemText(string problem) => problem switch
    {
        "TooNoisy" => "It's noisy — move closer to the microphone or away from the noise.",
        "NoSignal" => "No sound from the microphone — check it's plugged in and not muted.",
        "TooLoud" => "Too loud — move back a little, or lower the microphone level in Sound settings.",
        "TooSoft" or "TooQuiet" => "Too quiet — move closer, or raise the microphone level in Sound settings.",
        "TooFast" => "Try speaking a little slower.",
        "TooSlow" => "Try speaking in whole sentences, without long gaps.",
        "TooLong" => "That's the longest one dictation can be (three minutes) — writing it down now.",
        _ => null,
    };

    private static string LanguageName(string tag)
    {
        try { return CultureInfo.GetCultureInfo(tag).DisplayName; }
        catch (CultureNotFoundException) { return tag; }
    }

    private static void OnUi(Action a) => Hub.Ui.BeginInvoke(a);

    /// <summary>Plain-language reasons for the usual failures.</summary>
    internal static string Explain(Exception ex) => ex switch
    {
        _ when ex.HResult == unchecked((int)0x80045509) =>
            "Windows online speech recognition is off. Turn it on in Windows Settings › Privacy & security › Speech, or use the offline recognizer (Settings › Ask Aqua › Voice input).",
        _ when ex.HResult == unchecked((int)0x800455BC) || ex.Message.Contains("language is not supported", StringComparison.OrdinalIgnoreCase) =>
            "Windows online speech doesn't support this language on this PC. Add it in Windows Settings › Time & language › Speech, or use Whisper or “Windows, on this PC” (Settings › Ask Aqua › Voice input).",
        _ when ex.HResult == unchecked((int)0x80070005) || ex is UnauthorizedAccessException =>
            "Aqua can't use the microphone. Allow desktop apps to use it in Windows Settings › Privacy & security › Microphone.",
        InvalidOperationException when ex.Message.Contains("audio", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("input", StringComparison.OrdinalIgnoreCase) =>
            "No microphone was found. Plug one in or pick one in Windows Settings › System › Sound › Input.",
        _ => "Voice input stopped: " + ex.Message.Trim(),
    };

    public void Dispose()
    {
        IsListening = false;
        Cleanup();
    }
}
