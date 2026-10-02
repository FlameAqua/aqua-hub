using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AquaHub.Core.Analysis;

namespace AquaHub.Core.Ai.Assistant;

public sealed record FileHit(string Path, string Name, long Size, DateTime Modified, int Score, string? Snippet)
{
    /// <summary>The search words this file matched (for the activity list and the model).</summary>
    public IReadOnlyList<string> Matched { get; init; } = Array.Empty<string>();
    /// <summary>Only in the cloud (OneDrive): reading it would download it first.</summary>
    public bool CloudOnly { get; init; }
}

/// <summary>A file the Windows Search index returned (the platform queries it; Core checks and ranks it).</summary>
public sealed record IndexedFile(string Path, DateTime Modified, long Size, bool ContentMatch);

/// <summary>What to look for: words from the file's name (any of them, best matches first), a kind, folders to try first.</summary>
public sealed record FileQuery
{
    public IReadOnlyList<string> Terms { get; init; } = Array.Empty<string>();
    /// <summary>any | image | document | spreadsheet | presentation | pdf | video | audio | code | archive</summary>
    public string Kind { get; init; } = "any";
    /// <summary>Folders searched first (full paths; they must still be inside the allowed folders).</summary>
    public IReadOnlyList<string> Prefer { get; init; } = Array.Empty<string>();
    public int Max { get; init; } = 15;
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Also look inside small text files.</summary>
    public bool Content { get; init; } = true;
}

/// <summary>
/// The part of the PC Ask may look at: only the folders listed in Settings › Ask Aqua (Documents, Desktop, Downloads
/// and Pictures by default) plus files you attach yourself. Credentials, keys and app data are never read, even inside
/// those folders, and paths are resolved before checking so ".." or links can't step outside.
/// <para>
/// Searching walks your own folders first (Desktop, Documents, Pictures…), breadth-first, and skips Windows, program
/// and app-data folders altogether — so even when you allow a whole drive, a search reaches your files in time.
/// </para>
/// </summary>
public sealed partial class LocalFiles
{
    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly Func<IReadOnlyList<string>>? _userFolders;
    private readonly HashSet<string> _granted = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="roots">The allowed folders.</param>
    /// <param name="userFolders">Your Desktop, Documents, Pictures… — searched before the rest of a root that contains them.</param>
    public LocalFiles(Func<IReadOnlyList<string>> roots, Func<IReadOnlyList<string>>? userFolders = null)
    {
        _roots = roots;
        _userFolders = userFolders;
    }

    public static readonly string[] UserFolderTokens = { "%DESKTOP%", "%DOCUMENTS%", "%PICTURES%", "%DOWNLOADS%", "%VIDEOS%", "%MUSIC%", "%OneDrive%" };

