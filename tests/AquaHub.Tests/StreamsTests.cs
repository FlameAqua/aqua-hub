using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

public sealed class StreamsTests
{
    internal const string ChannelId = "UCabcdefghijklmnopqrstuv";
    private static ChannelRef Channel => new() { ChannelId = ChannelId, Name = "Fixture channel" };
    internal static string Page(bool live = true, bool modern = true)
    {
        JsonObject row = modern ? new()
        {
            ["lockupViewModel"] = new JsonObject
            {
                ["contentId"] = "abcDEF123_-", ["contentType"] = "LOCKUP_CONTENT_TYPE_VIDEO",
                ["metadata"] = new JsonObject { ["lockupMetadataViewModel"] = new JsonObject { ["title"] = new JsonObject { ["content"] = "Fixture &amp; broadcast" } } },
                ["contentImage"] = new JsonObject { ["thumbnailViewModel"] = new JsonObject { ["overlays"] = new JsonArray(new JsonObject
                { ["thumbnailBadgeViewModel"] = new JsonObject { ["badgeStyle"] = live ? "THUMBNAIL_OVERLAY_BADGE_STYLE_LIVE" : "THUMBNAIL_OVERLAY_BADGE_STYLE_DEFAULT", ["text"] = "LIVE" } }) } },
            },
        } : new()
        {
            ["videoRenderer"] = new JsonObject
            {
                ["videoId"] = "abcDEF123_-", ["title"] = new JsonObject { ["simpleText"] = "Fixture &amp; broadcast" },
                ["badges"] = new JsonArray(new JsonObject { ["metadataBadgeRenderer"] = new JsonObject { ["style"] = live ? "BADGE_STYLE_TYPE_LIVE_NOW" : "BADGE_STYLE_TYPE_SIMPLE", ["label"] = "LIVE" } }),
            },
        };
        var document = new JsonObject
        {
            ["metadata"] = new JsonObject { ["channelMetadataRenderer"] = new JsonObject { ["externalId"] = ChannelId } },
            ["contents"] = new JsonObject { ["twoColumnBrowseResultsRenderer"] = new JsonObject { ["tabs"] = new JsonArray(new JsonObject
            {
                ["tabRenderer"] = new JsonObject
                {
                    ["endpoint"] = new JsonObject { ["commandMetadata"] = new JsonObject { ["webCommandMetadata"] = new JsonObject { ["url"] = "/@fixture/streams" } } },
                    ["selected"] = true,
                    ["content"] = new JsonObject { ["richGridRenderer"] = new JsonObject { ["contents"] = new JsonArray(row) } },
                },
            }) } },
        };
        return "<script>var ytInitialData = " + document.ToJsonString() + ";</script>";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void YouTubeRequiresLiveBadgeAndConstructsCanonicalUrl(bool modern)
    {
        var item = Assert.Single(YouTubeStreams.Parse(Page(modern: modern), Channel));
        Assert.Equal("Fixture & broadcast", item.Title);
        Assert.Equal("https://www.youtube.com/watch?v=abcDEF123_-", item.Url);
        Assert.True(LiveStream.IsValid(item));
        Assert.Empty(YouTubeStreams.Parse(Page(live: false, modern: modern), Channel)); // A label saying LIVE is insufficient.
    }

    [Fact]
    public void YouTubeRejectsChangedWrongChannelAndOversizedPages()
    {
        Assert.Throws<StreamPageException>(() => YouTubeStreams.Parse("Consent required", Channel));
        Assert.Throws<StreamPageException>(() => YouTubeStreams.Parse(Page().Replace(ChannelId, "UCxxxxxxxxxxxxxxxxxxxxxx", StringComparison.Ordinal), Channel));
        Assert.Throws<StreamPageException>(() => YouTubeStreams.Parse(new string('x', YouTubeStreams.MaximumBytes + 1), Channel));
        Assert.Throws<StreamPageException>(() => YouTubeStreams.Parse(Page().Replace("\"selected\":true", "\"selected\":false", StringComparison.Ordinal), Channel));
        Assert.Throws<StreamPageException>(() => YouTubeStreams.Parse(Page().Replace("richGridRenderer", "unknownGrid", StringComparison.Ordinal), Channel));
    }

    [Theory]
    [InlineData("https://evil.example/watch?v=abcDEF123_-", "youtube:abcDEF123_-", "youtube")]
    [InlineData("https://www.youtube.com/watch?v=abcDEF123_-&redirect=evil", "youtube:abcDEF123_-", "youtube")]
    [InlineData("file:///C:/fixture.exe", "twitch:1", "twitch")]
    [InlineData("https://www.twitch.tv@evil.example/fixture", "twitch:1", "twitch")]
    [InlineData("https://www.twitch.tv/fixture?redirect=evil", "twitch:1", "twitch")]
    public void PersistedStreamLinksCannotEscapeProvider(string url, string id, string platform) =>
        Assert.False(LiveStream.IsValid(new(id, platform, "123", "channel", "title", url)));

    [Fact]
    public void SnapshotDropsMalformedLinksAndBoundsGrowth()
    {
        var bad = new LiveStream("twitch:1", "twitch", "123", "channel", "title", "https://evil.example/");
        var good = bad with { Url = "https://www.twitch.tv/fixture" };
        var sources = Enumerable.Range(0, 12).Select(i => new StreamObservation("source:" + i, "twitch", "fixture", null, true, "", new[] { good, bad, good })).ToArray();
        var cleaned = StreamsSnapshot.Clean(new(sources));
        Assert.Equal(7, cleaned.Sources.Count);
        Assert.All(cleaned.Sources, source => Assert.Equal(good, Assert.Single(source.Items)));
    }

    [Fact]
    public async Task TwitchDeviceFlowUsesPublicClientAndRejectsForeignActivationAddress()
    {
        using var handler = new RouteHandler(async (request, _) =>
        {
            Assert.Equal("https://id.twitch.tv/oauth2/device", request.RequestUri!.AbsoluteUri);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("client_id=fixture", body);
            Assert.Contains("user%3Aread%3Afollows", body);
            Assert.DoesNotContain("secret", body);
            return Json("{\"device_code\":\"device\",\"user_code\":\"ABC\",\"verification_uri\":\"https://evil.example/activate\",\"expires_in\":60,\"interval\":5}");
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TwitchException>(() => new TwitchClient(http).BeginAsync("fixture", default));
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("other", true)]
    [InlineData("fixture", false)]
    public async Task TwitchValidationRequiresMatchingClientAndReadFollows(string clientId, bool scope)
    {
        using var handler = new RouteHandler((request, _) =>
        {
            Assert.Equal("OAuth", request.Headers.Authorization!.Scheme);
            Assert.Equal("access", request.Headers.Authorization.Parameter);
            return Task.FromResult(Json(JsonSerializer.Serialize(new { client_id = clientId, scopes = scope ? new[] { TwitchClient.Scope } : Array.Empty<string>(), user_id = "123", login = "fixture", expires_in = 3600 })));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TwitchException>(() => new TwitchClient(http).ValidateAsync("fixture", "access", default));
        Assert.True(error.Reconnect);
    }

    [Fact]
    public async Task TwitchPendingAndSlowDownRespectServerIntervals()
    {
        var clock = new StreamClock();
        var replies = 0;
        using var handler = new RouteHandler((_, _) => Task.FromResult(Json(Interlocked.Increment(ref replies) switch
        {
            1 => "{\"message\":\"authorization_pending\"}",
            2 => "{\"message\":\"slow_down\"}",
            _ => "{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600}",
        })));
        handler.Status = count => count < 3 ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
        using var http = new HttpClient(handler);
        var pending = new TwitchClient(http, clock).CompleteAsync("fixture", new("device", "ABC", new("https://www.twitch.tv/activate"), 60, 5), default);
        await clock.WaitForTimers(2); clock.Advance(TimeSpan.FromSeconds(5));
        await clock.WaitForTimers(3); clock.Advance(TimeSpan.FromSeconds(5));
        await clock.WaitForTimers(4); clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(2, handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("access", (await pending.WaitAsync(TimeSpan.FromSeconds(5))).AccessToken);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task TwitchCancellationDoesNotPollOrLeakARequest()
    {
        using var handler = new RouteHandler((_, _) => throw new InvalidOperationException("No network expected"));
        using var http = new HttpClient(handler);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TwitchClient(http).CompleteAsync("fixture", new("device", "ABC", new("https://www.twitch.tv/activate"), 60, 5), cancel.Token));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task YouTubeCacheSurvivesFailureAndDisablingRemovesIt()
    {
        using var directory = new TempDir();
        var settings = new SettingsStore(Path.Combine(directory.Path, "settings.json"));
        settings.Update(s => { s.Social.Streams.YouTube = true; s.Social.YouTubeChannels = new() { Channel }; });
        var clock = new StreamClock();
        var fail = false;
        using var pageHandler = new RouteHandler((_, _) => Task.FromResult(fail ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(Page())));
        using var http = new HttpFetcher(null, pageHandler);
        var state = new HubState(null);
        await using var session = new StreamsSession(settings, state, http, new InMemorySecretStore(), time: clock);
        session.Visible = true;
        await session.RefreshAsync();
        var observed = Assert.Single(state.Streams.Sources);
        Assert.True(observed.Ok); Assert.Single(observed.Items);
        await session.RefreshAsync(); Assert.Equal(1, pageHandler.Requests);
        fail = true; clock.Advance(TimeSpan.FromMinutes(16));
        await session.RefreshAsync();
        var failed = Assert.Single(state.Streams.Sources);
        Assert.False(failed.Ok); Assert.Single(failed.Items); Assert.Equal(observed.Checked, failed.Checked);
        await session.RefreshAsync(); Assert.Equal(2, pageHandler.Requests);
        settings.Update(s => s.Social.Streams.YouTube = false);
        Assert.Empty(state.Streams.Sources);
    }

    [Fact]
    public async Task HiddenStreamsDoNotFetchAndShutdownCancelsAnObservedFetch()
    {
        using var directory = new TempDir();
        var settings = new SettingsStore(Path.Combine(directory.Path, "settings.json"));
        settings.Update(s => { s.Social.Streams.YouTube = true; s.Social.YouTubeChannels = new() { Channel }; });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RouteHandler(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(Page()); });
        using var http = new HttpFetcher(null, handler);
        await using var session = new StreamsSession(settings, new HubState(null), http, new InMemorySecretStore());
        await session.RefreshAsync(); Assert.Equal(0, handler.Requests);
        session.Visible = true;
        var active = session.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await session.StopAsync();
    }

    [Fact]
    public async Task RotatedRefreshTokenIsSavedEvenWhenValidationTransportFails()
    {
        using var directory = new TempDir();
        var clock = new StreamClock();
        var settings = TwitchSettings(directory.Path);
        var secrets = new InMemorySecretStore();
        SeedConnection(secrets, clock.GetUtcNow().AddSeconds(-1));
        var renewals = 0;
        var failValidation = true;
        using var auth = new RouteHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
            {
                renewals++;
                Assert.Contains("refresh_token=old-refresh", await request.Content!.ReadAsStringAsync());
                return Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}");
            }
            if (request.RequestUri.AbsolutePath == "/oauth2/validate")
            {
                if (failValidation) throw new HttpRequestException("fixture connection loss");
                Assert.Equal("new-access", request.Headers.Authorization!.Parameter);
                return Identity();
            }
            return Json("{\"data\":[]}");
        });
        using var publicHttp = new HttpFetcher(null);
        var state = new HubState(null);
        await using var session = new StreamsSession(settings, state, publicHttp, secrets, new HttpClient(auth), clock);
        session.Visible = true;
        await session.RefreshAsync();
        Assert.False(Assert.Single(state.Streams.Sources).Ok);
        Assert.Contains("new-refresh", secrets.Get(SecretKeys.TwitchConnection)!);
        Assert.DoesNotContain("old-refresh", secrets.Get(SecretKeys.TwitchConnection)!);
        Assert.DoesNotContain("new-refresh", File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        failValidation = false; clock.Advance(TimeSpan.FromMinutes(2));
        await session.RefreshAsync();
        Assert.True(Assert.Single(state.Streams.Sources).Ok);
        Assert.Equal(1, renewals);
    }

    [Fact]
    public async Task DisconnectPreventsLateRefreshFromRestoringCredentialsOrRows()
    {
        using var directory = new TempDir();
        var clock = new StreamClock();
        var settings = TwitchSettings(directory.Path);
        var secrets = new InMemorySecretStore();
        SeedConnection(secrets, clock.GetUtcNow().AddSeconds(-1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var auth = new RouteHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/oauth2/token")
            {
                started.TrySetResult(); await finish.Task;
                return Json("{\"access_token\":\"late-access\",\"refresh_token\":\"late-refresh\",\"expires_in\":3600}");
            }
            Assert.Equal("/oauth2/revoke", request.RequestUri.AbsolutePath);
            return Json("{}");
        });
        using var publicHttp = new HttpFetcher(null);
        var state = new HubState(null);
        await using var session = new StreamsSession(settings, state, publicHttp, secrets, new HttpClient(auth), clock);
        session.Visible = true;
        var fetch = session.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisconnectAsync();
        finish.TrySetResult(); await fetch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(session.TwitchLogin);
        Assert.Null(secrets.Get(SecretKeys.TwitchConnection));
        Assert.Empty(state.Streams.Sources);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareTheFreshObservation()
    {
        using var directory = new TempDir();
        var settings = new SettingsStore(Path.Combine(directory.Path, "settings.json"));
        settings.Update(s => { s.Social.Streams.YouTube = true; s.Social.YouTubeChannels = new() { Channel }; });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RouteHandler(async (_, _) => { started.TrySetResult(); await finish.Task; return Json(Page()); });
        using var publicHttp = new HttpFetcher(null, handler);
        var state = new HubState(null);
        await using var session = new StreamsSession(settings, state, publicHttp, new InMemorySecretStore());
        session.Visible = true;
        var first = session.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var others = Enumerable.Range(0, 10).Select(_ => session.RefreshAsync(force: true)).ToArray();
        finish.TrySetResult(); await Task.WhenAll(others.Append(first)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Requests);
        Assert.Single(Assert.Single(state.Streams.Sources).Items);
    }

    [Fact]
    public async Task LeavingSocialCancelsItsObservedRequestWithoutInventingOfflineData()
    {
        using var directory = new TempDir();
        var settings = new SettingsStore(Path.Combine(directory.Path, "settings.json"));
        settings.Update(s => { s.Social.Streams.YouTube = true; s.Social.YouTubeChannels = new() { Channel }; });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RouteHandler(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(Page()); });
        using var publicHttp = new HttpFetcher(null, handler);
        var state = new HubState(null);
        await using var session = new StreamsSession(settings, state, publicHttp, new InMemorySecretStore());
        session.Visible = true;
        var active = session.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Visible = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Empty(state.Streams.Sources);
    }

    [Fact]
    public async Task StoppingJoinsDeviceSignInAndDoesNotPersistTokens()
    {
        using var directory = new TempDir();
        var settings = TwitchSettings(directory.Path);
        var secrets = new InMemorySecretStore();
        using var auth = new RouteHandler((request, _) =>
        {
            Assert.Equal("/oauth2/device", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{\"device_code\":\"device\",\"user_code\":\"ABC\",\"verification_uri\":\"https://www.twitch.tv/activate\",\"expires_in\":60,\"interval\":5}"));
        });
        using var publicHttp = new HttpFetcher(null);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new StreamsSession(settings, new HubState(null), publicHttp, secrets, new HttpClient(auth));
        var signIn = session.ConnectAsync(_ => { shown.TrySetResult(); return Task.CompletedTask; });
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);
        Assert.Null(secrets.Get(SecretKeys.TwitchConnection));
        Assert.Equal(1, auth.Requests);
    }

    [Theory]
    [InlineData("access\r\nInjected: secret")]
    [InlineData("Bearer bad-token")]
    public async Task InvalidCredentialValuesAreRejectedBeforeBuildingHeaders(string token)
    {
        using var auth = new RouteHandler((_, _) => throw new InvalidOperationException("No request expected"));
        using var http = new HttpClient(auth);
        var error = await Assert.ThrowsAsync<TwitchException>(() => new TwitchClient(http).ValidateAsync("fixture", token, default));
        Assert.DoesNotContain(token, error.Message);
        Assert.Equal(0, auth.Requests);
    }

    private static SettingsStore TwitchSettings(string directory)
    {
        var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
        settings.Update(s => { s.Social.Streams.Twitch = true; s.Social.Streams.TwitchClientId = "fixture"; });
        return settings;
    }
    [Fact]
    public async Task ManualStreamCheckRespectsAgentPause()
    {
        using var directory = new TempDir();
        var settings = TwitchSettings(directory.Path);
        var secrets = new InMemorySecretStore();
        using var handler = new RouteHandler((_, _) => throw new InvalidOperationException("Paused agents must not fetch"));
        using var http = new HttpFetcher(null, handler);
        using var llm = new LlmClient(() => new AiSettings { Enabled = false }, secrets);
        var state = new HubState(null);
        await using var session = new StreamsSession(settings, state, http, secrets);
        session.Visible = true;
        using var runtime = new AgentRuntime(new HubContext
        {
            Settings = settings, Db = new HubDatabase(Path.Combine(directory.Path, "hub.db")),
            Http = http, Llm = llm, State = state, Secrets = secrets, Platform = new NullPlatform(), Streams = session,
        });
        runtime.Register(new StreamScoutAgent());
        runtime.Paused = true; session.RequestCheck();
        var report = await runtime.RunAndWaitAsync("stream-scout", TimeSpan.FromSeconds(1), default);
        Assert.Equal(AgentRunEnd.Paused, report!.End);
        Assert.Equal(0, handler.Requests);
    }
    [Fact]
    public async Task CompletedDeviceSignInFetchesFollowedStreamsAndDisconnectRevokes()
    {
        using var directory = new TempDir();
        var clock = new StreamClock();
        var settings = TwitchSettings(directory.Path);
        var secrets = new InMemorySecretStore();
        using var handler = new RouteHandler(async (request, _) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/oauth2/device": return Json("{\"device_code\":\"device\",\"user_code\":\"ABC\",\"verification_uri\":\"https://www.twitch.tv/activate\",\"expires_in\":60,\"interval\":5}");
                case "/oauth2/token": return Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}");
                case "/oauth2/validate": return Identity();
                case "/helix/streams/followed":
                    Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                    Assert.Equal("new-access", request.Headers.Authorization.Parameter);
                    Assert.Equal("fixture", Assert.Single(request.Headers.GetValues("Client-Id")));
                    Assert.Contains("user_id=123", request.RequestUri.Query);
                    return Json("{\"data\":[{\"id\":\"1\",\"type\":\"live\",\"user_id\":\"456\",\"user_login\":\"fixture_channel\",\"user_name\":\"Fixture channel\",\"title\":\"A fixture stream\",\"viewer_count\":12}]}");
                case "/oauth2/revoke":
                    Assert.Contains("token=new-access", await request.Content!.ReadAsStringAsync());
                    return Json("{}");
                default: throw new InvalidOperationException("Unexpected fixture request");
            }
        });
        using var publicHttp = new HttpFetcher(null);
        var state = new HubState(null);
        await using var session = new StreamsSession(settings, state, publicHttp, secrets, new HttpClient(handler), clock);
        var signIn = session.ConnectAsync(_ => Task.CompletedTask);
        await clock.WaitForTimers(2); clock.Advance(TimeSpan.FromSeconds(5));
        await signIn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("fixture", session.TwitchLogin);
        session.Visible = true; await session.RefreshAsync();
        var stream = Assert.Single(Assert.Single(state.Streams.Sources).Items);
        Assert.Equal("https://www.twitch.tv/fixture_channel", stream.Url);
        Assert.Equal(12, stream.Viewers);
        await session.DisconnectAsync();
        Assert.Null(session.TwitchLogin); Assert.Null(secrets.Get(SecretKeys.TwitchConnection)); Assert.Empty(state.Streams.Sources);
        Assert.Equal(5, handler.Requests);
    }

