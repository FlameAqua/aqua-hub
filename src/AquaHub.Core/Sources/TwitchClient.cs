using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Models;

namespace AquaHub.Core.Sources;

public sealed record TwitchDeviceCode(string DeviceCode, string UserCode, Uri VerificationUri, int ExpiresIn, int Interval);
public sealed record TwitchTokens(string AccessToken, string RefreshToken, int ExpiresIn);
public sealed record TwitchIdentity(string UserId, string Login, int ExpiresIn);
public sealed class TwitchException(string message, bool reconnect = false) : Exception(message)
{
    public bool Reconnect { get; } = reconnect;
}

/// <summary>Public-client device OAuth and read-only followed streams. The owner supplies a non-redirecting HTTP client.</summary>
public sealed partial class TwitchClient(HttpClient http, TimeProvider? time = null)
{
    public const string Scope = "user:read:follows";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    [GeneratedRegex("^[a-zA-Z0-9]{1,100}$")]
    private static partial Regex ClientIdPattern();
    [GeneratedRegex("^[a-zA-Z0-9_]{1,25}$")]
    private static partial Regex LoginPattern();
    public static bool ValidClientId(string? value) => value is not null && ClientIdPattern().IsMatch(value);
    public static bool ValidToken(string? value) => value is { Length: > 0 and <= 512 } && value.All(c => char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c));
    private static void CheckToken(string value) { if (!ValidToken(value)) throw new TwitchException("Twitch returned invalid credential data. Connect again.", true); }

    public async Task<TwitchDeviceCode> BeginAsync(string clientId, CancellationToken ct)
    {
        CheckClientId(clientId);
        using var request = Form("device", ("client_id", clientId), ("scopes", Scope));
        var (status, document) = await JsonAsync(request, 32768, ct).ConfigureAwait(false);
        using (document)
        {
            if (status != HttpStatusCode.OK) throw Failure(status, "start sign-in");
            var root = document.RootElement;
            var url = Text(root, "verification_uri");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
                uri.Host != "www.twitch.tv" || uri.AbsolutePath.TrimEnd('/') != "/activate" || uri.UserInfo.Length > 0 || url.Length > 2048)
                throw new TwitchException("Twitch returned an unrecognised sign-in address.");
            var expires = Number(root, "expires_in");
            var interval = Number(root, "interval");
            if (expires is < 1 or > 3600 || interval is < 1 or > 60) throw new TwitchException("Twitch returned invalid sign-in timing.");
            return new(Text(root, "device_code"), Text(root, "user_code"), uri, expires, interval);
        }
    }

    public async Task<TwitchTokens> CompleteAsync(string clientId, TwitchDeviceCode code, CancellationToken ct)
    {
        CheckClientId(clientId);
        using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(code.ExpiresIn), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        var interval = code.Interval;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), _time, linked.Token).ConfigureAwait(false);
                using var request = Form("token", ("client_id", clientId), ("scopes", Scope),
                    ("device_code", code.DeviceCode), ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"));
                var (status, document) = await JsonAsync(request, 32768, linked.Token).ConfigureAwait(false);
                using (document)
                {
                    if (status == HttpStatusCode.OK) return Tokens(document.RootElement);
                    var message = OptionalText(document.RootElement, "message");
                    if (message == "authorization_pending") continue;
                    if (message == "slow_down") { interval = Math.Min(60, interval + 5); continue; }
                    if (message is "access_denied" or "authorization_declined") throw new TwitchException("Twitch sign-in was declined.");
                    throw Failure(status, "complete sign-in");
                }
            }
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested && !ct.IsCancellationRequested)
        { throw new TwitchException("The Twitch sign-in code expired. Connect again for a new code."); }
    }

    public async Task<TwitchTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
    {
        CheckClientId(clientId); CheckToken(refreshToken);
        using var request = Form("token", ("client_id", clientId), ("refresh_token", refreshToken), ("grant_type", "refresh_token"));
        var (status, document) = await JsonAsync(request, 32768, ct).ConfigureAwait(false);
        using (document)
        {
            if (status != HttpStatusCode.OK) throw Failure(status, "renew the connection");
            return Tokens(document.RootElement);
        }
    }

    public async Task<TwitchIdentity> ValidateAsync(string clientId, string accessToken, CancellationToken ct)
    {
        CheckClientId(clientId); CheckToken(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        var (status, document) = await JsonAsync(request, 32768, ct).ConfigureAwait(false);
        using (document)
        {
            if (status != HttpStatusCode.OK) throw Failure(status, "validate the connection");
            var root = document.RootElement;
            if (Text(root, "client_id") != clientId || !root.TryGetProperty("scopes", out var scopes) ||
                scopes.ValueKind != JsonValueKind.Array || !scopes.EnumerateArray().Any(s => s.GetString() == Scope))
                throw new TwitchException("This Twitch connection doesn't grant access to your followed live channels. Connect again.", true);
            var userId = Text(root, "user_id");
            if (!userId.All(char.IsAsciiDigit)) throw new TwitchException("Twitch returned an invalid account ID.");
            return new(userId, Text(root, "login"), Number(root, "expires_in"));
        }
    }

    public async Task<IReadOnlyList<LiveStream>> FollowedAsync(string clientId, string userId, string accessToken, CancellationToken ct)
    {
        CheckClientId(clientId); CheckToken(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/streams/followed?first=100&user_id=" + Uri.EscapeDataString(userId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", clientId);
        var (status, document) = await JsonAsync(request, 1024 * 1024, ct).ConfigureAwait(false);
        using (document)
        {
            if (status != HttpStatusCode.OK) throw Failure(status, "read live streams");
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new TwitchException("Twitch returned an unrecognised stream list.");
            var streams = new List<LiveStream>();
            foreach (var row in data.EnumerateArray().Take(100))
            {
                if (OptionalText(row, "type") != "live") continue;
                var login = Text(row, "user_login");
                if (!LoginPattern().IsMatch(login)) continue;
                streams.Add(new("twitch:" + Text(row, "id"), "twitch", Text(row, "user_id"), Text(row, "user_name"),
                    Text(row, "title"), "https://www.twitch.tv/" + login)
                {
                    Category = OptionalText(row, "game_name"),
                    Viewers = row.TryGetProperty("viewer_count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) ? Math.Max(0, n) : null,
                    Started = DateTimeOffset.TryParse(OptionalText(row, "started_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
                });
            }
            return streams.DistinctBy(s => s.Id).ToArray();
        }
    }

    public async Task RevokeAsync(string clientId, string accessToken, CancellationToken ct)
    {
        CheckClientId(clientId); CheckToken(accessToken);
        using var request = Form("revoke", ("client_id", clientId), ("token", accessToken));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new TwitchException("Disconnected on this PC. Twitch couldn't confirm revocation; you can remove Aqua Hub in Twitch's Connections settings.");
    }

    private static HttpRequestMessage Form(string path, params (string Key, string Value)[] pairs) => new(HttpMethod.Post, "https://id.twitch.tv/oauth2/" + path)
    { Content = new FormUrlEncodedContent(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))) };

    private async Task<(HttpStatusCode Status, JsonDocument Document)> JsonAsync(HttpRequestMessage request, int maximum, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400) throw new TwitchException("Twitch redirected an authenticated request; Aqua refused it. Try again later.");
        if (response.Content.Headers.ContentLength > maximum) throw new TwitchException("Twitch returned an oversized response.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > maximum) throw new TwitchException("Twitch returned an oversized response.");
            memory.Write(buffer, 0, read);
        }
        try
        {
            var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new TwitchException("Twitch returned an unreadable response. Try again later."); }
            return (response.StatusCode, document);
        }
        catch (JsonException) { throw new TwitchException("Twitch returned an unreadable response. Try again later."); }
    }

    private static TwitchTokens Tokens(JsonElement root)
    {
        var expires = Number(root, "expires_in");
        if (expires is < 1 or > 86400) throw new TwitchException("Twitch returned invalid token timing.");
        var access = Text(root, "access_token"); var refresh = Text(root, "refresh_token");
        CheckToken(access); CheckToken(refresh);
        return new(access, refresh, expires);
    }
    private static string Text(JsonElement root, string key) => OptionalText(root, key) is { Length: > 0 } value ? value : throw new TwitchException("Twitch returned incomplete connection data.");
    private static string OptionalText(JsonElement root, string key) => root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "")[..Math.Min(p.GetString()!.Length, 2000)] : "";
    private static int Number(JsonElement root, string key) => root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) ? value : throw new TwitchException("Twitch returned invalid connection data.");
    private static void CheckClientId(string value) { if (!ValidClientId(value)) throw new TwitchException("Enter Aqua Hub's public Twitch client ID first."); }
    private static TwitchException Failure(HttpStatusCode status, string action) => new((int)status switch
    {
        400 or 401 or 403 => "Twitch couldn't authorise this connection. Connect again, using a Public Twitch app.",
        429 => "Twitch is limiting requests. Aqua will try again later.",
        _ => $"Couldn't {action} on Twitch (HTTP {(int)status}). Try again later.",
    }, (int)status is 400 or 401 or 403);
}
