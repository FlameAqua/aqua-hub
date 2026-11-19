using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AquaHub.Core.Settings;

namespace AquaHub.Core.Sources;

public sealed class GoogleYouTubeException(string message, bool reconnect = false) : Exception(message)
{
    public bool Reconnect { get; } = reconnect;
}
public sealed record GoogleDesktopClient(string ClientId, string ClientSecret)
{
    public static GoogleDesktopClient Parse(string json)
    {
        try
        {
            if (json.Length > 16384) throw new JsonException();
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var installed = document.RootElement.GetProperty("installed");
            var id = installed.GetProperty("client_id").GetString() ?? "";
            var secret = installed.TryGetProperty("client_secret", out var s) ? s.GetString() ?? "" : "";
            var result = new GoogleDesktopClient(id, secret);
            if (!result.Valid) throw new JsonException();
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new GoogleYouTubeException("Choose the JSON configuration for a Google OAuth Desktop app client, not a web app or service account."); }
    }
    public bool Valid => ClientId is { Length: > 0 and <= 200 } id && Regex.IsMatch(id, @"^[0-9]+-[A-Za-z0-9_-]+\.apps\.googleusercontent\.com$", RegexOptions.CultureInvariant)
        && ClientSecret is { Length: <= 200 } secret && secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
public sealed record GoogleYouTubeTokens(string AccessToken, string RefreshToken, int ExpiresIn);
public sealed record YouTubeSubscriptions(IReadOnlyList<ChannelRef> Channels, bool Truncated);

/// <summary>Read-only subscription API and system-browser OAuth with PKCE. No API key or embedded browser.</summary>
public sealed class GoogleYouTubeClient(HttpClient http)
{
    public const string Scope = "https://www.googleapis.com/auth/youtube.readonly";
    public const int MaximumSubscriptions = 1000;
    public static bool ValidToken(string? token) => token is { Length: > 0 and <= 2048 } && token.All(c => char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c));
    public static bool ValidRefreshToken(string? token) => token is { Length: <= 1024 } && ValidToken(token);
    private static string Random() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string FormQuery(IEnumerable<KeyValuePair<string, string>> values) => string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public async Task<GoogleYouTubeTokens> SignInAsync(GoogleDesktopClient client, Func<Uri, Task> openBrowser, CancellationToken ct)
    {
        CheckClient(client);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(4);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}/oauth2/callback/";
        var state = Random(); var verifier = Random();
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var query = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = Scope,
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["access_type"] = "offline", ["prompt"] = "consent select_account",
        };
        try
        {
            await openBrowser(new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + FormQuery(query))).ConfigureAwait(false);
            for (var attempt = 0; attempt < 32; attempt++)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var stream = socket.GetStream();
                var bytes = new byte[8192]; var length = 0;
                string? acceptedCode = null; var acceptedDenial = false;
                try
                {
                    while (length < bytes.Length)
                    {
                        var read = await stream.ReadAsync(bytes.AsMemory(length), requestTimeout.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        length += read;
                        if (Encoding.ASCII.GetString(bytes, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    var request = Encoding.ASCII.GetString(bytes, 0, length);
                    var first = request.Split("\r\n")[0].Split(' ');
                    var hosts = request.Split("\r\n").Where(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
                    string? code = null; var denied = false;
                    var valid = length < bytes.Length && request.Contains("\r\n\r\n", StringComparison.Ordinal) && first.Length == 3 && first[0] == "GET" &&
                        first[2] is "HTTP/1.1" or "HTTP/1.0" && first[1].StartsWith("/", StringComparison.Ordinal) && hosts.Length == 1 &&
                        hosts[0][5..].Trim() == $"127.0.0.1:{port}" && TryCallback(new Uri(redirect), first[1], state, out code, out denied);
                    // No request text or authorization response is reflected into the browser or logs.
                    var body = valid ? "You can close this tab and return to Aqua Hub." : "This sign-in response was not accepted. Return to Aqua Hub and try again.";
                    if (valid) { acceptedCode = code; acceptedDenial = denied; }
                    var response = Encoding.UTF8.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'\r\n\r\n{body}");
                    await stream.WriteAsync(response, requestTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                catch (IOException) when (!timeout.IsCancellationRequested) { }
                if (acceptedCode is null && !acceptedDenial) continue;
                listener.Stop(); socket.Close();
                if (acceptedDenial) throw new GoogleYouTubeException("Google sign-in was declined. You can try again when ready.");
                // Token transport failures belong to OAuth, not to the invalid-callback retry loop.
                return await ExchangeAsync(client, acceptedCode!, redirect, verifier, timeout.Token).ConfigureAwait(false);
            }
            throw new GoogleYouTubeException("Too many invalid sign-in responses. Start Google sign-in again.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new GoogleYouTubeException("Google sign-in timed out. Connect again when ready."); }
    }

    internal static bool TryCallback(Uri redirect, string target, string expectedState, out string? code, out bool denied)
    {
        code = null; denied = false;
        if (target.Length > 4096 || !Uri.TryCreate(redirect, target, out var uri) || uri.GetLeftPart(UriPartial.Path) != redirect.AbsoluteUri || uri.Fragment.Length != 0) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2 || !values.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1].Replace('+', ' ')))) return false;
            }
        }
        catch (UriFormatException) { return false; }
        if (!values.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState))) return false;
        denied = values.ContainsKey("error");
        if (denied) return !values.ContainsKey("code");
        if (!values.TryGetValue("code", out code) || code.Length is < 1 or > 2048 || code.Any(char.IsControl)) return false;
        return true;
    }

    private async Task<GoogleYouTubeTokens> ExchangeAsync(GoogleDesktopClient client, string code, string redirect, string verifier, CancellationToken ct) =>
        await TokenAsync(client, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier }, null, ct).ConfigureAwait(false);
    public Task<GoogleYouTubeTokens> RefreshAsync(GoogleDesktopClient client, string refresh, CancellationToken ct)
    {
        if (!ValidRefreshToken(refresh)) throw new GoogleYouTubeException("Google connection data is missing or invalid. Connect Google again.", true);
        return TokenAsync(client, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, refresh, ct);
    }
    private async Task<GoogleYouTubeTokens> TokenAsync(GoogleDesktopClient client, Dictionary<string, string> fields, string? previousRefresh, CancellationToken ct)
    {
        CheckClient(client);
        fields["client_id"] = client.ClientId;
        if (client.ClientSecret.Length > 0) fields["client_secret"] = client.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") { Content = new FormUrlEncodedContent(fields) };
        var (status, document) = await JsonAsync(request, 16384, ct).ConfigureAwait(false);
        using (document)
        {
            var root = document.RootElement;
            if (status != HttpStatusCode.OK)
                throw new GoogleYouTubeException(Text(root, "error") == "invalid_grant" ? "Google access has expired or was revoked. Connect Google again." : "Google couldn't authorise this client. Check the Desktop client configuration and try again.", Text(root, "error") == "invalid_grant");
            if (previousRefresh is null && !Text(root, "scope").Split(' ').Contains(Scope)) throw new GoogleYouTubeException("Google didn't grant read-only YouTube access. Connect again and allow that permission.");
            if (Text(root, "scope") is { Length: > 0 } scopes && !scopes.Split(' ').Contains(Scope)) throw new GoogleYouTubeException("Google didn't grant read-only YouTube access.", true);
            if (!Text(root, "token_type").Equals("Bearer", StringComparison.OrdinalIgnoreCase) || !root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) || seconds is < 1 or > 86400)
                throw new GoogleYouTubeException("Google returned incomplete connection data. Connect again.");
            var access = Text(root, "access_token"); var refresh = Text(root, "refresh_token");
            if (refresh.Length == 0) refresh = previousRefresh ?? "";
            CheckToken(access); CheckToken(refresh);
            if (!ValidRefreshToken(refresh)) throw new GoogleYouTubeException("Google returned a refresh token too large for Windows credential storage. Connect again.");
            return new(access, refresh, seconds);
        }
    }

    public async Task<YouTubeSubscriptions> SubscriptionsAsync(string access, CancellationToken ct)
    {
        CheckToken(access);
        var channels = new List<ChannelRef>(); var pages = new HashSet<string>(StringComparer.Ordinal); var page = "";
        for (var i = 0; i < 20; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/youtube/v3/subscriptions?part=snippet&mine=true&maxResults=50" + (page.Length > 0 ? "&pageToken=" + Uri.EscapeDataString(page) : ""));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            var (status, document) = await JsonAsync(request, 1024 * 1024, ct).ConfigureAwait(false);
            using (document)
            {
                if (status != HttpStatusCode.OK) throw new GoogleYouTubeException((int)status switch
                {
                    401 => "Google access was rejected. Connect Google again.",
                    403 => "YouTube couldn't list subscriptions. Enable YouTube Data API v3 for this Google project and check its quota and consent permissions.",
                    _ => "YouTube couldn't sync subscriptions. The last successful list is kept; try again later.",
                }, status == HttpStatusCode.Unauthorized);
                var root = document.RootElement;
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 50) throw new GoogleYouTubeException("YouTube returned an unreadable subscription list.");
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("snippet", out var snippet) || !snippet.TryGetProperty("resourceId", out var resource)) continue;
                    var id = Text(resource, "channelId"); var name = Text(snippet, "title");
                    if (YouTubeStreams.ValidChannel(id)) channels.Add(new() { ChannelId = id, Name = name[..Math.Min(name.Length, 200)] });
                }
                page = Text(root, "nextPageToken");
                if (page.Length == 0) return new(channels.DistinctBy(c => c.ChannelId).ToArray(), false);
                if (page.Length > 512 || !pages.Add(page)) throw new GoogleYouTubeException("YouTube returned an invalid subscription page. Try syncing again later.");
            }
        }
        return new(channels.DistinctBy(c => c.ChannelId).Take(MaximumSubscriptions).ToArray(), true);
    }
    public async Task RevokeAsync(string refresh, CancellationToken ct)
    {
        CheckToken(refresh);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/revoke") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new GoogleYouTubeException("Disconnected on this PC. Google couldn't confirm revocation; remove Aqua Hub in your Google Account's third-party connections if needed.");
    }
    private async Task<(HttpStatusCode, JsonDocument)> JsonAsync(HttpRequestMessage request, int maximum, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 || response.Content.Headers.ContentLength > maximum) throw new GoogleYouTubeException("Google returned an unexpected response. Try again later.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream(); var bytes = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > maximum) throw new GoogleYouTubeException("Google returned an oversized response.");
            memory.Write(bytes, 0, read);
        }
        try
        {
            var document = JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return (response.StatusCode, document);
            document.Dispose();
        }
        catch (JsonException) { }
        throw new GoogleYouTubeException("Google returned an unreadable response. Try again later.");
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static void CheckToken(string value) { if (!ValidToken(value)) throw new GoogleYouTubeException("Google connection data is missing or invalid. Connect Google again.", true); }
    private static void CheckClient(GoogleDesktopClient client) { if (!client.Valid) throw new GoogleYouTubeException("Import a Google Desktop client JSON configuration first."); }
}
