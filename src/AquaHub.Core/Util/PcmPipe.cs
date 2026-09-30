namespace AquaHub.Core.Util;

/// <summary>
/// Live audio from a chosen microphone to Windows' speech recognizer: the microphone writes, the recognizer reads.
/// A read waits until it can be filled, because the recognizer takes a short read as the end of the audio; it only
/// comes back short (or empty) once the pipe is closed. Holds a few seconds at most — if the reader falls behind, the
/// oldest audio goes. The recognizer asks for the position and length now and then, so those answer instead of throwing.
/// </summary>
public sealed class PcmPipe : Stream
{
    private readonly object _gate = new();
    private readonly byte[] _ring;
    private int _start, _count;
    private long _read;
    private bool _closed;

    public PcmPipe(int capacity = 16000 * 2 * 8) => _ring = new byte[capacity];

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    /// <summary>Unknown (live audio): the recognizer reads until the pipe is closed.</summary>
    public override long Length => -1;
    public override long Position { get { lock (_gate) return _read; } set { } }
    public override long Seek(long offset, SeekOrigin origin) => Position;
    public override void SetLength(long value) { }
    public override void Flush() { }

    /// <summary>Bytes waiting to be read.</summary>
    public int Available { get { lock (_gate) return _count; } }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (count >= _ring.Length) { offset += count - _ring.Length; count = _ring.Length; }
            // Full: drop the oldest audio rather than block the microphone.
            var overflow = _count + count - _ring.Length;
            if (overflow > 0) { _start = (_start + overflow) % _ring.Length; _count -= overflow; }
            var end = (_start + _count) % _ring.Length;
            var first = Math.Min(count, _ring.Length - end);
            Buffer.BlockCopy(buffer, offset, _ring, end, first);
            if (count > first) Buffer.BlockCopy(buffer, offset + first, _ring, 0, count - first);
            _count += count;
            Monitor.PulseAll(_gate);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            while (_count < count && !_closed) Monitor.Wait(_gate, 250);
            var take = Math.Min(count, _count);
            var first = Math.Min(take, _ring.Length - _start);
            Buffer.BlockCopy(_ring, _start, buffer, offset, first);
            if (take > first) Buffer.BlockCopy(_ring, 0, buffer, offset + first, take - first);
            _start = (_start + take) % _ring.Length;
            _count -= take;
            _read += take;
            return take;
        }
    }

    /// <summary>No more audio: a waiting read returns what is left, and later reads return 0.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>How loud 16-bit PCM is, 0–1, from its loudest sample: −60 dB and below is 0, full scale is 1.</summary>
    public static double PeakLevel(byte[] pcm, int count)
    {
        var peak = 0;
        for (var i = 0; i + 1 < Math.Min(count, pcm.Length); i += 2) peak = Math.Max(peak, Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)));
        return peak == 0 ? 0 : Math.Clamp((20 * Math.Log10(peak / 32768.0) + 60) / 60, 0, 1);
    }

    protected override void Dispose(bool disposing)
    {
        Complete();
        base.Dispose(disposing);
    }
}

/// <summary>Microphone names: the classic wave-in list cuts them at 31 characters; Windows' device list has them whole.</summary>
public static class MicNames
{
    /// <summary>The full Windows name for a cut-off one, when exactly one device's name starts with it.</summary>
    public static string Expand(string shortName, IEnumerable<string> fullNames)
    {
        var hits = fullNames.Where(f => f.StartsWith(shortName, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : shortName;
    }

    /// <summary>Whether a saved name is this device's (either may be the cut-off form).</summary>
    public static bool Same(string saved, string name) =>
        saved.Equals(name, StringComparison.OrdinalIgnoreCase)
        || (Math.Min(saved.Length, name.Length) >= 24 && (saved.StartsWith(name, StringComparison.OrdinalIgnoreCase) || name.StartsWith(saved, StringComparison.OrdinalIgnoreCase)));
}
