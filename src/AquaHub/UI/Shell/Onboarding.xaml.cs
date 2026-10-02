using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Shell;

/// <summary>First-run setup: five short steps, all skippable, with sensible local defaults.</summary>
public partial class Onboarding : UserControl
{
    private readonly HubSettings _s = Hub.Core.Settings.Clone();
    private readonly HubSettings _original;
    private int _step = 1;
    private const int Steps = 5;
    private CancellationTokenSource? _cts;

    private static readonly string[] SuggestedInterests =
    {
        "AI", "technology", "economy", "housing", "climate", "energy", "politics", "science", "health", "sport", "crypto", "startups", "space", "transport",
    };

    public Onboarding()
    {
        InitializeComponent();
        _original = SettingsStore.DeepCopy(_s);
        string flyout = _s.General.HotkeyFlyout, palette = _s.General.HotkeyPalette;
        var hints = new List<string>();
        if (flyout.Length > 0) hints.Add($"{flyout} opens the quick panel");
        if (palette.Length > 0) hints.Add($"{palette} asks Aqua anything");
        HotkeyHint.Text = hints.Count > 0 ? string.Join(" · ", hints) + "." : "";
        Shortcuts.Text = string.Join("\n", new[]
        {
            flyout.Length > 0 ? $"{flyout}  —  quick panel above the taskbar" : "",
            palette.Length > 0 ? $"{palette}  —  ask or command from anywhere" : "",
            "Ctrl + K  —  command palette inside the app",
            "Ctrl + 1…9  —  jump between pages",
        }.Where(t => t.Length > 0));
        CityChosen.Text = _s.Location.IsSet ? _s.Location.Label : NoPlace;
        Imperial.IsChecked = _s.Location.Units == "imperial";
        PlaceCredit.Text = WeatherSource.PlaceAttribution;

        foreach (var interest in SuggestedInterests)
        {
            var chip = new ToggleButton
            {
                Content = interest, Style = (Style)FindResource("Chip"),
                IsChecked = _s.News.Interests.Contains(interest, StringComparer.OrdinalIgnoreCase),
            };
            chip.Checked += (_, _) => { if (!_s.News.Interests.Contains(interest)) _s.News.Interests.Add(interest); };
            chip.Unchecked += (_, _) => _s.News.Interests.RemoveAll(i => i.Equals(interest, StringComparison.OrdinalIgnoreCase));
            InterestChips.Children.Add(chip);
        }
        Subs.Placeholder = "Add subreddit…";
        Subs.Normalize = s => s.Trim().TrimStart('/').Replace("r/", "", StringComparison.OrdinalIgnoreCase);
        Subs.SetItems(_s.Social.Subreddits);
        Subs.Changed += l => _s.Social.Subreddits = l.ToList();
        Watch.Placeholder = "Ticker, e.g. TSLA";
        Watch.Normalize = s => s.Trim().ToUpperInvariant();
        Watch.SetItems(_s.Markets.Watchlist.Select(w => w.Symbol));
        Watch.Changed += l => _s.Markets.Watchlist = l.Select(sym =>
            _s.Markets.Watchlist.FirstOrDefault(w => w.Symbol == sym) ?? new WatchSymbol { Symbol = sym, Name = sym }).ToList();
        SearchKeys.Attach(City, CityResults, () => OnPickCity(CityResults, null!), () => CityPopup.IsOpen = false);
        Render();
    }

    private void Render()
    {
        var panels = new[] { Step1, Step2, Step3, Step4, Step5 };
        for (var i = 0; i < panels.Length; i++) panels[i].Visibility = i + 1 == _step ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Hidden;
        NextButton.Content = _step switch { 1 => "Get started", Steps => "Finish", _ => "Next" };
        Dots.Children.Clear();
        for (var i = 1; i <= Steps; i++)
            Dots.Children.Add(new Rectangle
            {
                Width = i == _step ? 22 : 8, Height = 8, RadiusX = 4, RadiusY = 4, Margin = new Thickness(0, 0, 6, 0),
                Fill = Fmt.Res(i == _step ? "B.Accent" : "B.Track"),
            });
        if (_step == 4) _ = CheckAiAsync();
        // The place chosen in step 2 brings its country's communities; show them.
        if (_step == 3) Subs.SetItems(_s.Social.Subreddits);
    }

    private const string NoPlace = "No place yet. Until you choose one there's no weather or local news.";

