using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Search;
using Microsoft.Win32.SafeHandles;

namespace AquaHub.Platform;

/// <summary>Holds each directory against rename while inspecting its child. Opens metadata before requesting data.</summary>
public sealed class WindowsSearchFiles(string profile) : ISearchFiles
{
    private const uint Flags = 0x02000000 | 0x00200000 | 0x00100000; // backup semantics, open reparse point, no recall
    private readonly string _profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));

    internal static bool Excluded(FileAttributes attrs) =>
        (attrs & (FileAttributes.ReparsePoint | FileAttributes.Encrypted | FileAttributes.System | FileAttributes.Hidden)) != 0 || LocalFiles.IsCloudOnly(attrs);

    public string Approve(string path)
    {
        using var lease = Inspect(path, null, directory: true);
        return lease.Stamp.Identity;
    }

    public IDisposable DirectoryLease(string path, SearchRoot root) => Inspect(path, root, directory: true);
    public SearchFile Open(string path, SearchRoot root) => Inspect(path, root, directory: false);

    private Lease Inspect(string path, SearchRoot? root, bool directory)
    {
        if (path.Length < 4 || path.Length > 2048 || !char.IsAsciiLetter(path[0]) || path[1..3] != @":\"
            || path.AsSpan(2).Contains(':') || Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) != path
            || path.Split('\\').Skip(1).Any(s => s.Length == 0 || s.EndsWith(' ') || s.EndsWith('.') || s.Contains('~')))
            throw Denied();
        var drive = new DriveInfo(path[..3]);
        if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS" || !SingleDrive(drive.Name) || LocalFiles.IsSensitiveFolder(path)
            || path.Equals(_profile, StringComparison.OrdinalIgnoreCase) || path.StartsWith(_profile + "\\", StringComparison.OrdinalIgnoreCase)
            || LocalFiles.IsSensitiveName(path) || LocalFiles.InSkippedFolder(directory ? Path.Combine(path, "_") : path, drive.Name))
            throw Denied();
        if (root != null && path != root.Path && !path.StartsWith(root.Path + "\\", StringComparison.Ordinal)) throw Denied();

        var parts = path[3..].Split('\\');
        if (parts.Length > 64 || parts.Any(p => p.StartsWith('.') || p.StartsWith('$') || LocalFiles.IsSensitiveName(p))) throw Denied();
        var handles = new List<SafeFileHandle>();
        try
        {
            var current = drive.Name;
            SearchStamp? stamp = null;
            var foundRoot = root == null;
            foreach (var part in parts)
            {
                current = Path.Combine(current, part);
                bool isDirectory = current != path || directory;
                var handle = CreateFileW(current, 0x80, isDirectory ? 3u : 1u, IntPtr.Zero, 3, Flags, IntPtr.Zero);
                handles.Add(handle);
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw NativeError();
                var attrs = (FileAttributes)info.Attributes;
                if (Excluded(attrs) || attrs.HasFlag(FileAttributes.Directory) != isDirectory || (!isDirectory && info.Links != 1))
                    throw Denied();
                var final = new StringBuilder(4096);
                var count = GetFinalPathNameByHandleW(handle, final, (uint)final.Capacity, 0);
                if (count == 0 || count >= final.Capacity || !final.ToString().Equals(@"\\?\" + current, StringComparison.OrdinalIgnoreCase)) throw Denied();
                long changed = 0;
                if (!isDirectory)
                {
                    if (!GetFileInformationByHandleEx(handle, 0, out BasicInfo basic, (uint)Marshal.SizeOf<BasicInfo>())) throw NativeError();
                    changed = basic.Changed;
                }
                stamp = new SearchStamp($"{info.Volume:X8}-{info.IndexHigh:X8}{info.IndexLow:X8}-{info.CreatedHigh:X8}{info.CreatedLow:X8}",
                    ((long)info.SizeHigh << 32) | info.SizeLow, ((long)info.ModifiedHigh << 32) | info.ModifiedLow, changed);
                if (root?.Path == current)
                {
                    if (stamp.Identity != root.Identity) throw Denied();
                    foundRoot = true;
                }
            }
            if (!foundRoot || stamp == null) throw Denied();
            // Verify effective read access even for a cached preview, using the exact object already inspected.
            if (!directory)
            {
                var readable = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, Flags, IntPtr.Zero);
                handles.Add(readable);
                if (readable.IsInvalid || !GetFileInformationByHandle(readable, out var actual)) throw NativeError();
                if (stamp.Identity != $"{actual.Volume:X8}-{actual.IndexHigh:X8}{actual.IndexLow:X8}-{actual.CreatedHigh:X8}{actual.CreatedLow:X8}"
                    || actual.Links != 1 || LocalFiles.IsCloudOnly((FileAttributes)actual.Attributes)) throw Denied();
            }
            return new Lease(handles, stamp);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private static bool SingleDrive(string drive)
    {
        var volume = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(drive, volume, (uint)volume.Capacity)) return false;
        var mounts = new char[4096];
        if (!GetVolumePathNamesForVolumeNameW(volume.ToString(), mounts, (uint)mounts.Length, out var used)) return false;
        var letters = new string(mounts, 0, (int)used).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length == 3 && p[1] == ':').Take(2).ToArray();
        return letters.Length == 1 && letters[0].Equals(drive, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder volume, uint size);
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volume, [Out] char[] paths, uint size, out uint used);

    private static UnauthorizedAccessException Denied() => new("This location is excluded or its identity has changed.");
    private static Exception NativeError() => Marshal.GetLastWin32Error() switch
    {
        2 or 3 => new FileNotFoundException("File or folder is unavailable."),
        var code => new IOException("Windows could not grant access to this file.", new Win32Exception(code)),
    };

    private sealed class Lease(List<SafeFileHandle> handles, SearchStamp stamp) : SearchFile
    {
        public override SearchStamp Stamp => stamp;
        public override Stream Read() => new FileStream(handles[^1], FileAccess.Read);
        public override void Dispose() { foreach (var handle in handles) handle.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public long Created, Accessed, Modified, Changed; public uint Attributes; }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out BasicInfo info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, ModifiedLow, ModifiedHigh, Volume,
            SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative info);
    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
}
