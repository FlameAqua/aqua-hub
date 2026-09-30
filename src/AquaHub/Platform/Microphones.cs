using System.Runtime.InteropServices;
using AquaHub.Core.Util;

namespace AquaHub.Platform;

/// <summary>
/// The microphones Windows has. Windows' offline recognizer can only listen to the default microphone by itself, so
/// for a chosen one Aqua records it (<see cref="MicCapture"/>) and hands the recognizer the audio.
/// </summary>
public static class Microphones
{
    /// <summary>A microphone: its wave-in number and its name (the full Windows name when it can be matched).</summary>
    public sealed record Mic(int Index, string Name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public short Manufacturer;
        public short Product;
        public int DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public int Formats;
        public short Channels;
        public short Reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)] private static extern int waveInGetDevCaps(nuint device, ref WaveInCaps caps, int size);

    /// <summary>The microphones as the wave-in list names them (cut at 31 characters). Quick; no permission needed.</summary>
    public static IReadOnlyList<Mic> List()
    {
        var mics = new List<Mic>();
        try
        {
            var count = waveInGetNumDevs();
            for (var i = 0; i < count; i++)
            {
                var caps = new WaveInCaps();
                if (waveInGetDevCaps((nuint)i, ref caps, Marshal.SizeOf<WaveInCaps>()) == 0 && !string.IsNullOrWhiteSpace(caps.Name))
                    mics.Add(new Mic(i, caps.Name.Trim()));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return mics;
    }

    /// <summary>The microphones with their full Windows names.</summary>
    public static async Task<IReadOnlyList<Mic>> ListAsync()
    {
        var mics = List();
        try
        {
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.AudioCapture);
            var names = devices.Where(d => d.IsEnabled).Select(d => d.Name).ToList();
            return mics.Select(m => m with { Name = MicNames.Expand(m.Name, names) }).ToList();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug("voice", "Couldn't read the full microphone names: " + ex.Message);
            return mics;
        }
    }

    /// <summary>The name of Windows' default microphone, or null.</summary>
    public static async Task<string?> DefaultNameAsync()
    {
        try
        {
            var id = Windows.Media.Devices.MediaDevice.GetDefaultAudioCaptureId(Windows.Media.Devices.AudioDeviceRole.Default);
            return string.IsNullOrEmpty(id) ? null : (await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(id)).Name;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return null; }
    }

    /// <summary>The microphone a saved name means, or null when it isn't connected.</summary>
    public static Mic? Find(IReadOnlyList<Mic> mics, string name) =>
        name.Trim().Length == 0 ? null
        : mics.FirstOrDefault(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? mics.FirstOrDefault(m => MicNames.Same(name.Trim(), m.Name));
}

/// <summary>Why a microphone couldn't be used, in plain words.</summary>
public sealed class MicException(string message) : Exception(message);

/// <summary>
/// Records one microphone (or Windows' default, <see cref="DefaultDevice"/>) into a <see cref="PcmPipe"/> until
/// disposed — 16 kHz, 16-bit mono, the format the speech recognizers want (Windows converts from the device's own) —
/// and reports how loud it is as it goes.
/// </summary>
public sealed class MicCapture : IDisposable
{
    public const int SampleRate = 16000;
    /// <summary>Windows' default microphone (the wave mapper).</summary>
    public const int DefaultDevice = -1;
    private const int CallbackEvent = 0x00050000, WaveMapped = 0x0004, WhdrDone = 0x1;
    private const int Buffers = 4, BufferBytes = SampleRate * 2 / 10;   // 100 ms each

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr handle);

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsAt = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
    private static readonly int RecordedAt = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.BytesRecorded));

    private readonly PcmPipe _pipe;
    private readonly Action<double>? _level;
    private readonly AutoResetEvent _filled = new(false);
    private readonly IntPtr[] _headers = new IntPtr[Buffers];
    private readonly Thread? _thread;
    private IntPtr _handle;
    private volatile bool _running = true;
    private bool _disposed;

    /// <summary>The microphone stopped by itself (unplugged, or taken away by Windows).</summary>
    public bool Failed { get; private set; }

    public MicCapture(int device, PcmPipe pipe, Action<double>? level)
    {
        _pipe = pipe;
        _level = level;
        var format = new WaveFormat { FormatTag = 1, Channels = 1, SamplesPerSec = SampleRate, AvgBytesPerSec = SampleRate * 2, BlockAlign = 2, BitsPerSample = 16 };
        try
        {
            // A chosen device goes through the mapper too, which converts its own format to this one.
            Check(waveInOpen(out _handle, unchecked((uint)device), ref format, _filled.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero,
                             CallbackEvent | (device == DefaultDevice ? 0 : WaveMapped)));
            for (var i = 0; i < Buffers; i++)
            {
                var header = Marshal.AllocHGlobal(HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = Marshal.AllocHGlobal(BufferBytes), BufferLength = BufferBytes }, header, false);
                _headers[i] = header;
                Check(waveInPrepareHeader(_handle, header, HeaderSize));
                Check(waveInAddBuffer(_handle, header, HeaderSize));
            }
            Check(waveInStart(_handle));
        }
        catch
        {
            Release();
            throw;
        }
        _thread = new Thread(Pump) { IsBackground = true, Name = "Aqua microphone" };
        _thread.Start();
    }

    private static void Check(int result)
    {
        if (result == 0) return;
        throw new MicException(result switch
        {
            2 or 6 => "That microphone isn't connected any more — pick another in Settings › Ask Aqua › Microphone.",
            4 => "The microphone is busy, or Windows isn't letting desktop apps use it (Settings › Privacy & security › Microphone).",
            32 => "That microphone can't record the way speech recognition needs — try another one.",
            _ => $"The microphone couldn't be opened (Windows error {result}).",
        });
    }

    /// <summary>Takes each filled buffer in turn, passes the audio on and hands the buffer back.</summary>
    private void Pump()
    {
        var next = 0;
        while (_running)
        {
            _filled.WaitOne(300);
            while (_running)
            {
                var header = _headers[next];
                var flags = Marshal.ReadInt32(header, FlagsAt);
                if ((flags & WhdrDone) == 0) break;
                var recorded = Marshal.ReadInt32(header, RecordedAt);
                if (recorded > 0)
                {
                    var audio = new byte[recorded];
                    Marshal.Copy(Marshal.ReadIntPtr(header), audio, 0, recorded);
                    _pipe.Write(audio, 0, recorded);
                    _level?.Invoke(PcmPipe.PeakLevel(audio, recorded));
                }
                Marshal.WriteInt32(header, FlagsAt, flags & ~WhdrDone);
                Marshal.WriteInt32(header, RecordedAt, 0);
                if (waveInAddBuffer(_handle, header, HeaderSize) != 0)
                {
                    Failed = true;
                    _running = false;
                    _pipe.Complete();
                    break;
                }
                next = (next + 1) % Buffers;
            }
        }
    }

    private void Release()
    {
        if (_handle != IntPtr.Zero) waveInReset(_handle);
        for (var i = 0; i < _headers.Length; i++)
        {
            if (_headers[i] == IntPtr.Zero) continue;
            if (_handle != IntPtr.Zero) waveInUnprepareHeader(_handle, _headers[i], HeaderSize);
            Marshal.FreeHGlobal(Marshal.ReadIntPtr(_headers[i]));
            Marshal.FreeHGlobal(_headers[i]);
            _headers[i] = IntPtr.Zero;
        }
        if (_handle != IntPtr.Zero) waveInClose(_handle);
        _handle = IntPtr.Zero;
        _filled.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _filled.Set();
        _thread?.Join(1000);
        Release();
        _pipe.Complete();
    }
}
