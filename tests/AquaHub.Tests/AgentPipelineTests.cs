using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AquaHub.Core.Agents;
using AquaHub.Core.Markets;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;

namespace AquaHub.Tests;

/// <summary>
/// A stand-in network for <see cref="AquaHub.Core.Net.HttpFetcher"/>: it answers the real addresses the collectors use
/// (feeds, Yahoo Finance, Open-Meteo, Polymarket) from routes the test sets up, so nothing leaves the PC. Anything
/// without a route gets a 404, and every request is recorded.
/// </summary>
internal sealed class FakeNetwork : HttpMessageHandler
{
    private readonly List<(Func<Uri, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _routes = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeNetwork On(Func<Uri, bool> match, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _routes.Add((match, answer));
        return this;
    }

    /// <summary>Answers requests for an address starting with <paramref name="prefix"/> with the body.</summary>
    public FakeNetwork On(string prefix, string body, string contentType = "application/json") =>
        On(u => u.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal), _ => Ok(body, contentType));

    public static HttpResponseMessage Ok(string body, string contentType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    public static string? Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == name) is { Length: 2 } kv ? Uri.UnescapeDataString(kv[1]) : null;

    public int Count(string prefix) { lock (Requests) return Requests.Count(r => r.RequestUri!.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal)); }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        foreach (var (match, answer) in _routes)
            if (match(request.RequestUri!)) return Task.FromResult(answer(request));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

[CollectionDefinition("Brief requests")]
public sealed class BriefRequestsCollection
{
    // BriefingAgent.ManualFlag is shared: the classes that set or use it run one after another.
}

