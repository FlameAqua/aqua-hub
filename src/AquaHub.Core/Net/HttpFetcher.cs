using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AquaHub.Core.Data;
using AquaHub.Core.Util;

namespace AquaHub.Core.Net;

public enum FetchStatus { Ok, NotModified, Blocked, Failed }

public sealed class FetchResult
{
    public FetchStatus Status { get; init; }
    public int HttpCode { get; init; }
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string? ContentType { get; init; }
    public string? Charset { get; init; }
    public string? Error { get; init; }

    public bool Ok => Status == FetchStatus.Ok;

    private string? _text;
    public string Text => _text ??= Decode();

    private string Decode()
    {
        try
        {
            var enc = string.IsNullOrEmpty(Charset) ? Encoding.UTF8 : Encoding.GetEncoding(Charset.Trim('"'));
            return enc.GetString(Bytes);
        }
        catch { return Encoding.UTF8.GetString(Bytes); }
    }
}

/// <summary>
/// Hardened, shared HTTP client:
/// <list type="bullet">
/// <item>HTTPS only (plain HTTP allowed for loopback), no cookies, no credentials in URLs;</item>
/// <item>response size caps and timeouts;</item>
/// <item>conditional GET (ETag / Last-Modified) to avoid re-downloading unchanged feeds;</item>
/// <item>polite retries and per-host cool-down after HTTP 429/503.</item>
/// </list>
/// </summary>
public sealed class HttpFetcher : IDisposable
{
    public const string UserAgent = "AquaHub/1.0 (personal desktop dashboard; Windows)";
    private readonly HttpClient _client;
    private readonly HubDatabase? _db;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldown = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency = new(8);
    private int _networkFailures;

    /// <summary>
    /// Raised with false after requests to several hosts in a row failed at the network level (DNS, connect, timeout),
    /// and with true as soon as any server answers again.
    /// </summary>
    public event Action<bool>? ConnectivityChanged;
    public bool LooksOffline => Volatile.Read(ref _networkFailures) >= 3;

    private void NoteAnswer()
    {
        if (Interlocked.Exchange(ref _networkFailures, 0) >= 3) ConnectivityChanged?.Invoke(true);
    }

    private void NoteNetworkFailure()
    {
        if (Interlocked.Increment(ref _networkFailures) == 3) ConnectivityChanged?.Invoke(false);
    }

