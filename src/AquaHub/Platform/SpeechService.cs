using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using AquaHub.Core.Settings;
using AquaHub.Core.Speech;
using AquaHub.Services;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace AquaHub.Platform;

/// <summary>Shared offline playback and the Windows and audio.cpp adapters.</summary>
public sealed class SpeechService
{
    public static readonly object Briefing = new();
    public static readonly object Preview = new();
    private readonly ReadAloud _reader;
    private readonly Func<AskSettings> _settings;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _install;
    private Task? _closing;
    private CancellationTokenSource? _installCancellation;

    public SpeechService(KokoroSetup kokoro, Func<AskSettings> settings)
    {
        Kokoro = kokoro;
        _settings = settings;
        _reader = new(SynthesizeAsync, PlayAsync);
        _reader.Changed += Notify;
        Cleanup = Task.Run(kokoro.ClearAbandonedSpeech);
    }

    public KokoroSetup Kokoro { get; }
    private Task Cleanup { get; }
    public ReadAloudState State => _reader.State;
    public event Action? StateChanged;
    private void Notify() => Hub.OnUi(() => StateChanged?.Invoke());
    public bool IsActive(object owner) => State.Active && ReferenceEquals(State.Owner, owner);
    public void Stop() => _reader.Stop();

    public void Toggle(object owner, string text, string language, bool markdown = false)
    {
        var settings = _settings();
        _reader.Toggle(new(owner, text, language, settings.ReadAloudBackend, settings.ReadAloudVoice, markdown));
    }

    public Task InstallAsync(IProgress<long> progress, bool repair)
    {
        if (_closing is not null) return Task.CompletedTask;
        if (_install is { IsCompleted: false }) return _install;
        if (Sandbox.Intercept("kokoro-install")) return Task.CompletedTask;
        _installCancellation?.Dispose();
        _installCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var token = _installCancellation.Token;
        if (repair) _reader.Stop();
        return _install = Task.Run(() => Kokoro.InstallAsync(progress, repair, token));
    }

    public void CancelInstall() { if (_closing is null) _installCancellation?.Cancel(); }

    public Task ShutdownAsync() => _closing ??= JoinAsync();

    private async Task JoinAsync()
    {
        _shutdown.Cancel();
        await _reader.ShutdownAsync().ConfigureAwait(false);
        if (_install is { } install)
            try { await install.ConfigureAwait(false); }
            catch { /* The initiating settings control observes and reports the install result. */ }
        await Cleanup.ConfigureAwait(false);
        _reader.Changed -= Notify;
        _installCancellation?.Dispose();
        _shutdown.Dispose();
    }

    private async Task<IDisposable> SynthesizeAsync(ReadAloudRequest request, string chunk, CancellationToken cancellation)
    {
        if (Sandbox.Intercept("speak", request.Backend)) return new InterceptedAudio();
        return request.Backend == "kokoro"
            ? await SynthesizeKokoroAsync(request, chunk, cancellation).ConfigureAwait(false)
            : await SynthesizeWindowsAsync(request, chunk, cancellation).ConfigureAwait(false);
    }