    /// <summary>Expands the folder tokens used in settings (%DOCUMENTS%, %DESKTOP%, %DOWNLOADS%, %PICTURES%, environment variables).</summary>
    public static List<string> ExpandFolders(IEnumerable<string> folders, Func<string, string?>? knownFolder = null)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? Known(string token) => knownFolder?.Invoke(token) ?? token switch
        {
            "DOCUMENTS" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "DESKTOP" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "PICTURES" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "MUSIC" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "VIDEOS" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "DOWNLOADS" => Path.Combine(profile, "Downloads"),
            _ => null,
        };
        var list = new List<string>();
        foreach (var raw in folders)
        {
            var f = raw.Trim();
            var m = Regex.Match(f, @"^%([A-Z]+)%(.*)$");
            if (m.Success && Known(m.Groups[1].Value) is { Length: > 0 } baseDir) f = baseDir + m.Groups[2].Value;
            f = Environment.ExpandEnvironmentVariables(f);
            if (f.Length == 0 || !Path.IsPathFullyQualified(f)) continue;
            try { f = Path.GetFullPath(f); } catch { continue; }
            if (f.Length > 3) f = f.TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(f) && !list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        }
        return list;
    }

    public IReadOnlyList<string> Roots => _roots();

    /// <summary>Files you attached or picked yourself are readable wherever they are (still never credential files).</summary>
    public void Grant(string path)
    {
        try { _granted.Add(Path.GetFullPath(path)); } catch { }
    }

    [GeneratedRegex(@"(^|[\\/])(\.ssh|\.gnupg|\.aws|\.azure|\.kube|\.docker|\.config|AppData|\$Recycle\.Bin|System Volume Information|Windows|Program Files( \(x86\))?|ProgramData)([\\/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveFolder();

    [GeneratedRegex(@"(password|passwd|secret|credential|private[-_ ]?key|recovery[-_ ]?code|seed[-_ ]?phrase|wallet|id_rsa|id_ed25519|id_ecdsa|\.env$)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".kdbx", ".kdb", ".pfx", ".p12", ".pem", ".key", ".ppk", ".asc", ".gpg", ".pgp", ".keychain", ".wallet", ".ovpn", ".rdp",
        ".cer", ".crt", ".jks", ".keystore", ".sqlite", ".db", ".ldb", ".dat", ".vault",
    };

    /// <summary>Password stores, keys, certificates and similar files — never read, wherever they are.</summary>
    public static bool IsSensitiveName(string path) =>
        SensitiveExtensions.Contains(Path.GetExtension(path)) || SensitiveName().IsMatch(Path.GetFileName(path));

    /// <summary>Folders that hold app data or credentials (.ssh, AppData, Windows…) — never searched or offered as a root.</summary>
    public static bool IsSensitiveFolder(string path) => SensitiveFolder().IsMatch(path);

    /// <summary>
    /// A sensitive file, or one inside a sensitive folder <em>below</em> <paramref name="root"/> (a root you chose
    /// yourself is judged when you add it, so your Documents can live anywhere).
    /// </summary>
    public static bool IsSensitive(string fullPath, string? root = null) =>
        IsSensitiveName(fullPath) || IsSensitiveFolder(root is not null && IsUnder(fullPath, root) ? fullPath[root.TrimEnd(Path.DirectorySeparatorChar).Length..] : fullPath);

    public bool CanRead(string path, out string fullPath, out string reason)
    {
        reason = "";
        fullPath = "";
        try { fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
        catch { reason = "That isn't a valid path"; return false; }
        const string never = "Aqua never reads passwords, keys or app data";
        if (IsSensitiveName(fullPath)) { reason = never; return false; }
        // Files you attached yourself: only the file-name rule applies (they may come from a temp folder).
        if (_granted.Contains(fullPath)) return true;
        var candidate = fullPath;
        var root = Roots.FirstOrDefault(r => IsUnder(candidate, r));
        if (root is null)
        {
            reason = "That's outside the folders Ask may read (Settings › Ask Aqua › Folders)";
            return false;
        }
        if (IsSensitive(fullPath, root)) { reason = never; return false; }
        return true;
    }

    public static bool IsUnder(string path, string root) =>
        path.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Folders a search never walks into: system, programs, app data, caches, build output and game libraries.</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery",
        "PerfLogs", "AppData", "MSOCache", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS", "Config.Msi", "OneDriveTemp", "WindowsApps",
        "node_modules", ".git", ".svn", ".hg", "bin", "obj", ".vs", ".idea", "__pycache__", ".venv", "venv", "packages", "target", "dist", "build",
        "steamapps", "SteamLibrary", "Epic Games", "XboxGames", "Riot Games", "Battle.net", "EA Games", "Ubisoft Game Launcher", "GOG Galaxy",
        "Origin Games", "site-packages", ".nuget", ".cache", ".gradle", ".m2", ".npm", ".cargo", ".rustup",
    };

    private static readonly Dictionary<string, HashSet<string>> KindSets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image"] = Set(".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".svg", ".ico", ".psd", ".raw", ".cr2", ".nef", ".arw", ".dng"),
        ["document"] = Set(".pdf", ".docx", ".doc", ".odt", ".rtf", ".txt", ".md", ".pages", ".epub", ".docm"),
        ["spreadsheet"] = Set(".xlsx", ".xlsm", ".xls", ".csv", ".tsv", ".ods", ".numbers"),
        ["presentation"] = Set(".pptx", ".ppt", ".odp", ".key"),
        ["pdf"] = Set(".pdf"),
        ["video"] = Set(".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".flv", ".mpg", ".mpeg"),
        ["audio"] = Set(".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".opus"),
        ["code"] = Set(".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".go", ".rs", ".json", ".xml", ".ps1", ".sh", ".html", ".css", ".sql", ".yaml", ".yml"),
        ["archive"] = Set(".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso"),
    };

    private static HashSet<string> Set(params string[] e) => new(e, StringComparer.OrdinalIgnoreCase);

    public static bool IsKind(string path, string kind) =>
        kind is "" or "any" || KindSets.TryGetValue(kind, out var set) && set.Contains(Path.GetExtension(path));

    public static bool IsImageFile(string path) => KindSets["image"].Contains(Path.GetExtension(path));

    /// <summary>
    /// The words of a file name: split at spaces, punctuation, underscores, camelCase and digits, lower-cased and
    /// singular ("my_logo5" → my, logo, 5; "BlueFlameLogo" → blue, flame, logo).
    /// </summary>
    public static List<string> NameTokens(string name)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            var t = TextTools.Stem(sb.ToString().ToLowerInvariant());
            if (!tokens.Contains(t)) tokens.Add(t);
            sb.Clear();
        }
        var folded = TextTools.Fold(name);
        for (var i = 0; i < folded.Length; i++)
        {
            var ch = folded[i];
            if (!char.IsLetterOrDigit(ch)) { Flush(); continue; }
            if (sb.Length > 0)
            {
                var prev = folded[i - 1];
                var camel = char.IsUpper(ch) && char.IsLower(prev);
                var digitEdge = char.IsDigit(ch) != char.IsDigit(prev);
                if (camel || digitEdge) Flush();
            }
            sb.Append(ch);
        }
        Flush();
        return tokens;
    }

    /// <summary>Normalises search words the same way as file names.</summary>
    public static List<string> Normalise(IEnumerable<string> terms)
    {
        var list = new List<string>();
        foreach (var raw in terms)
            foreach (var t in NameTokens(raw))
                if (t.Length >= 2 && !list.Contains(t)) list.Add(t);
        return list;
    }

    /// <summary>
    /// How well a file name matches (pure; unit-tested). Every word counts on its own — "logo flame" still finds
    /// my_logo5.png — and files matching more of the words rank higher. Folder names count a little.
    /// </summary>
    public static (int Score, List<string> Matched) ScoreName(string fileName, string folderPath, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return (0, new());
        var tokens = NameTokens(Path.GetFileNameWithoutExtension(fileName));
        var squashed = string.Concat(tokens);
        var folderTokens = NameTokens(string.Join(' ', folderPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).TakeLast(3)));
        double points = 0;
        var strong = false;
        var matched = new List<string>();
        foreach (var t in terms)
        {
            double p = 0;
            if (tokens.Contains(t)) p = 3;
            else if (tokens.Any(n => t.Length >= 3 && n.StartsWith(t, StringComparison.Ordinal) || n.Length >= 4 && t.StartsWith(n, StringComparison.Ordinal))) p = 2;
            else if (t.Length >= 4 && squashed.Contains(t, StringComparison.Ordinal)) p = 1.5;
            else if (folderTokens.Contains(t)) p = 1;
            if (p >= 2) strong = true;
            if (p > 0) matched.Add(t);
            points += p;
        }
        if (matched.Count == 0 || !strong && matched.Count < terms.Count) return (0, matched);
        var score = (int)Math.Round(60 * points / (3.0 * terms.Count));
        if (matched.Count == terms.Count) score += terms.Count > 1 ? 35 : 10;
        return (score, matched);
    }

    /// <summary>
    /// Finds files: every allowed folder, your own folders first and breadth-first (shallow files before deep ones),
    /// within a time budget; Windows' search index results (when the platform has them) are checked and ranked the same way.
    /// </summary>
    public List<FileHit> Find(FileQuery q, IEnumerable<IndexedFile>? indexed = null, CancellationToken ct = default)
    {
        var terms = Normalise(q.Terms);
        var kind = q.Kind is null or "" ? "any" : q.Kind;
        if (terms.Count == 0 && kind == "any") return new();
        var sw = Stopwatch.StartNew();
        var roots = Roots;
        var hits = new Dictionary<string, FileHit>(StringComparer.OrdinalIgnoreCase);
        var prefer = q.Prefer.Where(p => roots.Any(r => IsUnder(p, r))).ToList();
        var mine = _userFolders?.Invoke() ?? Array.Empty<string>();

        void Consider(string path, string name, long size, DateTime modified, FileAttributes attrs, string root, ref int contentChecks)
        {
            if (IsSensitive(path, root) || !IsKind(path, kind)) return;
            var folder = Path.GetDirectoryName(path) ?? "";
            var relFolder = folder.Length > root.Length ? folder[root.TrimEnd(Path.DirectorySeparatorChar).Length..] : "";
            var (score, matched) = ScoreName(name, relFolder, terms);
            string? snippet = null;
            var cloud = IsCloudOnly(attrs);
            if (terms.Count == 0) score = 20;
            else if (q.Content && score < 95 && contentChecks < 400 && !cloud && IsPlainText(path) && size is > 0 and < 512 * 1024)
            {
                contentChecks++;
                var (found, snip) = ContentMatch(path, terms);
                if (found > 0 && found >= Math.Max(1, (int)Math.Ceiling(terms.Count * 0.6)))
                {
                    var contentScore = 35 + 40 * found / terms.Count;
                    if (contentScore > score) { score = contentScore; snippet = snip; }
                    foreach (var t in terms) if (!matched.Contains(t) && snip is not null) matched.Add(t);
                }
            }
            if (score <= 0) return;
            if (prefer.Any(p => IsUnder(path, p))) score += 15;
            else if (mine.Any(p => IsUnder(path, p))) score += 8; // your Desktop, Documents, Pictures… over app folders
            hits[path] = new FileHit(path, name, size, modified, score, snippet) { Matched = matched, CloudOnly = cloud };
        }

        var checks = 0;
        // 1. The Windows search index (fast, whole drives, document text) — only what the folder rules allow, and
        //    nothing from the folders a walk skips (build output, caches, game libraries…).
        if (indexed is not null)
        {
            foreach (var f in indexed)
            {
                if (!CanRead(f.Path, out var full, out _) || !File.Exists(full)) continue;
                var root = roots.FirstOrDefault(r => IsUnder(full, r)) ?? Path.GetPathRoot(full) ?? "";
                if (InSkippedFolder(full, root)) continue;
                Consider(full, Path.GetFileName(full), f.Size, f.Modified, FileAttributes.Normal, root, ref checks);
                if (f.ContentMatch && !hits.ContainsKey(full) && !IsSensitive(full, root) && IsKind(full, kind))
                    hits[full] = new FileHit(full, Path.GetFileName(full), f.Size, f.Modified, 50, null) { Matched = terms };
            }
        }

        // 2. Walk: folders you named, then your own folders, then the rest of each allowed folder.
        var visitedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starts = new List<(string Dir, string Root)>();
        void Start(string dir)
        {
            var root = roots.FirstOrDefault(r => IsUnder(dir, r));
            if (root is null || starts.Any(s => s.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)) || !Directory.Exists(dir)) return;
            if (IsSensitive(dir + Path.DirectorySeparatorChar + "x", root)) return;
            starts.Add((dir, root));
        }
        foreach (var p in prefer) Start(p);
        foreach (var u in _userFolders?.Invoke() ?? Array.Empty<string>()) Start(u);
        foreach (var r in roots) Start(r);

        var visited = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, ReturnSpecialDirectories = false };
        foreach (var (start, root) in starts)
        {
            if (sw.Elapsed > q.Budget) break;
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((start, 0));
            while (queue.Count > 0 && sw.Elapsed < q.Budget && visited < 250_000)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = queue.Dequeue();
                if (!visitedDirs.Add(dir)) continue;
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { continue; }
                foreach (var e in entries)
                {
                    if (e is DirectoryInfo d)
                    {
                        if (depth >= 10 || SkipDirs.Contains(d.Name) || d.Name.StartsWith('.') || d.Name.StartsWith('$')) continue;
                        if (d.LinkTarget is not null) continue; // junctions and symlinks (loops, other drives)
                        if (!visitedDirs.Contains(d.FullName)) queue.Enqueue((d.FullName, depth + 1));
                        continue;
                    }
                    if (e is not FileInfo f) continue;
                    if (++visited > 250_000 || sw.Elapsed > q.Budget) break;
                    if (hits.ContainsKey(f.FullName)) continue;
                    Consider(f.FullName, f.Name, f.Length, f.LastWriteTime, f.Attributes, root, ref checks);
                }
            }
        }
        LastSearch = new SearchStats(visited, sw.Elapsed, sw.Elapsed >= q.Budget);
        // Best matches first; among equals, the most recently changed.
        return hits.Values.OrderByDescending(h => h.Score).ThenByDescending(h => h.Modified).Take(q.Max).ToList();
    }

    /// <summary>A file inside a folder searches never walk (node_modules, bin, .git, AppData, game libraries…), below <paramref name="root"/>.</summary>
    internal static bool InSkippedFolder(string path, string root)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var relative = IsUnder(folder, root) ? folder[root.TrimEnd(Path.DirectorySeparatorChar).Length..] : folder;
        return relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                       .Any(d => SkipDirs.Contains(d) || d.StartsWith('.') || d.StartsWith('$'));
    }

    /// <summary>How the last search went (files looked at, time, whether it ran out of time).</summary>
    public sealed record SearchStats(int Files, TimeSpan Elapsed, bool OutOfTime);
    public SearchStats? LastSearch { get; private set; }

    /// <summary>Finds files by the words of a query (the model's search_files tool and older callers).</summary>
    public List<FileHit> Search(string query, string kind = "any", int max = 15, TimeSpan? budget = null, CancellationToken ct = default) =>
        Find(new FileQuery { Terms = Regex.Split(query, @"[^\p{L}\p{N}_]+").Where(t => t.Length > 1 && !TextTools.IsStopword(t.ToLowerInvariant())).ToList(), Kind = kind, Max = max, Budget = budget ?? TimeSpan.FromSeconds(5) }, null, ct);

    /// <summary>The newest files of a kind in some folders (a few levels deep) — for "find the photo of…" when no name matches.</summary>
    public List<FileHit> Recent(string kind, IEnumerable<string> folders, int max = 8, int depth = 2)
    {
        var list = new List<FileHit>();
        var roots = Roots;
        void Walk(string dir, string root, int level)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }).Take(3000).ToList(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }
            foreach (var e in entries)
            {
                if (e is DirectoryInfo d)
                {
                    if (level < depth && !SkipDirs.Contains(d.Name) && !d.Name.StartsWith('.') && d.LinkTarget is null) Walk(d.FullName, root, level + 1);
                }
                else if (e is FileInfo f && IsKind(f.FullName, kind) && !IsSensitive(f.FullName, root))
                    list.Add(new FileHit(f.FullName, f.Name, f.Length, f.LastWriteTime, 10, null) { CloudOnly = IsCloudOnly(f.Attributes) });
            }
        }
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            if (roots.FirstOrDefault(r => IsUnder(folder, r)) is { } root && Directory.Exists(folder)) Walk(folder, root, 0);
        return list.Where(h => !h.CloudOnly).OrderByDescending(h => h.Modified).Take(max).ToList();
    }

    /// <summary>
    /// Folders called <paramref name="name"/> inside <paramref name="root"/> (a few levels down, breadth-first, skipping
    /// system and app folders) — so "my Pictures" also means D:\Stuff\Pictures when that's what you allowed.
    /// </summary>
    public static List<string> FindFolders(string root, string name, int depth = 3, int max = 4)
    {
        var found = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        while (queue.Count > 0 && found.Count < max && visited++ < 600)
        {
            var (dir, level) = queue.Dequeue();
            IEnumerable<DirectoryInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateDirectories("*", options).Take(300).ToList(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { continue; }
            foreach (var d in children)
            {
                if (SkipDirs.Contains(d.Name) || d.Name.StartsWith('.') || d.Name.StartsWith('$') || d.LinkTarget is not null) continue;
                if (d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) found.Add(d.FullName);
                else if (level + 1 < depth) queue.Enqueue((d.FullName, level + 1));
            }
        }
        return found;
    }

    /// <summary>OneDrive placeholders (not downloaded): FILE_ATTRIBUTE_OFFLINE, RECALL_ON_OPEN or RECALL_ON_DATA_ACCESS.</summary>
    public static bool IsCloudOnly(FileAttributes a) => (a & FileAttributes.Offline) != 0 || ((int)a & 0x00440000) != 0;

    private static (int Found, string? Snippet) ContentMatch(string path, IReadOnlyList<string> terms)
    {
        try
        {
            var text = File.ReadAllText(path);
            var folded = TextTools.Fold(text).ToLowerInvariant();
            var found = terms.Count(t => folded.Contains(t, StringComparison.Ordinal));
            if (found == 0) return (0, null);
            var first = terms.Select(t => folded.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
            var start = Math.Clamp(first - 60, 0, Math.Max(0, text.Length - 1));
            return (found, text.Substring(start, Math.Min(180, text.Length - start)).ReplaceLineEndings(" ").Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (0, null); }
    }

    private static bool IsPlainText(string path)
    {
        var ext = Path.GetExtension(path);
        return Documents.CanRead(path) && !ext.Equals(".docx", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".pptx", StringComparison.OrdinalIgnoreCase) &&
               !ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && !ext.StartsWith(".od", StringComparison.OrdinalIgnoreCase) &&
               !ext.Equals(".docm", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) &&
               !ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lists a folder Ask may read (newest first).</summary>
    public List<FileHit> List(string folder, int max = 40, string kind = "any")
    {
        var list = new List<FileHit>();
        var root = Roots.FirstOrDefault(r => IsUnder(folder, r));
        foreach (var d in Directory.EnumerateDirectories(folder).Take(200))
        {
            if (IsSensitive(d + Path.DirectorySeparatorChar + "x", root) || kind != "any") continue;
            try
            {
                var di = new DirectoryInfo(d);
                if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                list.Add(new FileHit(d + Path.DirectorySeparatorChar, di.Name + "\\", -1, di.LastWriteTime, 0, null));
            }
            catch { }
        }
        foreach (var f in Directory.EnumerateFiles(folder).Take(4000))
        {
            if (IsSensitive(f, root) || !IsKind(f, kind)) continue;
            try
            {
                var fi = new FileInfo(f);
                if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                list.Add(new FileHit(f, fi.Name, fi.Length, fi.LastWriteTime, 0, null) { CloudOnly = IsCloudOnly(fi.Attributes) });
            }
            catch { }
        }
        return list.OrderByDescending(h => h.Modified).Take(max).ToList();
    }

    /// <summary>
    /// A folder attached in Ask: how many files it holds (counting stops at <paramref name="cap"/>, or after two seconds)
    /// and its newest entries, skipping what a search skips (system, app data, build output, keys).
    /// </summary>
    public static (int Files, bool More, List<FileHit> Newest) Survey(string folder, int cap = 5000, int newest = 40)
    {
        var top = new LocalFiles(() => new[] { folder }).List(folder, newest);
        var count = 0;
        var sw = Stopwatch.StartNew();
        var queue = new Queue<string>();
        queue.Enqueue(folder);
        while (queue.Count > 0 && count < cap && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            var dir = queue.Dequeue();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (IsSensitive(f, folder)) continue;
                    if (++count >= cap) break;
                }
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (SkipDirs.Contains(name) || name.StartsWith('.') || IsSensitive(d + Path.DirectorySeparatorChar + "x", folder)) continue;
                    queue.Enqueue(d);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        return (count, count >= cap || queue.Count > 0, top);
    }

    public static string Size(long bytes) => bytes switch
    {
        < 0 => "folder",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}
