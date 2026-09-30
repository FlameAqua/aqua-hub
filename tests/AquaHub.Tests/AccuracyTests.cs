using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Analysis;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;

namespace AquaHub.Tests;

/// <summary>Regression tests for the news-accuracy fixes (real headline pairs that were wrongly merged or labelled).</summary>
public class ClusteringAccuracyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);

    private static FeedItem Item(string title, string source, int tier = 1, bool local = false, int minutesAgo = 60) => new()
    {
        Id = "i" + Hash.Short(source, title),
        Kind = ItemKind.News,
        SourceId = source.ToLowerInvariant().Replace(" ", "-"),
        SourceName = source,
        Title = title,
        Tier = tier,
        IsLocal = local,
        Category = local ? "local" : "world",
        Published = Now.AddMinutes(-minutesAgo),
    };

    private static List<StoryCluster> Cluster(params FeedItem[] items) =>
        StoryClusterer.Build(items, new NewsSettings(), new LocationSettings(), Now);

    [Fact]
    public void AShootingInDublinCaliforniaIsNotMergedWithShootingsInSouthAfrica()
    {
        var clusters = Cluster(
            Item("Two mass shootings in South Africa leave 27 dead", "BBC News"),
            Item("Gunmen kill 18 in shooting at tavern and sports ground in South Africa", "Reuters", minutesAgo: 50),
            Item("South Africa police hunt gunmen after mass shooting at sports ground", "Al Jazeera", tier: 2, minutesAgo: 40),
            Item("Man killed in shooting at Dublin Sports Grounds", "KRON4", tier: 3, local: true, minutesAgo: 30),
            Item("Dublin Sports Grounds shooting leaves one man dead, police say", "NBC Bay Area", tier: 3, local: true, minutesAgo: 20));

        var sa = clusters.Single(c => c.Items.Any(i => i.SourceName == "BBC News"));
        Assert.DoesNotContain(sa.Items, i => i.Title.Contains("Dublin"));
        Assert.False(sa.IsLocal);
        var dublin = clusters.Single(c => c.Items.Any(i => i.SourceName == "KRON4"));
        Assert.Equal(2, dublin.Items.Count);
    }

    [Fact]
    public void SharingOnlyCountryNamesDoesNotMakeTheSameStory()
    {
        var clusters = Cluster(
            Item("U.S., China to lower tariffs under new trade agreement", "Reuters"),
            Item("Giant pandas arrive in Atlanta as part of 10-year US-China agreement", "AP News", minutesAgo: 30));
        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public void DifferentWordingOfTheSameStoryStillMerges()
    {
        var clusters = Cluster(
            Item("Iran rejects US proposal on nuclear talks", "Reuters"),
            Item("Iran turns down US nuclear talks proposal", "BBC News", minutesAgo: 30));
        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].SourceCount);
    }

    [Fact]
    public void OneLocalHeadlineInABigInternationalStoryDoesNotMakeItLocal()
    {
        var clusters = Cluster(
            Item("EU leaders agree new migration pact in Brussels", "BBC News"),
            Item("EU leaders strike migration pact after Brussels summit", "Reuters", minutesAgo: 50),
            Item("Migration pact agreed by EU leaders at Brussels summit", "DW", tier: 2, minutesAgo: 45),
            Item("Taoiseach welcomes EU migration pact agreed in Brussels", "RTÉ News", local: true, minutesAgo: 40));
        var story = Assert.Single(clusters);
        Assert.False(story.IsLocal);
    }

    [Fact]
    public void LocalOutletsCoveringAnInternationalStoryDontMakeItLocal()
    {
        // The real cluster from 28 Sep: three Irish pieces (two from one outlet), one with an Irish angle.
        var clusters = Cluster(
            Item("Madonna leads VMAs winners as Swift makes history", "RTÉ News", local: true, minutesAgo: 90) with
                { Summary = "Madonna was the biggest winner at the 2026 MTV Video Music Awards, taking home seven prizes, while Taylor Swift made history as the most-awarded artist in the ceremony's history." },
            Item("Taylor Swift becomes most-awarded artist in MTV VMAs history, dedicates top prize to Dolly Parton", "Variety", tier: 3, minutesAgo: 80),
            Item("Taylor Swift unveils new video after breaking MTV VMAs record - and the list of winners", "Irish Independent", tier: 2, local: true, minutesAgo: 70) with
                { Summary = "Swift debuted the music video for her new song Patient Zero during the show" },
            Item("Watch: Madonna and Taylor Swift win big at the MTV VMAs", "BBC News", minutesAgo: 60) with
                { Summary = "Taylor Swift, Madonna, and LISA were among the big winners at the MTV Video Music Awards (VMAs) on Sunday evening in downtown Los Angeles." },
            Item("‘I got to speak to Taylor Swift, and Madonna and Paris Hilton were sitting right by me’ – Irish actor Craig Hogan on his star-studded night at the VMAs",
                "Irish Independent", tier: 2, local: true, minutesAgo: 50) with
                { Summary = "The MTV Video Music Awards (VMAs) took place on Sunday evening in downtown Los Angeles – and the 20-year-old Irish choreographer was right there" });
        var vmas = clusters.Single(c => c.Items.Any(i => i.SourceName == "RTÉ News"));
        Assert.False(vmas.IsLocal);
    }

    [Fact]
    public void MostlyLocalCoverageIsLocal()
    {
        var clusters = Cluster(
            Item("Dublin Bus fares to rise from January", "RTÉ News", local: true),
            Item("Dublin Bus confirms fare rise from January", "TheJournal.ie", tier: 2, local: true, minutesAgo: 30));
        Assert.True(Assert.Single(clusters).IsLocal);
    }

    [Theory]
    [InlineData("TheJournal.ie", "The Journal")]
    [InlineData("RTÉ News", "RTE")]
    [InlineData("BBC Business", "BBC News")]
    [InlineData("The Irish Times", "Irish Times")]
    public void OnePublisherIsCountedOnce(string a, string b) => Assert.Equal(Publishers.Key(a), Publishers.Key(b));

    [Fact]
    public void DifferentPublishersStayDistinct()
    {
        Assert.NotEqual(Publishers.Key("Irish Independent"), Publishers.Key("The Irish Times"));
        Assert.Equal("thetfordtimes", Publishers.Key("Thetford Times"));
    }
}