    private async Task CheckAiAsync()
    {
        var h = await Hub.Core.Llm.CheckAsync();
        var gpu = Hub.State.System?.Gpu;
        var vram = gpu?.VramTotalGb ?? 0;
        if (h.Available)
        {
            AiTitle.Text = $"Ready — using {h.ActiveModel}";
            var fit = vram >= 10 ? $"A great fit for your {gpu!.Name} ({vram:0} GB)." : vram > 0 ? $"Runs on your {gpu!.Name}." : "";
            AiBody.Text = $"Found {h.Models.Count} installed model(s) via {(h.Provider == "ollama" ? "Ollama" : "your model server")}. {fit} You can switch models any time in Settings → AI.";
            AiBadge.Background = Fmt.Res("B.UpSoft");
            AiIcon.Kind = "check";
            AiIcon.Foreground = Fmt.Res("B.Up");
        }
        else
        {
            AiTitle.Text = "No local model found (that's OK)";
            var suggestion = vram >= 10 ? "qwen3.5:9b" : vram >= 6 ? "qwen3.5:4b" : "gemma3:4b";
            AiBody.Text = $"Aqua works without it, using simpler summaries. For AI briefs, install Ollama from ollama.com and run: ollama pull {suggestion}";
            AiBadge.Background = Fmt.Res("B.WarnSoft");
            AiIcon.Kind = "info";
            AiIcon.Foreground = Fmt.Res("B.Warn");
        }
    }

    private async void OnCityChanged(object sender, TextChangedEventArgs e)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var q = City.Text.Trim();
        if (q.Length < 2) { CityPopup.IsOpen = false; return; }
        try
        {
            await Task.Delay(350, cts.Token);
            var places = await WeatherSource.SearchPlacesAsync(Hub.Core.Http, q, cts.Token);
            if (cts.IsCancellationRequested) return;
            CityResults.ItemsSource = places;
            CityPopup.IsOpen = places.Count > 0;
        }
        catch (OperationCanceledException) { }
    }

    private void OnPickCity(object sender, MouseButtonEventArgs e)
    {
        if (CityResults.SelectedItem is not GeoPlace p) return;
        CityPopup.IsOpen = false;
        City.Text = "";
        Choose(p, located: false);
    }

    private void Choose(GeoPlace p, bool located)
    {
        var switched = LocalePacks.ChoosePlace(_s, p);
        // The country's units (°F in the US) unless you pick otherwise below.
        Imperial.IsChecked = _s.Location.Units == "imperial";
        Metric.IsChecked = Imperial.IsChecked != true;
        CityChosen.Text = switched.Length > 0 ? $"{p}\n{switched}" : p.ToString();
        LocationSettingsLink.Visibility = Visibility.Collapsed;
        PlaceCredit.Visibility = located ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnLocate(object sender, RoutedEventArgs e)
    {
        LocateButton.IsEnabled = false;
        LocationSettingsLink.Visibility = Visibility.Collapsed;
        CityChosen.Text = "Asking Windows where this PC is…";
        try
        {
            var outcome = await WindowsLocation.FindTownAsync(Hub.Core.Http, CancellationToken.None);
            if (outcome.Place is { } p)
            {
                Choose(p, located: true);
                return;
            }
            CityChosen.Text = outcome.Problem + (_s.Location.IsSet ? $"\nStill {_s.Location.Label}." : "");
            LocationSettingsLink.Visibility = outcome.LocationOff ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { LocateButton.IsEnabled = true; }
    }

    private void OnLocationSettings(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl(WindowsLocation.SettingsUri, allowAppProtocols: true);

    private void OnTaskbarSettings(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl("ms-settings:taskbar", allowAppProtocols: true);

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step > 1) _step--;
        Render();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step < Steps)
        {
            _step++;
            Render();
            return;
        }
        _s.Location.Units = Imperial.IsChecked == true ? "imperial" : "metric";
        _s.Ai.PauseWhenFullscreen = PauseGaming.IsChecked == true;
        _s.General.LaunchAtStartup = StartWithWindows.IsChecked == true;
        _s.OnboardingComplete = true;
        // Merge rather than replace: first-run app discovery may have finished while this overlay was open.
        Hub.Core.Settings.Merge(_original, _s);
        Hub.Core.Agents.RunAll();
        Hub.Windows.Main?.HideOverlay();
    }
}
