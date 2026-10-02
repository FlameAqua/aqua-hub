using System.Text.Json;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

/// <summary>A disposable agent context over a temporary profile (no network is touched by these tests).</summary>
internal sealed class TestContext : IDisposable
{
    private readonly TempDir _dir = new();
    public NullPlatform Platform { get; } = new();
    public HubContext Ctx { get; }

    /// <param name="network">A stand-in network (<see cref="FakeNetwork"/>) instead of the real one.</param>
    public TestContext(HttpMessageHandler? network = null)
    {
        var settings = new SettingsStore(Path.Combine(_dir.Path, "settings.json"));
        var db = new HubDatabase(Path.Combine(_dir.Path, "hub.db"));
        var secrets = new InMemorySecretStore();
        Ctx = new HubContext
        {
            Settings = settings, Db = db, Http = network is null ? new HttpFetcher(db) : new HttpFetcher(db, network),
            Llm = new LlmClient(() => settings.Current.Ai, secrets),
            State = new HubState(db), Secrets = secrets, Platform = Platform,
        };
    }

    public void Dispose() => _dir.Dispose();
}

public class SettingsMergeTests
{
    [Fact]
    public void MergeKeepsChangesMadeElsewhereWhileTheFormWasOpen()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var original = store.Clone();
        var edited = SettingsStore.DeepCopy(original);
        edited.General.UserName = "Aoife";
        edited.News.Interests = new() { "housing", "energy" };

        // Meanwhile: do-not-disturb toggled from the tray and an app added by first-run discovery.
        store.Update(s =>
        {
            s.Notifications.DoNotDisturb = true;
            s.Apps.Add(new AppEntry { Id = "spotify", Name = "Spotify", Target = "spotify.exe" });
        });

        Assert.True(store.Merge(original, edited));
        var now = store.Current;
        Assert.Equal("Aoife", now.General.UserName);
        Assert.Equal(new[] { "housing", "energy" }, now.News.Interests);
        Assert.True(now.Notifications.DoNotDisturb);
        Assert.Contains(now.Apps, a => a.Id == "spotify");
    }

    [Fact]
    public void MergeWithNoEditsChangesNothing()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var raised = 0;
        store.Changed += _ => raised++;
        var original = store.Clone();
        Assert.False(store.Merge(original, SettingsStore.DeepCopy(original)));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void MergedValuesAreStillValidated()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var original = store.Clone();
        var edited = SettingsStore.DeepCopy(original);
        edited.News.RefreshMinutes = 1;   // below the 3-minute floor
        store.Merge(original, edited);
        Assert.Equal(3, store.Current.News.RefreshMinutes);
    }
}

public class NotificationPolicyTests
{
    private static HubAlert Alert(string kind = "price", AlertSeverity severity = AlertSeverity.Notice) =>
        new() { Id = "a", Kind = kind, Severity = severity, Title = "NVDA +4%", Body = "", Created = DateTimeOffset.Now };

    private static readonly DateTime Noon = new(2026, 9, 27, 12, 0, 0);
    private static readonly DateTime Midnight = new(2026, 9, 27, 0, 30, 0);

    [Fact]
    public void DoNotDisturbDropsAndCriticalStillNeedsNotificationsOn()
    {
        var n = new NotificationSettings { DoNotDisturb = true };
        Assert.Equal(ToastDecision.Drop, NotificationPolicy.Decide(n, Alert(), Noon, false, out _));
        Assert.Equal(ToastDecision.Drop, NotificationPolicy.Decide(new NotificationSettings { Enabled = false }, Alert(severity: AlertSeverity.Critical), Noon, false, out _));
    }