/// <summary>
/// The collectors and AI agents end to end without the internet or a GPU: a fake network answers the real addresses
/// and a scripted model server stands in for Ollama. (The Phase 1 audit found no tests for any of them.)
/// </summary>
[Collection("Brief requests")]
public class AgentPipelineTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Rss(string channel, params (string Title, string Link, double HoursAgo)[] items)
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\"?><rss version=\"2.0\"><channel><title>{channel}</title>");
        foreach (var (title, link, ago) in items)
            sb.Append(Inv, $"<item><title>{title}</title><link>{link}</link><description>{title}. More detail from {channel}.</description><pubDate>{DateTimeOffset.UtcNow.AddHours(-ago):R}</pubDate></item>");
        return sb.Append("</channel></rss>").ToString();
    }

    [Fact]
    public async Task NewsIsFetchedSortedIntoStoriesAndOnlyDownloadedAgainWhenItChanged()
    {
        var net = new FakeNetwork()
            .On(u => u.AbsoluteUri == "https://wire.example/news.xml", req =>
            {
                // A conditional request for what it already has gets "not modified".
                if (req.Headers.TryGetValues("If-None-Match", out var tags) && tags.Contains("\"v1\"")) return new HttpResponseMessage(HttpStatusCode.NotModified);
                var ok = FakeNetwork.Ok(Rss("The Wire",
                    ("Dublin Bus drivers to strike on Friday over pay", "https://wire.example/bus", 1),
                    ("Budget 2027 increases childcare subsidies", "https://wire.example/budget", 2)), "application/rss+xml");
                ok.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return ok;
            })
            .On("https://paper.example/feed", Rss("The Paper", ("Dublin Bus strike on Friday as drivers reject pay offer", "https://paper.example/bus", 1.5)), "application/rss+xml");
        using var t = new TestContext(net);
        t.Ctx.Settings.Update(s => s.News.Sources = new()
        {
            new() { Id = "wire", Name = "The Wire", Url = "https://wire.example/news.xml", Tier = 1 },
            new() { Id = "paper", Name = "The Paper", Url = "https://paper.example/feed" },
            new() { Id = "gone", Name = "Gone", Url = "https://gone.example/rss" },
        });

        var scout = await new NewsScoutAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.True(scout.Ok);
        Assert.True(scout.Changed);
        Assert.Equal("3 new from 2/3 sources · unavailable: gone", scout.Message);
        Assert.NotNull(t.Ctx.State.FreshAt("news"));

        var curator = await new StoryCuratorAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.True(curator.Ok);
        var stories = t.Ctx.State.Stories;
        Assert.Equal(2, stories.Count);
        var strike = Assert.Single(stories, c => c.SourceCount == 2); // two outlets, one story
        Assert.Contains("Dublin Bus", strike.Title);
        Assert.Equal("2 stories (1 multi-source) from 3 articles", curator.Message);

        // Nothing new: the wire answers "not modified", the paper's items are already stored.
        var again = await new NewsScoutAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.Equal("0 new from 2/3 sources · unavailable: gone", again.Message);
        Assert.False(again.Changed); // so the curator and editor aren't woken for nothing
        Assert.Equal(2, net.Count("https://wire.example/"));
    }

    [Fact]
    public async Task WhenEveryNewsSourceFailsTheLastHeadlinesStay()
    {
        using var t = new TestContext(new FakeNetwork());
        t.Ctx.Settings.Update(s => s.News.Sources = new() { new() { Id = "gone", Name = "Gone", Url = "https://gone.example/rss" } });
        var result = await new NewsScoutAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("All news sources failed", result.Message);
        Assert.Null(t.Ctx.State.FreshAt("news"));
    }

    /// <summary>Yahoo's chart (a year of daily closes) and spark (today's prices) answers for a few symbols.</summary>
    private static FakeNetwork Yahoo(Dictionary<string, (string Currency, double Price, double Previous)> symbols)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new FakeNetwork()
            .On(u => u.AbsoluteUri.StartsWith("https://query1.finance.yahoo.com/v8/finance/chart/", StringComparison.Ordinal), req =>
            {
                var symbol = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath["/v8/finance/chart/".Length..]);
                if (!symbols.TryGetValue(symbol, out var s)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var days = Enumerable.Range(0, 40).Select(i => now - (40 - i) * 86400L).ToArray();
                var closes = days.Select((_, i) => Math.Round(s.Previous * (0.9 + i * 0.0025), 4)).ToArray();
                var body = new JsonObject
                {
                    ["chart"] = new JsonObject
                    {
                        ["result"] = new JsonArray(new JsonObject
                        {
                            ["meta"] = new JsonObject
                            {
                                ["currency"] = s.Currency, ["shortName"] = symbol + " Inc", ["instrumentType"] = symbol.EndsWith("=X") ? "CURRENCY" : "EQUITY",
                                ["regularMarketPrice"] = s.Price, ["previousClose"] = s.Previous, ["fullExchangeName"] = "NasdaqGS",
                            },
                            ["timestamp"] = new JsonArray(days.Select(d => (JsonNode)d).ToArray()),
                            ["indicators"] = new JsonObject { ["quote"] = new JsonArray(new JsonObject { ["close"] = new JsonArray(closes.Select(c => (JsonNode)c).ToArray()) }) },
                        }),
                    },
                };
                return FakeNetwork.Ok(body.ToJsonString());
            })
            .On(u => u.AbsoluteUri.StartsWith("https://query1.finance.yahoo.com/v8/finance/spark", StringComparison.Ordinal), req =>
            {
                var body = new JsonObject();
                foreach (var symbol in (FakeNetwork.Query(req.RequestUri!, "symbols") ?? "").Split(','))
                    if (symbols.TryGetValue(symbol, out var s))
                        body[symbol] = new JsonObject
                        {
                            ["timestamp"] = new JsonArray(now - 600, now - 300),
                            ["close"] = new JsonArray(Math.Round((s.Price + s.Previous) / 2, 4), s.Price),
                            ["previousClose"] = s.Previous,
                        };
                return FakeNetwork.Ok(body.ToJsonString());
            });
    }

    [Fact]
    public async Task MarketWatchPricesTheWatchlistAndValuesHoldingsInYourCurrency()
    {
        var net = Yahoo(new()
        {
            ["NVDA"] = ("USD", 180.5, 178.0),
            ["USDEUR=X"] = ("EUR", 0.92, 0.91),
        });
        using var t = new TestContext(net);
        t.Ctx.Settings.Update(s =>
        {
            s.Markets.Indices = new();
            s.Markets.Macro = new();
            s.Markets.Watchlist = new() { new() { Symbol = "NVDA", Name = "NVIDIA", Shares = 2, CostBasis = 100 } };
            s.Markets.BaseCurrency = "EUR";
        });

        var result = await new MarketWatchAgent().RunAsync(t.Ctx, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var nvda = t.Ctx.State.Quotes["NVDA"];
        Assert.Equal(180.5, nvda.Price);
        Assert.Equal(178.0, nvda.PreviousClose);
        Assert.Equal("USD", nvda.Currency);
        Assert.Equal("open", nvda.MarketState);
        Assert.True(t.Ctx.State.Indicators.ContainsKey("NVDA"), "a year of closes gives indicators");
        // The holding is valued in euro with the dollar's rate, fetched only because it's needed.
        Assert.True(t.Ctx.State.Fx.TryRate("USD", "EUR", out var rate));
        Assert.Equal(0.92, rate, 6);
        var value = Portfolio.Value(t.Ctx.S.Markets, t.Ctx.State.Quotes, t.Ctx.State.Fx);
        Assert.Equal("EUR", value.Currency);
        Assert.Equal(2 * 180.5 * 0.92, value.Value, 6);
        Assert.Equal("1 quotes (1 live), 1 with indicators, refreshed history for 2", result.Message);
        Assert.NotNull(t.Ctx.State.FreshAt("markets"));

        // History is kept for a day: the next run only asks for today's prices.
        var charts = net.Count("https://query1.finance.yahoo.com/v8/finance/chart/");
        await new MarketWatchAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.Equal(charts, net.Count("https://query1.finance.yahoo.com/v8/finance/chart/"));
    }

    [Fact]
    public async Task WithoutTheQuoteServiceMarketWatchKeepsTheLastQuotes()
    {
        using var t = new TestContext(new FakeNetwork());
        t.Ctx.Settings.Update(s => s.Markets.Watchlist = new() { new() { Symbol = "NVDA", Name = "NVIDIA" } });
        var result = await new MarketWatchAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("Quote service unavailable", result.Message);
    }

    [Fact]
    public async Task TheWeatherComesFromOpenMeteoForTheChosenPlaceOnly()
    {
        var hours = Enumerable.Range(0, 25).Select(i => DateTime.Today.AddHours(i).ToString("yyyy-MM-ddTHH:mm", Inv)).ToArray();
        var days = Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i).ToString("yyyy-MM-dd", Inv)).ToArray();
        JsonArray Nums(int n, double v) => new(Enumerable.Range(0, n).Select(i => (JsonNode)(v + i % 3)).ToArray());
        JsonArray Strs(IEnumerable<string> s) => new(s.Select(x => (JsonNode)x).ToArray());
        var forecast = new JsonObject
        {
            ["utc_offset_seconds"] = 3600,
            ["current"] = new JsonObject
            {
                ["temperature_2m"] = 14.2, ["apparent_temperature"] = 12.6, ["relative_humidity_2m"] = 81, ["precipitation"] = 0.2,
                ["weather_code"] = 61, ["wind_speed_10m"] = 18.4, ["is_day"] = 1,
            },
            ["hourly"] = new JsonObject
            {
                ["time"] = Strs(hours), ["temperature_2m"] = Nums(25, 12), ["precipitation_probability"] = Nums(25, 40), ["weather_code"] = Nums(25, 3), ["is_day"] = Nums(25, 0),
            },
            ["daily"] = new JsonObject
            {
                ["time"] = Strs(days), ["weather_code"] = Nums(7, 61), ["temperature_2m_max"] = Nums(7, 16), ["temperature_2m_min"] = Nums(7, 9),
                ["precipitation_probability_max"] = Nums(7, 60), ["sunrise"] = Strs(days.Select(d => d + "T07:21")), ["sunset"] = Strs(days.Select(d => d + "T19:02")),
                ["uv_index_max"] = Nums(7, 2),
            },
        };
        var net = new FakeNetwork().On("https://api.open-meteo.com/v1/forecast?", forecast.ToJsonString());
        using var t = new TestContext(net);

        // No place yet: nothing to look up, and nobody is asked.
        var none = await new WeatherAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.Equal("No place chosen yet (Settings › Location & weather)", none.Message);
        Assert.Empty(net.Requests);

        t.Ctx.Settings.Update(s => s.Location = new LocationSettings { City = "Dublin", Country = "IE", Latitude = 53.3498, Longitude = -6.2603 });
        var result = await new WeatherAgent().RunAsync(t.Ctx, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var url = Assert.Single(net.Requests).RequestUri!;
        Assert.Equal("53.3498", FakeNetwork.Query(url, "latitude"));
        Assert.Equal("-6.2603", FakeNetwork.Query(url, "longitude"));
        var w = t.Ctx.State.Weather!;
        Assert.Equal(14.2, w.Now!.Temp);
        Assert.Equal(25, w.Hours.Count);
        Assert.Equal(7, w.Days.Count);
        Assert.StartsWith("14° ", result.Message, StringComparison.Ordinal);
        Assert.EndsWith(" in Dublin", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PredictionMarketsAreTrackedOnceEachBusiestFirst()
    {
        static JsonObject Event(string id, string title, double volume, string yes) => new()
        {
            ["id"] = id, ["title"] = title, ["slug"] = id, ["endDate"] = DateTimeOffset.UtcNow.AddDays(40).ToString("O", Inv),
            ["volume24hr"] = volume, ["volume"] = volume * 8, ["tags"] = new JsonArray(new JsonObject { ["slug"] = "economy" }),
            ["markets"] = new JsonArray(new JsonObject
            {
                ["outcomes"] = "[\"Yes\", \"No\"]", ["outcomePrices"] = $"[\"{yes}\", \"{(1 - double.Parse(yes, Inv)).ToString(Inv)}\"]",
                ["oneDayPriceChange"] = 0.03, ["active"] = true, ["closed"] = false,
            }),
        };
        var events = new JsonArray(
            Event("101", "Will the ECB cut rates in December?", 250_000, "0.62"),
            Event("102", "Will Ireland's inflation fall below 2% this year?", 90_000, "0.35"),
            Event("103", "A quiet market nobody trades", 100, "0.5"),
            new JsonObject { ["id"] = "104", ["title"] = "Who wins the Champions League?", ["tags"] = new JsonArray(new JsonObject { ["slug"] = "soccer" }), ["markets"] = new JsonArray() });
        var net = new FakeNetwork().On("https://gamma-api.polymarket.com/events?", events.ToJsonString());
        using var t = new TestContext(net);
        t.Ctx.Settings.Update(s => s.Predictions.PolymarketTags = new() { "economy", "politics" });

        var result = await new PredictionScoutAgent().RunAsync(t.Ctx, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, net.Count("https://gamma-api.polymarket.com/")); // one request per topic
        var markets = t.Ctx.State.Predictions;
        Assert.Equal(new[] { "Will the ECB cut rates in December?", "Will Ireland's inflation fall below 2% this year?" }, markets.Select(m => m.Title));
        Assert.Equal(0.62, markets[0].Outcomes.First(o => o.Label == "Yes").Probability, 6);
        Assert.Equal("2 markets tracked", result.Message);
    }

    [Fact]
    public async Task SocialPostsComeFromYourSubredditsAndHackerNewsInOneRequestEach()
    {
        var updated = DateTimeOffset.UtcNow.AddHours(-1).ToString("O", Inv);
        var reddit = $"""
            <?xml version="1.0" encoding="UTF-8"?><feed xmlns="http://www.w3.org/2005/Atom"><title>r/dublin+ireland</title>
            <entry><title>Best chipper in Dublin?</title><link href="https://www.reddit.com/r/dublin/comments/abc/best_chipper/"/><updated>{updated}</updated>
            <author><name>/u/someone</name></author><content type="html">&lt;p&gt;Asking for a friend&lt;/p&gt;</content></entry></feed>
            """;
        var hn = new JsonObject
        {
            ["hits"] = new JsonArray(new JsonObject
            {
                ["objectID"] = "4242", ["title"] = "Show HN: A tiny database", ["url"] = "https://db.example/", ["author"] = "pg",
                ["created_at_i"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds(), ["points"] = 120, ["num_comments"] = 45,
            }),
        };
        var net = new FakeNetwork()
            .On("https://www.reddit.com/r/dublin+ireland/hot/.rss", reddit, "application/atom+xml")
            .On("https://hn.algolia.com/api/v1/search?tags=front_page", hn.ToJsonString());
        using var t = new TestContext(net);
        t.Ctx.Settings.Update(s =>
        {
            s.Social.Subreddits = new() { "dublin", "ireland" };
            s.Social.BlueskyTrending = false;
            s.Social.YouTubeChannels = new();
        });

        var result = await new SocialScoutAgent().RunAsync(t.Ctx, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("2 new posts from 2/2 sources", result.Message);
        Assert.Equal(1, net.Count("https://www.reddit.com/")); // every subreddit in one request (Reddit limits anonymous clients)
        var posts = t.Ctx.State.Social;
        Assert.Contains(posts, p => p.Platform == "reddit" && p.Title == "Best chipper in Dublin?" && p.IsLocal);
        var story = Assert.Single(posts, p => p.Platform == "hackernews");
        Assert.Equal((120, 45), (story.Score, story.Comments));
        Assert.Equal("https://news.ycombinator.com/item?id=4242", story.CommentsUrl);
    }

    [Fact]
    public async Task TheAgendaMergesYourCalendarWithPublicHolidays()
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:dentist-1\r\nSUMMARY:Dentist\r\n" +
                  $"DTSTART:{tomorrow.AddHours(10).ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}\r\nDURATION:PT30M\r\nEND:VEVENT\r\n" +
                  "BEGIN:VEVENT\r\nUID:old-1\r\nSUMMARY:Last year's party\r\nDTSTART:20251201T190000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var holiday = DateTime.Today.AddDays(5);
        var holidays = new JsonArray(
            new JsonObject { ["date"] = holiday.ToString("yyyy-MM-dd", Inv), ["localName"] = "Lá Saoire i mí Dheireadh Fómhair", ["name"] = "October Bank Holiday" },
            new JsonObject { ["date"] = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd", Inv), ["localName"] = "Gone", ["name"] = "Long gone" });
        var net = new FakeNetwork()
            .On("https://cal.example/work.ics", ics, "text/calendar")
            .On("https://date.nager.at/api/v3/PublicHolidays/", holidays.ToJsonString());
        using var t = new TestContext(net);
        t.Ctx.Settings.Update(s =>
        {
            s.Events.Calendars = new() { new() { Name = "Work", Url = "webcal://cal.example/work.ics" } };
            s.Events.HolidayCountries = new() { "IE" };
            s.Events.Economic = false;
            s.Events.Earnings = false;
        });

        var result = await new EventsScoutAgent().RunAsync(t.Ctx, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("2 upcoming items", result.Message);
        var events = t.Ctx.State.Events;
        Assert.Equal(new[] { "Dentist", "October Bank Holiday" }, events.Select(e => e.Title)); // in date order, nothing past
        Assert.Equal(EventKind.Holiday, events[1].Kind);
        Assert.Equal(1, net.Count("https://cal.example/")); // webcal:// is fetched over HTTPS
    }

    // ───────────────────────────── AI agents ─────────────────────────────

    private static async Task<TestContext> WithModel(FakeOllama server)
    {
        var t = new TestContext(new FakeNetwork());
        t.Ctx.Settings.Update(s => s.Ai.Endpoint = server.Endpoint);
        var health = await t.Ctx.Llm.CheckAsync();
        Assert.True(health.Available, health.Error);
        return t;
    }

    private static List<StoryCluster> TwoStories() => new()
    {
        new()
        {
            Id = "s-bus", Title = "Dublin Bus drivers to strike on Friday over pay", Category = "ireland", Importance = 9, ContentKey = "bus-1",
            Items = { AskFixtures.Item("b1", "RTÉ News", "Dublin Bus drivers to strike on Friday over pay"), AskFixtures.Item("b2", "The Journal", "Dublin Bus strike on Friday") },
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-2), Latest = DateTimeOffset.UtcNow.AddHours(-1),
        },
        new()
        {
            Id = "s-budget", Title = "Budget 2027 increases childcare subsidies", Category = "ireland", Importance = 7, ContentKey = "budget-1",
            Items = { AskFixtures.Item("c1", "Irish Times", "Budget 2027 increases childcare subsidies") },
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-3), Latest = DateTimeOffset.UtcNow.AddHours(-3),
        },
    };

    [Fact]
    public async Task TheNewsEditorSummarisesTopStoriesWithTheModelOnce()
    {
        using var server = new FakeOllama
        {
            Json = req => req.ToJsonString().Contains("Budget 2027", StringComparison.Ordinal)
                ? """{"headline":"Budget raises childcare subsidies","tldr":"Families get more help with childcare from January.","key_points":["Subsidies rise by a fifth"],"why_it_matters":"Childcare is a large cost for young families."}"""
                : """{"headline":"Dublin Bus drivers strike on Friday","tldr":"Read more: Drivers rejected the pay offer, so buses stop on Friday.","key_points":["Talks broke down on Wednesday","Services stop from 6am"],"why_it_matters":"Commuters need another way in."}""",
        };
        using var t = await WithModel(server);
        t.Ctx.State.SetStories(TwoStories());

        var result = await new NewsEditorAgent().RunAsync(t.Ctx, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("Summarised 2 stories", result.Message);
        var bus = t.Ctx.State.Stories.First(c => c.Id == "s-bus").Summary!;
        Assert.True(bus.IsAi);
        Assert.Equal("qwen3.5:9b", bus.Model);
        Assert.Equal("Drivers rejected the pay offer, so buses stop on Friday.", bus.Tldr); // the feed's "Read more:" is dropped
        Assert.Equal(2, bus.KeyPoints.Count);
        Assert.Equal("Budget raises childcare subsidies", t.Ctx.State.Stories.First(c => c.Id == "s-budget").Summary!.Headline);
        Assert.Equal(2, server.JsonChats.Count);

        // Summaries are kept per story content: nothing is asked again until the coverage changes.
        var again = await new NewsEditorAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.Equal("All top stories summarised", again.Message);
        Assert.Equal(2, server.JsonChats.Count);
    }

    [Fact]
    public async Task AiAgentsWaitWhileTheModelIsOffline()
    {
        // A model server that hangs up at once (a refused connection would take Windows a couple of seconds to give up on).
        var dead = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        dead.Start();
        _ = Task.Run(async () =>
        {
            try { while (true) (await dead.AcceptTcpClientAsync()).Close(); }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException) { }
        });
        using var t = new TestContext(new FakeNetwork());
        t.Ctx.Settings.Update(s => s.Ai.Endpoint = $"http://127.0.0.1:{((IPEndPoint)dead.LocalEndpoint).Port}");
        await t.Ctx.Llm.CheckAsync();
        dead.Stop();
        t.Ctx.State.SetStories(TwoStories());

        var editor = await new NewsEditorAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.True(editor.Waiting);
        Assert.Contains("Model server offline", editor.Message);

        // The brief still appears, as a compact one built without the model.
        var brief = await new BriefingAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.True(brief.Waiting);
        Assert.StartsWith("Compact brief only", brief.Message, StringComparison.Ordinal);
        Assert.False(t.Ctx.State.Brief!.IsAi);
    }

    [Fact]
    public async Task TheChiefOfStaffWritesTheBriefFromEveryAgentsWork()
    {
        using var server = new FakeOllama
        {
            Json = _ => """
                {"title":"A strike day ahead","summary":"Buses stop on Friday after talks broke down. The budget brings more help with childcare.",
                 "sections":[{"title":"Top stories","icon":"news","bullets":["Dublin Bus drivers strike on Friday after rejecting a pay offer.","The budget raises childcare subsidies."]}]}
                """,
        };
        using var t = await WithModel(server);
        t.Ctx.State.SetStories(TwoStories());

        var result = await new BriefingAgent().RunAsync(t.Ctx, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var brief = t.Ctx.State.Brief!;
        Assert.True(brief.IsAi);
        Assert.Equal("A strike day ahead", brief.Title);
        Assert.Contains(brief.Sections, s => s.Title == "Top stories" && s.Bullets.Count == 2);
        // What the model was given came from the agents' work: the stories are in its prompt.
        Assert.Contains("Dublin Bus drivers to strike on Friday", Assert.Single(server.JsonChats).ToJsonString());

        // A current brief isn't rewritten on every run, only when it's due or asked for.
        var again = await new BriefingAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.Equal("Brief is current", again.Message);
        BriefingAgent.ManualFlag.Request();
        var asked = await new BriefingAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.True(asked.Ok);
        Assert.Equal(2, server.JsonChats.Count);
    }
}