public class LocalNewsTests
{
    [Fact]
    public void TheLocalSearchIsQualifiedWithTheCountry()
    {
        var loc = new LocationSettings { City = "Dublin", Country = "IE", Language = "en-IE" };
        var url = RssSource.GoogleNewsUrl("{city} when:1d", loc); // older profiles' query is upgraded too
        Assert.Contains("q=Dublin%20Ireland%20when%3A1d", url);
        Assert.Contains("gl=IE", url);
    }

    [Fact]
    public void ForeignPublishersAreDroppedFromTheLocalSearch()
    {
        var hints = LocalePacks.PublisherHints(new LocationSettings { City = "Dublin", Country = "IE", Region = "Ireland" })!;
        Assert.True(RssSource.IsLocalPublisher("RTÉ", "https://www.rte.ie", hints));
        Assert.True(RssSource.IsLocalPublisher("The Irish Times", "https://www.irishtimes.com", hints));
        Assert.True(RssSource.IsLocalPublisher("Dublin Inquirer", "https://dublininquirer.com", hints));
        Assert.False(RssSource.IsLocalPublisher("KRON4", "https://www.kron4.com", hints));
        Assert.False(RssSource.IsLocalPublisher("Pleasanton Weekly", "https://www.pleasantonweekly.com", hints));
    }

    [Fact]
    public void AmericanCitiesAreNotFilteredByDomain() =>
        Assert.Null(LocalePacks.PublisherHints(new LocationSettings { City = "Dublin", Country = "US" }));

    [Fact]
    public void MovingToAnotherCountrySwapsTheIrishDefaults()
    {
        var s = new HubSettings();
        s.Location.City = "Berlin";
        s.Location.Region = "Germany";
        s.Location.Country = "DE";
        var note = LocalePacks.Apply(s, "IE");

        Assert.Contains("Germany", note);
        Assert.All(s.News.Sources.Where(x => x.Id is "rte" or "irishtimes" or "thejournal" or "independent-ie"), x => Assert.False(x.Enabled));
        var national = Assert.Single(s.News.Sources, x => x.Id == "gnews-national");
        Assert.Equal("{country} when:1d", national.Query); // no English Google News edition for Germany
        Assert.DoesNotContain(s.Markets.Indices, i => i.Symbol == "^ISEQ");
        Assert.Contains(s.Markets.Indices, i => i.Symbol == "^GDAXI");
        Assert.Equal(new[] { "DE" }, s.Events.HolidayCountries);
        Assert.Equal(new[] { "germany", "Berlin" }, s.Social.Subreddits);
        Assert.DoesNotContain("Taoiseach", s.Location.LocalKeywords);
        Assert.Contains("Berlin", s.Location.LocalKeywords);

        // …and back again restores the Irish outlets.
        s.Location.City = "Dublin";
        s.Location.Region = "Ireland";
        s.Location.Country = "IE";
        LocalePacks.Apply(s, "DE");
        Assert.All(s.News.Sources.Where(x => x.Id == "rte"), x => Assert.True(x.Enabled));
        Assert.DoesNotContain(s.News.Sources, x => x.Id == "gnews-national");
        Assert.Contains(s.Markets.Indices, i => i.Symbol == "^ISEQ");
    }

