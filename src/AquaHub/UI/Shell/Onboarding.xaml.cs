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
        HotkeyHint.Text = $"{_s.General.HotkeyFlyout} opens the quick panel · {_s.General.HotkeyPalette} asks Aqua anything.";
        Shortcuts.Text = $"{_s.General.HotkeyFlyout}  —  quick panel above the taskbar\n{_s.General.HotkeyPalette}  —  ask or command from anywhere\nCtrl + K  —  command palette inside the app\nCtrl + 1…9  —  jump between pages";
        CityChosen.Text = $"{_s.Location.City}, {_s.Location.Region}";
        Imperial.IsChecked = _s.Location.Units == "imperial";

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
    }

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
            AiBody.Text = $"Aqua Hub works without AI using fast extractive summaries. For AI briefs, install Ollama from ollama.com and run:  ollama pull {suggestion}";
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
        var previousCountry = _s.Location.Country;
        _s.Location.City = p.Name;
        _s.Location.Region = string.IsNullOrWhiteSpace(p.Country) ? p.Region : p.Country;
        _s.Location.Country = p.CountryCode.ToUpperInvariant();
        _s.Location.Latitude = p.Latitude;
        _s.Location.Longitude = p.Longitude;
        if (!string.IsNullOrEmpty(p.Timezone)) _s.Location.Timezone = p.Timezone;
        if (!_s.Location.LocalKeywords.Contains(p.Name)) _s.Location.LocalKeywords.Add(p.Name);
        var switched = LocalePacks.Apply(_s, previousCountry);
        CityChosen.Text = switched.Length > 0 ? $"{p}\n{switched}" : p.ToString();
        CityChosen.TextWrapping = TextWrapping.Wrap;
        CityPopup.IsOpen = false;
        City.Text = "";
    }

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
