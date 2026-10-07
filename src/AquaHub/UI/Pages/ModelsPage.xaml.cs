using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AquaHub.Core.Ai.Local;
using AquaHub.Core.Settings;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public partial class ModelsPage : UserControl, IPage
{
    private bool _syncing, _active, _downloadAction, _cancelRequested;
    private CancellationTokenSource? _browse;
    private HfRepoFiles? _repo;
    private HfFile? _projector;
    private readonly Dictionary<string, ModelRow> _rows = new();
    private readonly Dictionary<string, SuggestionRow> _suggestions = new();
    private readonly ObservableCollection<ModelRow> _modelItems = new();
    private readonly ObservableCollection<SuggestionRow> _suggestionItems = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Stopwatch _rateClock = new();
    private long _rateBytes;
    private string _rateStep = "";

    public ModelsPage()
    {
        InitializeComponent();
        Library.ItemsSource = _modelItems;
        Suggestions.ItemsSource = _suggestionItems;
        _timer.Tick += (_, _) => UpdateHardware();
    }

    public void OnNavigatedTo(string? arg)
    {
        if (!_active)
        {
            _active = true;
            Hub.LocalAi.Changed += Refresh;
            Hub.LocalAi.DownloadProgressChanged += OnProgress;
            Hub.Core.Settings.Changed += OnSettingsChanged;
        }
        Refresh();
        OnProgress(Hub.LocalAi.DownloadProgress);
        _timer.Start();
    }

    public void OnNavigatedFrom()
    {
        _active = false;
        Hub.LocalAi.Changed -= Refresh;
        Hub.LocalAi.DownloadProgressChanged -= OnProgress;
        Hub.Core.Settings.Changed -= OnSettingsChanged;
        _timer.Stop();
        _browse?.Cancel();
    }

    private void OnSettingsChanged(HubSettings _) => Hub.OnUi(() => { if (_active) Refresh(); });
    private void OnSettings(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings", "ai");

    private void UpdateHardware()
    {
        var ai = Hub.LocalAi;
        HardwareText.Text = ai.Gpu.Present ? $"{ai.Gpu.Label} · {Fmt.Bytes(ai.Gpu.FreeBytes)} free now"
            : "No graphics card Aqua can use: models run on the processor, slowly.";
        ProviderText.Text = ai.InUse ? $"Aqua's runtime · {(ai.RuntimeReady ? "ready to load a model when you ask" : "install the runtime in AI settings")}. Fit estimates reserve room for the desktop."
            : "Choose Aqua's own runtime in AI settings to use this library. You can prepare your models here first.";
    }

    private void Refresh()
    {
        _syncing = true;
        try
        {
            var ai = Hub.LocalAi;
            UpdateHardware();
            var models = ai.Models;
            // Reuse row objects so async errors and pending actions survive runtime/library notifications.
            foreach (var id in _rows.Keys.Except(models.Select(m => m.Id)).ToList()) _rows.Remove(id);
            foreach (var model in models)
            {
                if (!_rows.TryGetValue(model.Id, out var row)) _rows[model.Id] = row = new ModelRow(model);
                row.Update(model);
            }
            Synchronize(_modelItems, models.Select(m => _rows[m.Id]).ToList());
            EmptyLibrary.Visibility = models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LibraryCount.Text = $"{models.Count} available · {ai.ModelsFolder}";
            Roles.ItemsSource = new[]
            {
                MakeRole("main", "Main", "Ask Aqua and briefs", models),
                MakeRole("creative", "Creative", "Roleplay, on the GPU · defaults to Main", models),
                MakeRole("background", "Editor", "News and social analysis on the GPU · chat goes first", models),
                MakeRole("search", "Search", "Search by meaning · available in Phase 8", models),
            };
            Problems.Visibility = ai.Problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Problems.Header = $"{ai.Problems.Count} files couldn't be read";
            ProblemText.Text = string.Join(Environment.NewLine, ai.Problems);
            foreach (var r in ai.Recommended)
                if (!_suggestions.ContainsKey(r.Id)) _suggestions[r.Id] = new SuggestionRow(r);
            Synchronize(_suggestionItems, ai.Recommended.Select(r => _suggestions[r.Id]).ToList());
            UpdateDownloadButtons();
        }
        finally { _syncing = false; }
    }

    // Replacing an ItemsSource with the same row objects makes WPF reuse stale automation peers after navigation.
    // Keep the collection and containers alive; also preserves keyboard focus during runtime notifications.
    private static void Synchronize<T>(ObservableCollection<T> current, IReadOnlyList<T> wanted)
    {
        foreach (var old in current.Except(wanted).ToList()) current.Remove(old);
        for (var i = 0; i < wanted.Count; i++)
        {
            var index = current.IndexOf(wanted[i]);
            if (index < 0) current.Insert(i, wanted[i]);
            else if (index != i) current.Move(index, i);
        }
    }

    public sealed record Choice(string Id, string Label)
    {
        public override string ToString() => Label;
    }
    public sealed record RoleRow(string Id, string Title, string Hint, IReadOnlyList<Choice> Choices, string SelectedId)
    {
        public string AutomationId => "models-role-" + Id;
        public override string ToString() => Title;
    }

    private static RoleRow MakeRole(string role, string title, string hint, IReadOnlyList<LocalModel> models)
    {
        var list = models.Where(m => role == "search" ? m.Embedding : !m.Embedding)
            .Select(m => new Choice(m.Id, m.Name)).Prepend(new Choice("", role == "main" ? "Automatic" : role == "search" ? "None" : "Use Main (recommended)")).ToList();
        var raw = role switch { "main" => Hub.S.Ai.Model, "creative" => Hub.S.Ai.Llama.CreativeModel,
            "background" => Hub.S.Ai.Llama.BackgroundModel, _ => Hub.S.Ai.Llama.SearchModel };
        var id = Hub.LocalAi.ForRole(role)?.Id ?? (raw is "" or "auto" ? "" : raw);
        if (id.Length > 0 && list.All(c => c.Id != id)) list.Add(new Choice(id, raw + " (unavailable for this role)"));
        return new(role, title, hint, list, id);
    }

    private void OnRoleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ComboBox { DataContext: RoleRow role, SelectedItem: Choice choice } || choice.Id == role.SelectedId) return;
        var model = Hub.LocalAi.Models.FirstOrDefault(m => m.Id == choice.Id);
        if (choice.Id.Length > 0 && model is null) return;
        Hub.LocalAi.SetRole(role.Id, model);
    }

    public abstract class ActionRow : ObservableObject
    {
        private string _error = "";
        private bool _available = true;
        public string Error { get => _error; set => Set(ref _error, value); }
        public bool Available { get => _available; set => Set(ref _available, value); }
        public void Notify() => RaiseAll();
    }

    public sealed class ModelRow(LocalModel model) : ActionRow
    {
        public override string ToString() => Model.Name;
        public LocalModel Model { get; private set; } = model;
        public string Detail => $"{Model.Quant ?? "GGUF"} · {Fmt.Bytes(Model.Bytes)}";
        public string[] Capabilities => new[] { Model.Vision ? "Vision" : null, Model.Thinking ? "Thinking" : null,
            Model.Tools ? "Tools" : null, Model.Mtp ? "MTP" : null, Model.Embedding ? "Embedding" : null }.OfType<string>().ToArray();
        public FitEstimate Estimate => Hub.LocalAi.Fit(Model);
        public string Fit => FitText(Estimate);
        public Brush FitBrush => Fmt.Res(Estimate.Verdict is FitVerdict.Fits ? "B.Up" : "B.Warn");
        public string FitDetail => $"Estimate: {Fmt.Bytes(Estimate.TotalBytes)} at {Estimate.ContextTokens:N0} tokens. Actual memory use is checked when loading.";
        public string State => string.Join(" · ", Hub.LocalAi.Running.Where(r => r.ModelId == Model.Id && r.State != LlamaInstanceState.Stopped).Select(r =>
            r.State == LlamaInstanceState.Failed ? "Failed: " + r.Error : r.State == LlamaInstanceState.Starting ? "Starting…"
            : r.Lane == LlamaLane.Cpu ? "On the processor" : $"On the graphics card · {r.ContextTokens / 1024}K" + (r.PartlyOnCpu ? $" · {r.GpuLayers}/{r.TotalLayers} layers on the card" : "")));
        public string Speed
        {
            get
            {
                var (with, without) = Hub.LocalAi.Speed.Measured(Model.Id);
                return string.Join(" · ", new[] { with > 0 ? $"{with:0.0} tokens/s with MTP" : null, without > 0 ? $"{without:0.0} tokens/s without MTP" : null }.OfType<string>());
            }
        }
        public bool CanChat => !Model.Embedding;
        public bool CanStop => Hub.LocalAi.Running.Any(r => r.ModelId == Model.Id && r.State is LlamaInstanceState.Ready or LlamaInstanceState.Starting);
        public bool Failed => Hub.LocalAi.Running.Any(r => r.ModelId == Model.Id && r.State == LlamaInstanceState.Failed);
        public bool Removable => Model.Source == ModelSource.Aqua;
        public bool ManagedByOllama => !Removable;
        public void Update(LocalModel value) { Model = value; Notify(); }
    }

    private static string FitText(FitEstimate fit) => fit.Verdict switch
    {
        FitVerdict.Fits => $"Fits · {fit.ContextTokens / 1024}K context",
        FitVerdict.Tight => "Fits, little room left for games",
        FitVerdict.PartlyOnCpu => "Partly on the processor (slower)",
        FitVerdict.CpuOnly => "Processor only", _ => "Too big for this PC",
    };

    private void OnUse(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelRow row }) Hub.LocalAi.SetRole("main", row.Model);
    }
    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelRow row }) await Act(row, () => Hub.LocalAi.StopAsync(row.Model.Id));
    }
    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModelRow row }) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete {row.Model.Name} ({Fmt.Bytes(row.Model.Bytes)})? It can be downloaded again.",
            "Remove model", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Act(row, async () =>
        {
            if (!await Hub.LocalAi.RemoveModelAsync(row.Model) && !Sandbox.Enabled) throw new InvalidOperationException("The model couldn't be removed. Refresh the library and try again.");
        });
    }
    private static async Task Act(ActionRow row, Func<Task> action)
    {
        if (!row.Available) return;
        row.Available = false; row.Error = "";
        try { await action(); }
        catch (Exception ex) { row.Error = ex.Message; }
        finally { row.Available = true; }
    }
    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false; LibraryError.Text = "";
        try { await Hub.LocalAi.RefreshAsync(); }
        catch (Exception ex) { LibraryError.Text = ex.Message; }
        finally { RefreshButton.IsEnabled = true; }
    }

    public sealed class SuggestionRow(ModelRecommendation recommendation) : ActionRow
    {
        public override string ToString() => Recommendation.Name;
        public ModelRecommendation Recommendation { get; } = recommendation;
        public string Size => Fmt.Bytes(Recommendation.Bytes);
        public bool CanDownload { get; set; }
        public string ButtonText => Hub.LocalAi.Have(Recommendation) ? "Downloaded" : "Download";
    }
    public sealed class FileRow(HfFile file, string detail) : ActionRow
    {
        public override string ToString() => File.Name;
        public HfFile File { get; } = file;
        public string Detail { get; } = detail;
        public bool CanDownload { get; set; }
    }
    public sealed record RepoRow(string Id, string Detail)
    {
        public override string ToString() => Id;
    }

    private void UpdateDownloadButtons()
    {
        var busy = _downloadAction || Hub.LocalAi.Downloading;
        foreach (var row in _suggestions.Values) { row.CanDownload = !busy && !Hub.LocalAi.Have(row.Recommendation); row.Notify(); }
        foreach (var row in Files.Items.OfType<FileRow>()) { row.CanDownload = !busy && _repo?.Gated == false; row.Notify(); }
        DiscardButton.IsEnabled = !busy;
        CancelDownload.IsEnabled = Hub.LocalAi.Downloading;
    }
    private async Task Download(ActionRow row, Func<Task> action)
    {
        if (_downloadAction || Hub.LocalAi.Downloading) return;
        _downloadAction = true; row.Error = "";
        _cancelRequested = false;
        _rateStep = ""; _rateClock.Reset();
        UpdateDownloadButtons();
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadStep.Text = "Preparing download…";
        try
        {
            await action();
            DownloadStep.Text = Sandbox.Enabled ? "Download recorded in the test journal." : "Downloaded and verified. Ready in your library.";
        }
        catch (OperationCanceledException) { DownloadStep.Text = "Stopped. It carries on where it left off next time."; }
        catch (Exception ex)
        {
            // The backend wraps a CancelDownload request in a DownloadException for its other callers.
            if (_cancelRequested) DownloadStep.Text = "Stopped. It carries on where it left off next time.";
            else { row.Error = ex.Message; DownloadStep.Text = ex.Message; }
        }
        finally { _downloadAction = false; Refresh(); DownloadProgress.IsIndeterminate = false; }
    }
    private async void OnSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SuggestionRow row }) await Download(row, () => Hub.LocalAi.DownloadAsync(row.Recommendation));
    }
    private async void OnFileDownload(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileRow row } || _repo is null || _repo.Gated) return;
        var repo = _repo; var projector = VisionOption.IsChecked == true ? _projector : null;
        await Download(row, () => Hub.LocalAi.DownloadAsync(repo, row.File, projector));
    }
    private void OnCancel(object sender, RoutedEventArgs e) { _cancelRequested = true; Hub.LocalAi.CancelDownload(); CancelDownload.IsEnabled = false; }
    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Discard all unfinished downloads? Their saved progress will be lost.", "Discard downloads",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { LibraryError.Text = Hub.LocalAi.DiscardUnfinished() ? "Unfinished downloads discarded." : "Wait for the current download to stop first."; }
        catch (Exception ex) { LibraryError.Text = ex.Message; }
    }
    private void OnProgress(ModelDownloadProgress? p)
    {
        if (!_downloadAction && !Hub.LocalAi.Downloading) { if (p is null) DownloadPanel.Visibility = Visibility.Collapsed; return; }
        DownloadPanel.Visibility = Visibility.Visible;
        if (p is null) return;
        if (_rateStep != p.Step || p.Done < _rateBytes) { _rateStep = p.Step; _rateBytes = p.Done; _rateClock.Restart(); }
        var speed = _rateClock.Elapsed.TotalSeconds >= 1 ? $" · {Fmt.Rate(Math.Max(0, p.Done - _rateBytes) / _rateClock.Elapsed.TotalSeconds)}" : "";
        DownloadStep.Text = p.Step;
        DownloadDetail.Text = $"{Fmt.Bytes(p.Done)} / {Fmt.Bytes(p.Total)}{speed}";
        DownloadProgress.IsIndeterminate = p.Total <= 0;
        DownloadProgress.Value = p.Total > 0 ? Math.Clamp(100d * p.Done / p.Total, 0, 100) : 0;
        CancelDownload.IsEnabled = Hub.LocalAi.Downloading;
    }

    private CancellationTokenSource BeginBrowse()
    {
        _browse?.Cancel(); _browse?.Dispose();
        return _browse = new CancellationTokenSource();
    }
    private void OnSearchKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; OnSearch(sender, e); } }
    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) { SearchStatus.Text = "Enter a model or publisher to search."; return; }
        var request = BeginBrowse();
        SearchStatus.Text = "Searching Hugging Face…"; Repos.ItemsSource = null; RepoPanel.Visibility = Visibility.Collapsed;
        try
        {
            var results = await Hub.LocalAi.SearchAsync(query, request.Token);
            if (request.IsCancellationRequested) return;
            Repos.ItemsSource = results.Select(r => new RepoRow(r.Id, $"{r.Downloads:N0} downloads · {r.Likes:N0} likes" + (r.Updated is { } t ? $" · updated {t:dd MMM yyyy}" : ""))).ToList();
            SearchStatus.Text = results.Count == 0 ? "No public GGUF repositories found. Try a different name." : $"{results.Count} repositories · choose one to see its files";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) SearchStatus.Text = ex.Message; }
    }
    private async void OnRepo(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RepoRow row }) return;
        var request = BeginBrowse(); _repo = null; _projector = null;
        RepoPanel.Visibility = Visibility.Visible; RepoTitle.Text = row.Id; RepoStatus.Text = "Reading files…";
        Files.ItemsSource = null; VisionOption.Visibility = Visibility.Collapsed;
        try
        {
            var repo = await Hub.LocalAi.FilesAsync(row.Id, request.Token);
            if (request.IsCancellationRequested) return;
            _repo = repo;
            _projector = repo.Projectors.Where(p => p.Quant is "F16" or "BF16" || p.Name.Contains("f16", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("bf16", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Size).FirstOrDefault()
                ?? repo.Projectors.OrderBy(p => p.Size).FirstOrDefault();
            RepoStatus.Text = repo.Gated ? "This model needs a Hugging Face account, which Aqua doesn't use." : repo.Models.Count == 0 ? "No supported single-file GGUF models in this repository. Split models aren't supported yet."
                : $"{repo.Models.Count} files · license: {repo.License ?? "not supplied"}. Fit is a weights-only estimate until the GGUF header is downloaded; context memory is not yet known.";
            VisionOption.Visibility = _projector is not null && !repo.Gated ? Visibility.Visible : Visibility.Collapsed;
            VisionOption.Content = _projector is null ? "" : $"Also download vision (+{Fmt.Bytes(_projector.Size)})";
            VisionOption.IsChecked = false;
            FillFiles();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!request.IsCancellationRequested) RepoStatus.Text = ex.Message; }
    }
    private void OnVisionChanged(object sender, RoutedEventArgs e) => FillFiles();
    private void FillFiles()
    {
        if (_repo is not { } repo) return;
        var projector = VisionOption.IsChecked == true ? _projector : null;
        Files.ItemsSource = repo.Models.Select(f =>
        {
            var model = new LocalModel { Id = "preview", Name = f.Name, Path = f.Name, Source = ModelSource.Aqua, Size = f.Size,
                ProjectorPath = projector?.Path, ProjectorSize = projector?.Size ?? 0,
                Info = new GgufInfo { ContextLength = repo.ContextLength, Architecture = repo.Architecture ?? "", Parameters = repo.Parameters } };
            var fit = Hub.LocalAi.Fit(model);
            var verdict = fit.Verdict == FitVerdict.Fits ? "Weights fit on the card" : FitText(fit);
            return new FileRow(f, $"{Fmt.Bytes(model.Bytes)} · {f.Quant ?? "GGUF"} · {verdict} (estimate)");
        }).ToList();
        UpdateDownloadButtons();
    }
}
