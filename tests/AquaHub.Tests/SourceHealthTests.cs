using AquaHub.Core.Agents;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

/// <summary>A feed that keeps failing: logged less, rested once it's gone, and named in one alert and in Settings.</summary>
public class SourceHealthTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 10, 48, 0, TimeSpan.Zero);

    private static FetchOutcome Fail(string id, int code, string? error = null) => new(id, false, false, new(), error ?? $"HTTP {code}") { HttpCode = code };
    private static FetchOutcome Ok(string id) => new(id, true, false, new(), null);

    [Fact]
    public void AFeedThatIsntThereIsRestedAfterThreeRunsUntilItWorks()
    {
        var health = new SourceHealth();
        const string id = "feed:feed-f70daca7";
        Assert.Null(health.Record("social", Fail(id, 404), T0));
        Assert.Null(health.Record("social", Fail(id, 404), T0.AddMinutes(15)));
        Assert.True(health.Due(id, T0.AddMinutes(30)));
        var gone = health.Record("social", Fail(id, 404), T0.AddMinutes(30));
        Assert.True(gone is { Gone: true });
        Assert.Equal(T0, gone!.Since);

        // Rested for six hours, and failing again then doesn't count as newly gone (no second alert).
        var tried = T0.AddMinutes(30);
        Assert.False(health.Due(id, T0.AddMinutes(45)));
        Assert.False(health.Due(id, tried + SourceHealth.GoneRetry - TimeSpan.FromMinutes(1)));
        Assert.True(health.Due(id, tried + SourceHealth.GoneRetry));
        Assert.Null(health.Record("social", Fail(id, 404), tried + SourceHealth.GoneRetry));
        Assert.Equal("Not found at this address (HTTP 404) · first failed 7h ago · Aqua tries it every 6 hours until it works again",
            health[id]!.Describe(T0.AddHours(7)));

        // Back at its address: fetched every run again.
        Assert.Null(health.Record("social", Ok(id), T0.AddHours(13)));
        Assert.Null(health[id]);
        Assert.True(health.Due(id, T0.AddHours(13)));
    }

    [Fact]
    public void OtherFailuresAreTriedEveryRun()
    {
        var health = new SourceHealth();
        for (var i = 0; i < 10; i++) Assert.Null(health.Record("news", Fail("rte", 503), T0.AddMinutes(15 * i)));
        Assert.True(health.Due("rte", T0.AddHours(3)));
        Assert.Equal((10, false), (health["rte"]!.Failures, health["rte"]!.Gone));
        Assert.Equal("Couldn't read it: HTTP 503 · first failed 3h ago", health["rte"]!.Describe(T0.AddHours(3)));

        // "Not found" has to come three runs in a row: a timeout in between starts the count again.
        Assert.Null(health.Record("news", Fail("x", 404), T0));
        Assert.Null(health.Record("news", Fail("x", 404), T0.AddMinutes(15)));
        Assert.Null(health.Record("news", Fail("x", 0, "Timed out"), T0.AddMinutes(30)));
        Assert.Null(health.Record("news", Fail("x", 404), T0.AddMinutes(45)));
        Assert.True(health.Due("x", T0.AddMinutes(60)));
    }

    [Fact]
    public void AFailingFeedIsLoggedWhenItStartsWhenItChangesAndOnceADay()
    {
        const string id = "feed:log-test-3f1c";
        var lines = new List<string>();
        void Seen(LogEntry e)
        {
            if (e.Message.StartsWith(id, StringComparison.Ordinal)) lock (lines) lines.Add(e.Message);
        }
        Log.Written += Seen;
        try
        {
            var health = new SourceHealth();
            for (var i = 0; i < 8; i++) health.Record("social", Fail(id, 503), T0.AddMinutes(15 * i));   // one line, not eight
            health.Record("social", Fail(id, 0, "Parse error: bad XML"), T0.AddHours(2));              // another error
            health.Record("social", Fail(id, 0, "Parse error: bad XML"), T0.AddHours(3));
            health.Record("social", Fail(id, 0, "Parse error: bad XML"), T0.AddHours(2) + SourceHealth.LogAgainAfter);
            health.Record("social", Ok(id), T0.AddDays(2));
        }
        finally { Log.Written -= Seen; }
        Assert.Equal(new[]
        {
            $"{id}: HTTP 503", $"{id}: Parse error: bad XML", $"{id}: Parse error: bad XML (still, after 11 runs)", $"{id} works again (after 11 failed runs)",
        }, lines);
    }

    [Fact]
    public async Task YourFeedThatIsntThereIsRestedAndNamedInOneAlert()
    {
        var net = new FakeNetwork();   // nothing routed: every address answers 404
        using var t = new TestContext(net);
        t.Ctx.Settings.Update(s =>
        {
            s.Social.Subreddits = new();
            s.Social.BlueskyTrending = false;
            s.Social.HackerNews = false;
            s.Social.MastodonHashtags = new();
            s.Social.YouTubeChannels = new();
            s.Social.ExtraFeeds = new() { new NewsSource { Id = "feed-f70daca7", Name = "My blog", Url = "https://blog.example/feed.xml", Category = "social", Tier = 3, Enabled = true } };
        });

        var results = new List<AgentResult>();
        for (var run = 0; run < 4; run++) results.Add(await new SocialScoutAgent().RunAsync(t.Ctx, CancellationToken.None));

        Assert.Equal(3, net.Count("https://blog.example/feed.xml"));   // the fourth run left it alone
        Assert.Equal("Every social source is resting after failing", results[3].Message);
        var alert = Assert.Single(t.Platform.Delivered);
        Assert.Equal("My blog isn't at its address any more", alert.Title);
        Assert.Equal("settings:social:extrafeeds", alert.Target);
        Assert.Contains("blog.example answers “not found” (HTTP 404)", alert.Body);
        Assert.Contains("Settings › Social › Extra feeds", alert.Body);
        Assert.True(t.Ctx.State.Sources["feed:feed-f70daca7"] is { Gone: true });
    }
}