    [Fact]
    public void QuietHoursAcrossMidnightHoldForLaterButCriticalGetsThrough()
    {
        var n = new NotificationSettings { QuietStart = "23:00", QuietEnd = "07:00" };
        Assert.Equal(ToastDecision.Hold, NotificationPolicy.Decide(n, Alert(), Midnight, false, out var why));
        Assert.Equal("quiet", why);
        Assert.Equal(ToastDecision.Show, NotificationPolicy.Decide(n, Alert(severity: AlertSeverity.Critical), Midnight, false, out _));
        Assert.Equal(ToastDecision.Show, NotificationPolicy.Decide(n, Alert(), Noon, false, out _));
    }

    [Fact]
    public void GamesHoldToastsUnlessTheUserOptedOut()
    {
        Assert.Equal(ToastDecision.Hold, NotificationPolicy.Decide(new NotificationSettings(), Alert(), Noon, true, out var why));
        Assert.Equal("game", why);
        Assert.Equal(ToastDecision.Show, NotificationPolicy.Decide(new NotificationSettings { SuppressWhenFullscreen = false }, Alert(), Noon, true, out _));
    }

    [Fact]
    public void DisabledKindsAreDropped() =>
        Assert.Equal(ToastDecision.Drop, NotificationPolicy.Decide(new NotificationSettings { PriceAlerts = false }, Alert("price"), Noon, false, out _));

    [Fact]
    public void HeldAlertsBecomeOneSummary()
    {
        var held = new List<HubAlert>
        {
            Alert() with { Title = "NVDA +4%" },
            Alert(severity: AlertSeverity.Important) with { Title = "Fed decision tomorrow" },
            Alert() with { Title = "Rain from 16:00" },
        };
        var (title, body) = NotificationPolicy.Summary(held, "game");
        Assert.Equal("While you were gaming: 3 alerts", title);
        Assert.StartsWith("Fed decision tomorrow", body);
        Assert.EndsWith("+1 more in the bell", body);
    }
}

public class AgentSchedulingTests
{
    private sealed class TestAgent : Agent
    {
        private readonly string _id;
        private readonly Func<AgentResult> _run;
        private readonly string[] _after;
        private readonly TimeSpan _delay;
        private readonly Func<bool>? _enabled;
        private readonly TimeSpan _takes;
        public int Runs;

        public TestAgent(string id, Func<AgentResult> run, string[]? after = null, TimeSpan? delay = null, Func<bool>? enabled = null, TimeSpan? takes = null)
        {
            _id = id;
            _run = run;
            _after = after ?? Array.Empty<string>();
            _delay = delay ?? TimeSpan.Zero;
            _enabled = enabled;
            _takes = takes ?? TimeSpan.Zero;
        }

        public override string Id => _id;
        public override string Name => _id;
        public override string Description => "test";
        public override string[] After => _after;
        public override bool Stretchable => false;
        public override TimeSpan InitialDelay => _delay;
        public override bool IsEnabled(HubContext ctx) => _enabled?.Invoke() ?? true;
        public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromHours(1);

