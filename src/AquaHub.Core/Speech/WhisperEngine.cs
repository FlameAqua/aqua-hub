using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AquaHub.Core.Util;

namespace AquaHub.Core.Speech;

/// <summary>Why Whisper couldn't write down what was said, in plain words.</summary>
public sealed class WhisperException(string message) : Exception(message);

/// <summary>A recording for Whisper: 16 kHz, 16-bit mono PCM — what the microphone capture gives.</summary>
public static class SpeechAudio
{
    public const int SampleRate = 16000;
    /// <summary>The longest dictation kept: three minutes.</summary>
    public const int MaxSeconds = 180;
    public const int MaxBytes = SampleRate * 2 * MaxSeconds;
    private const int Frame = SampleRate * 30 / 1000;   // 30 ms

    /// <summary>The PCM as a WAV file (what whisper-cli reads from its input).</summary>
    public static byte[] Wav(ReadOnlySpan<byte> pcm)
    {
        var length = pcm.Length & ~1;
        var wav = new byte[44 + length];
        var w = new BinaryWriter(new MemoryStream(wav));
        w.Write("RIFF"u8);
        w.Write(36 + length);
        w.Write("WAVEfmt "u8);
        w.Write(16);                  // format chunk size
        w.Write((short)1);            // PCM
        w.Write((short)1);            // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);      // bytes per second
        w.Write((short)2);            // block align
        w.Write((short)16);           // bits per sample
        w.Write("data"u8);
        w.Write(length);
        w.Flush();
        pcm[..length].CopyTo(wav.AsSpan(44));
        return wav;
    }

    /// <summary>
    /// The part of a recording with speech in it — a little of the quiet either side kept, and turned up when it was
    /// recorded quietly — or null when nobody spoke (only room noise, a click, a cough). Whisper invents words for long
    /// silences ("Thank you."), so they don't go to it. <paramref name="speech"/> is how long the speech itself lasted.
    /// </summary>
    public static byte[]? SpeechPart(byte[] pcm, out TimeSpan speech)
    {
        speech = TimeSpan.Zero;
        var samples = pcm.Length / 2;
        var frames = samples / Frame;
        if (frames == 0) return null;
        var loudness = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            for (var i = f * Frame; i < (f + 1) * Frame; i++)
            {
                var s = Sample(pcm, i) / 32768.0;
                sum += s * s;
            }
            loudness[f] = 10 * Math.Log10(Math.Max(sum / Frame, 1e-10));   // dBFS
        }
        // Speech stands out from the room: 10 dB over its quietest tenth, and never below −50 dBFS.
        var floor = loudness.Order().ElementAt(frames / 10);
        var threshold = Math.Max(Math.Min(floor + 10, -30), -50);
        int first = -1, last = -1, spoken = 0;
        for (var f = 0; f < frames; f++)
        {
            if (loudness[f] < threshold) continue;
            spoken++;
            if (first < 0) first = f;
            last = f;
        }
        speech = TimeSpan.FromMilliseconds(spoken * 30);
        if (spoken * 30 < 200) return null;
        var start = Math.Max(0, first - 17) * Frame;                  // ½ s before
        var end = Math.Min(samples, (last + 21) * Frame);             // ⅔ s after
        var clip = new byte[(end - start) * 2];
        Buffer.BlockCopy(pcm, start * 2, clip, 0, clip.Length);

        // Quiet microphones: bring the loudest peak up to about −3 dBFS (at most ×10).
        var peak = 1;
        for (var i = 0; i < clip.Length / 2; i++) peak = Math.Max(peak, Math.Abs((int)Sample(clip, i)));
        var gain = Math.Min(10.0, 23_000.0 / peak);
        if (gain > 1.2)
            for (var i = 0; i < clip.Length / 2; i++)
            {
                var v = (short)Math.Clamp((int)Math.Round(Sample(clip, i) * gain), short.MinValue, short.MaxValue);
                clip[i * 2] = (byte)v;
                clip[i * 2 + 1] = (byte)(v >> 8);
            }
        return clip;
    }

    private static short Sample(byte[] pcm, int index) => (short)(pcm[index * 2] | pcm[index * 2 + 1] << 8);
}

/// <summary>How Aqua runs whisper-cli, and what it makes of its output.</summary>
public static partial class WhisperCli
{
    /// <summary>
    /// The recording comes in on standard input ("-f -"), so it's never written to disk. An output name ("-of") makes
    /// whisper-cli print the words to standard output — without one, input from a pipe sends them to the console — and
    /// with no output format chosen it writes no files. No timestamps, no log, non-speech tokens (♪, [MUSIC]) suppressed.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string model, string language, bool englishOnly, int threads) => new[]
    {
        "-m", model, "-f", "-", "-of", "aqua-dictation", "-l", Language(language, englishOnly),
        "-t", threads.ToString(CultureInfo.InvariantCulture), "-nt", "-np", "-sns",
    };

    /// <summary>Whisper's two-letter code for the language ("en-IE" → "en"); "auto" when it has none.</summary>
    public static string Language(string language, bool englishOnly)
    {
        if (englishOnly) return "en";
        try
        {
            var two = CultureInfo.GetCultureInfo(language.Trim()).TwoLetterISOLanguageName;
            return two.Length == 2 && two != "iv" ? two : "auto";
        }
        catch (CultureNotFoundException) { return "auto"; }
    }

