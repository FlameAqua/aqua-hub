using System.Net.Http;
using System.Text.Json;
using AquaHub.Core.Data;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Sources;

/// <summary>Optional, manually synced subscription catalogue. Tokens never enter settings, the database or AI context.</summary>
public sealed class YouTubeAccount : IAsyncDisposable
{
    private const string CacheKey = "account:youtube-subscriptions";
    private readonly SettingsStore _settings;
    private readonly HubDatabase _db;
    private readonly ISecretStore _secrets;
    private readonly string _clientKey, _refreshKey;
    private readonly HttpClient _http;
    private readonly GoogleYouTubeClient _client;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<TaskCompletionSource> _workers = new();
    private CancellationTokenSource? _active;
    private GoogleDesktopClient? _configuration;
    private string? _refresh;
    private GoogleYouTubeTokens? _tokens;
    private DateTimeOffset _expires;
    private int _epoch;
    private Task? _stopped, _disposed;
    private Catalogue _catalogue = new("", Array.Empty<ChannelRef>(), null, false);
    private sealed record Catalogue(string ClientId, IReadOnlyList<ChannelRef> Channels, DateTimeOffset? Synced, bool Truncated);

    public YouTubeAccount(SettingsStore settings, HubDatabase db, ISecretStore secrets, HttpClient? http = null, TimeProvider? time = null)
    {
        _settings = settings; _db = db; _secrets = secrets; _time = time ?? TimeProvider.System;
        _clientKey = CredentialKey(SecretKeys.GoogleYouTubeClient, db.Path);
        _refreshKey = CredentialKey(SecretKeys.GoogleYouTubeRefresh, db.Path);
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(25) };
        _client = new(_http);
        try
        {
            if (secrets.Get(_clientKey) is { Length: > 0 and < 1500 } json)
            {
                var config = JsonSerializer.Deserialize<GoogleDesktopClient>(json, JsonUtil.Options);
                if (config?.Valid == true) _configuration = config;
            }
            var refresh = secrets.Get(_refreshKey);
            if (_configuration is not null && GoogleYouTubeClient.ValidRefreshToken(refresh))
            {
                _refresh = refresh;
                if (db.GetJson<Catalogue>(CacheKey) is { } cached && cached.ClientId == _configuration.ClientId && cached.Channels is not null)
                    _catalogue = cached with { Channels = Clean(cached.Channels) };
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { }
        if (_refresh is null) _db.PutJson(CacheKey, _catalogue);
    }
    public bool Configured { get { lock (_sync) return _configuration is not null; } }
    public bool Connected { get { lock (_sync) return _refresh is not null; } }
    public IReadOnlyList<ChannelRef> Channels { get { lock (_sync) return Clean(_catalogue.Channels); } }
    public DateTimeOffset? LastSynced { get { lock (_sync) return _catalogue.Synced; } }
    public bool Truncated { get { lock (_sync) return _catalogue.Truncated; } }
    public event Action? Changed;
    internal static string CredentialKey(string name, string databasePath) => name + "." + Hash.Short(Path.GetFullPath(databasePath).ToUpperInvariant())[..16];
    private static ChannelRef[] Clean(IEnumerable<ChannelRef> channels) => channels.Where(c => c is not null && YouTubeStreams.ValidChannel(c.ChannelId))
        .DistinctBy(c => c.ChannelId).Take(GoogleYouTubeClient.MaximumSubscriptions).Select(c => new ChannelRef { ChannelId = c.ChannelId, Name = (c.Name ?? "")[..Math.Min(c.Name?.Length ?? 0, 200)] }).ToArray();

    public void Configure(string desktopJson)
    {
        var configuration = GoogleDesktopClient.Parse(desktopJson);
        lock (_sync)
        {
            if (_stopped is not null) throw new OperationCanceledException();
            if (_configuration == configuration) return;
            ClearConnection();
            SaveSecret(_clientKey, JsonSerializer.Serialize(configuration, JsonUtil.Options));
            _configuration = configuration;
        }
        Changed?.Invoke();
    }
    public void Cancel() { lock (_sync) { _epoch++; _active?.Cancel(); } }

    public async Task ConnectAsync(Func<Uri, Task> openBrowser, CancellationToken ct = default)
    {
        using var worker = Own();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        GoogleDesktopClient configuration; int epoch;
        lock (_sync)
        {
            configuration = _configuration ?? throw new GoogleYouTubeException("Import a Google Desktop client JSON configuration first.");
            ClearConnection();
            _active = cancellation; epoch = _epoch;
        }
        Changed?.Invoke();
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            entered = true;
            var tokens = await _client.SignInAsync(configuration, openBrowser, cancellation.Token).ConfigureAwait(false);
            lock (_sync) { Check(epoch, cancellation.Token); SaveTokens(tokens); }
            await SyncOwnedAsync(configuration, epoch, cancellation.Token).ConfigureAwait(false);
        }
        finally { Finish(cancellation, entered); }
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        using var worker = Own();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            GoogleDesktopClient configuration; int epoch;
            lock (_sync)
            {
                configuration = _configuration ?? throw new GoogleYouTubeException("Import a Google Desktop client JSON configuration first.");
                if (_refresh is null) throw new GoogleYouTubeException("Connect Google to sync your subscriptions.");
                _active = cancellation; epoch = _epoch;
            }
            await SyncOwnedAsync(configuration, epoch, cancellation.Token).ConfigureAwait(false);
        }
        finally { Finish(cancellation); }
    }
    private async Task SyncOwnedAsync(GoogleDesktopClient configuration, int epoch, CancellationToken ct)
    {
        try
        {
            GoogleYouTubeTokens? tokens; string refresh; DateTimeOffset expires;
            lock (_sync) { Check(epoch, ct); tokens = _tokens; refresh = _refresh ?? throw new OperationCanceledException(); expires = _expires; }
            async Task Renew()
            {
                tokens = await _client.RefreshAsync(configuration, refresh, ct).ConfigureAwait(false);
                lock (_sync) { Check(epoch, ct); SaveTokens(tokens); refresh = tokens.RefreshToken; }
            }
            if (tokens is null || expires <= _time.GetUtcNow().AddMinutes(1)) await Renew().ConfigureAwait(false);
            YouTubeSubscriptions subscriptions;
            try { subscriptions = await _client.SubscriptionsAsync(tokens!.AccessToken, ct).ConfigureAwait(false); }
            catch (GoogleYouTubeException ex) when (ex.Reconnect)
            {
                await Renew().ConfigureAwait(false);
                subscriptions = await _client.SubscriptionsAsync(tokens!.AccessToken, ct).ConfigureAwait(false);
            }
            lock (_sync)
            {
                Check(epoch, ct);
                _catalogue = new(configuration.ClientId, Clean(subscriptions.Channels), _time.GetUtcNow(), subscriptions.Truncated);
                _db.PutJson(CacheKey, _catalogue);
                // A partial list cannot prove that a previously selected channel was unsubscribed.
                ReconcileSelection(subscriptions.Truncated);
            }
        }
        catch (GoogleYouTubeException ex) when (ex.Reconnect)
        {
            lock (_sync) { if (epoch == _epoch) ClearConnection(); }
            throw;
        }
    }
    private void Finish(CancellationTokenSource cancellation, bool entered = true)
    {
        lock (_sync) { if (ReferenceEquals(_active, cancellation)) _active = null; }
        if (entered) _gate.Release();
        Changed?.Invoke();
    }
    private void Check(int epoch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (epoch != _epoch) throw new OperationCanceledException("Google connection changed.");
    }
    private void SaveTokens(GoogleYouTubeTokens tokens)
    {
        // Access tokens stay in memory. Each bounded refresh token fits a single Windows credential blob.
        SaveSecret(_refreshKey, tokens.RefreshToken);
        _refresh = tokens.RefreshToken; _tokens = tokens; _expires = _time.GetUtcNow().AddSeconds(tokens.ExpiresIn);
    }
    private void SaveSecret(string key, string? value)
    {
        _secrets.Set(key, value);
        if (_secrets.Get(key) != value) throw new GoogleYouTubeException("Windows couldn't update the Google connection. Check AquaHub/youtube.google entries in Credential Manager and try again.");
    }
    private void ReconcileSelection(bool keepMissing = false)
    {
        if (_settings.Current.Social.Streams.YouTubeSelection is null) return;
        var available = _settings.Current.Social.YouTubeChannels.Concat(_catalogue.Channels).DistinctBy(c => c.ChannelId).ToDictionary(c => c.ChannelId);
        _settings.Update(s => s.Social.Streams.YouTubeSelection = StreamSettings.SelectedYouTube(s.Social)
            .Where(c => keepMissing || available.ContainsKey(c.ChannelId)).Select(c => available.GetValueOrDefault(c.ChannelId) ?? c).ToList());
    }
    private void ClearConnection()
    {
        Cancel(); _refresh = null; _tokens = null;
        _catalogue = new("", Array.Empty<ChannelRef>(), null, false);
        _db.PutJson(CacheKey, _catalogue);
        ReconcileSelection();
        SaveSecret(_refreshKey, null);
    }
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        using var worker = Own();
        string? refresh;
        lock (_sync) { refresh = _refresh; ClearConnection(); }
        Changed?.Invoke();
        if (refresh is null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        await _client.RevokeAsync(refresh, cancellation.Token).ConfigureAwait(false);
    }
    private IDisposable Own()
    {
        lock (_sync)
        {
            if (_stopped is not null) throw new OperationCanceledException();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _workers.Add(completion);
            return new Worker(() => { lock (_sync) _workers.Remove(completion); completion.TrySetResult(); });
        }
    }
    private sealed class Worker(Action done) : IDisposable { public void Dispose() => done(); }
    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_stopped is not null) return _stopped;
            var tasks = _workers.Select(w => w.Task).ToArray();
            // Publish stop before cancelling, so callbacks cannot start new work.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopped = completion.Task;
            _stop.Cancel();
            _ = Task.WhenAll(tasks).ContinueWith(t => { if (t.Exception is { } error) completion.TrySetException(error.InnerExceptions); else completion.TrySetResult(); }, TaskScheduler.Default);
            return _stopped;
        }
    }
    public ValueTask DisposeAsync() { lock (_sync) return new(_disposed ??= DisposeOwnedAsync()); }
    private async Task DisposeOwnedAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose(); _stop.Dispose(); _gate.Dispose();
    }
}
