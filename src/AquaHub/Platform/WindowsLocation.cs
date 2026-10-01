using AquaHub.Core.Net;
using AquaHub.Core.Sources;
using AquaHub.Core.Util;
using AquaHub.Services;
using Windows.Devices.Geolocation;

namespace AquaHub.Platform;

/// <summary>
/// "Use my location": asks Windows where this PC roughly is, only when you click, and turns that into a town.
/// Windows decides whether desktop apps may know (Settings › Privacy &amp; security › Location) and may ask you first;
/// Aqua asks for city-level accuracy, rounds the position to about a kilometre and keeps only the town you accept.
/// </summary>
public static class WindowsLocation
{
    /// <summary>What happened: a town, or why there isn't one (<see cref="LocationOff"/> means Windows said no).</summary>
    public sealed record Outcome(GeoPlace? Place, string? Problem, bool LocationOff = false);

    /// <summary>Where Windows lets you allow location for desktop apps.</summary>
    public const string SettingsUri = "ms-settings:privacy-location";

    /// <summary>A fixed town for dry runs (E2E), so the flow is tested without asking Windows or the network.</summary>
    private static readonly GeoPlace DryRunPlace = new("Cork", "Munster", "Ireland", "IE", 51.90, -8.47, "Europe/Dublin");

    public static async Task<Outcome> FindTownAsync(HttpFetcher http, CancellationToken ct)
    {
        if (Sandbox.Intercept("locate", "Windows location (simulated)")) return new(DryRunPlace, null);
        double lat, lon;
        try
        {
            var access = await Geolocator.RequestAccessAsync().AsTask(ct);
            if (access != GeolocationAccessStatus.Allowed)
                return new(null, "Windows isn't sharing this PC's location with apps.", LocationOff: true);
            // Coarse is plenty for a town (and quicker): Wi-Fi or network positioning rather than GPS.
            var locator = new Geolocator { DesiredAccuracyInMeters = 3000 };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(20)).AsTask(ct);
            (lat, lon) = (position.Coordinate.Point.Position.Latitude, position.Coordinate.Point.Position.Longitude);
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, "Windows isn't sharing this PC's location with apps.", LocationOff: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn("location", "Windows location failed", ex);
            return new(null, "Windows couldn't work out where this PC is right now. Search for your town instead.");
        }

        var place = await WeatherSource.PlaceNearAsync(http, lat, lon, ct);
        if (place is not null) return new(place, null);
        return new(null, http.LooksOffline
            ? "Couldn't look up the town (are you offline?). Search for it instead."
            : "Couldn't find a town near you. Search for it instead.");
    }
}
