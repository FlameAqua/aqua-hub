using System.Text.Json;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;

namespace AquaHub.Tests;

/// <summary>Profiles set up for a place the way onboarding leaves them (a new profile has none).</summary>
internal static class TestPlaces
{
    public static readonly GeoPlace Dublin = new("Dublin", "Leinster", "Ireland", "IE", 53.35, -6.26, "Europe/Dublin");
    public static readonly GeoPlace Galway = new("Galway", "Connacht", "Ireland", "IE", 53.27, -9.05, "Europe/Dublin");
    public static readonly GeoPlace Berlin = new("Berlin", "Land Berlin", "Germany", "DE", 52.52, 13.41, "Europe/Berlin");
    public static readonly GeoPlace Munich = new("Munich", "Bavaria", "Germany", "DE", 48.14, 11.58, "Europe/Berlin");
    public static readonly GeoPlace Paris = new("Paris", "Île-de-France", "France", "FR", 48.85, 2.35, "Europe/Paris");
    public static readonly GeoPlace London = new("London", "England", "United Kingdom", "GB", 51.51, -0.13, "Europe/London");
    public static readonly GeoPlace Austin = new("Austin", "Texas", "United States", "US", 30.27, -97.74, "America/Chicago");

    /// <summary>Settings for someone who chose <paramref name="place"/> (Dublin unless said otherwise).</summary>
    public static HubSettings Settings(GeoPlace? place = null)
    {
        var s = new HubSettings();
        LocalePacks.ChoosePlace(s, place ?? Dublin);
        return s;
    }

    public static LocationSettings Location(GeoPlace? place = null) => Settings(place).Location;
}

public class NoPlaceTests
{
    private static readonly string[] IrishOutlets = { "rte", "irishtimes", "thejournal", "independent-ie", "siliconrepublic" };

    [Fact]
    public void ANewProfileIsNobodysCity()
    {
        var s = new HubSettings();
        Assert.False(s.Location.IsSet);
        Assert.False(s.Location.HasCoordinates);
        Assert.Equal("", s.Location.Label);
        Assert.Equal("Local", s.Location.LocalTitle);
        Assert.Empty(s.Location.LocalKeywords);
        Assert.DoesNotContain(s.News.Sources, x => IrishOutlets.Contains(x.Id));
        Assert.Empty(s.Social.Subreddits);
        Assert.Empty(s.Social.MastodonHashtags);
        Assert.Equal("mastodon.social", s.Social.MastodonInstance);
        Assert.Empty(s.Events.HolidayCountries);
        Assert.DoesNotContain(s.Markets.Indices, i => i.Symbol == "^ISEQ");
        Assert.DoesNotContain(s.Markets.Watchlist, w => w.Symbol.EndsWith(".IR", StringComparison.Ordinal));
        Assert.Equal("USD", s.Markets.BaseCurrency);
    }

    [Fact]
    public void ASavedPlaceSurvivesARoundTrip()
    {
        // Older profiles have Dublin written out in full, so they keep it; the derived values aren't saved.
        var json = JsonSerializer.Serialize(TestPlaces.Settings(), AquaHub.Core.Util.JsonUtil.Options);
        Assert.DoesNotContain("isSet", json);
        Assert.DoesNotContain("localTitle", json);
        var back = AquaHub.Core.Util.JsonUtil.Deserialize<HubSettings>(json)!;
        Assert.Equal("Dublin, Ireland", back.Location.Label);
        Assert.True(back.Location.HasCoordinates);
    }

    [Fact]
    public void ASearchForYourPlaceWaitsUntilThereIsOne()
    {
        var local = new HubSettings().News.Sources.Single(x => x.Id == "gnews-local");
        var feed = new NewsSource { Id = "x", Url = "https://example.com/feed" };
        var countryWide = new NewsSource { Id = "n", Kind = "google", Query = "{country} when:1d" };
        var none = new LocationSettings();
        Assert.False(RssSource.CanFetch(local, none));
        Assert.False(RssSource.CanFetch(countryWide, none));
        Assert.True(RssSource.CanFetch(feed, none));
        var dublin = TestPlaces.Location();
        Assert.True(RssSource.CanFetch(local, dublin));
        Assert.True(RssSource.CanFetch(countryWide, dublin));
    }

