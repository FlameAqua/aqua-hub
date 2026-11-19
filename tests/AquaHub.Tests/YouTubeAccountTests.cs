using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AquaHub.Core.Agents;
using AquaHub.Core.Data;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;
using static AquaHub.Tests.StreamsTests;

namespace AquaHub.Tests;

public sealed class YouTubeAccountTests
{
    private const string Id = "123456-fixture.apps.googleusercontent.com";
    private const string Config = "{\"installed\":{\"client_id\":\"" + Id + "\",\"client_secret\":\"fixture-secret\",\"token_uri\":\"https://evil.example/\"}}";
    private const string List = "{\"items\":[{\"snippet\":{\"title\":\"Fixture subscription\",\"resourceId\":{\"channelId\":\"" + ChannelId + "\"}}}]}";
    private static string Tokens(string refresh = "refresh-fixture") => JsonSerializer.Serialize(new { access_token = "access-fixture", refresh_token = refresh, expires_in = 3600, token_type = "Bearer", scope = GoogleYouTubeClient.Scope });
    private static Dictionary<string, string> Query(string value) => value.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    private static InMemorySecretStore Secrets(HubDatabase db)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(YouTubeAccount.CredentialKey(SecretKeys.GoogleYouTubeClient, db.Path), JsonSerializer.Serialize(new GoogleDesktopClient(Id, "fixture-secret"), JsonUtil.Options));
        secrets.Set(YouTubeAccount.CredentialKey(SecretKeys.GoogleYouTubeRefresh, db.Path), "refresh-fixture");
        return secrets;
    }

    [Theory]
    [InlineData("{\"web\":{\"client_id\":\"123-fixture.apps.googleusercontent.com\"}}")]
    [InlineData("{\"installed\":{\"client_id\":\"https://evil.example\"}}")]
    [InlineData("{\"installed\":{\"client_id\":\"123-fixture.apps.googleusercontent.com\",\"client_secret\":\"bad\\r\\nsecret\"}}")]
    [InlineData("not json")]
    public void OnlyDesktopClientConfigurationIsAccepted(string json) => Assert.Throws<GoogleYouTubeException>(() => GoogleDesktopClient.Parse(json));

    [Fact]
    public async Task BrowserSignInUsesPkceStateAndFixedEndpoints()
    {
        Dictionary<string, string>? auth = null;
        using var handler = new RouteHandler(async (request, _) =>
        {
            Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.AbsoluteUri);
            var form = Query(await request.Content!.ReadAsStringAsync());
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("fixture-code", form["code"]);
            Assert.Equal("fixture-secret", form["client_secret"]);
            Assert.Equal(auth!["redirect_uri"], form["redirect_uri"]);
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(auth["code_challenge"], challenge);
            return Json(Tokens());
        });
        using var providerHttp = new HttpClient(handler);
        using var browser = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        Task<HttpResponseMessage>? callback = null;
        var result = await new GoogleYouTubeClient(providerHttp).SignInAsync(GoogleDesktopClient.Parse(Config), uri =>
        {
            Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", uri.GetLeftPart(UriPartial.Path));
            auth = Query(uri.Query);
            Assert.Equal(GoogleYouTubeClient.Scope, auth["scope"]);
            Assert.Equal("S256", auth["code_challenge_method"]);
            Assert.Equal("offline", auth["access_type"]);
            Assert.Equal("127.0.0.1", new Uri(auth["redirect_uri"]).Host);
            Assert.False(auth.ContainsKey("client_secret"));
            callback = browser.GetAsync(auth["redirect_uri"] + "?state=" + auth["state"] + "&code=fixture-code");
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        using var response = await callback!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("refresh-fixture", result.RefreshToken);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task TokenTransportFailureDoesNotWaitForAnotherBrowserCallback()
    {
        using var handler = new RouteHandler((_, _) => throw new IOException("fixture token transport failure"));
        using var http = new HttpClient(handler);
        using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<HttpResponseMessage>? callback = null;
        await Assert.ThrowsAsync<IOException>(() => new GoogleYouTubeClient(http).SignInAsync(GoogleDesktopClient.Parse(Config), uri =>
        {
            var auth = Query(uri.Query);
            callback = browser.GetAsync(auth["redirect_uri"] + "?state=" + auth["state"] + "&code=fixture-code");
            return Task.CompletedTask;
        }, cancellation.Token));
        using var response = await callback!;
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("/oauth2/callback/?state=wrong&code=code")]
    [InlineData("/oauth2/callback/?state=expected&state=expected&code=code")]
    [InlineData("/oauth2/callback/?state=expected&code=code&code=other")]
    [InlineData("/other?state=expected&code=code")]
    [InlineData("//evil.example/oauth2/callback/?state=expected&code=code")]
    [InlineData("/oauth2/callback/?state=expected&code=code#error")]
    public void CallbackRejectsForgeryAndAmbiguity(string target) => Assert.False(GoogleYouTubeClient.TryCallback(new("http://127.0.0.1:12345/oauth2/callback/"), target, "expected", out _, out _));

    [Fact]
    public async Task SubscriptionsPaginateDeduplicateAndKeepAuthOutOfUrls()
    {
        using var handler = new RouteHandler((request, _) =>
        {
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.DoesNotContain("access-fixture", request.RequestUri.Query);
            Assert.Contains("mine=true", request.RequestUri.Query);
            return Task.FromResult(Json(request.RequestUri.Query.Contains("pageToken=") ? List : List[..^1] + ",\"nextPageToken\":\"next + page\"}"));
        });
        using var http = new HttpClient(handler);
        var result = await new GoogleYouTubeClient(http).SubscriptionsAsync("access-fixture", CancellationToken.None);
        Assert.Equal(ChannelId, Assert.Single(result.Channels).ChannelId);
        Assert.False(result.Truncated); Assert.Equal(2, handler.Requests);
    }
    [Fact]
    public async Task SubscriptionPaginationHasAHardBound()
    {
        var count = 0;
        using var handler = new RouteHandler((_, _) => Task.FromResult(Json(List[..^1] + ",\"nextPageToken\":\"page" + ++count + "\"}")));
        using var http = new HttpClient(handler);
        var result = await new GoogleYouTubeClient(http).SubscriptionsAsync("access-fixture", CancellationToken.None);
        Assert.True(result.Truncated); Assert.Equal(20, handler.Requests);
    }
    [Fact]
    public async Task SyncRefreshesCredentialsAndCachesMetadataWithoutAccessTokens()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var db = new HubDatabase(Path.Combine(dir.Path, "hub.db")); var secrets = Secrets(db);
        using var handler = new RouteHandler((request, _) => Task.FromResult(Json(request.RequestUri!.Host == "oauth2.googleapis.com" ? Tokens("rotated-refresh") : List)));
        await using (var account = new YouTubeAccount(settings, db, secrets, new HttpClient(handler)))
        {
            await account.SyncAsync();
            Assert.Equal(ChannelId, Assert.Single(account.Channels).ChannelId);
            Assert.NotNull(account.LastSynced);
            Assert.Equal("rotated-refresh", secrets.Get(YouTubeAccount.CredentialKey(SecretKeys.GoogleYouTubeRefresh, db.Path)));
            Assert.DoesNotContain("access-fixture", File.ReadAllText(Path.Combine(dir.Path, "settings.json")));
            Assert.DoesNotContain("access-fixture", db.GetJson<JsonElement>("account:youtube-subscriptions").GetRawText());
        }
        await using var restarted = new YouTubeAccount(settings, db, secrets);
        Assert.True(restarted.Connected); Assert.Single(restarted.Channels); Assert.Equal(2, handler.Requests);
    }
    [Fact]
    public async Task FailedSyncPreservesLastSuccessfulListAndTime()
    {
        using var dir = new TempDir(); var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var db = new HubDatabase(Path.Combine(dir.Path, "hub.db")); var fail = false;
        using var handler = new RouteHandler((request, _) => Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") } : Json(request.RequestUri!.Host == "oauth2.googleapis.com" ? Tokens() : List)));
        await using var account = new YouTubeAccount(settings, db, Secrets(db), new HttpClient(handler));
        await account.SyncAsync(); var timestamp = account.LastSynced;
        fail = true; await Assert.ThrowsAsync<GoogleYouTubeException>(() => account.SyncAsync());
        Assert.True(account.Connected); Assert.Single(account.Channels); Assert.Equal(timestamp, account.LastSynced);
    }
    [Fact]
    public async Task DisconnectRejectsLateSyncAndRemovesOnlyImportedSelections()
    {
        using var dir = new TempDir(); var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        settings.Update(s => { s.Social.YouTubeChannels.Clear(); s.Social.Streams.YouTubeSelection = new() { new() { ChannelId = ChannelId, Name = "Private selection" } }; });
        var db = new HubDatabase(Path.Combine(dir.Path, "hub.db")); var secrets = Secrets(db);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RouteHandler(async (request, _) =>
        {
            if (request.RequestUri!.Host == "www.googleapis.com") { started.TrySetResult(); await release.Task; return Json(List); }
            return Json(request.RequestUri.AbsolutePath == "/revoke" ? "{}" : Tokens());
        });
        await using var account = new YouTubeAccount(settings, db, secrets, new HttpClient(handler));
        var sync = account.SyncAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await account.DisconnectAsync(); release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        Assert.False(account.Connected); Assert.Empty(account.Channels); Assert.Empty(settings.Current.Social.Streams.YouTubeSelection!);
        Assert.Null(secrets.Get(YouTubeAccount.CredentialKey(SecretKeys.GoogleYouTubeRefresh, db.Path)));
        Assert.NotNull(secrets.Get(YouTubeAccount.CredentialKey(SecretKeys.GoogleYouTubeClient, db.Path)));
    }
    [Fact]
    public async Task RejectedRefreshRequiresReconnectAndClearsPrivateCatalogue()
    {
        using var dir = new TempDir(); var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json")); var db = new HubDatabase(Path.Combine(dir.Path, "hub.db"));
        var secrets = Secrets(db);
        using var handler = new RouteHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") }));
        await using var account = new YouTubeAccount(settings, db, secrets, new HttpClient(handler));
        Assert.True((await Assert.ThrowsAsync<GoogleYouTubeException>(() => account.SyncAsync())).Reconnect);
        Assert.False(account.Connected); Assert.Empty(account.Channels); Assert.Null(secrets.Get(YouTubeAccount.CredentialKey(SecretKeys.GoogleYouTubeRefresh, db.Path)));
    }
    [Fact]
    public async Task StopCancelsAndJoinsBrowserCallbackListener()
    {
        using var dir = new TempDir(); var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json")); var db = new HubDatabase(Path.Combine(dir.Path, "hub.db"));
        var secrets = new InMemorySecretStore();
        await using var account = new YouTubeAccount(settings, db, secrets);
        account.Configure(Config);
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connecting = account.ConnectAsync(uri => { opened.TrySetResult(uri); return Task.CompletedTask; });
        var auth = Query((await opened.Task.WaitAsync(TimeSpan.FromSeconds(5))).Query);
        var stopped = account.StopAsync(); Assert.Same(stopped, account.StopAsync());
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.False(account.Connected); Assert.Null(secrets.Get(YouTubeAccount.CredentialKey(SecretKeys.GoogleYouTubeRefresh, db.Path)));
        // Rebinding the exact owned port proves the listener released it, independently of firewall/connect timeout behaviour.
        using var rebound = new System.Net.Sockets.TcpListener(IPAddress.Loopback, new Uri(auth["redirect_uri"]).Port) { ExclusiveAddressUse = true };
        rebound.Start();
    }
    [Fact]
    public async Task SelectedSubscriptionsDriveLiveChecksAndEmptySelectionSurvivesRestart()
    {
        using var dir = new TempDir(); var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        Assert.Null(settings.Current.Social.Streams.YouTubeSelection);
        Assert.Single(StreamSettings.SelectedYouTube(settings.Current.Social));
        settings.Update(s => { s.Social.Streams.YouTube = true; s.Social.Streams.YouTubeSelection = new() { new() { ChannelId = ChannelId, Name = "Selected subscription" } }; });
        using var handler = new RouteHandler((request, _) => { Assert.Contains(ChannelId, request.RequestUri!.AbsolutePath); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page()) }); });
        using var fetcher = new HttpFetcher(null, handler);
        var state = new HubState(null);
        await using var streams = new StreamsSession(settings, state, fetcher, new InMemorySecretStore());
        await streams.RefreshAsync(force: true);
        Assert.Single(Assert.Single(state.Streams.Sources).Items);
        settings.Update(s => s.Social.Streams.YouTubeSelection = new());
        Assert.Empty(state.Streams.Sources);
        Assert.Empty(StreamSettings.SelectedYouTube(new SettingsStore(Path.Combine(dir.Path, "settings.json")).Current.Social));
    }

    [Fact]
    public async Task GoogleCredentialsAndCatalogueDoNotCrossProfiles()
    {
        using var dir = new TempDir();
        var first = new HubDatabase(Path.Combine(dir.Path, "first", "hub.db"));
        var second = new HubDatabase(Path.Combine(dir.Path, "second", "hub.db"));
        var secrets = Secrets(first);
        using var handler = new RouteHandler((request, _) => Task.FromResult(Json(request.RequestUri!.Host == "oauth2.googleapis.com" ? Tokens() : List)));
        await using var account = new YouTubeAccount(new SettingsStore(Path.Combine(dir.Path, "first", "settings.json")), first, secrets, new HttpClient(handler));
        await account.SyncAsync();
        await using var other = new YouTubeAccount(new SettingsStore(Path.Combine(dir.Path, "second", "settings.json")), second, secrets);
        Assert.False(other.Configured); Assert.False(other.Connected); Assert.Empty(other.Channels);
        Assert.Single(account.Channels);
    }

    [Fact]
    public async Task CredentialWriteFailureIsReportedAndDoesNotPretendConfigured()
    {
        using var dir = new TempDir();
        var db = new HubDatabase(Path.Combine(dir.Path, "hub.db"));
        await using var account = new YouTubeAccount(new SettingsStore(Path.Combine(dir.Path, "settings.json")), db, new RefusingSecrets());
        var error = Assert.Throws<GoogleYouTubeException>(() => account.Configure(Config));
        Assert.Contains("Windows couldn't update", error.Message);
        Assert.False(account.Configured); Assert.False(account.Connected);
    }
    private sealed class RefusingSecrets : ISecretStore
    {
        public string? Get(string key) => null;
        public void Set(string key, string? value) { }
    }

    [Theory]
    [InlineData(302, "{}")]
    [InlineData(200, "{\"items\":false}")]
    [InlineData(200, "{\"items\":[],\"nextPageToken\":\"repeat\"}")]
    public async Task SubscriptionProtocolFailuresDoNotReturnAFalseEmptySuccess(int status, string body)
    {
        using var handler = new RouteHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<GoogleYouTubeException>(() => new GoogleYouTubeClient(http).SubscriptionsAsync("access-fixture", CancellationToken.None));
    }
}
