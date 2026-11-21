using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AquaHub.Core.Ai.Local;

namespace AquaHub.Core.Search;

public sealed record SearchEngine(string Server, string Model);

public interface ISearchEmbeddings : IDisposable
{
    bool Configured { get; }
    Task<float[]> EncodeAsync(string text, CancellationToken ct);
    void Release();
}

public sealed class SearchEmbeddings(Func<SearchEngine?> configuration, ILlamaProcessHost host, Func<int, int?> listener) : ISearchEmbeddings
{
    public static ModelRecommendation Pin => ModelDownloads.Recommendations.Single(m => m.Role == "search");
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    { MaxResponseContentBufferSize = 128 * 1024, Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _gate = new(1);
    private ILlamaProcess? _process;
    private string _key = "";
    private int _port;
    private SearchEngine? _current;
    private DateTime _last;
    public bool Configured => configuration() != null;

    public async Task<float[]> EncodeAsync(string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        bool ready = false;
        try
        {
            var wanted = configuration() ?? throw new InvalidOperationException("Select EmbeddingGemma and install Aqua’s pinned runtime.");
            if (_current != wanted || _process is not { HasExited: false })
            {
                Stop();
                using var file = new FileStream(wanted.Model, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length != Pin.File.Size || !Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false))
                    .Equals(Pin.File.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The Search model failed verification.");
                _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _port = host.FreePort();
                var args = new[] { "-m",wanted.Model,"-a","search","--host","127.0.0.1","--port",_port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--embeddings","--pooling","mean","--device","none","-ngl","0","-t","2","-tb","2","-np","1","-c","2048","-b","2048","-ub","2048",
                    "--poll","0","--poll-batch","0","--offline","--no-webui","--no-slots","-lv","1" };
                _process = host.Start(wanted.Server, args, new Dictionary<string, string> { [LlamaArgs.KeyVariable] = _key }, Path.GetDirectoryName(wanted.Server)!, _ => { });
                _current = wanted;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(35));
                while (!_process.HasExited)
                {
                    wait.Token.ThrowIfCancellationRequested();
                    if (listener(_port) == _process.Id)
                    {
                        using var health = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}/health");
                        health.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                        using var response = await _http.SendAsync(health, wait.Token).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode) break;
                    }
                    await Task.Delay(100, wait.Token).ConfigureAwait(false);
                }
            }
            if (_process.HasExited || listener(_port) != _process.Id) throw new IOException("The search engine is unavailable.");
            ready = true;
            // Include title and task prefixes in the input budget. Leave space for special tokens.
            while (System.Text.Encoding.UTF8.GetByteCount(text) > 1984) text = text[..^16];
            if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text = text[..^1];
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_port}/v1/embeddings");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            req.Content = JsonContent.Create(new { model = "search", input = text, encoding_format = "float" });
            using var result = await _http.SendAsync(req, ct).ConfigureAwait(false);
            result.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await result.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            var vector = body.RootElement.GetProperty("data")[0].GetProperty("embedding").Deserialize<float[]>();
            if (vector is not { Length: 768 } || vector.Any(v => !float.IsFinite(v)) || Math.Abs(vector.Sum(v => (double)v * v) - 1) > 0.01)
                throw new InvalidDataException("The Search model returned an invalid embedding.");
            _last = DateTime.UtcNow;
            return vector;
        }
        catch (OperationCanceledException) when (ready && ct.IsCancellationRequested) { throw; }
        catch { Stop(); throw; }
        finally { _gate.Release(); }
    }
    private void Stop()
    {
        _process?.Dispose();
        _process = null;
        _current = null;
    }
    public async Task StopProcessAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { Stop(); }
        finally { _gate.Release(); }
    }

    public void Release()
    {
        if (!_gate.Wait(0)) return;
        try { if (!Configured || DateTime.UtcNow - _last >= TimeSpan.FromMinutes(1)) Stop(); }
        finally { _gate.Release(); }
    }
    public void Dispose() { Stop(); _http.Dispose(); _gate.Dispose(); }
}
