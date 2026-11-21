using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace AquaHub.Core.Search;

public sealed record SearchStatus(string Activity, int Files, bool Paused, string Semantic, int Roots);

/// <summary>One writer, one coalesced wake-up and a bounded set of change hints. The database owns consent.</summary>
public sealed class FileSearchService
{
    private readonly SearchStore _store;
    private readonly ISearchFiles _files;
    private readonly ISearchEmbeddings _engine;
    private readonly object _gate = new();
    private readonly object _authority = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(1);
    private readonly Dictionary<long, FileSystemWatcher> _watchers = [];
    private readonly HashSet<long> _failedWatchers = [];
    private readonly Dictionary<string, long> _hints = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _operations = [];
    private readonly Task _worker;
    private CancellationTokenSource? _pass;
    private bool _rescan = true;
    private long _revision;
    private int _queries;
    private Task? _shutdown;
    private volatile SearchStatus _status = new("Starting Search", 0, false, "", 0);
    public SearchStatus Status => _status;
    public bool MeaningAvailable => _engine.Configured;
    public event Action? Changed;
    public long Revision => Interlocked.Read(ref _revision);

    public FileSearchService(SearchStore store, ISearchFiles files, ISearchEmbeddings engine)
    {
        _store = store;
        _files = files;
        _engine = engine;
        _worker = Task.Run(WorkAsync);
    }

