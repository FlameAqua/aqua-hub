using System.Globalization;
using System.Text.Json;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Sources;

public sealed record GeoPlace(string Name, string Region, string Country, string CountryCode, double Latitude, double Longitude, string Timezone)
{
    public override string ToString() => string.Join(", ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
}

/// <summary>Open-Meteo forecast + geocoding (free, no key, no tracking cookies).</summary>
public static class WeatherSource
{
    public static async Task<WeatherSnapshot?> FetchAsync(HttpFetcher http, LocationSettings loc, CancellationToken ct)
    {
        var imperial = loc.Units == "imperial";
        var lat = loc.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        var lon = loc.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
        var url =
            $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m,is_day" +
            "&hourly=temperature_2m,precipitation_probability,weather_code,is_day" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max" +
            "&timezone=auto&forecast_days=7&forecast_hours=25" +
            (imperial ? "&temperature_unit=fahrenheit&wind_speed_unit=mph&precipitation_unit=inch" : "");
        using var doc = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        if (doc is null) return null;
        var root = doc.RootElement;
        var offset = TimeSpan.FromSeconds(root.Lng("utc_offset_seconds") ?? 0);

        DateTimeOffset? Local(string? iso) =>
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? new DateTimeOffset(dt, offset) : null;

        WeatherNow? now = null;
        if (root.TryProp("current", out var cur))
        {
            now = new WeatherNow(cur.Dbl("temperature_2m") ?? 0, cur.Dbl("apparent_temperature") ?? 0, (int)(cur.Lng("relative_humidity_2m") ?? 0),
                cur.Dbl("precipitation") ?? 0, (int)(cur.Lng("weather_code") ?? 0), cur.Dbl("wind_speed_10m") ?? 0, (cur.Lng("is_day") ?? 1) == 1);
        }

        var hours = new List<WeatherHour>();
        if (root.TryProp("hourly", out var h) && h.TryProp("time", out var ht))
        {
            var times = ht.EnumerateArray().Select(t => t.GetString()).ToList();
            var temps = h.Doubles("temperature_2m");
            var pops = h.Doubles("precipitation_probability");
            var codes = h.Doubles("weather_code");
            var days = h.Doubles("is_day");
            for (var i = 0; i < times.Count; i++)
            {
                if (Local(times[i]) is not { } t) continue;
                hours.Add(new WeatherHour(t, At(temps, i), (int)At(pops, i), (int)At(codes, i), At(days, i) >= 1));
            }
        }

        var dailyList = new List<WeatherDay>();
        if (root.TryProp("daily", out var d) && d.TryProp("time", out var dt2))
        {
            var dates = dt2.EnumerateArray().Select(t => t.GetString()).ToList();
            var codes = d.Doubles("weather_code");
            var max = d.Doubles("temperature_2m_max");
            var min = d.Doubles("temperature_2m_min");
            var pop = d.Doubles("precipitation_probability_max");
            var uv = d.Doubles("uv_index_max");
            var sunrise = d.TryProp("sunrise", out var sr) ? sr.EnumerateArray().Select(x => x.GetString()).ToList() : new();
            var sunset = d.TryProp("sunset", out var ss) ? ss.EnumerateArray().Select(x => x.GetString()).ToList() : new();
            for (var i = 0; i < dates.Count; i++)
            {
                if (!DateOnly.TryParse(dates[i], CultureInfo.InvariantCulture, out var date)) continue;
                dailyList.Add(new WeatherDay(date, (int)At(codes, i), At(max, i), At(min, i), (int)At(pop, i),
                    i < sunrise.Count ? Local(sunrise[i]) : null, i < sunset.Count ? Local(sunset[i]) : null, At(uv, i)));
            }
        }

        return new WeatherSnapshot
        {
            Location = loc.City,
            Now = now,
            Hours = hours,
            Days = dailyList,
            UpdatedAt = DateTimeOffset.Now,
            Units = loc.Units,
        };
    }

    private static double At(double[] arr, int i) => i < arr.Length && !double.IsNaN(arr[i]) ? arr[i] : 0;