    public HttpFetcher(HubDatabase? db) : this(db, new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxConnectionsPerServer = 6,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        UseCookies = false,
        UseProxy = true,
    })
    {
    }

    /// <summary>Over another handler: the tests' stand-in network, which answers the real addresses without leaving the PC.</summary>
    internal HttpFetcher(HubDatabase? db, HttpMessageHandler handler)
    {
        _db = db;
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en;q=0.9");
    }

    public HttpClient Client => _client;

    public static bool IsAllowed(string url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (!string.IsNullOrEmpty(u.UserInfo)) return false;
        if (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback))
        {
            uri = u;
            return true;
        }
        return false;
    }

    public async Task<FetchResult> GetAsync(string url, bool conditional = false, int maxBytes = 8 * 1024 * 1024,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        if (!IsAllowed(url, out var uri) || uri is null)
            return new FetchResult { Status = FetchStatus.Blocked, Error = "URL not allowed (HTTPS only)" };

        if (_cooldown.TryGetValue(uri.Host, out var until) && until > DateTimeOffset.UtcNow)
            return new FetchResult { Status = FetchStatus.Failed, Error = $"{uri.Host} is cooling down after rate limiting" };

        await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                if (headers is not null)
                    foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
                if (conditional && _db is not null)
                {
                    var (etag, lastModified) = _db.GetValidators(url);
                    if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
                    if (!string.IsNullOrEmpty(lastModified)) req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
                }

                HttpResponseMessage resp;
                try
                {
                    resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    if (attempt < 1) { await Task.Delay(800, ct).ConfigureAwait(false); continue; }
                    NoteNetworkFailure();
                    return new FetchResult { Status = FetchStatus.Failed, Error = ex.Message };
                }

                NoteAnswer();
                using (resp)
                {
                    var code = (int)resp.StatusCode;
                    if (resp.StatusCode == HttpStatusCode.NotModified)
                        return new FetchResult { Status = FetchStatus.NotModified, HttpCode = code };

                    if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        var hinted = resp.Headers.RetryAfter?.Delta;
                        if (attempt < 1 && hinted is { } w && w <= TimeSpan.FromSeconds(8))
                        {
                            await Task.Delay(w, ct).ConfigureAwait(false);
                            continue;
                        }
                        // Without a hint, back off for a meaningful window (e.g. Reddit's anonymous limits).
                        var cool = hinted ?? TimeSpan.FromMinutes(12);
                        _cooldown[uri.Host] = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(Math.Clamp(cool.TotalMinutes, 2, 30));
                        return new FetchResult { Status = FetchStatus.Failed, HttpCode = code, Error = $"HTTP {code} (rate limited)" };
                    }

                    if (code >= 500 && attempt < 1)
                    {
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                        return new FetchResult { Status = FetchStatus.Failed, HttpCode = code, Error = $"HTTP {code}" };

                    if (resp.Content.Headers.ContentLength is long len && len > maxBytes)
                        return new FetchResult { Status = FetchStatus.Failed, HttpCode = code, Error = "Response too large" };

                    var bytes = await ReadCappedAsync(resp.Content, maxBytes, ct).ConfigureAwait(false);
                    if (bytes is null)
                        return new FetchResult { Status = FetchStatus.Failed, HttpCode = code, Error = "Response too large" };

                    if (conditional && _db is not null)
                    {
                        var etag = resp.Headers.ETag?.ToString();
                        var lastModified = resp.Content.Headers.LastModified?.ToString("R");
                        if (etag is not null || lastModified is not null) _db.PutValidators(url, etag, lastModified);
                    }

                    return new FetchResult
                    {
                        Status = FetchStatus.Ok,
                        HttpCode = code,
                        Bytes = bytes,
                        ContentType = resp.Content.Headers.ContentType?.MediaType,
                        Charset = resp.Content.Headers.ContentType?.CharSet,
                    };
                }
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// POSTs a JSON body (used only for signing in to your own Bluesky account). Same rules as GET: HTTPS only,
    /// capped response size, no cookies. Returns the status code and the parsed JSON (null if not JSON).
    /// </summary>
    public async Task<(int Status, JsonDocument? Json)> PostJsonAsync(string url, string jsonBody,
        IReadOnlyDictionary<string, string>? headers = null, int maxBytes = 1024 * 1024, CancellationToken ct = default)
    {
        if (!IsAllowed(url, out var uri) || uri is null) return (0, null);
        await _concurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
            };
            if (headers is not null)
                foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            HttpResponseMessage resp;
            try
            {
                resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                NoteNetworkFailure();
                return (0, null);
            }
            NoteAnswer();
            using (resp)
            {
                var bytes = await ReadCappedAsync(resp.Content, maxBytes, ct).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0) return ((int)resp.StatusCode, null);
                try { return ((int)resp.StatusCode, JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 })); }
                catch (JsonException) { return ((int)resp.StatusCode, null); }
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async Task<JsonDocument?> GetJsonAsync(string url, IReadOnlyDictionary<string, string>? headers = null,
        int maxBytes = 8 * 1024 * 1024, CancellationToken ct = default)
    {
        var res = await GetAsync(url, conditional: false, maxBytes, headers, ct).ConfigureAwait(false);
        if (!res.Ok)
        {
            Log.Debug("http", $"{Redact(url)} -> {res.Error}");
            return null;
        }
        try
        {
            return JsonDocument.Parse(res.Bytes, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            Log.Warn("http", $"Invalid JSON from {Redact(url)}", ex);
            return null;
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>Removes query strings (which may contain tokens) before logging.</summary>
    public static string Redact(string url)
    {
        var q = url.IndexOf('?');
        return q < 0 ? url : url[..q] + "?…";
    }

    public void Dispose() => _client.Dispose();
}
