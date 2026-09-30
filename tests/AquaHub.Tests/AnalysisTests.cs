using AquaHub.Core.Analysis;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;

namespace AquaHub.Tests;

public class ClusteringTests
{
    private static FeedItem Item(string id, string source, string title, int tier = 2, double hoursAgo = 1, string category = "world", bool local = false, string summary = "") => new()
    {
        Id = id, Kind = ItemKind.News, SourceId = source.ToLowerInvariant(), SourceName = source, Title = title, Tier = tier,
        Published = DateTimeOffset.UtcNow.AddHours(-hoursAgo), Category = category, IsLocal = local, Summary = summary,
    };

    [Fact]
    public void GroupsCoverageOfTheSameStoryAcrossOutlets()
    {
        var items = new List<FeedItem>
        {
            Item("1", "Reuters", "ECB holds interest rates steady as inflation cools", tier: 1),
            Item("2", "BBC News", "European Central Bank keeps interest rates on hold as inflation cools", tier: 1),
            Item("3", "The Guardian", "ECB leaves interest rates unchanged, says inflation is cooling"),
            Item("4", "Ars Technica", "New GPU architecture doubles ray tracing throughput", category: "tech"),
            Item("5", "The Verge", "Smartwatch battery life test: two weeks on one charge", category: "tech"),
        };
        var clusters = StoryClusterer.Build(items, new NewsSettings(), new LocationSettings());
        var ecb = clusters.First(c => c.Items.Any(i => i.Id == "1"));
        Assert.True(ecb.SourceCount >= 2, $"expected ECB coverage to merge, got {ecb.SourceCount}");
        Assert.Contains(ecb.Items, i => i.Id == "2");
        Assert.DoesNotContain(ecb.Items, i => i.Id == "4");
        Assert.Equal(ecb.Id, clusters.OrderByDescending(c => c.Importance).First().Id); // widest coverage ranks first
        Assert.Equal(1, ecb.Items[0].Tier); // most reliable outlet leads the card
    }

    [Fact]
    public void DetectsLocalStoriesAndInterests()
    {
        var settings = new NewsSettings { Interests = new() { "housing" } };
        var items = new List<FeedItem>
        {
            Item("a", "RTÉ News", "Dublin rents rise again as housing supply stalls", tier: 1, local: true, category: "local"),
            Item("b", "BBC News", "Mars rover finds ancient lake bed", tier: 1),
        };
        var clusters = StoryClusterer.Build(items, settings, new LocationSettings());
        var local = clusters.Single(c => c.Items.Any(i => i.Id == "a"));
        Assert.True(local.IsLocal);
        Assert.Equal("local", local.Category);
        Assert.Contains("housing", local.Matches);
        Assert.False(clusters.Single(c => c.Items.Any(i => i.Id == "b")).IsLocal);
    }

    [Fact]
    public void MutedTopicsSinkToTheBottom()
    {
        var settings = new NewsSettings { Muted = new() { "horoscope" } };
        var items = new List<FeedItem>
        {
            Item("x", "Paper", "Your weekly horoscope is here", tier: 1),
            Item("y", "Paper", "Budget talks resume at government buildings", tier: 2),
        };
        var clusters = StoryClusterer.Build(items, settings, new LocationSettings());
        Assert.Equal("y", clusters.First().Items[0].Id);
    }

    [Fact]
    public void ExtractiveSummaryPicksInformativeSentences()
    {
        var c = new StoryCluster
        {
            Id = "s", Title = "Council approves new cycle lanes",
            Items =
            {
                Item("1", "A", "Council approves new cycle lanes", summary: "The city council approved 12 km of new protected cycle lanes on Monday. Work begins in spring. Photo: Getty."),
                Item("2", "B", "Cycle lanes plan passes", summary: "Councillors voted 40 to 12 in favour of the protected cycle lanes plan, which will cost 30 million euro."),
            },
        };
        var s = StoryClusterer.ExtractiveSummary(c);
        Assert.False(s.IsAi);
        Assert.Contains("cycle lanes", s.Tldr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Photo", s.Tldr);
    }
}

public class IndicatorTests
{
    private static List<(long, double)> Series(Func<int, double> f, int n = 260) =>
        Enumerable.Range(0, n).Select(i => (DateTimeOffset.UtcNow.AddDays(i - n).ToUnixTimeSeconds(), f(i))).ToList();

    [Fact]
    public void RsiMatchesKnownBounds()
    {
        var up = Enumerable.Range(1, 30).Select(i => (double)i).ToList();
        Assert.Equal(100, TechnicalIndicators.Rsi(up));
        var zigzag = Enumerable.Range(0, 60).Select(i => 100 + (i % 2 == 0 ? 1.0 : -1.0)).ToList();
        Assert.InRange(TechnicalIndicators.Rsi(zigzag)!.Value, 40, 60);
    }

    [Fact]
    public void SteadyUptrendScoresBullish()
    {
        var ind = TechnicalIndicators.Compute("UP", Series(i => 100 * Math.Pow(1.002, i) * (1 + 0.004 * Math.Sin(i))));
        Assert.Equal("uptrend", ind.Trend);
        Assert.Equal(TechSignal.Bullish, ind.Signal);
        Assert.True(ind.Ret3M > 0);
        Assert.NotEmpty(ind.Factors);
        Assert.InRange(ind.Position52!.Value, 0.7, 1.0);
    }

    [Fact]
    public void SteadyDowntrendScoresBearish()
    {
        var ind = TechnicalIndicators.Compute("DOWN", Series(i => 100 * Math.Pow(0.997, i) * (1 + 0.004 * Math.Sin(i))));
        Assert.Equal("downtrend", ind.Trend);
        Assert.Equal(TechSignal.Bearish, ind.Signal);
        Assert.True(ind.FromHigh < -20);
    }

    [Fact]
    public void HandlesShortHistoryGracefully()
    {
        var ind = TechnicalIndicators.Compute("NEW", Series(i => 10 + i, 12), livePrice: 25);
        Assert.Null(ind.Sma200);
        Assert.Equal(25, ind.Last);
        Assert.Equal(TechSignal.Neutral, ind.Signal);
    }

    [Fact]
    public void VolatilityIsAnnualisedPercent()
    {
        var flat = Series(i => 100.0, 40);
        Assert.Equal(0, TechnicalIndicators.Volatility(flat.Select(x => x.Item2).ToList())!.Value, 6);
    }
}