    public static async Task<List<GeoPlace>> SearchPlacesAsync(HttpFetcher http, string query, CancellationToken ct)
    {
        var list = new List<GeoPlace>();
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return list;
        using var doc = await http.GetJsonAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query.Trim())}&count=8&language=en&format=json", ct: ct).ConfigureAwait(false);
        if (doc is null) return list;
        foreach (var r in doc.RootElement.Arr("results"))
        {
            list.Add(new GeoPlace(r.Str("name") ?? "", r.Str("admin1") ?? "", r.Str("country") ?? "", r.Str("country_code") ?? "",
                r.Dbl("latitude") ?? 0, r.Dbl("longitude") ?? 0, r.Str("timezone") ?? ""));
        }
        return list;
    }

    /// <summary>
    /// "Use my location": the town or city at an approximate position from Windows. The position is rounded to about a
    /// kilometre before it leaves the PC and goes to OpenStreetMap's Nominatim, once per click (as its usage policy
    /// asks); Open-Meteo's entry for the same town then supplies its time zone and the name a search would show.
    /// </summary>
    public static async Task<GeoPlace?> PlaceNearAsync(HttpFetcher http, double latitude, double longitude, CancellationToken ct)
    {
        var (lat, lon) = (Rounded(latitude), Rounded(longitude));
        using var doc = await http.GetJsonAsync(ReverseUrl(lat, lon), ct: ct).ConfigureAwait(false);
        if (doc is null || ParseReverse(doc.RootElement, lat, lon) is not { } near) return null;
        var matches = await SearchPlacesAsync(http, near.Name, ct).ConfigureAwait(false);
        return SamePlace(matches, near) ?? near;
    }

    /// <summary>Shown wherever a place found this way appears (OpenStreetMap's data licence asks for it).</summary>
    public const string PlaceAttribution = "Place names © OpenStreetMap contributors";

    /// <summary>Two decimals: about a kilometre, enough for the town and its weather.</summary>
    public static double Rounded(double degrees) => Math.Round(degrees, 2, MidpointRounding.AwayFromZero);

    public static string ReverseUrl(double lat, double lon) => string.Create(CultureInfo.InvariantCulture,
        $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={lat:0.##}&lon={lon:0.##}&zoom=10&addressdetails=1&accept-language=en");

    /// <summary>The town or city in a Nominatim reverse result; null when there's none (open sea, an error).</summary>
    public static GeoPlace? ParseReverse(JsonElement root, double lat, double lon)
    {
        if (!root.TryProp("address", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var name = new[] { "city", "town", "village", "municipality", "suburb", "county" }
            .Select(k => a.Str(k)?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        var cc = a.Str("country_code")?.Trim() ?? "";
        if (name is null || cc.Length != 2) return null;
        var region = (a.Str("state") ?? a.Str("county") ?? "").Trim();
        return new GeoPlace(name, region.Equals(name, StringComparison.OrdinalIgnoreCase) ? "" : region, a.Str("country")?.Trim() ?? "",
            cc.ToUpperInvariant(), lat, lon, "");
    }

    /// <summary>The search result for the same town: same country and within 30 km, nearest first.</summary>
    public static GeoPlace? SamePlace(IEnumerable<GeoPlace> candidates, GeoPlace near) =>
        candidates.Where(c => c.CountryCode.Equals(near.CountryCode, StringComparison.OrdinalIgnoreCase))
            .Select(c => (Place: c, Km: DistanceKm(c.Latitude, c.Longitude, near.Latitude, near.Longitude)))
            .Where(x => x.Km <= 30).OrderBy(x => x.Km).Select(x => x.Place).FirstOrDefault();

    /// <summary>Great-circle distance.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * 6371 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>WMO weather interpretation codes → short description.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Light showers",
        81 => "Showers",
        82 => "Violent showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "—",
    };
}

/// <summary>Public holidays (Nager.Date), economic calendar (weekly JSON feed) and optional Finnhub earnings.</summary>
public static class CalendarFeeds
{
    public static async Task<List<HubEvent>> FetchHolidaysAsync(HttpFetcher http, string country, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct)
    {
        var list = new List<HubEvent>();
        foreach (var year in Enumerable.Range(from.Year, to.Year - from.Year + 1))
        {
            using var doc = await http.GetJsonAsync($"https://date.nager.at/api/v3/PublicHolidays/{year}/{country}", ct: ct).ConfigureAwait(false);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var h in doc.RootElement.EnumerateArray())
            {
                if (!DateOnly.TryParse(h.Str("date"), CultureInfo.InvariantCulture, out var date)) continue;
                var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue)));
                if (start < from.Date.AddDays(-1) || start > to) continue;
                var local = h.Str("localName");
                var name = h.Str("name") ?? local ?? "Holiday";
                list.Add(new HubEvent
                {
                    Id = "hol" + Hash.Short(country, h.Str("date"), name),
                    Title = name,
                    Detail = local is not null && local != name ? $"{local} · {country}" : $"Public holiday · {country}",
                    Start = start,
                    AllDay = true,
                    Kind = EventKind.Holiday,
                    Source = "Public holidays",
                    Importance = 2,
                });
            }
        }
        return list;
    }

    public static async Task<List<HubEvent>> FetchEconomicAsync(HttpFetcher http, IReadOnlyCollection<string> currencies, string minImpact,
        CancellationToken ct)
    {
        var list = new List<HubEvent>();
        var cur = new HashSet<string>(currencies, StringComparer.OrdinalIgnoreCase);
        var min = ImpactRank(minImpact);
        foreach (var url in new[] { "https://nfs.faireconomy.media/ff_calendar_thisweek.json", "https://nfs.faireconomy.media/ff_calendar_nextweek.json" })
        {
            using var doc = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var country = e.Str("country") ?? "";
                var impact = e.Str("impact") ?? "Low";
                if (cur.Count > 0 && !cur.Contains(country)) continue;
                if (ImpactRank(impact) < min) continue;
                if (TimeText.ParseLenient(e.Str("date")) is not { } when) continue;
                var title = e.Str("title") ?? "Economic release";
                var forecast = e.Str("forecast");
                var previous = e.Str("previous");
                var detail = string.Join(" · ", new[]
                {
                    $"{country} · {impact} impact",
                    string.IsNullOrWhiteSpace(forecast) ? null : $"forecast {forecast}",
                    string.IsNullOrWhiteSpace(previous) ? null : $"previous {previous}",
                }.Where(s => s is not null));
                list.Add(new HubEvent
                {
                    Id = "eco" + Hash.Short(country, title, e.Str("date")),
                    Title = $"{country} {title}",
                    Detail = detail,
                    Start = when,
                    Kind = EventKind.Economic,
                    Source = "Economic calendar",
                    Importance = ImpactRank(impact) >= 3 ? 3 : 2,
                });
            }
        }
        return list.GroupBy(e => e.Id).Select(g => g.First()).ToList();
    }

    private static int ImpactRank(string impact) => impact.ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        "holiday" => 0,
        _ => 1,
    };

    public static async Task<List<HubEvent>> FetchEarningsAsync(HttpFetcher http, string apiKey, IReadOnlyCollection<string> symbols,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var list = new List<HubEvent>();
        if (string.IsNullOrWhiteSpace(apiKey) || symbols.Count == 0) return list;
        var wanted = new HashSet<string>(symbols.Select(s => s.Split('.')[0]), StringComparer.OrdinalIgnoreCase);
        var url = $"https://finnhub.io/api/v1/calendar/earnings?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&token={Uri.EscapeDataString(apiKey)}";
        using var doc = await http.GetJsonAsync(url, ct: ct).ConfigureAwait(false);
        if (doc is null) return list;
        foreach (var e in doc.RootElement.Arr("earningsCalendar"))
        {
            var sym = e.Str("symbol") ?? "";
            if (!wanted.Contains(sym)) continue;
            if (!DateOnly.TryParse(e.Str("date"), CultureInfo.InvariantCulture, out var date)) continue;
            var hour = e.Str("hour") switch { "bmo" => "before market open", "amc" => "after market close", _ => "time TBC" };
            var eps = e.Dbl("epsEstimate");
            list.Add(new HubEvent
            {
                Id = "earn" + Hash.Short(sym, e.Str("date")),
                Title = $"{sym} earnings",
                Detail = eps is null ? hour : string.Create(CultureInfo.InvariantCulture, $"{hour} · EPS est. {eps:0.00}"),
                Start = new DateTimeOffset(date.ToDateTime(new TimeOnly(e.Str("hour") == "amc" ? 21 : 13, 0)), TimeSpan.Zero),
                AllDay = e.Str("hour") is not ("bmo" or "amc"),
                Kind = EventKind.Earnings,
                Source = "Earnings",
                Importance = 2,
                Url = $"https://finance.yahoo.com/quote/{sym}",
            });
        }
        return list;
    }
}