    [Fact]
    public void YourOwnSubredditsAreKept()
    {
        var s = new HubSettings();
        s.Social.Subreddits = new() { "ireland", "Dublin", "irishpersonalfinance" };
        s.Location.City = "London";
        s.Location.Country = "GB";
        LocalePacks.Apply(s, "IE");
        Assert.Contains("irishpersonalfinance", s.Social.Subreddits);
    }
}

public class ModelCheckTests
{
    private static PredictionMarket Market(string title, params (string Label, double P)[] outcomes) => new()
    {
        Id = "m" + Hash.Short(title),
        Title = title,
        Source = "polymarket",
        Outcomes = outcomes.Select(o => new PredictionOutcome(o.Label, o.P, null)).ToList(),
        IsBinary = outcomes.Length == 2,
    };

    [Fact]
    public void OddsMustComeFromTheMarketTheSentenceIsAbout()
    {
        var markets = new[]
        {
            Market("US-Iran ceasefire continues through September 30?", ("Yes", 0.92), ("No", 0.08)),
            Market("Israel x Iran ceasefire broken by October?", ("No", 0.97), ("Yes", 0.03)),
        };
        var f = new Foresight
        {
            Overview = "A 97% crowd consensus says the US-Iran ceasefire continues through September 30.",
            Items = new(),
        };
        var (checkedF, fixes) = FactCheck.Foresight(f, Array.Empty<HubEvent>(), markets, "fallback");
        Assert.Equal(1, fixes);
        Assert.Equal("fallback", checkedF.Overview);
    }

    [Fact]
    public void CorrectlyQuotedOddsAreKept()
    {
        var markets = new[] { Market("US-Iran ceasefire continues through September 30?", ("Yes", 0.92), ("No", 0.08)) };
        var f = new Foresight { Overview = "The crowd gives the US-Iran ceasefire a 92% chance of holding through September 30.", Items = new() };
        var (checkedF, fixes) = FactCheck.Foresight(f, Array.Empty<HubEvent>(), markets, "fallback");
        Assert.Equal(0, fixes);
        Assert.StartsWith("The crowd gives", checkedF.Overview);
    }

    [Fact]
    public void PostsThatDontShareAWordWithTheirTopicAreDropped()
    {
        var posts = new List<FeedItem>
        {
            new() { Id = "a", Kind = ItemKind.Social, SourceId = "r", SourceName = "r/ireland", Title = "Ireland players vote to play Israel despite withdrawal" },
            new() { Id = "b", Kind = ItemKind.Social, SourceId = "r", SourceName = "r/ireland", Title = "Any podcast similar to lines of enquiry?" },
        };
        var kept = FactCheck.RelatedPosts("Football match tensions", "Ireland's players and the Israel fixture", new[] { 1, 2 }, posts);
        Assert.Equal(new[] { 1 }, kept);
    }

    [Fact]
    public void AiTextExpiresWhenTheModelIsDown()
    {
        var now = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
        Assert.False(AiShelfLife.Expired(now.AddHours(-2), TimeSpan.FromHours(6), now: now));
        Assert.True(AiShelfLife.Expired(now.AddHours(-7), TimeSpan.FromHours(6), now: now));
        // A market view from yesterday evening describes yesterday's session.
        Assert.True(AiShelfLife.Expired(now.AddHours(-12).AddMinutes(-1), TimeSpan.FromHours(24), sameDay: true, now: now));
    }

    [Fact]
    public void TimeStampsSayWhichDay()
    {
        var now = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 28, 9, 0, 0)));
        Assert.Equal("08:30", TimeText.Stamp(now.AddMinutes(-30), now: now));
        Assert.StartsWith("yesterday ", TimeText.Stamp(now.AddHours(-12), now: now));
        Assert.Matches(@"^[A-Z][a-z]{2} \d\d:\d\d$", TimeText.Stamp(now.AddDays(-3), now: now));
    }
}