    private static async Task<Audio> SynthesizeWindowsAsync(ReadAloudRequest request, string chunk, CancellationToken cancellation)
    {
        using var synth = new SpeechSynthesizer();
        var voices = SpeechSynthesizer.AllVoices;
        var language = request.Language.Split('-', '_')[0];
        var voice = voices.FirstOrDefault(v => v.Language.Equals(request.Language, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.Language.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase) && v.Language.EndsWith("-GB", StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.Language.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
        if (voice is null) throw new ReadAloudException("No Windows read-aloud voice matches your language. Add one in Windows speech settings.");
        synth.Voice = voice;
        synth.Options.SpeakingRate = 1.05;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stream = await synth.SynthesizeTextToStreamAsync(chunk).AsTask(timeout.Token).ConfigureAwait(false);
        if (stream.Size is > 0 and <= MaxWaveBytes) return new Audio(stream, stream.ContentType);
        stream.Dispose();
        throw new ReadAloudException("Windows returned an unexpected audio size.");
    }

    private async Task<Audio> SynthesizeKokoroAsync(ReadAloudRequest request, string chunk, CancellationToken cancellation)
    {
        if (!KokoroSetup.Supported)
            throw new ReadAloudException("Kokoro is unavailable on this processor. Choose Windows voices in Read-aloud settings.");
        if (!request.Language.Equals("en", StringComparison.OrdinalIgnoreCase)
            && !request.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            throw new ReadAloudException("The Kokoro voices in Aqua currently read English. Choose Windows voices for your language.");
        if (Volatile.Read(ref _install) is { IsCompleted: false })
            throw new ReadAloudException("Kokoro setup is still running. Try again when it finishes.");
        var install = Kokoro.Installed() ?? throw new ReadAloudException("Install or repair Kokoro in Read-aloud settings, or choose Windows voices.");
        await Cleanup.ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var work = Kokoro.CreateWorkFolder();
        try
        {
            var text = Path.Combine(work, "text.txt");
            await File.WriteAllTextAsync(text, chunk, cancellation).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            await RunKokoroAsync(install, KokoroSetup.Voice(request.Voice), text, work, timeout.Token).ConfigureAwait(false);
            var wave = Path.Combine(work, "line_1.wav");
            await using var file = new FileStream(wave, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.Asynchronous);
            if (file.Length is < 44 or > MaxWaveBytes)
                throw new ReadAloudException("Kokoro returned an unexpected audio size.");
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
            if (!bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                throw new ReadAloudException("Kokoro returned audio Aqua couldn't open.");
            return new Audio(new MemoryStream(bytes, writable: false).AsRandomAccessStream(), "audio/wav");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (ReadAloudException) { throw; }
        catch (Exception)
        {
            // The child may echo input. Report a fixed message and never include its output or command in logs.
            throw new ReadAloudException("Kokoro could not prepare speech. Try again, or choose Windows voices in Settings.");
        }
        finally { Kokoro.ReleaseWorkFolder(work); }
    }

    private const int MaxWaveBytes = 4 * 1024 * 1024;

    private static async Task RunKokoroAsync(KokoroInstall install, KokoroVoice voice, string text, string work,
        CancellationToken cancellation)
    {
        using var job = Native.CreateKillOnCloseJob() ?? throw new ReadAloudException("Windows couldn't start an isolated voice worker.");
        using var process = new Process
        {
            StartInfo = new(install.Cli)
            {
                WorkingDirectory = install.DataRoot, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.Environment["ESPEAK_DATA_PATH"] = install.DataRoot;
        process.StartInfo.Environment["OMP_NUM_THREADS"] = "2";
        foreach (var argument in new[] { "--task", "tts", "--family", "kokoro_tts", "--backend", "cpu", "--threads", "2",
                     "--model", install.Model, "--language", voice.Language, "--voice-id", voice.Id, "--batch-text-file", text, "--out-dir", work })
            process.StartInfo.ArgumentList.Add(argument);
        cancellation.ThrowIfCancellationRequested();
        if (!process.Start()) throw new ReadAloudException("Kokoro could not start.");
        var attached = false;
        Task output = Task.CompletedTask, error = Task.CompletedTask;
        try
        {
            attached = Native.AssignProcessToJobObject(job, process.Handle);
            if (!attached) throw new ReadAloudException("Windows couldn't isolate the voice worker.");
            output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            error = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new ReadAloudException("Kokoro could not start or synthesize. Check its installation in Settings.");
        }
        finally
        {
            if (!process.HasExited)
            {
                if (attached) job.Dispose();
                else
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
    }

    private static async Task PlayAsync(IDisposable value, CancellationToken cancellation)
    {
        if (value is InterceptedAudio)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation).ConfigureAwait(false);
            return;
        }
        var audio = (Audio)value;
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = MediaSource.CreateFromStream(audio.Stream, audio.ContentType);
        using var player = new MediaPlayer { Source = source };
        player.CommandManager.IsEnabled = false;
        void Ended(MediaPlayer _, object __) => result.TrySetResult();
        void Failed(MediaPlayer _, MediaPlayerFailedEventArgs __) =>
            result.TrySetException(new ReadAloudException("Windows could not play this audio. Check your audio device and try again."));
        player.MediaEnded += Ended;
        player.MediaFailed += Failed;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            player.Play();
            await result.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellation).ConfigureAwait(false);
        }
        finally
        {
            player.MediaEnded -= Ended;
            player.MediaFailed -= Failed;
            player.Pause();
        }
    }

    private sealed class Audio(IRandomAccessStream stream, string contentType) : IDisposable
    {
        public IRandomAccessStream Stream { get; } = stream;
        public string ContentType { get; } = contentType;
        public void Dispose() => Stream.Dispose();
    }

    private sealed class InterceptedAudio : IDisposable { public void Dispose() { } }
}