    [Fact]
    public async Task TheWeatherWaitsForAPlaceWithoutAskingAnyone()
    {
        using var t = new TestContext();
        var result = await new WeatherAgent().RunAsync(t.Ctx, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.Contains("No place chosen", result.Message);
        Assert.Null(t.Ctx.State.Weather);
    }

    [Fact]
    public void NothingIsLocalWithoutAPlace()
    {
        var item = new FeedItem
        {
            Id = "a", Kind = ItemKind.News, SourceId = "x", SourceName = "Paper", Title = "Dublin rents rise again as supply stalls", Tier = 1,
            Published = DateTimeOffset.UtcNow.AddHours(-1), Category = "local", IsLocal = true,
        };
        Assert.False(Assert.Single(AquaHub.Core.Analysis.StoryClusterer.Build(new[] { item }, new NewsSettings(), new LocationSettings())).IsLocal);
        Assert.True(Assert.Single(AquaHub.Core.Analysis.StoryClusterer.Build(new[] { item }, new NewsSettings(), TestPlaces.Location())).IsLocal);
    }

    [Fact]
    public void PromptsLeaveThePlaceOutRatherThanInventOne()
    {
        var none = new HubSettings();
        var brief = Prompts.Brief("WEATHER: none", "morning", none);
        Assert.Contains("'Local' (local news", brief.System);
        Assert.DoesNotContain(", :", brief.Messages[0].Content);
        Assert.Contains("hasn't set their location", Digest.Situation(new HubState(null), none));
        Assert.Contains("'Dublin' (local news", Prompts.Brief("x", "morning", TestPlaces.Settings()).System);
        Assert.Contains("location Dublin, Ireland.", Digest.Situation(new HubState(null), TestPlaces.Settings()));
    }

    [Fact]
    public void AskUsesWindowsTimeZoneUntilAPlaceHasOne()
    {
        Assert.Equal("Europe/Dublin", AskAgent.TimeZoneName(TestPlaces.Location()));
        Assert.False(string.IsNullOrWhiteSpace(AskAgent.TimeZoneName(new LocationSettings())));
    }
}

public class ChoosingAPlaceTests
{
    [Fact]
    public void TheFirstPlaceSetsUpItsCountry()
    {
        var s = new HubSettings();
        var note = LocalePacks.ChoosePlace(s, TestPlaces.Galway);
        Assert.StartsWith("Set up for Ireland", note);
        Assert.Equal("Galway, Ireland", s.Location.Label);
        Assert.Equal("Europe/Dublin", s.Location.Timezone);
        Assert.Equal("en-IE", s.Location.Language);
        Assert.All(new[] { "rte", "irishtimes", "thejournal", "independent-ie", "siliconrepublic" },
            id => Assert.True(Assert.Single(s.News.Sources, x => x.Id == id).Enabled));
        Assert.Contains(s.Markets.Indices, i => i.Symbol == "^ISEQ");
        Assert.Equal(new[] { "IE" }, s.Events.HolidayCountries);
        Assert.Equal(new[] { "ireland", "Galway" }, s.Social.Subreddits);
        Assert.Equal("mastodon.ie", s.Social.MastodonInstance);
        Assert.Equal("EUR", s.Markets.BaseCurrency);
        Assert.Contains("Galway", s.Location.LocalKeywords);
        Assert.Contains("Taoiseach", s.Location.LocalKeywords);
    }

    [Fact]
    public void AnAmericanPlaceGetsTheUsEditionAndImperialUnits()
    {
        var s = new HubSettings();
        LocalePacks.ChoosePlace(s, TestPlaces.Austin);
        Assert.Equal("imperial", s.Location.Units);
        Assert.Equal("en-US", s.Location.Language);
        Assert.Equal("", Assert.Single(s.News.Sources, x => x.Id == "gnews-national").Query);
        Assert.DoesNotContain(s.News.Sources, x => x.Id == "rte");   // never Irish, so no Irish outlets to switch off
        Assert.Equal("USD", s.Markets.BaseCurrency);
        Assert.Equal("mastodon.social", s.Social.MastodonInstance);
    }