        public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            if (_takes > TimeSpan.Zero) await Task.Delay(_takes, ct);
            return _run();
        }
    }

    /// <summary>A runtime that looks for due agents every 50 ms and runs followers 100 ms after (not 1 s and 2 s).</summary>
    private static AgentRuntime Quick(TestContext t, bool paused = false) =>
        new(t.Ctx) { Tick = TimeSpan.FromMilliseconds(50), FollowUpDelay = TimeSpan.FromMilliseconds(100), Paused = paused };

    private static async Task<AgentStatus> WaitForRuns(AgentRuntime runtime, string id, int runs)
    {
        for (var i = 0; i < 600; i++)
        {
            var s = runtime.Statuses.First(x => x.Id == id);
            if (s.Runs >= runs && s.State != AgentState.Running && s.State != AgentState.Waiting) return s;
            await Task.Delay(25);
        }
        throw new TimeoutException($"{id} did not reach {runs} run(s)");
    }

    [Fact]
    public async Task FailuresBackOffExponentially()
    {
        using var t = new TestContext();
        using var runtime = Quick(t);
        runtime.Register(new TestAgent("flaky", () => AgentResult.Fail("source down")));
        runtime.Start();

        var first = await WaitForRuns(runtime, "flaky", 1);
        Assert.Equal(AgentState.Error, first.State);
        Assert.InRange((first.NextRun!.Value - first.LastRun!.Value).TotalSeconds, 25, 40);

        runtime.RunNow("flaky");
        var second = await WaitForRuns(runtime, "flaky", 2);
        Assert.InRange((second.NextRun!.Value - second.LastRun!.Value).TotalSeconds, 55, 70);
        Assert.Equal(2, second.Errors);
    }

    [Fact]
    public async Task AChangedResultTriggersDependentAgentsSoon()
    {
        using var t = new TestContext();
        using var runtime = Quick(t);
        var scout = new TestAgent("scout", () => AgentResult.Success("3 new"));
        var curator = new TestAgent("curator", () => AgentResult.Success("clustered"), after: new[] { "scout" }, delay: TimeSpan.FromHours(1));
        runtime.Register(scout);
        runtime.Register(curator);
        runtime.Start();

        await WaitForRuns(runtime, "curator", 1);   // would otherwise wait an hour
        Assert.Equal(1, curator.Runs);
    }

    [Fact]
    public async Task UnchangedResultsDoNotTriggerDependents()
    {
        using var t = new TestContext();
        using var runtime = Quick(t);
        runtime.Register(new TestAgent("scout", () => AgentResult.Unchanged("nothing new")));
        var curator = new TestAgent("curator", () => AgentResult.Success("clustered"), after: new[] { "scout" }, delay: TimeSpan.FromHours(1));
        runtime.Register(curator);
        runtime.Start();

        await WaitForRuns(runtime, "scout", 1);
        await Task.Delay(600); // a dozen ticks, several times the follow-up delay
        Assert.Equal(0, curator.Runs);
    }

    [Fact]
    public async Task RunAndWaitRunsAnAgentNowAndReportsHowItWent()
    {
        using var t = new TestContext();
        using var runtime = Quick(t);
        var scout = new TestAgent("scout", () => AgentResult.Success("12 new articles"), delay: TimeSpan.FromHours(1));
        runtime.Register(scout);
        runtime.Register(new TestAgent("off", () => AgentResult.Success("never"), delay: TimeSpan.FromHours(1), enabled: () => false));
        runtime.Start();

        var report = await runtime.RunAndWaitAsync("scout", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(AgentRunEnd.Finished, report!.End);
        Assert.Equal("12 new articles", report.Status.LastMessage);
        Assert.Equal(1, scout.Runs);

        Assert.Null(await runtime.RunAndWaitAsync("nobody", TimeSpan.FromSeconds(1), CancellationToken.None));
        var off = await runtime.RunAndWaitAsync("off", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(AgentRunEnd.Disabled, off!.End);
    }

    [Fact]
    public async Task RunAndWaitWaitsOnARunUnderWayAndGivesUpAtItsTimeout()
    {
        using var t = new TestContext();
        using var runtime = Quick(t);
        var slow = new TestAgent("slow", () => AgentResult.Success("done"), takes: TimeSpan.FromMilliseconds(400));
        runtime.Register(slow);
        runtime.Start();
        for (var i = 0; i < 60 && runtime.Statuses[0].State != AgentState.Running; i++) await Task.Delay(50);

        var report = await runtime.RunAndWaitAsync("slow", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(AgentRunEnd.Finished, report!.End);
        Assert.Equal(1, slow.Runs); // the run under way was waited on, not doubled

        var tooSoon = await runtime.RunAndWaitAsync("slow", TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal(AgentRunEnd.StillRunning, tooSoon!.End);
    }

    [Fact]
    public async Task RunAndWaitComesBackAtOnceWhileAgentsArePaused()
    {
        using var t = new TestContext();
        using var runtime = Quick(t, paused: true);
        var scout = new TestAgent("scout", () => AgentResult.Success("ok"));
        runtime.Register(scout);
        runtime.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = await runtime.RunAndWaitAsync("scout", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(AgentRunEnd.Paused, report!.End);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(0, scout.Runs);
    }
}

public class SentinelTests
{
    [Fact]
    public async Task ABigMoveAlertsOncePerDay()
    {
        using var t = new TestContext();
        t.Ctx.Settings.Update(s =>
        {
            s.Markets.Watchlist = new() { new WatchSymbol { Symbol = "TEST", Name = "Test Co" } };
            s.Markets.AlertMovePercent = 3;
        });
        t.Ctx.State.SetQuotes(new Dictionary<string, Quote>
        {
            ["TEST"] = new() { Symbol = "TEST", Name = "Test Co", Price = 110, PreviousClose = 100, MarketState = "open", Currency = "EUR", Time = DateTimeOffset.UtcNow },
        });

        var sentinel = new SentinelAgent();
        await sentinel.RunAsync(t.Ctx, CancellationToken.None);
        await sentinel.RunAsync(t.Ctx, CancellationToken.None);

        var alert = Assert.Single(t.Platform.Delivered);
        Assert.Equal("price", alert.Kind);
        Assert.Contains("+10.0%", alert.Title);
    }

    [Fact]
    public async Task ClosedMarketsDoNotRaiseMoveAlerts()
    {
        using var t = new TestContext();
        t.Ctx.Settings.Update(s => s.Markets.Watchlist = new() { new WatchSymbol { Symbol = "TEST", Name = "Test Co" } });
        t.Ctx.State.SetQuotes(new Dictionary<string, Quote>
        {
            ["TEST"] = new() { Symbol = "TEST", Name = "Test Co", Price = 110, PreviousClose = 100, MarketState = "closed" },
        });
        await new SentinelAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.Empty(t.Platform.Delivered);
    }
}

public class FactCheckTests
{
    private static PredictionMarket Fed() => new()
    {
        Id = "fed", Title = "Fed decision in October?",
        Outcomes = new() { new("25 bp hike", 0.65, 0.03), new("No change", 0.34, -0.02), new("25 bp cut", 0.01, null) },
    };

    [Fact]
    public void FlagsPercentagesThatAreNotInTheData()
    {
        var allowed = FactCheck.ForesightInputs(Array.Empty<HubEvent>(), new[] { Fed() });
        Assert.Empty(FactCheck.UnsupportedPercentages("A hike is priced at 65% after a 3% rise.", allowed));
        Assert.Equal(new[] { 80.0 }, FactCheck.UnsupportedPercentages("Traders see an 80% chance of a hike.", allowed));
    }

    [Fact]
    public void CatchesTheWrongFavourite()
    {
        Assert.True(FactCheck.MisnamesFavourite("The crowd favours no change at 34%.", Fed()));
        Assert.False(FactCheck.MisnamesFavourite("The crowd favours a 25 bp hike at 65%.", Fed()));
        Assert.False(FactCheck.MisnamesFavourite("No change is at 34%, well behind a hike.", Fed()));
    }

    [Fact]
    public void CorrectsTheOutlookFromTheData()
    {
        var f = new Foresight
        {
            Overview = "A big week for rates.",
            Items = new()
            {
                new ForesightItem { Title = "Fed decision in October", Detail = "The crowd favours no change at 34%." },
                new ForesightItem { Title = "Unrelated claim", Detail = "Something at 91%." },
                new ForesightItem { Title = "ECB speech Tuesday", Detail = "Watch for hints on cuts." },
            },
        };
        var (result, fixes) = FactCheck.Foresight(f, Array.Empty<HubEvent>(), new[] { Fed() }, "fallback");
        Assert.Equal(2, fixes);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("The crowd favours “25 bp hike” at 65% (of 3 outcomes).", result.Items[0].Detail);
        Assert.Equal("ECB speech Tuesday", result.Items[1].Title);
        Assert.Equal("A big week for rates.", result.Overview);
    }
}

public class ModelJsonTests
{
    [Fact]
    public void RepeatedKeysAreMergedNotDropped()
    {
        const string text = """{"title":"Evening brief","sections":[{"title":"Weather","bullets":["Dry"],"bullets":["Windy later"]}]}""";
        Assert.True(LlmClient.TryParseJson(text, null, out var doc));
        using (doc)
        {
            var bullets = doc!.RootElement.GetProperty("sections")[0].GetProperty("bullets").EnumerateArray().Select(b => b.GetString()).ToList();
            Assert.Equal(new[] { "Dry", "Windy later" }, bullets);
        }
    }
}

public class PredictionTidyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("September 30", 2026, 9, 30)]
    [InlineData("by Oct 15, 2027", 2027, 10, 15)]
    [InlineData("Sept. 21", 2026, 9, 21)]
    [InlineData("October", 2026, 10, 31)]
    [InlineData("January", 2027, 1, 31)]
    public void ParsesDateOutcomes(string label, int y, int m, int d) =>
        Assert.Equal(new DateTime(y, m, d), PredictionSources.OutcomeDate(label, Now));

    [Fact]
    public void NonDatesAreNotDates() => Assert.Null(PredictionSources.OutcomeDate("25 bp hike", Now));

    [Fact]
    public void DateOutcomesAreChronologicalWithoutPastDates()
    {
        var outcomes = new List<PredictionOutcome>
        {
            new("October 31", 0.40, null), new("September 21", 0.01, null), new("September 30", 0.35, null), new("December 31", 0.2, null),
        };
        var tidy = PredictionSources.TidyOutcomes(outcomes, binary: false, Now);
        Assert.Equal(new[] { "September 30", "October 31", "December 31" }, tidy.Select(o => o.Label));
    }

    [Fact]
    public void OtherOutcomesAreMostLikelyFirstAndTheLeadIsTheMostLikely()
    {
        var tidy = PredictionSources.TidyOutcomes(new() { new("B", 0.2, null), new("A", 0.7, null), new("C", 0.001, null) }, false, Now);
        Assert.Equal(new[] { "A", "B" }, tidy.Select(o => o.Label));
        var market = new PredictionMarket { Id = "x", Title = "x", Outcomes = PredictionSources.TidyOutcomes(new() { new("October 31", 0.7, null), new("September 30", 0.3, null) }, false, Now) };
        Assert.Equal("October 31", market.Lead!.Label);
    }

    [Fact]
    public void NearCertainMarketsCountAsResolved()
    {
        Assert.True(PredictionSources.IsEffectivelyResolved(new[] { new PredictionOutcome("Yes", 0.99, null), new PredictionOutcome("No", 0.01, null) }));
        Assert.False(PredictionSources.IsEffectivelyResolved(new[] { new PredictionOutcome("Yes", 0.9, null), new PredictionOutcome("No", 0.1, null) }));
    }
}

public class MarketSessionTests
{
    [Fact]
    public void ClosedMarketsSayWhichCloseTheFiguresAreFrom()
    {
        var sunday = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        var friday = new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);
        var quotes = new[] { new Quote { Symbol = "^GSPC", Kind = InstrumentKind.Index, MarketState = "closed", Time = friday } };
        Assert.Equal("Markets closed · figures from Fri close", MarketSession.Summary(quotes, sunday));
        Assert.Equal("Live", MarketSession.Label(quotes[0] with { MarketState = "open" }, sunday));
        Assert.Equal("24/7", MarketSession.Label(quotes[0] with { Kind = InstrumentKind.Crypto }, sunday));
    }

    [Fact]
    public void QuickDigestMarketLinesAreReadableAndSayWhenFiguresAreFrom()
    {
        var sunday = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        var friday = new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);
        var state = new HubState(null);
        state.SetQuotes(new Dictionary<string, Quote>
        {
            ["^GSPC"] = new() { Symbol = "^GSPC", Name = "S&P 500", Kind = InstrumentKind.Index, MarketState = "closed", Time = friday, PreviousClose = 100, Price = 100.51 },
            ["BTC-USD"] = new() { Symbol = "BTC-USD", Name = "Bitcoin", Kind = InstrumentKind.Crypto, MarketState = "open", Time = sunday, PreviousClose = 100, Price = 98.2 },
            ["MSFT"] = new() { Symbol = "MSFT", Name = "Microsoft", Kind = InstrumentKind.Equity, MarketState = "closed", Time = friday, PreviousClose = 100, Price = 103.7 },
        });
        var m = new MarketSettings
        {
            Indices = new() { new WatchSymbol { Symbol = "^GSPC" } },
            Macro = new() { new WatchSymbol { Symbol = "BTC-USD" } },
            Watchlist = new() { new WatchSymbol { Symbol = "MSFT" } },
        };
        var lines = Fallbacks.MarketBullets(state, m, sunday);
        Assert.Equal(new[] { "S&P 500 +0.5% at Fri close.", "Elsewhere: Bitcoin -1.8%.", "Your watchlist: MSFT +3.7%." }, lines);
    }

    [Fact]
    public void QuickDigestWeatherLinesReadNaturally()
    {
        var now = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
        var w = new WeatherSnapshot
        {
            Now = new WeatherNow(12, 11, 67, 0, 0, 1, true),
            Days = new List<WeatherDay> { new(new DateOnly(2026, 9, 28), 3, 18, 10, 0, null, null, 3) },
            Hours = new List<WeatherHour>(),
        };
        var lines = Fallbacks.WeatherBullets(w, now);
        Assert.Equal(2, lines.Count);
        Assert.Matches(@"^12° and \w.* now, feels like 11°\.$", lines[0]);
        Assert.EndsWith("; staying dry.", lines[1]);
    }

    [Fact]
    public void CardSubtitlesStayShort()
    {
        var sunday = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
        var friday = new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);
        var closed = new Quote { Symbol = "^GSPC", Kind = InstrumentKind.Index, MarketState = "closed", Time = friday };
        var open = closed with { Symbol = "^FTSE", MarketState = "open", Time = sunday };
        Assert.Equal("Closed · Fri close", MarketSession.Short(new[] { closed }, sunday));
        Assert.Equal("Open · delayed ~15m", MarketSession.Short(new[] { open }, sunday));
        Assert.Equal("Partly open", MarketSession.Short(new[] { closed, open }, sunday));
        Assert.Equal("", MarketSession.Short(new[] { closed with { Kind = InstrumentKind.Crypto } }, sunday));
        Assert.True(MarketSession.Short(new[] { closed }, sunday).Length <= 24);
    }
}

public class TextHelperTests
{
    [Theory]
    [InlineData(1, "story", "1 story")]
    [InlineData(3, "story", "3 stories")]
    [InlineData(2, "day", "2 days")]
    [InlineData(0, "source", "0 sources")]
    [InlineData(4, "match", "4 matches")]
    public void Plurals(int n, string word, string expected) => Assert.Equal(expected, Plural.Of(n, word));

    [Fact]
    public void RelativeTimesReadNaturally()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("just now", TimeText.AgoPhrase(now.AddSeconds(-20), now));
        Assert.Equal("5m ago", TimeText.AgoPhrase(now.AddMinutes(-5), now));
        Assert.StartsWith("on ", TimeText.AgoPhrase(now.AddDays(-9), now));
        Assert.Equal("in under a minute", TimeText.Until(now.AddSeconds(30), now));
    }

    [Fact]
    public void StyledUnicodeLettersFoldToPlainText()
    {
        Assert.Equal("Bold news", HtmlText.FoldStyledLetters("𝐁𝐨𝐥𝐝 news"));
        Assert.Equal("Café 😀", HtmlText.FoldStyledLetters("Café 😀"));   // accents and emoji are untouched
    }
}
