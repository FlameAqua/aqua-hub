using System.Globalization;
using AquaHub.Core.Analysis;
using AquaHub.Core.Sources;

namespace AquaHub.Core.Settings;

/// <summary>
/// Country-specific defaults. A new profile has none (no place is chosen yet); when you pick a place, <see cref="Apply"/>
/// fills in that country's parts — local outlets, subreddits and hashtags, local keywords, public holidays, the local
/// stock index, currency, units — and when you move to another country it swaps the previous country's for the new
/// one's, leaving everything you added alone. Profiles from before this were set up for Ireland, so the Irish pack
/// is also what older settings files contain.
/// </summary>
public static class LocalePacks
{
    private sealed record Pack(string Demonym, string? Index, string? IndexName, string? Subreddit, string? Currency, string? Mastodon = null);

    private static readonly Dictionary<string, Pack> Packs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IE"] = new("irish", "^ISEQ", "ISEQ", "ireland", "EUR", "mastodon.ie"),
        ["GB"] = new("british", "^FTSE", "FTSE 100", "unitedkingdom", "GBP"),
        ["US"] = new("american", null, null, null, "USD"),
        ["CA"] = new("canadian", "^GSPTSE", "S&P/TSX", "canada", "CAD"),
        ["AU"] = new("australian", "^AXJO", "ASX 200", "australia", "AUD"),
        ["NZ"] = new("new zealand", "^NZ50", "NZX 50", "newzealand", "NZD"),
        ["DE"] = new("german", "^GDAXI", "DAX", "germany", "EUR"),
        ["FR"] = new("french", "^FCHI", "CAC 40", "france", "EUR"),
        ["NL"] = new("dutch", "^AEX", "AEX", "thenetherlands", "EUR"),
        ["ES"] = new("spanish", "^IBEX", "IBEX 35", "spain", "EUR"),
        ["IT"] = new("italian", "FTSEMIB.MI", "FTSE MIB", "italy", "EUR"),
        ["PT"] = new("portuguese", "PSI20.LS", "PSI", "portugal", "EUR"),
        ["BE"] = new("belgian", "^BFX", "BEL 20", "belgium", "EUR"),
        ["CH"] = new("swiss", "^SSMI", "SMI", "switzerland", "CHF"),
        ["SE"] = new("swedish", "^OMX", "OMX Stockholm 30", "sweden", "SEK"),
        ["NO"] = new("norwegian", "OBX.OL", "OBX", "norway", "NOK"),
        ["DK"] = new("danish", "^OMXC25", "OMX Copenhagen 25", "denmark", "DKK"),
        ["PL"] = new("polish", "WIG20.WA", "WIG20", "poland", "PLN"),
        ["JP"] = new("japanese", "^N225", "Nikkei 225", "japan", "JPY"),
        ["IN"] = new("indian", "^NSEI", "Nifty 50", "india", "INR"),
        ["SG"] = new("singapore", "^STI", "STI", "singapore", "SGD"),
        ["HK"] = new("hong kong", "^HSI", "Hang Seng", "hongkong", "HKD"),
        ["KR"] = new("korean", "^KS11", "KOSPI", "korea", "KRW"),
        ["BR"] = new("brazilian", "^BVSP", "Ibovespa", "brasil", "BRL"),
        ["MX"] = new("mexican", "^MXX", "IPC", "mexico", "MXN"),
        ["ZA"] = new("south african", "^J203.JO", "JSE All Share", "southafrica", "ZAR"),
    };

    /// <summary>Ireland's own outlets, added when you choose a place there (and turned off, not removed, if you move abroad).</summary>
    private static List<NewsSource> IrishSources() => new()
    {
        new() { Id = "rte", Name = "RTÉ News", Url = "https://www.rte.ie/feeds/rss/?index=/news/", Category = "local", Tier = 1, Local = true },
        new() { Id = "irishtimes", Name = "The Irish Times", Url = "https://www.irishtimes.com/arc/outboundfeeds/feed-irish-news/?outputType=xml", Category = "local", Tier = 1, Local = true },
        new() { Id = "thejournal", Name = "TheJournal.ie", Url = "https://www.thejournal.ie/feed/", Category = "local", Tier = 2, Local = true },
        new() { Id = "independent-ie", Name = "Irish Independent", Url = "https://www.independent.ie/rss/", Category = "local", Tier = 2, Local = true },
        new() { Id = "siliconrepublic", Name = "Silicon Republic", Url = "https://www.siliconrepublic.com/feed", Category = "tech", Tier = 2, Local = true },
    };

    private static readonly string[] IrishOutlets = IrishSources().Select(x => x.Id).ToArray();

    /// <summary>Words that mark a story as Irish news, beyond the city and country names.</summary>
    private static readonly string[] IrishKeywords =
    {
        "Ireland", "Irish", "Dublin", "Cork", "Galway", "Limerick", "Waterford", "Kilkenny", "Belfast",
        "Taoiseach", "Tánaiste", "Dáil", "Oireachtas", "Seanad", "HSE", "Garda", "Gardaí", "RTÉ",
        "Leinster", "Munster", "Connacht", "Ulster", "Stormont", "Fianna Fáil", "Fine Gael", "Sinn Féin",
    };
    private static readonly string[] EnglishEditions = { "IE", "GB", "US", "CA", "AU", "NZ", "IN", "SG", "ZA", "NG", "KE", "PH", "MY", "PK" };
    private const string NationalFeedId = "gnews-national";

    public static bool HasEnglishEdition(string countryCode) => EnglishEditions.Contains(countryCode.ToUpperInvariant());

    public static string CountryName(string countryCode)
    {
        try { return new RegionInfo(countryCode).EnglishName; }
        catch (ArgumentException) { return countryCode; }
    }

    public static string Demonym(string countryCode) =>
        Packs.TryGetValue(countryCode, out var p) ? p.Demonym : CountryName(countryCode).ToLowerInvariant();

    /// <summary>
    /// What a local publisher's name or domain contains: the country's domain ending, its name and adjective, the city
    /// and region. Null for the US, where local outlets use .com like everyone else (the query alone scopes it).
    /// </summary>
    public static IReadOnlyList<string>? PublisherHints(LocationSettings loc)
    {
        var cc = loc.Country.ToUpperInvariant();
        if (cc is "US" or "") return null;
        var hints = new List<string> { "." + (cc == "GB" ? "uk" : cc.ToLowerInvariant()) };
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            var folded = TextTools.Fold(s).ToLowerInvariant().Trim();
            if (folded.Length >= 3 && !hints.Contains(folded)) hints.Add(folded);
            var joined = folded.Replace(" ", "");
            if (joined != folded && joined.Length >= 3 && !hints.Contains(joined)) hints.Add(joined);
        }
        Add(CountryName(cc));
        Add(Demonym(cc));
        Add(loc.City);
        if (!string.Equals(loc.Region, CountryName(cc), StringComparison.OrdinalIgnoreCase)) Add(loc.Region);
        if (cc == "GB") { Add("britain"); Add("bbc"); }
        return hints;
    }

    /// <summary>
    /// Makes <paramref name="p"/> your place (from the search or "Use my location") and applies its country's defaults.
    /// Returns what changed beyond the place itself, for the UI; empty when the country stayed the same.
    /// </summary>
    public static string ChoosePlace(HubSettings s, GeoPlace p)
    {
        var previousCountry = s.Location.Country;
        var previousCity = s.Location.City;
        s.Location.City = p.Name;
        s.Location.Region = string.IsNullOrWhiteSpace(p.Country) ? p.Region : p.Country;
        s.Location.Country = p.CountryCode.ToUpperInvariant();
        s.Location.Latitude = p.Latitude;
        s.Location.Longitude = p.Longitude;
        // A place without a known time zone falls back to Windows' own rather than keeping the previous place's.
        s.Location.Timezone = p.Timezone;
        // The old town stops counting as local (unless it's one of the new country's own words, like Cork for Ireland).
        var packWords = s.Location.Country == "IE" ? IrishKeywords : Array.Empty<string>();
        if (previousCity.Length > 0 && !previousCity.Equals(p.Name, StringComparison.OrdinalIgnoreCase) &&
            !packWords.Contains(previousCity, StringComparer.OrdinalIgnoreCase))
            s.Location.LocalKeywords.RemoveAll(k => k.Equals(previousCity, StringComparison.OrdinalIgnoreCase));
        if (!s.Location.LocalKeywords.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) s.Location.LocalKeywords.Add(p.Name);
        // A new town in the same country: its own subreddit and hashtag take over from the old town's (a new country
        // replaces the whole set in Apply).
        if (previousCity.Length > 0 && previousCountry.Equals(s.Location.Country, StringComparison.OrdinalIgnoreCase))
        {
            var (oldTown, newTown) = (previousCity.Replace(" ", ""), p.Name.Replace(" ", ""));
            var sub = s.Social.Subreddits.FindIndex(x => x.Equals(oldTown, StringComparison.OrdinalIgnoreCase));
            if (sub >= 0 && !s.Social.Subreddits.Contains(newTown, StringComparer.OrdinalIgnoreCase)) s.Social.Subreddits[sub] = newTown;
            var tag = s.Social.MastodonHashtags.FindIndex(x => x.Equals(oldTown, StringComparison.OrdinalIgnoreCase));
            if (tag >= 0 && !s.Social.MastodonHashtags.Contains(newTown, StringComparer.OrdinalIgnoreCase)) s.Social.MastodonHashtags[tag] = newTown.ToLowerInvariant();
        }
        return Apply(s, previousCountry);
    }

    /// <summary>
    /// Applies the chosen place's country after a city is picked (the first time, or after moving country). Only
    /// replaces values that are still the defaults or the previous country's, so your own sources, keywords and symbols
    /// are kept. Returns a short description of what changed, for the UI.
    /// </summary>
    /// <param name="previousCountry">The country before this pick; empty when no place was set.</param>
    public static string Apply(HubSettings s, string previousCountry)
    {
        var cc = s.Location.Country.ToUpperInvariant();
        var prev = previousCountry.ToUpperInvariant();
        if (cc.Length == 0 || cc == prev) return "";
        var changes = new List<string>();
        var name = CountryName(cc);

        // Language (English edition where Google News has one) and units.
        if (EnglishEditions.Contains(cc)) s.Location.Language = "en-" + cc;
        s.Location.Units = cc is "US" or "LR" or "MM" ? "imperial" : "metric";

        // Local words: drop the previous country's, keep anything personal, add the new place's.
        if (prev.Length > 0)
        {
            var previousWords = new HashSet<string>(prev == "IE" ? IrishKeywords : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase)
                { CountryName(prev), Demonym(prev) };
            s.Location.LocalKeywords = s.Location.LocalKeywords.Where(k => !previousWords.Contains(k)).ToList();
        }
        foreach (var k in new[] { s.Location.City, s.Location.Region, name }.Concat(cc == "IE" ? IrishKeywords : Array.Empty<string>()))
            if (!string.IsNullOrWhiteSpace(k) && !s.Location.LocalKeywords.Contains(k, StringComparer.OrdinalIgnoreCase)) s.Location.LocalKeywords.Add(k);
        changes.Add("local keywords");

        // Local news: Ireland has its own outlets; elsewhere Google News' national edition. Irish outlets are turned
        // off rather than removed when you move abroad, so they can be turned back on.
        if (cc != "IE")
        {
            foreach (var src in s.News.Sources.Where(x => IrishOutlets.Contains(x.Id))) src.Enabled = false;
            // Google News has a national edition for English-speaking countries; elsewhere, English coverage of the country.
            s.News.Sources.RemoveAll(x => x.Id == NationalFeedId);
            s.News.Sources.Add(new NewsSource
            {
                Id = NationalFeedId, Name = $"{name} (Google News)", Kind = "google", Category = "local", Tier = 3, Local = true,
                Query = HasEnglishEdition(cc) ? "" : "{country} when:1d",
            });
            changes.Add($"local news from {name}");
        }
        else
        {
            foreach (var irish in IrishSources())
            {
                if (s.News.Sources.FirstOrDefault(x => x.Id == irish.Id) is { } existing) existing.Enabled = true;
                else s.News.Sources.Add(irish);
            }
            s.News.Sources.RemoveAll(x => x.Id == NationalFeedId);
            changes.Add("Irish outlets");
        }

        // Social: replace the default Irish communities with the new city's and country's.
        var social = new SocialSettings();
        var pack = Packs.GetValueOrDefault(cc);
        var prevPack = Packs.GetValueOrDefault(prev);
        var city = s.Location.City.Replace(" ", "");
        // Only lists that are still the shipped ones, or that this method generated for the previous country.
        bool Generated(List<string> list, List<string> shipped, string? previousFirst) =>
            list.SequenceEqual(shipped, StringComparer.OrdinalIgnoreCase) ||
            (previousFirst is not null && list.Count <= 2 && string.Equals(list.FirstOrDefault(), previousFirst, StringComparison.OrdinalIgnoreCase));
        if (Generated(s.Social.Subreddits, social.Subreddits, prevPack?.Subreddit))
        {
            s.Social.Subreddits = new[] { pack?.Subreddit, city }.OfType<string>().Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            changes.Add("subreddits");
        }
        if (Generated(s.Social.MastodonHashtags, social.MastodonHashtags, null) ||
            (s.Social.MastodonHashtags.Count == 2 && string.Equals(s.Social.MastodonHashtags[1], CountryName(prev).Replace(" ", ""), StringComparison.OrdinalIgnoreCase)))
            s.Social.MastodonHashtags = new List<string> { city.ToLowerInvariant(), name.Replace(" ", "").ToLowerInvariant() };
        // The country's own Mastodon server, if it has a well-known one; only while the server is still a default.
        if (s.Social.MastodonInstance == social.MastodonInstance || s.Social.MastodonInstance == prevPack?.Mastodon)
            s.Social.MastodonInstance = pack?.Mastodon ?? social.MastodonInstance;

        // Holidays and the local stock index.
        if (s.Events.HolidayCountries.Count == 0 || s.Events.HolidayCountries.SequenceEqual(new[] { prev }))
            s.Events.HolidayCountries = new List<string> { cc };
        // Only the index the previous pack added (named the pack's way); one you added yourself stays.
        if (Packs.GetValueOrDefault(prev) is { Index: { } oldIndex } oldPack)
            s.Markets.Indices.RemoveAll(i => i.Symbol == oldIndex && i.Name == (oldPack.IndexName ?? oldIndex));
        if (pack?.Index is { } idx && s.Markets.Indices.All(i => i.Symbol != idx))
        {
            s.Markets.Indices.Add(new WatchSymbol { Symbol = idx, Name = pack.IndexName ?? idx, Kind = "index" });
            changes.Add(pack.IndexName ?? idx);
        }
        if (pack?.Currency is { } cur)
        {
            if (!s.Events.EconomicCurrencies.Contains(cur)) s.Events.EconomicCurrencies.Add(cur);
            // Holdings are valued in the country's currency, unless you chose one yourself.
            if (s.Markets.BaseCurrency == new MarketSettings().BaseCurrency || s.Markets.BaseCurrency == prevPack?.Currency) s.Markets.BaseCurrency = cur;
        }

        return (prev.Length == 0 ? $"Set up for {name}: " : $"Switched to {name}: ") + string.Join(", ", changes) + ". Review them in Settings → News and Social.";
    }
}
