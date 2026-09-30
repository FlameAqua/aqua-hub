using System.Globalization;
using AquaHub.Core.Analysis;

namespace AquaHub.Core.Settings;

/// <summary>
/// Country-specific defaults. The shipped defaults are Irish (Dublin); when you pick a city in another country,
/// <see cref="Apply"/> swaps the Ireland-only parts — local outlets, subreddits and hashtags, local keywords,
/// public holidays, the local stock index, units — for sensible equivalents, and leaves everything you added alone.
/// </summary>
public static class LocalePacks
{
    private sealed record Pack(string Demonym, string? Index, string? IndexName, string? Subreddit, string? Currency);

    private static readonly Dictionary<string, Pack> Packs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IE"] = new("irish", "^ISEQ", "ISEQ", "ireland", "EUR"),
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

    /// <summary>Shipped outlets that only make sense for Ireland: every default local feed except the Google search.</summary>
    private static readonly string[] IrishOutlets = new NewsSettings().Sources.Where(x => x.Local && x.Kind != "google").Select(x => x.Id).ToArray();
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
    /// Switches Ireland-specific defaults to the given country after a city is picked there. Only replaces values
    /// that are still the shipped defaults (or the previous country's), so your own sources, keywords and
    /// symbols are kept. Returns a short description of what changed, for the UI.
    /// </summary>
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

        // Local words: drop the previous country's defaults, keep anything personal.
        var defaults = new LocationSettings().LocalKeywords;
        var previousWords = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase) { CountryName(prev), Demonym(prev) };
        s.Location.LocalKeywords = s.Location.LocalKeywords.Where(k => !previousWords.Contains(k)).ToList();
        foreach (var k in new[] { s.Location.City, s.Location.Region, name })
            if (!string.IsNullOrWhiteSpace(k) && !s.Location.LocalKeywords.Contains(k, StringComparer.OrdinalIgnoreCase)) s.Location.LocalKeywords.Add(k);
        changes.Add("local keywords");

        // Local news: Irish outlets off (kept in the list so they can be turned back on), national edition on.
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
            foreach (var src in s.News.Sources.Where(x => IrishOutlets.Contains(x.Id))) src.Enabled = true;
            s.News.Sources.RemoveAll(x => x.Id == NationalFeedId);
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
        if (s.Social.MastodonInstance == social.MastodonInstance && cc != "IE") s.Social.MastodonInstance = "mastodon.social";

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
        if (pack?.Currency is { } cur && !s.Events.EconomicCurrencies.Contains(cur)) s.Events.EconomicCurrencies.Add(cur);

        return $"Switched to {name}: " + string.Join(", ", changes) + ". Review them in Settings → News and Social.";
    }
}