public class OllamaPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static AiSettings Settings(Action<AiSettings>? tweak = null)
    {
        var s = new AiSettings();
        tweak?.Invoke(s);
        return s;
    }

    private static OllamaObservation Seen(bool running = true, bool busy = false, double idleMinutes = 0, string? gpu = null,
        bool stoppedForGpu = false, double? clearMinutes = null) =>
        new(running, busy, Now.AddMinutes(-idleMinutes), gpu, stoppedForGpu, clearMinutes is { } c ? Now.AddMinutes(-c) : null);

    [Fact]
    public void AGameStopsTheServerAndItComesBackAfterwards()
    {
        Assert.Equal(OllamaAction.StopForGpu, OllamaPolicy.Decide(Settings(), Seen(gpu: "a full-screen game"), Now));
        Assert.Equal(OllamaAction.None, OllamaPolicy.Decide(Settings(), Seen(running: false, stoppedForGpu: true, clearMinutes: 1), Now));
        Assert.Equal(OllamaAction.StartAfterGpu, OllamaPolicy.Decide(Settings(), Seen(running: false, stoppedForGpu: true, clearMinutes: 3), Now));
        // It was stopped for some other reason: don't bring it back just because the GPU is free.
        Assert.Equal(OllamaAction.None, OllamaPolicy.Decide(Settings(), Seen(running: false, clearMinutes: 10), Now));
    }

    [Fact]
    public void AnUnusedServerIsStoppedToFreeMemory()
    {
        Assert.Equal(OllamaAction.StopIdle, OllamaPolicy.Decide(Settings(), Seen(idleMinutes: 16), Now));
        Assert.Equal(OllamaAction.None, OllamaPolicy.Decide(Settings(), Seen(idleMinutes: 10), Now));
        Assert.Equal(OllamaAction.None, OllamaPolicy.Decide(Settings(), Seen(idleMinutes: 60, busy: true), Now));
        Assert.Equal(OllamaAction.None, OllamaPolicy.Decide(Settings(s => s.StopOllamaIdleMinutes = 0), Seen(idleMinutes: 600), Now));
    }

    [Fact]
    public void ARemoteOrUnmanagedServerIsNeverTouched()
    {
        Assert.Equal(OllamaAction.None, OllamaPolicy.Decide(Settings(s => s.Endpoint = "http://192.168.1.20:11434"), Seen(idleMinutes: 600, gpu: "a game"), Now));
        Assert.Equal(OllamaAction.None, OllamaPolicy.Decide(Settings(s => s.ManageOllama = false), Seen(idleMinutes: 600, gpu: "a game"), Now));
        Assert.Equal(OllamaAction.None, OllamaPolicy.Decide(Settings(s => s.Provider = "openai"), Seen(idleMinutes: 600), Now));
    }
}

/// <summary>Fact checks against the real prediction-market list of 28 Sep (where "October" used to match the Fed market).</summary>
public class ForesightMatchingTests
{
    private static PredictionMarket Market(string title, params (string Label, double P)[] outcomes) => new()
    {
        Id = "m" + Hash.Short(title),
        Title = title,
        Outcomes = outcomes.Select(o => new PredictionOutcome(o.Label, o.P, null)).ToList(),
        IsBinary = outcomes.Length == 2 && outcomes[0].Label is "Yes" or "No",
    };

    private static readonly PredictionMarket[] Markets =
    {
        Market("Fed Decision in October?", ("25 bps increase", 0.655), ("No change", 0.335), ("50+ bps increase", 0.0085)),
        Market("Fed Decision in January?", ("25 bps increase", 0.48), ("No change", 0.46), ("25 bps decrease", 0.045)),
        Market("Saudi Oil Pipeline (East-West) restarts by...?", ("September 30", 0.29), ("October 15", 0.585), ("October 31", 0.7905)),
        Market("US-Iran ceasefire continues through...?", ("September 30", 0.967), ("October 31", 0.56), ("November 30", 0.415)),
        Market("Strait of Hormuz traffic returns to normal by October 31?", ("Yes", 0.06), ("No", 0.94)),
        Market("Next US-Iran senior diplomatic meeting by...?", ("September 30, 2026", 0.56), ("October 31, 2026", 0.69)),
        Market("Brazil Presidential Election", ("Flávio Bolsonaro", 0.5765), ("Luiz Inácio Lula da Silva", 0.415)),
        Market("Next French Presidential Election", ("Marine Le Pen", 0.3965), ("Édouard Philippe", 0.195)),
    };

