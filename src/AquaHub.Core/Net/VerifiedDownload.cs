using System.Net;
using System.Security.Cryptography;
using AquaHub.Core.Util;

namespace AquaHub.Core.Net;

/// <summary>A file to download: exactly this size and SHA-256 (lower or upper case hex), or it's thrown away.</summary>
public sealed record DownloadSpec(string Url, long Size, string Sha256);

/// <summary>Why a download didn't finish, in plain words for the person who started it.</summary>
public sealed class DownloadException(string message) : Exception(message);

/// <summary>
/// Downloads a file over HTTPS to "target.part", checking its size as it arrives and its SHA-256 at the end, and only then
/// moves it into place; anything else is deleted. A stalled connection gives up after a while. With resume on (multi-GB
/// models), a ".part" left by an earlier attempt is hashed and continued from where it stopped when the server allows it.
/// </summary>
public static class VerifiedDownload
{
    public static async Task DownloadAsync(HttpClient http, DownloadSpec file, string target, Action<long> progress,
        TimeSpan stallAfter, bool resume, CancellationToken ct)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new DownloadException("Downloads go only over HTTPS.");
        if (file.Size <= 0 || file.Sha256.Length != 64) throw new DownloadException("The file to download isn't described fully (size and SHA-256).");
        var part = target + ".part";
        var keepPart = false;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long got = 0;
            if (resume && File.Exists(part))
            {
                got = new FileInfo(part).Length;
                if (got > file.Size) got = 0;
                else if (got > 0) got = await HashExistingAsync(part, got, sha, progress, ct).ConfigureAwait(false);
            }
            if (got == 0) File.Delete(part);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (got > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(got, null);
            stall.CancelAfter(stallAfter);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            // HttpClient never follows a redirect from HTTPS to HTTP; this is the belt to those braces.
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new DownloadException("The download was redirected away from HTTPS, so it was stopped.");
            if (got > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // The server sent the whole file: start again from the beginning.
                got = 0;
                sha.GetHashAndReset();
                File.Delete(part);
            }
            if (!response.IsSuccessStatusCode)
                throw new DownloadException($"The download server said {(int)response.StatusCode} {response.ReasonPhrase} — try again later.");
            if (response.Content.Headers.ContentLength is long announced && got + announced > file.Size && response.Content.Headers.ContentEncoding.Count == 0)
                throw new DownloadException("The download is bigger than the file Aqua expects, so it wasn't saved.");

            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using (var output = new FileStream(part, got > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 20];
                while (true)
                {
                    stall.CancelAfter(stallAfter);
                    var n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    got += n;
                    if (got > file.Size) throw new DownloadException("The download is bigger than the file Aqua expects, so it was deleted.");
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    progress(got);
                }
            }
            if (got != file.Size)
            {
                keepPart = resume;
                throw new DownloadException("The download ended early — try again" + (resume ? "; it carries on where it stopped." : "."));
            }
            if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("download", $"{Path.GetFileName(target)} didn't match its SHA-256");
                throw new DownloadException("The download didn't match its published fingerprint (SHA-256), so it was deleted. Try again later.");
            }
            File.Move(part, target, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            keepPart = resume;
            throw new DownloadException("The download stalled — check the internet connection and try again" + (resume ? "; it carries on where it stopped." : "."));
        }
        catch (OperationCanceledException)
        {
            keepPart = resume;
            throw;
        }
        catch (HttpRequestException ex)
        {
            keepPart = resume;
            Log.Warn("download", "Download failed: " + ex.Message);
            throw new DownloadException("Couldn't download — check the internet connection and try again.");
        }
        finally
        {
            if (!keepPart)
                try { if (File.Exists(part)) File.Delete(part); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Feeds an earlier attempt's bytes to the hash (reading as fast as the disk allows); returns how many.</summary>
    private static async Task<long> HashExistingAsync(string part, long length, IncrementalHash sha, Action<long> progress, CancellationToken ct)
    {
        await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        long done = 0;
        while (done < length)
        {
            var n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - done)), ct).ConfigureAwait(false);
            if (n <= 0) break;
            sha.AppendData(buffer, 0, n);
            done += n;
            progress(done);
        }
        return done;
    }
}
