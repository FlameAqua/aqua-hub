using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Search;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.Controls;
using AquaHub.UI.Shell;
using Microsoft.Win32;

namespace AquaHub.UI.Pages;

public partial class FileSearchPage : UserControl, IPage
{
    private FileSearchService _search = null!;
    private readonly Func<Task<FileSearchService>> _load;
    private CancellationTokenSource? _query;
    private bool _active, _binding;
    private long _revision = -1;
    private List<SearchRoot> _roots = [];
    private string? _selectedPath;
    private bool _managementBusy;
    private SearchRoot? _editing;
    private int _updatePosted;
    private int _navigation;
    private string? _folder;

    public FileSearchPage(Func<Task<FileSearchService>> load) { _load = load; InitializeComponent(); }

    public async void OnNavigatedTo(string? arg)
    {
        var navigation = ++_navigation;
        _active = true;
        _query?.Cancel();
        Results.ItemsSource = null;
        Commands.IsEnabled = false;
        try { _search = await _load(); }
        catch (Exception)
        {
            if (_active && navigation == _navigation) Notice.Show("Could not open the local search index. Check available space and profile access.", error: true, persistent: true,
                actionLabel: "Retry", action: () => OnNavigatedTo(arg));
            return;
        }
        if (!_active || navigation != _navigation) return;
        _binding = true;
        _folder = arg?.StartsWith("folder:", StringComparison.Ordinal) == true ? arg[7..] : null;
        if (arg is not null)
        {
            Scope.SelectedIndex = 0;
            Extension.Clear();
            Recent.IsChecked = false;
            Consent.Visibility = Visibility.Collapsed;
            Folders.Visibility = Visibility.Collapsed;
            if (arg.StartsWith("query:", StringComparison.Ordinal))
            {
                string query = arg[6..];
                Query.Text = query.Length <= Query.MaxLength ? query : query[..Query.MaxLength];
                Query.CaretIndex = Query.Text.Length;
            }
        }
        _binding = false;
        _revision = -1;
        Commands.IsEnabled = true;
        _search.Changed -= Changed;
        _search.Changed += Changed;
        Update();
        FocusWhenActive(Query);
    }
    public void OnNavigatedFrom()
    {
        _active = false;
        _navigation++;
        if (_search != null) _search.Changed -= Changed;
        _query?.Cancel();
        Results.ItemsSource = null;
        Consent.Visibility = Visibility.Collapsed;
        Notice.Hide();
        _revision = -1;
    }
    private void Changed()
    {
        if (Interlocked.Exchange(ref _updatePosted, 1) == 0)
            Dispatcher.InvokeAsync(() => { Interlocked.Exchange(ref _updatePosted, 0); if (_active) Update(); });
    }
    private async void Update()
    {
        var status = _search.Status;
        Progress.Text = status.Roots == 0 ? "Add a folder to start searching." : $"{status.Activity} · {status.Files:N0} {(status.Files == 1 ? "file" : "files")}";
        SemanticTools.Visibility = status.Roots == 0 ? Visibility.Collapsed : Visibility.Visible;
        Meaning.Text = status.Semantic;
        PauseIcon.Kind = status.Paused ? "play" : "pause";
        Pause.ToolTip = status.Paused ? "Resume indexing" : "Pause indexing";
        System.Windows.Automation.AutomationProperties.SetName(Pause, (string)Pause.ToolTip);
        FolderButton.Visibility = status.Roots == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_revision == _search.Revision) return;
        int navigation = _navigation;
        var revision = _revision = _search.Revision;
        Results.ItemsSource = null;
        try
        {
            var roots = await _search.RootsAsync();
            if (!_active || navigation != _navigation || revision != _revision) return;
            if (!_roots.SequenceEqual(roots) || Scope.Items.Count == 0)
            {
                _binding = true;
                var filter = (Scope.SelectedItem as SearchRoot)?.Id;
                var selected = (Managed.SelectedItem as SearchRoot)?.Id;
                _roots = roots;
                Managed.ItemsSource = roots;
                Managed.SelectedItem = roots.Find(r => r.Id == selected) ?? roots.FirstOrDefault();
                Scope.ItemsSource = new SearchRoot[] { new(0, "All folders", "", false) }.Concat(roots).ToList();
                Scope.SelectedItem = roots.Find(r => r.Id == filter) ?? Scope.Items[0];
                _binding = false;
            }
            UpdateFolderScope();
            RunQuery();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_active) Notice.Show("Could not read the local index.", error: true); }
    }

    private void OnFilter(object sender, RoutedEventArgs e) { if (_active && !_binding) RunQuery(); }
    private void OnQuery(object sender, TextChangedEventArgs e) { if (_active && !_binding) RunQuery(); }

    private bool FolderHasGrants => _roots.Any(new SearchFilter(Folder: _folder).Intersects);

    private void UpdateFolderScope()
    {
        FolderScope.Visibility = _folder is null ? Visibility.Collapsed : Visibility.Visible;
        Scope.IsEnabled = _folder is null;
        if (_folder is null) return;
        var name = System.IO.Path.GetFileName(_folder);
        ScopedPath.Text = string.IsNullOrEmpty(name) ? _folder : $"In {name}";
        ScopedPath.ToolTip = _folder;
        System.Windows.Automation.AutomationProperties.SetHelpText(ScopedPath, _folder);
        ReviewFolder.Visibility = FolderHasGrants || _folder.StartsWith(@"\\", StringComparison.Ordinal) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnClearFolderScope(object sender, RoutedEventArgs e)
    {
        _folder = null;
        Consent.Visibility = Visibility.Collapsed;
        UpdateFolderScope();
        RunQuery();
        FocusWhenActive(Query);
    }

    private void OnReviewFolder(object sender, RoutedEventArgs e)
    {
        if (_folder is { } folder && !FolderHasGrants) ShowConsent(folder, null);
    }

    private async void RunQuery()
    {
        if (_search == null) return;
        _query?.Cancel();
        using var request = new CancellationTokenSource();
        _query = request;
        var query = Query.Text.Trim();
        var revision = _search.Revision;
        var selected = _selectedPath;
        Results.ItemsSource = null;
        Summary.Text = query.Length == 0 ? "" : "Searching…";
        try
        {
            if (_folder != null && !FolderHasGrants)
            {
                Summary.Text = _folder.StartsWith(@"\\", StringComparison.Ordinal) ? "File Search uses local folders" : "Folder needs Search approval";
                return;
            }
            if (query.Length == 0 || _roots.Count == 0) { Summary.Text = ""; return; }
            var ext = Extension.Text.Trim();
            var root = _folder is null ? (Scope.SelectedItem as SearchRoot)?.Id : null;
            var filter = new SearchFilter(root is > 0 ? root : null, ext.Length == 0 ? "" : "." + ext.TrimStart('.'),
                Recent.IsChecked == true ? DateTime.UtcNow.AddDays(-30) : null, _folder);
            await Task.Delay(180, request.Token);
            foreach (bool meaning in new[] { false, true })
            {
                if (meaning && (!_search.MeaningAvailable || !_roots.Any(r => r.Content))) break;
                var hits = await _search.FindAsync(query, filter, meaning, request.Token);
                if (request.IsCancellationRequested || !_active || revision != _search.Revision) return;
                selected = (Results.SelectedItem as SearchHit)?.Path ?? selected;
                bool keepFocus = Results.IsKeyboardFocusWithin;
                Results.ItemsSource = hits;
                Results.SelectedItem = hits.Find(h => h.Path == selected);
                if (keepFocus && Results.SelectedItem is SearchHit current)
                {
                    Results.ScrollIntoView(current);
                    Results.UpdateLayout();
                    FocusWhenActive(Results.ItemContainerGenerator.ContainerFromItem(current) as ListBoxItem);
                }
                Summary.Text = hits.Count switch { 0 => "No matches", 1 => "1 matching file", 50 => "Top 50 matching files", _ => $"{hits.Count} matching files" };
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_active && !request.IsCancellationRequested) { Summary.Text = "Search unavailable"; Notice.Show("Could not query the index.", error: true); } }
        finally { if (_query == request) _query = null; }
    }

    private void OnQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || Results.Items.Count == 0) return;
        Results.SelectedIndex = 0;
        FocusWhenActive(Results.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem);
        e.Handled = true;
    }
    private void FocusWhenActive(UIElement? control)
    {
        if (_active && Window.GetWindow(this)?.IsActive == true) control?.Focus();
    }
    private void OnResultKey(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { Act(!Open.IsEnabled); e.Handled = true; } }
    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (Open == null) return;
        Open.IsEnabled = Results.SelectedItem is SearchHit hit && AnswerText.OpensWithItsApp(hit.Path);
        if (Results.SelectedItem is SearchHit selected) _selectedPath = selected.Path;
        Reveal.IsEnabled = Results.SelectedItem != null;
        ResultActions.Visibility = Reveal.IsEnabled ? Visibility.Visible : Visibility.Hidden;
    }
    private void OnOpen(object sender, RoutedEventArgs e) => Act(false);
    private void OnReveal(object sender, RoutedEventArgs e) => Act(true);
    private async void Act(bool reveal)
    {
        if (Results.SelectedItem is not SearchHit hit) return;
        try { await _search.ActAsync(hit, p => MarkdownView.OpenFileChecked(p, reveal)); }
        catch (System.ComponentModel.Win32Exception) { if (_active) Notice.Show("Windows could not open this file. Try Show in folder.", error: true); }
        catch (Exception) { if (_active) { RunQuery(); Notice.Show("File unavailable or folder permission changed.", error: true); } }
    }
    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a folder for Search", AddToRecent = false };
        if (Sandbox.Enabled) picker.InitialDirectory = Hub.Core.Paths.Root;
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        ShowConsent(picker.FolderName, null);
    }
    private void ShowConsent(string path, SearchRoot? root)
    {
        _editing = root;
        ChosenPath.Text = path;
        IncludeContent.IsChecked = root != null;
        Consent.Visibility = Visibility.Visible;
        Folders.Visibility = Visibility.Collapsed;
        FocusWhenActive(IncludeContent);
    }
    private void OnCancel(object sender, RoutedEventArgs e) { Consent.Visibility = Visibility.Collapsed; FocusWhenActive(Query); }
    private async void OnApprove(object sender, RoutedEventArgs e)
    {
        var content = IncludeContent.IsChecked == true;
        var path = ChosenPath.Text;
        var root = _editing;
        await Manage(async () =>
        {
            if (root == null) await _search.AddAsync(path, content);
            else await _search.ContentAsync(root, content);
            Consent.Visibility = Visibility.Collapsed;
            FocusWhenActive(Query);
        });
    }
    private void OnFolders(object sender, RoutedEventArgs e) { Consent.Visibility = Visibility.Collapsed; Folders.Visibility = Folders.IsVisible ? Visibility.Collapsed : Visibility.Visible; }
    private void OnFilters(object sender, RoutedEventArgs e) { Filters.Visibility = Filters.IsVisible ? Visibility.Collapsed : Visibility.Visible; }
    private void OnManaged(object sender, SelectionChangedEventArgs e)
    { if (Mode != null) Mode.Content = Managed.SelectedItem is SearchRoot { Content: true } ? "Turn off contents" : "Include contents…"; }
    private async void OnMode(object sender, RoutedEventArgs e)
    {
        if (Managed.SelectedItem is not SearchRoot root) return;
        if (root.Content) await Manage(() => _search.ContentAsync(root, false));
        else ShowConsent(root.Path, root);
    }
    private async void OnRemove(object sender, RoutedEventArgs e)
    { if (Managed.SelectedItem is SearchRoot root) await Manage(() => _search.RemoveAsync(root.Id)); }
    private async void OnPause(object sender, RoutedEventArgs e) => await Manage(() => _search.PauseAsync(!_search.Status.Paused));
    private async Task Manage(Func<Task> action)
    {
        if (_managementBusy) return;
        _managementBusy = true;
        try { await action(); }
        catch (Exception ex) { if (_active) Notice.Show(ex is InvalidOperationException or UnauthorizedAccessException ? ex.Message : "Could not update Search. Check folder access and available disk space.", error: true); }
        finally { _managementBusy = false; }
    }
    private void OnModel(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("models", "search");
    private void OnRuntime(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("settings", "ai:runtime");
}