    private static (Foresight, int) Check(string title, string detail) =>
        FactCheck.Foresight(new Foresight { Overview = "", Items = new() { new ForesightItem { Title = title, When = "by Oct 31", Detail = detail } } },
            Array.Empty<HubEvent>(), Markets, "fallback");

    [Fact]
    public void ACorrectFigureIsKept()
    {
        var (f, fixes) = Check("Saudi Oil Pipeline Restart Date", "Traders give a 79% chance the East-West pipeline restarts by October 31.");
        Assert.Equal(0, fixes);
        Assert.Contains("79%", f.Items.Single().Detail);
    }

    [Fact]
    public void AWrongFigureIsReplacedWithTheRightMarketsLeadNeverAnothers()
    {
        var (f, fixes) = Check("Saudi Oil Pipeline Restart Date", "Traders give a 95% chance the East-West pipeline restarts by October 31.");
        Assert.Equal(1, fixes);
        var detail = f.Items.Single().Detail;
        Assert.DoesNotContain("bps", detail);
        Assert.Contains("October 31", detail);
        Assert.Contains("79%", detail);
    }

    [Fact]
    public void WithoutAClearMarketTheUnsupportedSentenceIsRemovedNotRewritten()
    {
        var (f, fixes) = Check("Rate moves ahead", "Central banks meet in October. Traders see a 72% chance of a surprise.");
        Assert.Equal(1, fixes);
        var detail = f.Items.Single().Detail;
        Assert.Equal("Central banks meet in October.", detail);
    }

    [Fact]
    public void MonthsAloneNeverIdentifyAMarket()
    {
        Assert.Null(FactCheck.MatchMarket("Something happens by October 31", Markets, out _));
        Assert.Same(Markets[2], FactCheck.MatchMarket("Saudi pipeline restart", Markets, out var clear));
        Assert.True(clear);
    }
}

public class LocalePackLeftoverTests
{
    [Fact]
    public void EveryIrishDefaultOutletIsSwitchedOffAbroad()
    {
        var s = new HubSettings();
        s.Location.City = "Paris";
        s.Location.Country = "FR";
        LocalePacks.Apply(s, "IE");
        Assert.False(s.News.Sources.Single(x => x.Id == "siliconrepublic").Enabled);
    }

    [Fact]
    public void AnIndexYouAddedYourselfIsKept()
    {
        var s = new HubSettings();
        s.Markets.Indices.RemoveAll(i => i.Symbol == "^ISEQ");
        s.Markets.Indices.Add(new WatchSymbol { Symbol = "^ISEQ", Name = "ISEQ All Share Index", Kind = "index" });
        s.Location.City = "Paris";
        s.Location.Country = "FR";
        LocalePacks.Apply(s, "IE");
        Assert.Contains(s.Markets.Indices, i => i.Symbol == "^ISEQ");
        Assert.Contains(s.Markets.Indices, i => i.Symbol == "^FCHI");
    }
}

public class PostTitleTests
{
    [Theory]
    [InlineData("RE: https://social.crem.in/@jonathan/117345 I agree with this completely", "I agree with this completely")]
    [InlineData("Great read on housing https://example.com/a-long-link", "Great read on housing")]
    [InlineData("https://www.rte.ie/news/2026/0928/story", "Link to www.rte.ie")]
    [InlineData("Re: the council vote on cycle lanes", "Re: the council vote on cycle lanes")]
    public void LinksNeverBecomeTheTitle(string text, string expected) => Assert.Equal(expected, HtmlText.PostTitle(text));

    [Fact]
    public void ALinkOnlyPostUsesTheCardTitle() =>
        Assert.Equal("Dublin Bus fares to rise", HtmlText.PostTitle("https://www.rte.ie/news/2026/0928/story", "Dublin Bus fares to rise"));
}

