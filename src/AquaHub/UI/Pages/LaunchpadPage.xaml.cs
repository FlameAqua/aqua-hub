using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

/// <summary>Launchpad reuses the Today view model for media and adds the full app & scene lists.</summary>
public sealed class LaunchpadVM : ObservableObject
{
    public TodayVM Today { get; } = new();
    public List<AppVM> Apps { get; set; } = new();
    public List<SceneVM> Scenes { get; set; } = new();
    public void Changed() => RaiseAll();
}

public partial class LaunchpadPage : UserControl, IPage
{
    private readonly LaunchpadVM _vm = new();
    private List<CatalogApp> _catalog = new();

    public LaunchpadPage()
    {
        InitializeComponent();
        DataContext = _vm;
        SearchKeys.Attach(PickerSearch, PickerList, () => OnPickApp(PickerList, null!), () => Picker.Visibility = Visibility.Collapsed);
    }

    public void OnNavigatedTo(string? arg)
    {
        Subtitle.Text = Hub.S.General.HotkeyPalette is { Length: > 0 } hotkey
            ? $"Your apps, music and scenes. Also in the command palette ({hotkey})."
            : "Your apps, music and scenes. Also in the command palette.";
        _vm.Today.Attach();
        Hub.Core.Settings.Changed -= OnSettingsChanged;
        Hub.Core.Settings.Changed += OnSettingsChanged;
        Refresh();
    }

    private void OnEditApps(object sender, RoutedEventArgs e)
    {
        var editing = EditApps.IsChecked == true;
        EditAppsText.Text = editing ? "Done" : "Edit";
        foreach (var a in _vm.Apps) a.IsEditing = editing;
    }

    public void OnNavigatedFrom()
    {
        EditApps.IsChecked = false;
        _vm.Today.Detach();
        Hub.Core.Settings.Changed -= OnSettingsChanged;
    }

    /// <summary>Apps added, removed (or restored with Undo) and scenes edited elsewhere show up at once.</summary>
    private void OnSettingsChanged(Core.Settings.HubSettings _) => Hub.OnUi(Refresh);

    private void Refresh()
    {
        _vm.Apps = Hub.S.Apps.Select(a => new AppVM(a)).ToList();
        foreach (var a in _vm.Apps)
        {
            a.IsRunning = Hub.Launcher.IsRunning(a.Entry);
            a.IsEditing = EditApps.IsChecked == true;
        }
        _vm.Scenes = Hub.S.Scenes.Select(s => new SceneVM(s)).ToList();
        _vm.Changed();
    }

    private void OnMusicKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnMusicSearch(sender, e);
    }

    private void OnMusicSearch(object sender, RoutedEventArgs e)
    {
        var q = MusicQuery.Text.Trim();
        if (q.Length == 0) return;
        Hub.Launcher.MusicSearch(q, Hub.S.Apps);
        MusicQuery.Text = "";
    }

    private async void OnAddApp(object sender, RoutedEventArgs e)
    {
        Picker.Visibility = Visibility.Visible;
        _catalog = await Hub.Catalog.GetAppsAsync();
        FilterPicker();
        PickerSearch.Focus();
    }

    private void OnClosePicker(object sender, RoutedEventArgs e) => Picker.Visibility = Visibility.Collapsed;

    private void OnPickerSearch(object sender, TextChangedEventArgs e) => FilterPicker();

    private void FilterPicker()
    {
        var q = PickerSearch.Text.Trim();
        var existing = Hub.S.Apps.Select(a => a.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PickerList.ItemsSource = _catalog.Where(a => !existing.Contains(a.Target) && (q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).Take(60).ToList();
    }

    private void OnPickApp(object sender, MouseButtonEventArgs e)
    {
        if (PickerList.SelectedItem is not CatalogApp app) return;
        var id = new string(app.Name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (id.Length == 0) id = "app" + Hash.Short(app.Target)[..6];
        Hub.Core.Settings.Update(s =>
        {
            if (s.Apps.Any(a => a.Id == id)) id += s.Apps.Count;
            s.Apps.Add(new AppEntry
            {
                Id = id, Name = app.Name, Kind = app.Kind == "uwp" ? "uwp" : "shortcut", Target = app.Target,
                ProcessName = app.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(app.Target) : app.Name.Split(' ')[0],
                Pinned = s.Apps.Count(a => a.Pinned) < 8,
                IsMusicPlayer = app.Name.Contains("Spotify", StringComparison.OrdinalIgnoreCase) || app.Name.Contains("Music", StringComparison.OrdinalIgnoreCase),
            });
        });
        Picker.Visibility = Visibility.Collapsed;
        Refresh();
    }
}