    [Fact]
    public void MovingTownDropsTheOldTownButKeepsTheCountrysOwnWords()
    {
        var s = TestPlaces.Settings(TestPlaces.Berlin);
        s.Location.LocalKeywords.Add("Kreuzberg");   // yours
        Assert.Equal("", LocalePacks.ChoosePlace(s, TestPlaces.Munich));   // same country: nothing else changes
        Assert.DoesNotContain("Berlin", s.Location.LocalKeywords);
        Assert.Contains("Munich", s.Location.LocalKeywords);
        Assert.Contains("Kreuzberg", s.Location.LocalKeywords);

        var irish = TestPlaces.Settings(TestPlaces.Dublin);
        LocalePacks.ChoosePlace(irish, TestPlaces.Galway);
        Assert.Contains("Dublin", irish.Location.LocalKeywords);   // an Irish word whichever Irish town you're in
        Assert.Contains("Galway", irish.Location.LocalKeywords);
    }

    [Fact]
    public void ANewTownInTheSameCountryTakesOverTheOldTownsCommunities()
    {
        var s = TestPlaces.Settings(TestPlaces.Dublin);
        s.Social.Subreddits.Add("irishpersonalfinance");
        Assert.Equal(new[] { "dublin", "ireland" }, s.Social.MastodonHashtags);
        LocalePacks.ChoosePlace(s, TestPlaces.Galway);
        Assert.Equal(new[] { "ireland", "Galway", "irishpersonalfinance" }, s.Social.Subreddits);
        Assert.Equal(new[] { "galway", "ireland" }, s.Social.MastodonHashtags);
    }

    [Fact]
    public void APlaceWithoutATimeZoneDoesntKeepThePreviousOne()
    {
        var s = TestPlaces.Settings(TestPlaces.Berlin);
        LocalePacks.ChoosePlace(s, TestPlaces.Paris with { Timezone = "" });
        Assert.Equal("", s.Location.Timezone);
    }
}

public class PlaceFinderTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void TheTownComesFromTheAddress()
    {
        var city = WeatherSource.ParseReverse(Json("""
            {"lat":"53.3498","lon":"-6.2603","address":{"city":"Dublin","county":"County Dublin","state":"Leinster","country":"Ireland","country_code":"ie"}}
            """), 53.35, -6.26)!;
        Assert.Equal(new GeoPlace("Dublin", "Leinster", "Ireland", "IE", 53.35, -6.26, ""), city);

        var village = WeatherSource.ParseReverse(Json("""
            {"address":{"village":"Doolin","county":"County Clare","country":"Ireland","country_code":"ie"}}
            """), 53.02, -9.38)!;
        Assert.Equal("Doolin", village.Name);
        Assert.Equal("County Clare", village.Region);
    }

    [Theory]
    [InlineData("""{"error":"Unable to geocode"}""")]
    [InlineData("""{"address":{"ocean":"Atlantic Ocean"}}""")]
    [InlineData("""{"address":{"city":"Somewhere"}}""")]
    public void NoTownMeansNoPlace(string json) => Assert.Null(WeatherSource.ParseReverse(Json(json), 50, -20));

    [Fact]
    public void TheSearchEntryForTheSameTownIsPreferred()
    {
        var near = new GeoPlace("Dublin", "Leinster", "Ireland", "IE", 53.35, -6.26, "");
        var candidates = new[]
        {
            new GeoPlace("Dublin", "California", "United States", "US", 37.70, -121.94, "America/Los_Angeles"),
            new GeoPlace("Dublin", "Leinster", "Ireland", "IE", 53.33, -6.25, "Europe/Dublin"),
        };
        Assert.Equal("Europe/Dublin", WeatherSource.SamePlace(candidates, near)!.Timezone);
        // A namesake far away in the same country isn't the same town.
        Assert.Null(WeatherSource.SamePlace(new[] { new GeoPlace("Dublin", "Leinster", "Ireland", "IE", 52.0, -8.0, "Europe/Dublin") }, near));
    }

    [Fact]
    public void OnlyAnApproximatePositionLeavesThePc()
    {
        Assert.Equal(53.35, WeatherSource.Rounded(53.34981));
        Assert.Equal(-6.26, WeatherSource.Rounded(-6.26034));
        var url = WeatherSource.ReverseUrl(WeatherSource.Rounded(53.34981), WeatherSource.Rounded(-6.26034));
        Assert.StartsWith("https://nominatim.openstreetmap.org/reverse?", url);
        Assert.Contains("lat=53.35&lon=-6.26&zoom=10", url);
    }

    [Fact]
    public void DistancesAreGreatCircle() =>
        Assert.InRange(WeatherSource.DistanceKm(53.35, -6.26, 53.27, -9.05), 180, 190);   // Dublin to Galway
}
