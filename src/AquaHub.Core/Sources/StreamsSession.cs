using System.Net.Http;
using System.Text.Json;
using AquaHub.Core.Agents;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Sources;

/// <summary>Owns stream refresh/sign-in work, rotating Twitch credentials and bounded public-page observations.</summary>
public sealed class StreamsSession : IAsyncDisposable
{
    public const int MaxYouTubeChannels = 6;
    public static readonly TimeSpan YouTubeInterval = TimeSpan.FromMinutes(15);
    private readonly SettingsStore _settings;
    private readonly HubState _state;
    private readonly HttpFetcher _http;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _authHttp;
    private readonly TwitchClient _twitch;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refresh = new(1, 1), _oauth = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<TaskCompletionSource> _workers = new();
    private CancellationTokenSource? _connecting;
    private CancellationTokenSource? _viewRefresh;
    private bool _stopping, _visible;
    private int _version, _authVersion;
    private string _configuration;
    private string _clientId;
    private int _manualCheck;
    private Connection? _connection;
    private Task? _stopped, _disposed;
    private sealed record Connection(string ClientId, TwitchTokens Tokens, TwitchIdentity Identity, DateTimeOffset Expires, DateTimeOffset Validated);

    public StreamsSession(SettingsStore settings, HubState state, HttpFetcher http, ISecretStore secrets,
        HttpClient? authHttp = null, TimeProvider? time = null)
    {
        _settings = settings; _state = state; _http = http; _secrets = secrets;
        _time = time ?? TimeProvider.System;
        _authHttp = authHttp ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(25) };
        _twitch = new TwitchClient(_authHttp, _time);
        _clientId = settings.Current.Social.Streams.TwitchClientId;
        _configuration = Configuration(settings.Current);
        try
        {
            if (secrets.Get(SecretKeys.TwitchConnection) is { Length: > 0 and < 6000 } saved)
                _connection = JsonSerializer.Deserialize<Connection>(saved, JsonUtil.Options);
            if (_connection?.ClientId != _clientId || _connection?.Identity is not { UserId.Length: > 0, Login.Length: > 0 } ||
                !TwitchClient.ValidToken(_connection?.Tokens?.AccessToken) || !TwitchClient.ValidToken(_connection?.Tokens?.RefreshToken)) _connection = null;
            else if (_connection is not null) _connection = _connection with { Validated = DateTimeOffset.MinValue };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { _connection = null; }
        _settings.Changed += OnSettings;
        Prune();
    }

    public string? TwitchLogin { get { lock (_sync) return _connection?.Identity.Login; } }
    public bool Visible
    {
        get { lock (_sync) return _visible; }
        set { lock (_sync) { _visible = value; if (!value) { try { _viewRefresh?.Cancel(); } catch (ObjectDisposedException) { } } } }
    }
    public bool Enabled => _settings.Current.Social.Streams is { } s && (s.Twitch || s.YouTube);
    public void RequestCheck() => Interlocked.Exchange(ref _manualCheck, 1);
    public bool TakeRequestedCheck() => Interlocked.Exchange(ref _manualCheck, 0) != 0;
    public event Action? Changed;

    private static string Configuration(HubSettings s) => JsonSerializer.Serialize(new { s.Social.Streams, Channels = s.Social.YouTubeChannels.Select(c => new { c.ChannelId, c.Name }) });
    private void OnSettings(HubSettings settings)
    {
        var configuration = Configuration(settings);
        lock (_sync)
        {
            if (configuration == _configuration) return;
            _configuration = configuration;
            _version++;
            if (_clientId != settings.Social.Streams.TwitchClientId)
            {
                _clientId = settings.Social.Streams.TwitchClientId;
                ForgetConnection();
            }
            if (!settings.Social.Streams.Twitch) CancelConnect();
            Prune();
        }
        Changed?.Invoke();
    }

    private void Prune()
    {
        var s = _settings.Current.Social;
        var ids = StreamSettings.SelectedYouTube(s).Select(c => "youtube:" + c.ChannelId).ToHashSet(StringComparer.Ordinal);
        var keep = _state.Streams.Sources.Take(MaxYouTubeChannels + 1).Where(o => o.Platform == "twitch" ? s.Streams.Twitch && _connection is not null && o.SourceId == "twitch:following" : o.Platform == "youtube" && s.Streams.YouTube && ids.Contains(o.SourceId))
            .Select(o => o with { Items = o.Items.Where(LiveStream.IsValid).Take(100).ToArray() }).ToArray();
        if (keep.Length != _state.Streams.Sources.Count || keep.SelectMany(o => o.Items).Count() != _state.Streams.Sources.Sum(o => o.Items.Count)) _state.SetStreams(new(keep));
    }

