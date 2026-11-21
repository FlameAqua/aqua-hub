using System.IO.Pipes;
using System.Text;
using AquaHub.Core.Util;

namespace AquaHub.Platform;

/// <summary>Delivers only a folder scope to the first Aqua instance for this profile.</summary>
internal sealed class SearchFolderActivation(string pipeName)
{
    private const int MaxPath = 2048;
    private static readonly UTF8Encoding Encoding = new(false, true);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    internal static string? Normalize(string? path)
    {
        if (path is null || path.Length is < 3 or > MaxPath || path.AsSpan().ContainsAny(Path.GetInvalidPathChars())) return null;
        bool drivePath = char.IsAsciiLetter(path[0]) && path[1..3] == @":\" && !path.AsSpan(2).Contains(':');
        bool uncPath = path.StartsWith(@"\\", StringComparison.Ordinal) && path[2] is not ('?' or '.' or '\\') && !path.Contains(':');
        if (!drivePath && !uncPath) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
    }

    public async Task<bool> SendAsync(string folder)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, Options);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync(Encoding.GetBytes(folder), timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or EncoderFallbackException)
        {
            return false;
        }
    }

    public async Task ListenAsync(Action<string> open, CancellationToken stop)
    {
        try
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, Options);
            var bytes = new byte[MaxPath * 3 + 1];
            while (!stop.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(stop);
                read.CancelAfter(Timeout);
                try
                {
                    int size = await pipe.ReadAtLeastAsync(bytes, bytes.Length, false, read.Token).ConfigureAwait(false);
                    if (size < bytes.Length && Normalize(Encoding.GetString(bytes, 0, size)) is { } folder)
                        open(folder);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or DecoderFallbackException) { }
                finally { if (pipe.IsConnected) pipe.Disconnect(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("search", "Could not listen for Explorer folder requests", ex);
        }
    }
}