    /// <summary>About one thread per core (Windows counts two per core on most processors), 1–8.</summary>
    public static int Threads(int processors) => Math.Clamp(processors / 2, 1, 8);

    [GeneratedRegex(@"\[[^\]\n]{0,60}\]|[♪♫🎵🎶]+")]
    private static partial Regex BracketRx();

    [GeneratedRegex(@"[(*]\s*(?:(?:\w+\s+){0,2}music|laugh(?:s|ing|ter)?|silence|applause|inaudible|cough(?:s|ing)?|sigh(?:s|ing)?|noise|static|breath(?:es|ing)?|clears throat|blank[_ ]audio|no speech|mumbl(?:es|ing))\s*[)*]", RegexOptions.IgnoreCase)]
    private static partial Regex SoundRx();

    [GeneratedRegex(@"^\W*(?:thank you\W*|thanks(?: for watching)?\W*|thank you (?:so much )?for watching\W*|you\W*|bye\W*|\.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SilenceWordsRx();

    // Subtitle credits it learned from films; nobody dictates them.
    [GeneratedRegex(@"(?:subtitles|captions) (?:by|from) the amara\.org community\.?", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsRx();

    /// <summary>
    /// The words Whisper wrote, without what isn't speech — "[BLANK_AUDIO]", "(music)", "*laughs*", subtitle credits —
    /// and, after very little speech, without the phrases it makes up from noise ("Thank you.", "you").
    /// </summary>
    public static string Clean(string output, TimeSpan speech)
    {
        var text = SoundRx().Replace(BracketRx().Replace(output, " "), " ");
        text = CreditsRx().Replace(text, " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (speech < TimeSpan.FromSeconds(1.2) && SilenceWordsRx().IsMatch(text)) return "";
        return text.Trim('-', ' ').Length == 0 ? "" : text;
    }

    /// <summary>Plain words for a whisper-cli failure, from its exit code and what it printed.</summary>
    public static string Explain(int exitCode, string errors)
    {
        var last = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return exitCode switch
        {
            unchecked((int)0xC0000135) or unchecked((int)0xC000007B) =>
                "Whisper is missing one of its files — reinstall it in Settings › Ask Aqua › Voice.",
            unchecked((int)0xC000001D) => "This PC's processor can't run this Whisper build.",
            _ when errors.Contains("failed to load model", StringComparison.OrdinalIgnoreCase)
                   || errors.Contains("failed to initialize whisper context", StringComparison.OrdinalIgnoreCase) =>
                "Whisper couldn't load its model — reinstall it in Settings › Ask Aqua › Voice.",
            _ when errors.Contains("failed to read audio", StringComparison.OrdinalIgnoreCase) || errors.Contains("from stdin", StringComparison.OrdinalIgnoreCase) =>
                "Whisper couldn't read the recording.",
            _ => $"Whisper stopped (code {exitCode})" + (last.Length > 0 ? ": " + HtmlText.Truncate(last, 160) : "."),
        };
    }
}

/// <summary>Runs whisper-cli on one recording, handing it the audio through a pipe.</summary>
public static class WhisperRunner
{
    /// <summary>The words said in a recording (PCM from <see cref="SpeechAudio.SpeechPart"/>), or "" when there were none.</summary>
    public static async Task<string> TranscribeAsync(WhisperInstall install, byte[] pcm, TimeSpan speech, string language, CancellationToken ct)
    {
        var seconds = pcm.Length / (double)(SpeechAudio.SampleRate * 2);
        var info = new ProcessStartInfo(install.Cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(install.Cli)!,
        };
        foreach (var arg in WhisperCli.Arguments(install.Model, language, install.EnglishOnly, WhisperCli.Threads(Environment.ProcessorCount)))
            info.ArgumentList.Add(arg);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Generous: the big model on a slow processor takes a few times as long as the recording.
        limit.CancelAfter(TimeSpan.FromSeconds(60 + seconds * 4));
        Process process;
        try { process = Process.Start(info) ?? throw new WhisperException("Whisper didn't start."); }
        catch (Win32Exception ex)
        {
            Log.Warn("whisper", "Couldn't start whisper-cli", ex);
            throw new WhisperException("Whisper couldn't start (" + ex.Message.TrimEnd('.') + ") — reinstall it in Settings › Ask Aqua › Voice.");
        }
        var watch = Stopwatch.StartNew();
        using (process)
        {
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(limit.Token);
                var errors = process.StandardError.ReadToEndAsync(limit.Token);
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(SpeechAudio.Wav(pcm), limit.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException) { /* It ended before reading everything; its exit code and errors say why. */ }
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                var text = await output.ConfigureAwait(false);
                var problems = await errors.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    Log.Warn("whisper", $"whisper-cli exited with {process.ExitCode}: {HtmlText.Truncate(problems.Trim(), 400)}");
                    throw new WhisperException(WhisperCli.Explain(process.ExitCode, problems));
                }
                Log.Debug("whisper", $"Transcribed {seconds:0.0} s of audio in {watch.Elapsed.TotalSeconds:0.0} s");
                return WhisperCli.Clean(text, speech);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                if (ct.IsCancellationRequested) throw;
                throw new WhisperException("Whisper took too long — a smaller model (Settings › Ask Aqua › Voice) is quicker.");
            }
        }
    }
}
