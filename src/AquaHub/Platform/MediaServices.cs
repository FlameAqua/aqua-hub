using System.Runtime.InteropServices;
using AquaHub.Core.Media;
using AquaHub.Core.Util;
using AquaHub.Services;
using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace AquaHub.Platform;

public sealed record MediaInfo(string Title, string Artist, string Album, string SourceApp, bool IsPlaying,
    TimeSpan Position, TimeSpan Duration, byte[]? Thumbnail, bool CanNext, bool CanPrevious);

/// <summary>
/// Controls whatever is playing (Spotify, browser, Media Player…) through Windows' global media
/// session manager — no per-app integrations or credentials required.
/// </summary>
public sealed class MediaController
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _key;
    private string? _album;
    private byte[]? _thumb;
    /// <summary>The previous title's artwork, which a browser keeps reporting for a moment with a new title.</summary>
    private byte[]? _previousArt;
    private string? _previousAlbum;
    private int _generation;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _hookGate = new();
    private System.Threading.Timer? _watch;

    public MediaInfo? Current { get; private set; }
    public event Action? Changed;

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => { Hook(); _ = RefreshAsync(artwork: true); };
            _manager.SessionsChanged += (_, _) => { Hook(); _ = RefreshAsync(artwork: true); };
            Hook();
            await RefreshAsync(artwork: true);
            // Windows doesn't always say when a player closes; a quiet look every few seconds catches it.
            _watch = new System.Threading.Timer(_ => { if (_session is not null || Current is not null) { Hook(); _ = RefreshAsync(artwork: false); } },
                null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Warn("media", "Media session manager unavailable", ex);
        }
    }

    private void Hook()
    {
        lock (_hookGate) HookLocked();
    }

    private void HookLocked()
    {
        GlobalSystemMediaTransportControlsSession? session;
        try { session = _manager?.GetCurrentSession(); }
        catch (COMException) { session = null; }
        if (ReferenceEquals(session, _session)) return;
        if (_session is { } old)
        {
            // A player that has closed can't be unsubscribed from; it's dropped either way.
            try
            {
                old.MediaPropertiesChanged -= OnMediaChanged;
                old.PlaybackInfoChanged -= OnPlaybackChanged;
            }
            catch (COMException) { }
        }
        _session = session;
        if (session is null) return;
        try
        {
            session.MediaPropertiesChanged += OnMediaChanged;
            session.PlaybackInfoChanged += OnPlaybackChanged;
        }
        catch (COMException) { _session = null; }
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync(artwork: true);
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ = RefreshAsync(artwork: false);

    public Task RefreshAsync() => RefreshAsync(artwork: true);

    /// <param name="artwork">Read the artwork again (the media changed), not just the playback state.</param>
    private async Task RefreshAsync(bool artwork)
    {
        var generation = Interlocked.Increment(ref _generation);
        // Only the latest refresh may change what's shown: an older one finishing late would bring back the previous
        // title or picture. They also take turns, as events arrive on several threads at once.
        bool Stale() => generation != Volatile.Read(ref _generation);
        await _refreshGate.WaitAsync();
        try
        {
            if (Stale()) return;
            var session = _session;
            if (session is null)
            {
                Set(null);
                return;
            }
            GlobalSystemMediaTransportControlsSessionMediaProperties props;
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback;
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline;
            try
            {
                props = await session.TryGetMediaPropertiesAsync();
                playback = session.GetPlaybackInfo();
                timeline = session.GetTimelineProperties();
            }
            catch (COMException)
            {
                // The player has gone (its process ended): look again at what Windows has now.
                if (Stale()) return;
                if (ReferenceEquals(_session, session)) _session = null;
                Hook();
                if (_session is null) Set(null);
                else _ = RefreshAsync(artwork: true);
                return;
            }
            if (Stale()) return;

            var title = props.Title ?? "";
            var key = title + "|" + props.Artist + "|" + props.AlbumTitle;
            var titleChanged = key != _key;
            if (titleChanged)
            {
                _previousArt = _thumb;
                _previousAlbum = _album;
                _key = key;
                _album = props.AlbumTitle;
                _thumb = null;
            }
            if (titleChanged || artwork)
            {
                var art = await ReadArtworkAsync(props);
                if (Stale()) return;
                _thumb = art is not null && NowPlayingRules.IsStaleArtwork(art, _previousArt, props.AlbumTitle, _previousAlbum) ? null : art;
                // The new picture often follows a moment later; look once more in case no event says so.
                if (titleChanged) _ = Task.Delay(1500).ContinueWith(_ => RefreshAsync(artwork: true), TaskScheduler.Default);
            }

            var controls = playback?.Controls;
            var state = (PlaybackState)(int)(playback?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
            switch (NowPlayingRules.Decide(title, state, controls?.IsPlayEnabled ?? false, controls?.IsPlayPauseToggleEnabled ?? false))
            {
                case NowPlayingDecision.Hide:
                    Set(null);
                    return;
                case NowPlayingDecision.Keep when Current is not null:
                    return;
            }
            var app = FriendlyApp(session.SourceAppUserModelId);
            Set(new MediaInfo(
                string.IsNullOrWhiteSpace(title) ? (string.IsNullOrEmpty(app) ? "Playing" : $"Playing in {app}") : title,
                props.Artist ?? "",
                props.AlbumTitle ?? "",
                app,
                state == PlaybackState.Playing,
                timeline.Position,
                timeline.EndTime - timeline.StartTime,
                _thumb,
                controls?.IsNextEnabled ?? false,
                controls?.IsPreviousEnabled ?? false));
        }
        catch (Exception ex)
        {
            Log.Debug("media", "Refresh failed: " + ex.Message);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<byte[]?> ReadArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail is null) return null;
        try
        {
            using var stream = await props.Thumbnail.OpenReadAsync();
            if (stream.Size is not (> 0 and < 4_000_000)) return null;
            await using var net = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private void Set(MediaInfo? info)
    {
        var current = Current;
        Current = info;
        // Only the position moved (or nothing at all): no need to redraw anything.
        if (info is null ? current is null : current is not null && info with { Position = current.Position, Duration = current.Duration } == current) return;
        Changed?.Invoke();
    }

    /// <summary>Resolves an AppUserModelID to a display name via the Start-menu catalog (e.g. Firefox's hash-like id).</summary>
    public static Func<string, string?> CatalogLookup { get; set; } = _ => null;

    public static string FriendlyApp(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        if (CatalogLookup(aumid) is { Length: > 0 } known) return known;
        var s = aumid;
        if (s.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (s.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (s.Contains("MSEdge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (s.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (s.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase)) return "Media Player";
        if (s.Contains("Tidal", StringComparison.OrdinalIgnoreCase)) return "TIDAL";
        s = s.Split('!')[0].Split('_')[0];
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9A-F]{16}$")) return "Browser";
        return Path.GetFileNameWithoutExtension(s);
    }

    public bool HasSession => _session is not null;

    public Task<bool> PlayPauseAsync() => SendAsync("toggle", s => s.TryTogglePlayPauseAsync().AsTask());
    public Task<bool> PlayAsync() => SendAsync("play", s => s.TryPlayAsync().AsTask());
    public Task<bool> PauseAsync() => SendAsync("pause", s => s.TryPauseAsync().AsTask());
    public Task<bool> NextAsync() => SendAsync("next", s => s.TrySkipNextAsync().AsTask());
    public Task<bool> PreviousAsync() => SendAsync("previous", s => s.TrySkipPreviousAsync().AsTask());

    /// <summary>A control for the current player; Now Playing then shows what it really did (a refused one may mean it's gone).</summary>
    private async Task<bool> SendAsync(string action, Func<GlobalSystemMediaTransportControlsSession, Task<bool>> send)
    {
        if (Sandbox.Intercept("media", action)) return true;
        if (_session is not { } session) return false;
        bool ok;
        try { ok = await send(session); }
        catch (COMException) { ok = false; }
        _ = Task.Delay(ok ? 700 : 0).ContinueWith(_ => { Hook(); return RefreshAsync(artwork: false); }, TaskScheduler.Default);
        return ok;
    }

    /// <summary>Sends the hardware play/pause media key (wakes a player without an active session).</summary>
    public static void SendMediaKey(byte vk = 0xB3)
    {
        if (Sandbox.Intercept("media-key", vk switch { 0xB0 => "next", 0xB1 => "previous", _ => "play-pause" })) return;
        Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
        Native.keybd_event(vk, 0, 2, UIntPtr.Zero);
    }
}

/// <summary>Default audio endpoint volume via Core Audio COM interfaces.</summary>
public sealed class VolumeController
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj);
            return (IAudioEndpointVolume)obj;
        }
        catch (Exception ex)
        {
            Log.Debug("volume", "No audio endpoint: " + ex.Message);
            return null;
        }
    }

    private int _simVolume = 50;
    private bool _simMuted;

    public int? GetVolume()
    {
        if (Sandbox.Enabled) return _simVolume;
        var ep = Endpoint();
        if (ep is null) return null;
        ep.GetMasterVolumeLevelScalar(out var level);
        return (int)Math.Round(level * 100);
    }

    public bool? IsMuted()
    {
        if (Sandbox.Enabled) return _simMuted;
        var ep = Endpoint();
        if (ep is null) return null;
        ep.GetMute(out var muted);
        return muted;
    }

    public void SetVolume(int percent)
    {
        if (Sandbox.Intercept("volume", Math.Clamp(percent, 0, 100).ToString())) { _simVolume = Math.Clamp(percent, 0, 100); if (percent > 0) _simMuted = false; return; }
        var ep = Endpoint();
        if (ep is null) return;
        var ctx = Guid.Empty;
        ep.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref ctx);
        if (percent > 0) ep.SetMute(false, ref ctx);
    }

    public void Step(int delta) => SetVolume((GetVolume() ?? 50) + delta);

    public void ToggleMute()
    {
        if (Sandbox.Intercept("mute", (!_simMuted).ToString())) { _simMuted = !_simMuted; return; }
        var ep = Endpoint();
        if (ep is null) return;
        ep.GetMute(out var muted);
        var ctx = Guid.Empty;
        ep.SetMute(!muted, ref ctx);
    }
}

/// <summary>Reads text aloud with the built-in Windows voices (on-device, offline).</summary>
public sealed class SpeechService
{
    private MediaPlayer? _player;
    private int _generation;
    public bool IsSpeaking { get; private set; }
    public event Action? StateChanged;

    public async Task SpeakAsync(string text, string language)
    {
        Stop();
        var generation = _generation;
        if (Sandbox.Intercept("speak", text.Length > 80 ? text[..80] : text))
        {
            IsSpeaking = true;
            StateChanged?.Invoke();
            return;
        }
        try
        {
            using var synth = new SpeechSynthesizer();
            var voices = SpeechSynthesizer.AllVoices;
            var voice = voices.FirstOrDefault(v => v.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                        ?? voices.FirstOrDefault(v => v.Language.StartsWith(language.Split('-')[0], StringComparison.OrdinalIgnoreCase) && v.Language.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
                        ?? voices.FirstOrDefault(v => v.Language.StartsWith(language.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            if (voice is not null) synth.Voice = voice;
            synth.Options.SpeakingRate = 1.05;
            var stream = await synth.SynthesizeTextToStreamAsync(text);
            // Stopped (or replaced) while the voice was being prepared: don't start talking now.
            if (generation != _generation) { stream.Dispose(); return; }
            _player = new MediaPlayer { Source = MediaSource.CreateFromStream(stream, stream.ContentType) };
            _player.MediaEnded += (_, _) => { IsSpeaking = false; StateChanged?.Invoke(); };
            _player.Play();
            IsSpeaking = true;
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("speech", "Text-to-speech failed", ex);
            IsSpeaking = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Stops speaking — also when nothing is playing yet (being prepared, or dry-run mode, which has no player).</summary>
    public void Stop()
    {
        _generation++;
        var player = _player;
        _player = null;
        if (player is not null) try { player.Pause(); player.Dispose(); } catch { }
        if (!IsSpeaking) return;
        IsSpeaking = false;
        StateChanged?.Invoke();
    }
}
