using AquaHub.Core;
using AquaHub.Core.Ai.Local;
using AquaHub.Core.Search;
using AquaHub.Core.Settings;
using AquaHub.Platform;

namespace AquaHub.Services;

public sealed class SearchSession
{
    private readonly HubCore _core;
    private volatile SearchEngine? _configuration;
    private readonly SearchEmbeddings _embeddings;
    private int _maintenance;
    private readonly object _lifetime = new();
    private Task<FileSearchService>? _index;
    private Task? _shutdown;

    public SearchSession(HubCore core, ILlamaProcessHost host)
    {
        _core = core;
        _embeddings = new SearchEmbeddings(() => Volatile.Read(ref _maintenance) == 0 ? _configuration : null, host, Native.ListenerPid);
        if (File.Exists(IndexPath)) _ = GetFilesAsync();
        core.Settings.Changed += SettingsChanged;
        core.Models.Changed += Configure;
        core.LlamaSetup.Changed += Configure;
        Configure();
    }

    private string IndexPath => Path.Combine(_core.Paths.Root, "search.sqlite");

    public Task<FileSearchService> GetFilesAsync()
    {
        lock (_lifetime)
        {
            if (_shutdown != null) throw new ObjectDisposedException(nameof(SearchSession));
            if (_index == null || _index.IsFaulted)
            {
                _index = Task.Run(() => new FileSearchService(new SearchStore(IndexPath), new WindowsSearchFiles(_core.Paths.Root), _embeddings));
                _ = _index.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            return _index;
        }
    }

    private void Refresh()
    {
        lock (_lifetime) { if (_index is { IsCompletedSuccessfully: true }) _index.Result.Refresh(); }
    }

    private void SettingsChanged(HubSettings _) => Configure();
    private void Configure()
    {
        var model = _core.Models.Find(_core.Settings.Current.Ai.Llama.SearchModel);
        var next = _core.Settings.Current.Ai.Enabled && model is { Source: ModelSource.Aqua, Embedding: true } &&
            model.Sha256 == SearchEmbeddings.Pin.File.Sha256 && _core.LlamaSetup.Installed() is { Build: LlamaCatalog.Build } install
            ? new SearchEngine(install.Server, model.Path) : null;
        if (_configuration == next) return;
        _configuration = next;
        Refresh();
    }

    public async Task<IDisposable> SuspendEngineAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _maintenance);
        var lease = new Maintenance(this);
        try { await _embeddings.StopProcessAsync(ct).ConfigureAwait(false); return lease; }
        catch { lease.Dispose(); throw; }
    }

    private sealed class Maintenance(SearchSession owner) : IDisposable
    {
        public void Dispose()
        {
            if (Interlocked.Decrement(ref owner._maintenance) == 0) owner.Refresh();
        }
    }

    public Task StopAsync()
    {
        _core.Settings.Changed -= SettingsChanged;
        _core.Models.Changed -= Configure;
        _core.LlamaSetup.Changed -= Configure;
        lock (_lifetime) return _shutdown ??= JoinAsync(_index);
    }

    private async Task JoinAsync(Task<FileSearchService>? init)
    {
        if (init != null)
        {
            FileSearchService? service = null;
            try { service = await init.ConfigureAwait(false); }
            catch (Exception) when (init.IsFaulted) { }
            if (service != null) { await service.StopAsync().ConfigureAwait(false); return; }
        }
        _embeddings.Dispose();
    }
}