    private Task<T> Owned<T>(Func<CancellationToken, Task<T>> action, CancellationToken token = default)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return Task.FromCanceled<T>(_stop.Token);
            if (_operations.Count >= 16) throw new InvalidOperationException("Search is busy. Try again shortly.");
            var task = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
                linked.Token.ThrowIfCancellationRequested();
                return await action(linked.Token).ConfigureAwait(false);
            });
            _operations.Add(task);
            _ = task.ContinueWith(t => { lock (_gate) _operations.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    public Task<List<SearchRoot>> RootsAsync() => Owned(_ => Task.FromResult(_store.Roots()));
    public Task AddAsync(string path, bool content) => Change(() => _store.Add(path, _files.Approve(path), content));
    public Task RemoveAsync(long root) => Change(() => _store.Remove(root));
    public Task ContentAsync(SearchRoot root, bool content) => Change(() =>
    {
        if (_files.Approve(root.Path) != root.Identity) throw new UnauthorizedAccessException("Choose this folder again.");
        _store.ChangeContent(root, content);
    });
    public Task PauseAsync(bool paused) => Change(() => _store.Paused = paused);
    private Task Change(Action action) => Owned(ct =>
    {
        lock (_authority)
        {
            ct.ThrowIfCancellationRequested();
            action();
            Interlocked.Increment(ref _revision);
        }
        lock (_gate)
        {
            _rescan = true;
            _pass?.Cancel();
            _wake.Writer.TryWrite(true);
        }
        Changed?.Invoke();
        return Task.FromResult(true);
    });

    public Task<List<SearchHit>> FindAsync(string query, SearchFilter filter, bool meaning, CancellationToken ct) => Owned(async token =>
    {
        if (query.Trim().Length is 0 or > 256) return [];
        Interlocked.Increment(ref _queries);
        try
        {
            var roots = _store.Roots().ToDictionary(r => r.Id);
            float[]? vector = null;
            if (meaning && roots.Values.Any(r => r.Content && filter.Intersects(r) && (filter.Root == null || filter.Root == r.Id)) && _engine.Configured)
            {
                try { vector = await _engine.EncodeAsync("task: search result | query: " + query, token).ConfigureAwait(false); }
                catch (Exception) when (!token.IsCancellationRequested) { SetSemantic("Meaning search unavailable. Check the Search model."); }
            }
            token.ThrowIfCancellationRequested();
            var hits = new List<SearchHit>();
            foreach (var h in _store.Candidates(query, filter, vector, token))
            {
                token.ThrowIfCancellationRequested();
                if (roots.TryGetValue(h.RootId, out var root) && Valid(root, h))
                {
                    var relative = Path.GetRelativePath(root.Path, Path.GetDirectoryName(h.Path)!);
                    hits.Add(h with { Location = relative == "." ? Path.GetFileName(root.Path) : Path.Combine(Path.GetFileName(root.Path), relative) });
                }
                if (hits.Count == 50) break;
            }
            var grants = _store.Roots().Select(r => r.Id).ToHashSet();
            hits.RemoveAll(h => !grants.Contains(h.RootId));
            return hits;
        }
        finally
        {
            if (Interlocked.Decrement(ref _queries) == 0) _wake.Writer.TryWrite(true);
        }
    }, ct);

    private bool Valid(SearchRoot root, SearchHit h)
    {
        try { using var file = _files.Open(h.Path, root); return file.Stamp == h.Stamp; }
        catch (Exception ex) when (FileFailure(ex)) { return false; }
    }

    public Task ActAsync(SearchHit hit, Action<string> effect) => Owned(ct =>
    {
        lock (_authority)
        {
            ct.ThrowIfCancellationRequested();
            var root = _store.Roots().Find(r => r.Id == hit.RootId) ?? throw new UnauthorizedAccessException("Folder permission was removed.");
            using var file = _files.Open(hit.Path, root);
            if (file.Stamp != hit.Stamp) throw new IOException("This file changed. Search again.");
            effect(hit.Path);
        }
        return Task.FromResult(true);
    });

    public void Refresh()
    {
        lock (_gate) { _rescan = true; _wake.Writer.TryWrite(true); }
    }

    private void Hint(long root, string? path)
    {
        lock (_gate)
        {
            if (path == null) _failedWatchers.Add(root);
            if (path == null || _hints.Count >= 256) { _rescan = true; _hints.Clear(); }
            else if (!_rescan) _hints[path] = root;
            _wake.Writer.TryWrite(true);
        }
    }

    private void Watch(List<SearchRoot> roots)
    {
        long[] failed;
        lock (_gate) { failed = _failedWatchers.ToArray(); _failedWatchers.Clear(); }
        foreach (var id in _watchers.Keys.Where(id => failed.Contains(id) || roots.All(r => r.Id != id)).ToArray())
        { _watchers[id].Dispose(); _watchers.Remove(id); }
        foreach (var root in roots.Where(r => !_watchers.ContainsKey(r.Id)))
        {
            try
            {
                using var dir = _files.DirectoryLease(root.Path, root);
                var watcher = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.Security
                };
                _watchers[root.Id] = watcher;
                watcher.Created += (_, e) => Hint(root.Id, e.FullPath);
                watcher.Changed += (_, e) => Hint(root.Id, e.FullPath);
                watcher.Deleted += (_, e) => Hint(root.Id, e.FullPath);
                watcher.Renamed += (_, e) => { Hint(root.Id, e.OldFullPath); Hint(root.Id, e.FullPath); };
                watcher.Error += (_, _) => Hint(root.Id, null);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (FileFailure(ex)) { if (_watchers.Remove(root.Id, out var watcher)) watcher.Dispose(); }
        }
    }

    private static bool FileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    private async Task<bool> EmbedPendingAsync(List<SearchRoot> roots, CancellationToken ct)
    {
        SetSemantic("Indexing meaning");
        bool skipped = false;
        long after = 0;
        while (Volatile.Read(ref _queries) == 0 && _store.NextPassage(after) is { } item)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate) { if (_rescan || _hints.Count > 0) return true; }
            after = item.Id;
            var root = roots.Find(r => r.Id == item.Hit.RootId);
            if (root == null || !Valid(root, item.Hit)) { skipped = true; continue; }
            var title = Path.GetFileNameWithoutExtension(item.Hit.Path);
            var vector = await _engine.EncodeAsync($"title: {title} | text: {item.Text}", ct).ConfigureAwait(false);
            if (Valid(root, item.Hit)) _store.SaveVector(item.Id, vector);
            else skipped = true;
        }
        if (_store.NextPassage(after) != null) return true;
        SetSemantic(skipped ? "Partial meaning index · files unavailable" : "Meaning search ready");
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
        return false;
    }
    private void SetSemantic(string value)
    {
        if (_status.Semantic == value) return;
        lock (_gate) _status = _status with { Semantic = value };
        Changed?.Invoke();
    }

    private async Task WorkAsync()
    {
        var nextScan = DateTime.UtcNow;
        var incomplete = new HashSet<long>();
        bool pendingVectors = true;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                KeyValuePair<string, long>[] hints;
                bool scan;
                lock (_gate)
                {
                    _pass = pass;
                    scan = _rescan || DateTime.UtcNow >= nextScan;
                    _rescan = false;
                    hints = _hints.ToArray();
                    _hints.Clear();
                }
                try
                {
                    if (scan || hints.Length > 0 || pendingVectors)
                    {
                        var roots = _store.Roots();
                        bool paused = _store.Paused;
                        incomplete.RemoveWhere(id => roots.All(r => r.Id != id));
                        if (scan || hints.Length > 0)
                        {
                            Watch(paused ? [] : roots);
                            _status = new(paused ? "Indexing paused" : "Indexing", _store.Count(), paused, _status.Semantic, roots.Count);
                            Changed?.Invoke();
                            if (!paused)
                            {
                                foreach (var root in roots)
                                {
                                    var changed = hints.Where(h => h.Value == root.Id).Select(h => h.Key).ToArray();
                                    if (!scan && changed.Length == 0) continue;
                                    if (!Index(root, scan, changed, pass.Token)) incomplete.Add(root.Id);
                                    else if (scan) incomplete.Remove(root.Id);
                                }
                            }
                            if (scan) nextScan = DateTime.UtcNow.AddMinutes(15);
                            _status = _status with { Files = _store.Count(), Activity = paused ? "Indexing paused" : incomplete.Count > 0 ? "Partial index · some files are unavailable or exceed limits" : "Index up to date" };
                            pendingVectors = true;
                            Interlocked.Increment(ref _revision);
                            Changed?.Invoke();
                        }
                        if (!paused && pendingVectors && _engine.Configured && roots.Any(r => r.Content))
                        {
                            pendingVectors = await EmbedPendingAsync(roots, pass.Token).ConfigureAwait(false);
                        }
                        else if (!_engine.Configured || paused || !roots.Any(r => r.Content))
                        {
                            pendingVectors = false;
                            SetSemantic(!roots.Any(r => r.Content) ? "Names and paths only" : !_engine.Configured ? "Meaning search unavailable"
                                : paused ? "Meaning indexing paused" : "Names and paths only");
                        }
                    }
                    _engine.Release();
                }
                catch (OperationCanceledException) when (pass.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (ex is SqliteException or SearchIndexLimitException)
                        _status = _status with { Activity = "Partial index · check available space and folder limits" };
                    else SetSemantic("Meaning search unavailable · check the runtime and model");
                    pendingVectors = false;
                    Changed?.Invoke();
                }
                finally { lock (_gate) _pass = null; }
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                tick.CancelAfter(pendingVectors && _engine.Configured && Volatile.Read(ref _queries) > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1));
                try { await _wake.Reader.ReadAsync(tick.Token).ConfigureAwait(false); await Task.Delay(100, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (tick.IsCancellationRequested) { }
            }
        }
        finally { foreach (var watcher in _watchers.Values) watcher.Dispose(); }
    }

    private bool Index(SearchRoot root, bool full, string[] hints, CancellationToken ct)
    {
        bool complete = true;
        bool capacityReached = false;
        int visited = 0, written = 0;
        var progress = Stopwatch.StartNew();
        try
        {
            using var rootLease = _files.DirectoryLease(root.Path, root);
            var previous = _store.Inventory(root.Id).ToDictionary(v => v.Path, v => v.Stamp, StringComparer.Ordinal);
            void Inspect(string path, bool force)
            {
                ct.ThrowIfCancellationRequested();
                if (capacityReached && !previous.ContainsKey(path)) return;
                try
                {
                    using var file = _files.Open(path, root);
                    if (!force && previous.TryGetValue(path, out var old) && old == file.Stamp) return;
                    List<string> chunks = [];
                    if (root.Content && SearchText.Supports(path) && file.Stamp.Length <= SearchText.InputLimit)
                    {
                        try { using var data = file.Read(); chunks = SearchText.Extract(path, data, ct); }
                        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or ArgumentException or NotSupportedException) { complete = false; }
                    }
                    ct.ThrowIfCancellationRequested();
                    _store.Replace(root, path, file.Stamp, chunks);
                    if (++written % 50 == 0 && progress.ElapsedMilliseconds >= 800)
                    {
                        progress.Restart();
                        Interlocked.Increment(ref _revision);
                        _status = _status with { Files = _store.Count() };
                        Changed?.Invoke();
                    }
                }
                catch (SearchIndexLimitException) { capacityReached = true; complete = false; }
                catch (FileNotFoundException) { _store.Forget(root.Id, path); }
                catch (UnauthorizedAccessException) { _store.Forget(root.Id, path); }
                catch (Exception ex) when (FileFailure(ex)) { complete = false; }
            }
            void Walk(string dir, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (depth > 64 || visited >= 100000) { complete = false; return; }
                bool directoryApproved = false;
                try
                {
                    using var lease = _files.DirectoryLease(dir, root);
                    directoryApproved = true;
                    foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++visited >= 100000) { complete = false; break; }
                        if (entry.Attributes.HasFlag(FileAttributes.Directory)) Walk(entry.FullName, depth + 1);
                        else Inspect(entry.FullName, false);
                    }
                }
                catch (UnauthorizedAccessException) { if (directoryApproved) complete = false; }
                catch (Exception ex) when (FileFailure(ex)) { complete = false; }
            }
            if (full) Walk(root.Path, 0);
            else foreach (var path in hints)
            {
                try { using var dir = _files.DirectoryLease(path, root); Walk(path, 0); }
                catch (Exception ex) when (FileFailure(ex)) { Inspect(path, true); }
            }
            // Confirm absence independently: an interrupted/inaccessible walk never authorizes bulk eviction.
            foreach (var path in previous.Keys.Where(p => full || hints.Any(h => SearchStore.Inside(p, h))))
            {
                ct.ThrowIfCancellationRequested();
                try { using var file = _files.Open(path, root); }
                catch (FileNotFoundException) { _store.Forget(root.Id, path); }
                catch (UnauthorizedAccessException) { _store.Forget(root.Id, path); }
                catch (Exception ex) when (FileFailure(ex)) { complete = false; }
            }
        }
        catch (Exception ex) when (FileFailure(ex)) { complete = false; }
        return complete;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_shutdown != null) return _shutdown;
            _stop.Cancel();
            return _shutdown = FinishAsync(_operations.Append(_worker).ToArray());
        }
    }
    private async Task FinishAsync(Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception) when (_worker.IsCompletedSuccessfully) { /* Operation failures return to their individual callers. */ }
        finally { _engine.Dispose(); _store.ReleaseConnections(); }
    }
}