    [Fact]
    public async Task RejectedRefreshClearsConnectionInsteadOfPretendingConnected()
    {
        using var directory = new TempDir();
        var clock = new StreamClock();
        var secrets = new InMemorySecretStore(); SeedConnection(secrets, clock.GetUtcNow().AddSeconds(-1));
        using var handler = new RouteHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"message\":\"invalid token\"}") }));
        using var publicHttp = new HttpFetcher(null);
        await using var session = new StreamsSession(TwitchSettings(directory.Path), new HubState(null), publicHttp, secrets, new HttpClient(handler), clock);
        session.Visible = true; await session.RefreshAsync();
        Assert.Null(session.TwitchLogin); Assert.Null(secrets.Get(SecretKeys.TwitchConnection));
    }
    [Fact]
    public async Task CorruptSavedCredentialDoesNotBreakStartupOrPretendConnected()
    {
        using var directory = new TempDir();
        var secrets = new InMemorySecretStore();
        secrets.Set(SecretKeys.TwitchConnection, "{\"clientId\":\"fixture\",\"tokens\":null,\"identity\":null}");
        using var http = new HttpFetcher(null);
        await using var session = new StreamsSession(TwitchSettings(directory.Path), new HubState(null), http, secrets);
        Assert.Null(session.TwitchLogin);
    }
    private static void SeedConnection(ISecretStore secrets, DateTimeOffset expires) => secrets.Set(SecretKeys.TwitchConnection,
        JsonSerializer.Serialize(new { ClientId = "fixture", Tokens = new TwitchTokens("old-access", "old-refresh", 3600),
            Identity = new TwitchIdentity("123", "fixture", 3600), Expires = expires, Validated = DateTimeOffset.MinValue }, JsonUtil.Options));
    private static HttpResponseMessage Identity() => Json("{\"client_id\":\"fixture\",\"scopes\":[\"user:read:follows\"],\"user_id\":\"123\",\"login\":\"fixture\",\"expires_in\":3600}");

    internal static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    internal sealed class RouteHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        public int Requests;
        public Func<int, HttpStatusCode>? Status;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref Requests);
            var response = await route(request, cancellationToken);
            if (Status is not null) response.StatusCode = Status(count);
            return response;
        }
    }
    internal sealed class StreamClock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        private readonly List<Timer> _timers = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new Timer(this, callback, state); timer.Change(dueTime, period); _timers.Add(timer);
                _changed.TrySetResult(); _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); return timer;
            }
        }
        public async Task WaitForTimers(int count)
        {
            while (true)
            {
                Task wait;
                lock (_gate) { if (_timers.Count >= count) return; wait = _changed.Task; }
                await wait.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (_gate) { _now += by; due = _timers.Where(t => t.Due <= _now).ToList(); foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue; }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class Timer(StreamClock owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due = DateTimeOffset.MaxValue;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner._gate) { if (_disposed) return false; Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner._now + dueTime; return true; } }
            public void Fire() { lock (owner._gate) { if (_disposed) return; } callback(state); }
            public void Dispose() { lock (owner._gate) { _disposed = true; Due = DateTimeOffset.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