    public void CancelConnect()
    {
        lock (_sync)
        {
            _authVersion++;
            try { _connecting?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public async Task ConnectAsync(Func<TwitchDeviceCode, Task> showCode, CancellationToken ct = default)
    {
        using var operation = Own();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        string clientId;
        int version;
        lock (_sync)
        {
            CancelConnect();
            _connecting = cancellation;
            version = _authVersion;
            clientId = _clientId;
            ClearTwitch();
        }
        var entered = false;
        try
        {
            await _oauth.WaitAsync(cancellation.Token).ConfigureAwait(false);
            entered = true;
            var code = await _twitch.BeginAsync(clientId, cancellation.Token).ConfigureAwait(false);
            await showCode(code).ConfigureAwait(false);
            var tokens = await _twitch.CompleteAsync(clientId, code, cancellation.Token).ConfigureAwait(false);
            var identity = await _twitch.ValidateAsync(clientId, tokens.AccessToken, cancellation.Token).ConfigureAwait(false);
            SaveConnection(new(clientId, tokens, identity, _time.GetUtcNow().AddSeconds(tokens.ExpiresIn), _time.GetUtcNow()), version, cancellation.Token);
        }
        finally
        {
            if (entered) _oauth.Release();
            lock (_sync) { if (ReferenceEquals(_connecting, cancellation)) _connecting = null; }
            Changed?.Invoke();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        using var operation = Own();
        Connection? before;
        lock (_sync) { before = _connection; _version++; ForgetConnection(); }
        Changed?.Invoke();
        if (before is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        await _twitch.RevokeAsync(before.ClientId, before.Tokens.AccessToken, cancellation.Token).ConfigureAwait(false);
    }

    private void ForgetConnection()
    {
        CancelConnect();
        _connection = null;
        ClearTwitch();
        _secrets.Set(SecretKeys.TwitchConnection, null);
        if (_secrets.Get(SecretKeys.TwitchConnection) is { Length: > 0 })
            throw new TwitchException("Windows couldn't remove the saved Twitch connection. Remove AquaHub/twitch.connection in Credential Manager.");
    }
    private void ClearTwitch() => _state.SetStreams(new(_state.Streams.Sources.Where(s => s.Platform != "twitch").ToArray()));
    private void SaveConnection(Connection connection, int version, CancellationToken ct)
    {
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            if (version != _authVersion || connection.ClientId != _clientId) throw new OperationCanceledException("Twitch connection changed.");
            var json = JsonSerializer.Serialize(connection, JsonUtil.Options);
            if (json.Length > 1200) throw new TwitchException("Twitch connection data exceeds Windows credential storage limits. Connect again.");
            _secrets.Set(SecretKeys.TwitchConnection, json);
            if (_secrets.Get(SecretKeys.TwitchConnection) != json) throw new TwitchException("Windows couldn't save the Twitch connection. Check Credential Manager and connect again.");
            _connection = connection;
        }
    }

    private async Task<Connection> AuthoriseAsync(CancellationToken ct)
    {
        await _oauth.WaitAsync(ct).ConfigureAwait(false);
        var version = -1;
        try
        {
            Connection connection;
            lock (_sync) { connection = _connection ?? throw new TwitchException("Connect Twitch to see live channels you follow.", true); version = _authVersion; }
            async Task Renew()
            {
                var tokens = await _twitch.RefreshAsync(connection.ClientId, connection.Tokens.RefreshToken, ct).ConfigureAwait(false);
                connection = connection with { Tokens = tokens, Expires = _time.GetUtcNow().AddSeconds(tokens.ExpiresIn), Validated = DateTimeOffset.MinValue };
                // Public-client refresh tokens rotate and are single-use. Persist the replacement before another HTTP request.
                SaveConnection(connection, version, ct);
            }
            if (connection.Expires <= _time.GetUtcNow().AddMinutes(1)) await Renew().ConfigureAwait(false);
            if (_time.GetUtcNow() - connection.Validated >= TimeSpan.FromHours(1))
            {
                TwitchIdentity identity;
                try { identity = await _twitch.ValidateAsync(connection.ClientId, connection.Tokens.AccessToken, ct).ConfigureAwait(false); }
                catch (TwitchException ex) when (ex.Reconnect)
                {
                    await Renew().ConfigureAwait(false);
                    identity = await _twitch.ValidateAsync(connection.ClientId, connection.Tokens.AccessToken, ct).ConfigureAwait(false);
                }
                connection = connection with { Identity = identity, Validated = _time.GetUtcNow() };
                SaveConnection(connection, version, ct);
            }
            return connection;
        }
        catch (TwitchException ex) when (ex.Reconnect)
        {
            lock (_sync) { if (version >= 0 && version == _authVersion) { _version++; ForgetConnection(); } }
            Changed?.Invoke();
            throw;
        }
        finally { _oauth.Release(); }
    }

    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        using var operation = Own();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _refresh.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            var s = _settings.Current.Social;
            int version;
            lock (_sync) { version = _version; if (!_visible && !force) return; _viewRefresh = cancellation; }
            var observations = _state.Streams.Sources.ToDictionary(o => o.SourceId, StringComparer.Ordinal);
            var now = _time.GetUtcNow();
            async Task Observe(string id, string platform, string label, TimeSpan interval, Func<Task<IReadOnlyList<LiveStream>>> fetch)
            {
                observations.TryGetValue(id, out var previous);
                if (!_state.Sources.Due(id, now)) return;
                var minimum = force ? TimeSpan.FromMinutes(1) : interval;
                if ((previous?.Attempted ?? previous?.Checked) is { } checkedAt && now - checkedAt < minimum) return;
                try
                {
                    observations[id] = new(id, platform, label, now, true, "", await fetch().ConfigureAwait(false)) { Attempted = now };
                    _state.Sources.Record("streams", new(id, true, false, new(), null), now);
                }
                catch (Exception ex) when (ex is TwitchException or StreamPageException or HttpRequestException or JsonException or InvalidOperationException || ex is OperationCanceledException && !cancellation.IsCancellationRequested)
                {
                    observations[id] = new(id, platform, label, previous?.Checked, false,
                        ex is TwitchException or StreamPageException ? ex.Message : "Couldn't check this source. Aqua will try again later.", previous?.Items ?? Array.Empty<LiveStream>()) { Attempted = now };
                    _state.Sources.Record("streams", new(id, false, false, new(), observations[id].Message) { HttpCode = ex is StreamPageException page ? page.HttpCode : 0 }, now);
                }
            }
            if (s.Streams.Twitch)
                await Observe("twitch:following", "twitch", "Twitch", TimeSpan.FromMinutes(1), async () =>
                {
                    var connection = await AuthoriseAsync(cancellation.Token).ConfigureAwait(false);
                    try { return await _twitch.FollowedAsync(connection.ClientId, connection.Identity.UserId, connection.Tokens.AccessToken, cancellation.Token).ConfigureAwait(false); }
                    catch (TwitchException ex) when (ex.Reconnect)
                    {
                        lock (_sync)
                            if (_connection == connection) _connection = connection with { Expires = DateTimeOffset.MinValue, Validated = DateTimeOffset.MinValue };
                        connection = await AuthoriseAsync(cancellation.Token).ConfigureAwait(false);
                        return await _twitch.FollowedAsync(connection.ClientId, connection.Identity.UserId, connection.Tokens.AccessToken, cancellation.Token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            if (s.Streams.YouTube)
                foreach (var channel in StreamSettings.SelectedYouTube(s))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await Observe("youtube:" + channel.ChannelId, "youtube", channel.Name, YouTubeInterval, async () =>
                    {
                        if (!YouTubeStreams.ValidChannel(channel.ChannelId)) throw new StreamPageException("Add a valid YouTube channel link in Sources.");
                        var result = await _http.GetAsync("https://www.youtube.com/channel/" + channel.ChannelId + "/streams?hl=en",
                            maxBytes: YouTubeStreams.MaximumBytes, headers: YouTubeChannels.PageHeaders, ct: cancellation.Token).ConfigureAwait(false);
                        if (!result.Ok) throw new StreamPageException("YouTube couldn't be checked. The last observation is kept; try again later.", result.HttpCode);
                        return YouTubeStreams.Parse(result.Text, channel);
                    }).ConfigureAwait(false);
                }
            lock (_sync)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (version == _version) { _state.SetStreams(new(observations.Values.Take(MaxYouTubeChannels + 1).ToArray())); Prune(); }
            }
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_viewRefresh, cancellation)) _viewRefresh = null; }
            _refresh.Release();
        }
    }

    private IDisposable Own()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) { if (_stopping) throw new OperationCanceledException("Stream services are stopping."); _workers.Add(completion); }
        return new Worker(() => { lock (_sync) _workers.Remove(completion); completion.TrySetResult(); });
    }
    private sealed class Worker(Action done) : IDisposable { public void Dispose() => done(); }
    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_stopped is not null) return _stopped;
            _stopping = true;
            var tasks = _workers.Select(w => w.Task).ToArray();
            _stop.Cancel();
            return _stopped = Task.WhenAll(tasks);
        }
    }
    public ValueTask DisposeAsync() { lock (_sync) return new(_disposed ??= DisposeOwnedAsync()); }
    private async Task DisposeOwnedAsync()
    {
        _settings.Changed -= OnSettings;
        await StopAsync().ConfigureAwait(false);
        _authHttp.Dispose();
        _stop.Dispose();
        _oauth.Dispose(); _refresh.Dispose();
    }
}