public class OfflineAskTests
{
    [Fact]
    public void AgendaAndOddsQuestionsAreAnsweredFromTheHubsOwnData()
    {
        using var t = new TestContext();
        var now = DateTimeOffset.Now;
        t.Ctx.State.SetEvents(new List<HubEvent> { new() { Id = "e1", Title = "Dentist", Start = now.AddDays(2) } });
        t.Ctx.State.SetPredictions(new List<PredictionMarket>
        {
            new() { Id = "p1", Title = "Brazil Presidential Election", Outcomes = new() { new("Flávio Bolsonaro", 0.58, null), new("Lula", 0.41, null) } },
        });
        var ask = new AskService(t.Ctx.Db, t.Ctx.State, t.Ctx.Llm, () => t.Ctx.Settings.Current);
        // A news item that merely contains "week" must not be cited for an agenda question.
        var context = new AskContext("", new List<Citation> { new(1, "Fashion week opens in Paris", "BBC News", null) });

        var agenda = ask.OfflineAnswer("What's on my agenda this week?", context, "the local model isn't available", now);
        Assert.Contains("Dentist", agenda);
        Assert.DoesNotContain("Fashion", agenda);

        var odds = ask.OfflineAnswer("What do prediction markets expect next?", context, "the local model isn't available", now);
        Assert.Contains("Flávio Bolsonaro 58%", odds);
    }

    [Fact]
    public void NewsIsCitedWhenItMatchesTheQuestion()
    {
        using var t = new TestContext();
        var ask = new AskService(t.Ctx.Db, t.Ctx.State, t.Ctx.Llm, () => t.Ctx.Settings.Current);
        var context = new AskContext("", new List<Citation> { new(1, "Dublin Bus fares to rise from January", "RTÉ News", null) });
        var answer = ask.OfflineAnswer("Are Dublin Bus fares going up?", context, "the local model isn't available");
        Assert.Contains("[1]", answer);
    }
}

public class BriefCheckTests
{
    [Fact]
    public void TheSectionTitleDecidesWhatBelongsInIt()
    {
        // The real brief of 28 Sep: the model titled a section "Social Pulse" but gave it the predictions icon.
        using var t = new TestContext();
        t.Ctx.State.SetPredictions(new List<PredictionMarket>
        {
            new() { Id = "fr", Title = "Next French Presidential Election", Outcomes = new() { new("Marine Le Pen", 0.3965, null), new("Édouard Philippe", 0.195, null) } },
        });
        var brief = new DailyBrief
        {
            Sections = new()
            {
                new BriefSection { Title = "Agenda", Icon = "agenda", Bullets = new() { "Crowds expect the Fed to raise rates in October" } },
                new BriefSection { Title = "Social Pulse", Icon = "predictions", Bullets = new() { "Crowd forecast for French Presidential Election predicts Marine Le Pen at 40%." } },
            },
        };
        var (tidied, moved) = BriefCheck.Tidy(brief, t.Ctx.State, t.Ctx.Settings.Current);
        Assert.Equal(1, moved);
        Assert.Single(tidied.Sections); // the emptied "Social Pulse" section is gone
        Assert.Contains(tidied.Sections[0].Bullets, b => b.Contains("Le Pen"));
    }

    [Fact]
    public void AForecastFiledUnderSocialPulseMovesToTheAgenda()
    {
        using var t = new TestContext();
        t.Ctx.State.SetPredictions(new List<PredictionMarket>
        {
            new() { Id = "fr", Title = "Next French Presidential Election", Outcomes = new() { new("Marine Le Pen", 0.3965, null), new("Édouard Philippe", 0.195, null) } },
        });
        t.Ctx.State.SetPulse(new SocialPulse
        {
            Overview = "Football dominates.",
            Topics = new() { new PulseTopic { Title = "Football match tensions", Summary = "Fans debate the Ireland v Israel fixture and player protests." } },
        });
        var brief = new DailyBrief
        {
            Sections = new()
            {
                new BriefSection { Title = "Social Pulse", Icon = "local", Bullets = new() { "Crowd forecast for French Presidential Election predicts Marine Le Pen at 40%", "Fans debate the Israel fixture and player protests" } },
                new BriefSection { Title = "Agenda", Icon = "agenda", Bullets = new() { "US inflation data due Wednesday" } },
            },
        };
        var (tidied, moved) = BriefCheck.Tidy(brief, t.Ctx.State, t.Ctx.Settings.Current);
        Assert.Equal(1, moved);
        Assert.DoesNotContain(tidied.Sections[0].Bullets, b => b.Contains("Le Pen"));
        Assert.Contains(tidied.Sections[1].Bullets, b => b.Contains("Le Pen"));
        Assert.Contains(tidied.Sections[0].Bullets, b => b.Contains("Israel fixture"));
    }
}
