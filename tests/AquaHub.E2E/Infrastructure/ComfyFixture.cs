using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AquaHub.E2E.Infrastructure;

/// <summary>Owned loopback HTTP fixture on an OS-assigned port. Never connects to an installed server or runs a model.</summary>
public sealed class ComfyFixture : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private readonly Task _accept;
    public ConcurrentQueue<string> Requests { get; } = new();
    public ConcurrentQueue<Exception> Errors { get; } = new();
    public TaskCompletionSource RequestHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ClientDisconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? HoldPath { get; init; }
    private int _busyQueue;
    public bool BusyQueue { get => Volatile.Read(ref _busyQueue) != 0; set => Volatile.Write(ref _busyQueue, value ? 1 : 0); }
    public string Endpoint { get; }

    public ComfyFixture()
    {
        _listener.Start();
        Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_connections) _connections.Add(ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream();
            var header = new List<byte>();
            var one = new byte[1];
            while (header.Count < 16384)
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            if (header.Count == 16384) throw new InvalidDataException("Fixture request header exceeds limit.");
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length != 3) throw new InvalidDataException("Invalid fixture HTTP request.");
            var lengthHeader = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthHeader is null ? 0 : int.Parse(lengthHeader.Split(':', 2)[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            if (length is < 0 or > 65536) throw new InvalidDataException("Fixture body exceeds limit.");
            if (length > 0) await stream.ReadExactlyAsync(new byte[length], _stop.Token);
            if (lines.Any(l => l.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)))
            {
                var total = 0;
                while (true)
                {
                    var size = int.Parse((await ReadLineAsync(stream)).Split(';')[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                    if (size < 0 || size > 65536 - total) throw new InvalidDataException("Chunked fixture body exceeds limit.");
                    total += size;
                    if (size == 0)
                    {
                        if (await ReadLineAsync(stream) != "") throw new InvalidDataException("Fixture does not accept trailers.");
                        break;
                    }
                    await stream.ReadExactlyAsync(new byte[size], _stop.Token);
                    if (await ReadLineAsync(stream) != "") throw new InvalidDataException("Invalid fixture chunk terminator.");
                }
            }
            var path = new Uri(Endpoint + first[1]).AbsolutePath.Trim('/');
            Requests.Enqueue(first[0] + " /" + path);
            if (path == HoldPath)
            {
                RequestHeld.TrySetResult();
                // The caller's cancellation must close the connection; no response or arbitrary fixture delay.
                try { if (await stream.ReadAsync(one, _stop.Token) == 0) ClientDisconnected.TrySetResult(); }
                catch (IOException) { ClientDisconnected.TrySetResult(); }
                return;
            }
            var json = (first[0], path) switch
            {
                ("GET", "models/diffusion_models") => "[\"flux-2-klein-base-4b-fp8.safetensors\"]",
                ("GET", "models/text_encoders") => "[\"qwen_3_4b.safetensors\",\"qwen_3_8b.safetensors\"]",
                ("GET", "models/vae") => "[\"flux2-vae.safetensors\"]",
                ("GET", "object_info") => JsonSerializer.Serialize(new[] { "UNETLoader", "CLIPLoader", "VAELoader", "CLIPTextEncode", "RandomNoise", "KSamplerSelect", "Flux2Scheduler", "EmptyFlux2LatentImage", "SamplerCustomAdvanced", "VAEDecode", "SaveImage", "CFGGuider", "BasicGuider", "LoadImage", "VAEEncode", "ReferenceLatent" }.ToDictionary(n => n, _ => new { })),
                ("GET", "queue") => BusyQueue ? "{\"queue_running\":[[1,\"fixture-job\"]],\"queue_pending\":[]}" : "{\"queue_running\":[],\"queue_pending\":[]}",
                ("GET", "system_stats") => "{\"devices\":[{\"type\":\"cuda\",\"torch_vram_total\":0,\"torch_vram_free\":0}]}",
                ("POST", "free") => "{}",
                _ => throw new InvalidOperationException("Unexpected fixture request: " + first[0] + " /" + path),
            };
            var body = Encoding.UTF8.GetBytes(json);
            var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeader, _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        catch (Exception ex) { Errors.Enqueue(ex); }
    }

    private async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 128)
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0) throw new IOException("Fixture client disconnected.");
            bytes.Add(one[0]);
            if (bytes.Count >= 2 && bytes[^2] == 13 && bytes[^1] == 10) return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
        }
        throw new InvalidDataException("Fixture HTTP line exceeds limit.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _accept.GetAwaiter().GetResult();
        Task[] connections;
        lock (_connections) connections = _connections.ToArray();
        Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _stop.Dispose();
        Assert.Empty(Errors);
    }
}
