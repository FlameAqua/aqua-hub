using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Ai.Assistant;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Core.Speech;
using AquaHub.Core.Updates;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.Controls;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public sealed record SectionVM(string Id, string Name, string Icon)
{
    // Screen readers announce a list item by its ToString, so rows say what they show.
    public override string ToString() => Name;
}

public partial class SettingsPage : UserControl, IPage
{
    private HubSettings _s;
    private HubSettings _original;
    private readonly UiThrottle _save;
    private bool _loading, _saving;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cityCts;

    private static readonly SectionVM[] SectionDefs =
    {
        new("general", "General", "settings"), new("location", "Location & weather", "location"), new("news", "News", "news"),
        new("social", "Social", "social"), new("markets", "Markets", "markets"), new("agenda", "Agenda", "upcoming"),
        new("predictions", "Predictions", "target"), new("ai", "AI & models", "cube"), new("ask", "Ask Aqua", "ask"), new("apps", "Apps & scenes", "launchpad"),
        new("notifications", "Notifications", "bell"), new("privacy", "Privacy & data", "shield"), new("debug", "Debug", "bug"), new("about", "About", "info"),
    };

    private static readonly string[] StepActions = { "launch", "close", "focus", "media", "volume", "mute", "dnd", "ai", "open-url", "open-page", "wait", "music-search", "read-brief" };

    public SettingsPage()
    {
        InitializeComponent();
        _s = Hub.Core.Settings.Clone();
        _original = SettingsStore.DeepCopy(_s);
        _save = new UiThrottle(Save, 600);
        Sections.ItemsSource = SectionDefs;

        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnAnyChange));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnAnyChange));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => { if (e.OriginalSource != CitySearch) OnAnyChange(s, e); }));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) => { if (e.OriginalSource is ComboBox) OnAnyChange(s, e); }));

        SetupChips();
        OllamaIdle.ItemsSource = new[]
        {
            new IdleOption(0, "Never"), new IdleOption(5, "After 5 minutes"), new IdleOption(10, "After 10 minutes"),
            new IdleOption(15, "After 15 minutes"), new IdleOption(30, "After 30 minutes"), new IdleOption(60, "After 1 hour"),
            new IdleOption(180, "After 3 hours"),
        };
        SearchKeys.Attach(CitySearch, CityResults, () => OnPickCity(CityResults, null!), () => CityPopup.IsOpen = false);
        ResearchPagesCombo.ItemsSource = new[] { 3, 4, 6, 8, 10, 12 };
        PdfPagesCombo.ItemsSource = new[] { 5, 10, 15, 30, 60 };
        ChatRetentionCombo.ItemsSource = new[]
        {
            new RetentionOption(7, "After 7 days"), new RetentionOption(30, "After 30 days"), new RetentionOption(90, "After 90 days"),
            new RetentionOption(365, "After a year"), new RetentionOption(0, "Never"),
        };
        ContextWindowCombo.ItemsSource = new[]
        {
            new WindowOption(0, "Automatic"), new WindowOption(8192, "8K tokens"), new WindowOption(16384, "16K tokens"),
            new WindowOption(32768, "32K tokens"), new WindowOption(65536, "64K tokens"), new WindowOption(131072, "128K tokens"),
        };
        HistoryMessagesCombo.ItemsSource = new[] { 4, 6, 10, 16, 20, 30, 40 };
        WhisperModelCombo.ItemsSource = WhisperCatalog.Choices.Select(c => new WhisperOption(c.Id, c.Label(Hub.S.Location.Language))).ToList();
    }

    public sealed record RetentionOption(int Days, string Label);
    public sealed record WindowOption(int Tokens, string Label);

    private void OnOpenWorkbench(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("workbench");

    public void OnNavigatedTo(string? arg)
    {
        Load();
        // "social:youtube" opens a section and brings one of its rows into view.
        var parts = (arg ?? "").Split(':', 2);
        var section = SectionDefs.FirstOrDefault(s => s.Id == parts[0]) ?? (Sections.SelectedItem as SectionVM) ?? SectionDefs[0];
        Sections.SelectedItem = section;
        if (parts.Length == 2 && FindName("Row_" + parts[1]) is FrameworkElement row)
            Dispatcher.BeginInvoke(() => Reveal(row), System.Windows.Threading.DispatcherPriority.Loaded);
        if (!Hub.SnapshotMode && !Sandbox.Enabled) _ = BackfillChannelsAsync();
        Hub.Core.Settings.Changed -= OnSettingsChangedElsewhere;
        Hub.Core.Settings.Changed += OnSettingsChangedElsewhere;
        Hub.Ollama.Changed -= OnOllamaChanged;
        Hub.Ollama.Changed += OnOllamaChanged;
        Hub.Core.Whisper.Changed -= OnWhisperChanged;
        Hub.Core.Whisper.Changed += OnWhisperChanged;
        Hub.Core.Whisper.Progress -= OnWhisperProgress;
        Hub.Core.Whisper.Progress += OnWhisperProgress;
        Hub.Updates.Changed -= OnUpdatesChanged;
        Hub.Updates.Changed += OnUpdatesChanged;
        UpdateOllamaStatus();
    }

    /// <summary>Scrolls a setting to the top of the view, flashes it and focuses its first input.</summary>
    private void Reveal(FrameworkElement row)
    {
        var top = row.TransformToAncestor(Panels).Transform(new Point(0, 0)).Y;
        Scroller.ScrollToVerticalOffset(Math.Max(0, top - 12));
        var flash = new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(900)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
        row.BeginAnimation(OpacityProperty, flash);
        var input = FindFirst<TextBox>(row);
        input?.Focus();
    }

    private static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindFirst<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    public sealed record IdleOption(int Minutes, string Label);

    private void OnOllamaChanged() => Hub.OnUi(UpdateOllamaStatus);

    private void UpdateOllamaStatus()
    {
        var local = OllamaManager.IsLocalOllama;
        OllamaStatus.Text = Hub.Ollama.StatusText;
        OllamaToggle.Content = !OllamaManager.Installed ? "Get Ollama" : Hub.Ollama.Running == true ? "Turn off" : "Turn on";
        OllamaToggle.IsEnabled = local && !Hub.Ollama.Transitioning;
    }

    private async void OnOllamaToggle(object sender, RoutedEventArgs e)
    {
        if (!OllamaManager.Installed) { AppLauncher.OpenUrl("https://ollama.com/download"); return; }
        if (Hub.Ollama.Running == true) await Hub.Ollama.TurnOffAsync();
        else await Hub.Ollama.TurnOnAsync();
        UpdateOllamaStatus();
    }

    public void OnNavigatedFrom()
    {
        _voiceTest?.Dispose();
        _voiceTest = null;
        Hub.Core.Settings.Changed -= OnSettingsChangedElsewhere;
        Hub.Ollama.Changed -= OnOllamaChanged;
        Hub.Core.Whisper.Changed -= OnWhisperChanged;
        Hub.Core.Whisper.Progress -= OnWhisperProgress;
        Hub.Updates.Changed -= OnUpdatesChanged;
        _save.Stop();
        Save();
    }

    /// <summary>Settings changed outside this page (tray, palette, Markets…): keep the form in step with them.</summary>
    private void OnSettingsChangedElsewhere(HubSettings _) => Hub.OnUi(() =>
    {
        if (_saving || _loading) return;
        _save.Stop();
        Save();
        var offset = Scroller.VerticalOffset;
        Load();
        Scroller.ScrollToVerticalOffset(offset);
    });

    private void Load()
    {
        _loading = true;
        _s = Hub.Core.Settings.Clone();
        _original = SettingsStore.DeepCopy(_s);
        DataContext = _s;
        LocalKeywords.SetItems(_s.Location.LocalKeywords);
        Interests.SetItems(_s.News.Interests);
        Muted.SetItems(_s.News.Muted);
        Subreddits.SetItems(_s.Social.Subreddits);
        Hashtags.SetItems(_s.Social.MastodonHashtags);
        BskyAccounts.SetItems(_s.Social.BlueskyAccounts);
        BskyFeeds.SetItems(_s.Social.BlueskyFeeds);
        KalshiCats.SetItems(_s.Predictions.KalshiCategories);
        Indices.SetItems(_s.Markets.Indices.Select(i => i.Symbol));
        Macro.SetItems(_s.Markets.Macro.Select(i => i.Symbol));
        Holidays.SetItems(_s.Events.HolidayCountries);
        Currencies.SetItems(_s.Events.EconomicCurrencies);
        PmTags.SetItems(_s.Predictions.PolymarketTags);
        BriefTimes.SetItems(_s.Ai.BriefTimes);
        Keywords.SetItems(_s.Notifications.Keywords);
        AllowedApps.SetItems(_s.Ask.AllowedApps);
        RefreshLists();

        var accent = _s.General.Accent;
        AccentCombo.SelectedValue = accent is "aqua" or "system" ? accent : "custom";
        AccentHex.Visibility = accent.StartsWith('#') ? Visibility.Visible : Visibility.Collapsed;
        AccentHex.Text = accent.StartsWith('#') ? accent : "";
        CityCurrent.Text = CurrentPlace();
        FillModels();
        ShowHotkeyErrors();
        FinnhubKey.Password = Hub.Core.Secrets.Get(SecretKeys.FinnhubApiKey) ?? "";
        AiKey.Password = Hub.Core.Secrets.Get(SecretKeys.OpenAiCompatibleKey) ?? "";
        BraveKey.Password = Hub.Core.Secrets.Get(SecretKeys.BraveSearchKey) ?? "";
        UpdateSearchEngineRows();
        ShowConnections();
        _ = FillMicsAsync();
        UpdateWhisper();
        var (items, saved, cache, bytes) = Hub.Core.Db.Stats();
        DbRow.Description = $"{items:N0} items ({saved} saved), {cache} cached AI results · {Fmt.Bytes(bytes)} on disk";
        VersionText.Text = $"Version {Hub.Updates.CurrentVersion} · .NET {Environment.Version} · {Environment.OSVersion.VersionString}";
        UpdateUpdates();
        PathsText.Text = $"Profile: {Hub.Core.Paths.Root}";
        _loading = false;
    }

    private void RefreshLists()
    {
        SourcesList.ItemsSource = null;
        SourcesList.ItemsSource = _s.News.Sources;
        WatchList.ItemsSource = null;
        WatchList.ItemsSource = _s.Markets.Watchlist;
        CalendarList.ItemsSource = null;
        CalendarList.ItemsSource = _s.Events.Calendars;
        AppsList.ItemsSource = null;
        AppsList.ItemsSource = _s.Apps;
        ChannelList.ItemsSource = null;
        ChannelList.ItemsSource = _s.Social.YouTubeChannels;
        ExtraFeedList.ItemsSource = null;
        ExtraFeedList.ItemsSource = _s.Social.ExtraFeeds;
        AskFolderList.ItemsSource = _s.Ask.Folders.Select(f => new AskFolderVM(Pages.AskPage.FolderLabel(f),
            LocalFiles.ExpandFolders(new[] { f }, Hub.Ask.Platform.KnownFolder).FirstOrDefault() ?? f + " (not found)", f)).ToList();
        BuildScenes();
    }

    private void SetupChips()
    {
        void Bind(ChipEditor editor, Action<List<string>> apply, Func<string, string?>? normalize = null, string placeholder = "Add…")
        {
            editor.Placeholder = placeholder;
            if (normalize is not null) editor.Normalize = normalize;
            editor.Changed += list => { apply(list.ToList()); _save.Request(); };
        }
        Bind(LocalKeywords, l => _s.Location.LocalKeywords = l, placeholder: "Add a place or name…");
        Bind(Interests, l => _s.News.Interests = l, placeholder: "Add an interest…");
        Bind(Muted, l => _s.News.Muted = l, placeholder: "Mute a topic…");
        Bind(Subreddits, l => _s.Social.Subreddits = l, s => s.Trim().TrimStart('/').Replace("r/", "", StringComparison.OrdinalIgnoreCase), "Add subreddit…");
        Bind(Hashtags, l => _s.Social.MastodonHashtags = l, s => s.Trim().TrimStart('#'), "Add hashtag…");
        Bind(BskyAccounts, l => _s.Social.BlueskyAccounts = l, s => s.Trim().TrimStart('@'), "handle.bsky.social");
        Bind(BskyFeeds, l => _s.Social.BlueskyFeeds = l, NormalizeFeedLink, "bsky.app/profile/…/feed/… or at://…");
        Bind(KalshiCats, l => _s.Predictions.KalshiCategories = l, placeholder: "Economics…");
        Bind(Indices, l => _s.Markets.Indices = MergeSymbols(_s.Markets.Indices, l, "index"), s => s.Trim().ToUpperInvariant(), "^GDAXI…");
        Bind(Macro, l => _s.Markets.Macro = MergeSymbols(_s.Markets.Macro, l, "fx"), s => s.Trim().ToUpperInvariant(), "GBPEUR=X…");
        Bind(Holidays, l => _s.Events.HolidayCountries = l, s => s.Trim().ToUpperInvariant() is { Length: 2 } c ? c : null, "GB…");
        Bind(Currencies, l => _s.Events.EconomicCurrencies = l, s => s.Trim().ToUpperInvariant() is { Length: 3 } c ? c : null, "JPY…");
        Bind(PmTags, l => _s.Predictions.PolymarketTags = l, s => s.Trim().ToLowerInvariant(), "crypto…");
        Bind(BriefTimes, l => _s.Ai.BriefTimes = l, s => TimeOnly.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var t) ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : null, "12:30");
        Bind(Keywords, l => _s.Notifications.Keywords = l, placeholder: "Track a keyword…");
        Bind(AllowedApps, l => _s.Ask.AllowedApps = l, s => s.Trim() is { Length: > 0 } a ? (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a) : null, "notepad…");
    }

    private static List<WatchSymbol> MergeSymbols(List<WatchSymbol> existing, List<string> symbols, string kind) =>
        symbols.Select(sym => existing.FirstOrDefault(e => e.Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase))
                              ?? new WatchSymbol { Symbol = sym, Name = sym.TrimStart('^'), Kind = sym.EndsWith("=X") ? "fx" : sym.Contains('-') ? "crypto" : sym.EndsWith("=F") ? "commodity" : kind }).ToList();

    private void OnAnyChange(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _save.Request();
    }

    private void Save()
    {
        if (_loading) return;
        _saving = true;
        try
        {
            // Only what this form changed is written; everything else keeps its current value.
            if (!Hub.Core.Settings.Merge(_original, _s)) return;
            _original = SettingsStore.DeepCopy(_s);
        }
        finally { _saving = false; }
        SavedText.Text = "Saved " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        Dispatcher.BeginInvoke(ShowHotkeyErrors, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ShowHotkeyErrors()
    {
        HotkeyErrors.Text = string.Join("\n", App.HotkeyErrors);
        HotkeyErrors.Visibility = App.HotkeyErrors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSection(object sender, SelectionChangedEventArgs e)
    {
        if (Sections.SelectedItem is not SectionVM s) return;
        foreach (var panel in Panels.Children.OfType<StackPanel>())
            panel.Visibility = panel.Name == "P_" + s.Id ? Visibility.Visible : Visibility.Collapsed;
        if (s.Id == "debug") ShowProblems();
        Scroller.ScrollToTop();
    }

    // ───────────── General ─────────────
    private void OnAccentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AccentCombo.SelectedValue is not string v) return;
        AccentHex.Visibility = v == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (v != "custom") _s.General.Accent = v;
        else if (AccentHex.Text.StartsWith('#')) _s.General.Accent = AccentHex.Text.Trim();
    }

    private void OnAccentHex(object sender, RoutedEventArgs e)
    {
        var hex = AccentHex.Text.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
        {
            _s.General.Accent = hex;
            _save.Request();
        }
    }

    // ───────────── Location ─────────────
    private async void OnCitySearch(object sender, TextChangedEventArgs e)
    {
        _cityCts?.Cancel();
        var cts = _cityCts = new CancellationTokenSource();
        var q = CitySearch.Text.Trim();
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

    private string CurrentPlace() => _s.Location.IsSet
        ? string.Create(CultureInfo.InvariantCulture, $"Currently {_s.Location.Label} ({_s.Location.Latitude:0.###}, {_s.Location.Longitude:0.###})")
        : "Not set yet: there's no weather or local news until you choose a place.";

    private void OnPickCity(object sender, MouseButtonEventArgs e)
    {
        if (CityResults.SelectedItem is not GeoPlace p) return;
        CityPopup.IsOpen = false;
        CitySearch.Text = "";
        ChoosePlace(p, located: false);
    }

    private async void OnLocate(object sender, RoutedEventArgs e)
    {
        LocateButton.IsEnabled = false;
        LocationSettingsLink.Visibility = Visibility.Collapsed;
        CityCurrent.Text = "Asking Windows where this PC is…";
        try
        {
            var outcome = await WindowsLocation.FindTownAsync(Hub.Core.Http, CancellationToken.None);
            if (outcome.Place is { } p)
            {
                ChoosePlace(p, located: true);
                return;
            }
            CityCurrent.Text = outcome.Problem + (_s.Location.IsSet ? " " + CurrentPlace() + "." : "");
            LocationSettingsLink.Visibility = outcome.LocationOff ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { LocateButton.IsEnabled = true; }
    }

    private void OnLocationSettings(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl(WindowsLocation.SettingsUri, allowAppProtocols: true);

    private void ChoosePlace(GeoPlace p, bool located)
    {
        var switched = LocalePacks.ChoosePlace(_s, p);
        Save();
        // Local keywords always change; with a new country, local sources, subreddits, holidays and the index too.
        var offset = Scroller.VerticalOffset;
        Load();
        Scroller.ScrollToVerticalOffset(offset);
        LocationSettingsLink.Visibility = Visibility.Collapsed;
        CityCurrent.Text = $"Currently {p}." + (switched.Length > 0 ? " " + switched : "") + (located ? " " + WeatherSource.PlaceAttribution + "." : "");
        Hub.Core.Agents.RunNow("weather");
        Hub.Core.Agents.RunNow("news-scout");
        Hub.Core.Agents.RunNow("social-scout");
        Hub.Core.Agents.RunNow("market-watch");
    }

    // ───────────── News sources ─────────────
    private void OnAddSource(object sender, RoutedEventArgs e)
    {
        var url = NewSourceUrl.Text.Trim();
        var name = NewSourceName.Text.Trim();
        if (!SettingsStore.IsSafeHttpUrl(url)) { SourceError.Text = "Please enter an https:// feed URL."; return; }
        if (name.Length == 0) name = new Uri(url).Host.Replace("www.", "");
        var id = "custom-" + Hash.Short(url)[..8];
        if (_s.News.Sources.Any(s => s.Url == url)) { SourceError.Text = "That feed is already added."; return; }
        var category = (NewSourceCat.SelectedItem as ComboBoxItem)?.Content as string ?? "world";
        _s.News.Sources.Add(new NewsSource { Id = id, Name = name, Url = url, Category = category, Tier = 2, Local = category == "local" });
        SourceError.Text = "";
        NewSourceUrl.Text = NewSourceName.Text = "";
        RefreshLists();
        Save();
        Hub.Core.Agents.RunNow("news-scout");
    }

    private void OnRemoveSource(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        _s.News.Sources.RemoveAll(s => s.Id == id);
        RefreshLists();
        Save();
    }

    // ───────────── Markets ─────────────
    private void OnRemoveWatch(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string sym }) return;
        _s.Markets.Watchlist.RemoveAll(w => w.Symbol == sym);
        RefreshLists();
        Save();
    }

    // ───────────── Agenda ─────────────
    private void OnBrowseIcs(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Calendar files (*.ics)|*.ics", Title = "Choose a calendar file" };
        if (dlg.ShowDialog() == true)
        {
            NewCalUrl.Text = dlg.FileName;
            if (NewCalName.Text.Length == 0) NewCalName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void OnAddCalendar(object sender, RoutedEventArgs e)
    {
        var url = NewCalUrl.Text.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        var isFile = Path.IsPathFullyQualified(url) && url.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) && File.Exists(url);
        if (!isFile && !SettingsStore.IsSafeHttpUrl(url)) { CalError.Text = "Enter an https:// ICS link or pick a .ics file."; return; }
        var palette = new[] { "#4CC2FF", "#FF8FB1", "#8BD17C", "#F5B84B", "#B892FF" };
        _s.Events.Calendars.Add(new CalendarSource
        {
            Name = NewCalName.Text.Trim() is { Length: > 0 } n ? n : "Calendar",
            Url = url,
            Color = palette[_s.Events.Calendars.Count % palette.Length],
        });
        CalError.Text = "";
        NewCalName.Text = NewCalUrl.Text = "";
        RefreshLists();
        Save();
        Hub.Core.Agents.RunNow("events-scout");
    }

    private void OnRemoveCalendar(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;
        _s.Events.Calendars.RemoveAll(c => c.Url == url);
        RefreshLists();
        Save();
    }

    // ───────────── Social: your own timelines, channels and feeds ─────────────
    private void ShowConnections()
    {
        var bsky = Hub.Core.Secrets.Get(SecretKeys.BlueskyAppPassword) is { Length: > 0 };
        BskyStatus.Text = bsky && _s.Social.BlueskyHandle.Length > 0
            ? $"Connected as @{_s.Social.BlueskyHandle} · your Following timeline feeds the pulse"
            : "Not connected — only public posts are read.";
        var masto = Hub.Core.Secrets.Get(SecretKeys.MastodonToken) is { Length: > 0 };
        MastodonStatus.Text = masto ? $"Connected to {_s.Social.MastodonInstance} · your home timeline feeds the pulse" : "Not connected — only public hashtags are read.";
    }

    private async void OnConnectBluesky(object sender, RoutedEventArgs e)
    {
        var handle = _s.Social.BlueskyHandle.Trim().TrimStart('@');
        var password = BskyPassword.Password.Trim();
        if (handle.Length == 0 || password.Length == 0) { BskyStatus.Text = "Enter your handle and an app password first."; return; }
        BskyStatus.Text = "Signing in…";
        var who = await BlueskySource.TestSignInAsync(Hub.Core.Http, handle, password);
        if (who is null) { BskyStatus.Text = "Sign-in failed — check the handle and that you used an app password (not your main password)."; return; }
        Hub.Core.Secrets.Set(SecretKeys.BlueskyAppPassword, password);
        BskyPassword.Password = "";
        Save();
        ShowConnections();
        Hub.Core.Agents.RunNow("social-scout");
    }

    private void OnDisconnectBluesky(object sender, RoutedEventArgs e)
    {
        Hub.Core.Secrets.Set(SecretKeys.BlueskyAppPassword, null);
        ShowConnections();
    }

    private async void OnConnectMastodon(object sender, RoutedEventArgs e)
    {
        var token = MastodonToken.Password.Trim();
        var instance = _s.Social.MastodonInstance.Trim().Replace("https://", "").TrimEnd('/');
        if (token.Length == 0 || instance.Length == 0) { MastodonStatus.Text = "Enter your server (above) and an access token first."; return; }
        MastodonStatus.Text = "Checking…";
        var account = await MastodonSource.VerifyAsync(Hub.Core.Http, instance, token);
        if (account is null) { MastodonStatus.Text = "The server didn't accept that token — it needs the read:statuses scope."; return; }
        Hub.Core.Secrets.Set(SecretKeys.MastodonToken, token);
        MastodonToken.Password = "";
        Save();
        MastodonStatus.Text = $"Connected as @{account}@{instance} · your home timeline feeds the pulse";
        Hub.Core.Agents.RunNow("social-scout");
    }

    private void OnDisconnectMastodon(object sender, RoutedEventArgs e)
    {
        Hub.Core.Secrets.Set(SecretKeys.MastodonToken, null);
        ShowConnections();
    }

    /// <summary>bsky.app/profile/{actor}/feed/{rkey} links become at:// addresses; at:// addresses pass through.</summary>
    internal static string? NormalizeFeedLink(string input)
    {
        var s = input.Trim();
        if (s.StartsWith("at://", StringComparison.Ordinal)) return s;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"bsky\.app/profile/([^/\s]+)/feed/([A-Za-z0-9._~-]+)");
        return m.Success ? $"at://{m.Groups[1].Value}/app.bsky.feed.generator/{m.Groups[2].Value}" : null;
    }

    private bool _backfilling;

    /// <summary>Channels missing their logo or name (added offline, or before logos were fetched) are looked up again.</summary>
    private async Task BackfillChannelsAsync()
    {
        if (_backfilling) return;
        var missing = Hub.S.Social.YouTubeChannels.Where(c => string.IsNullOrEmpty(c.AvatarUrl) || c.Name is "" or "YouTube channel")
            .Select(c => c.ChannelId).Take(12).ToList();
        if (missing.Count == 0) return;
        _backfilling = true;
        var found = new Dictionary<string, YouTubeChannelInfo>();
        try
        {
            foreach (var id in missing)
            {
                try
                {
                    if (await YouTubeChannels.ResolveAsync(Hub.Core.Http, id) is { } info && info.ChannelId == id && (info.AvatarUrl is { Length: > 0 } || info.Name.Length > 0)) found[id] = info;
                }
                catch (Exception ex) { Core.Util.Log.Debug("settings", $"Channel lookup {id}: {ex.Message}"); }
            }
        }
        finally { _backfilling = false; }
        if (found.Count == 0) return;
        Hub.Core.Settings.Update(s =>
        {
            foreach (var c in s.Social.YouTubeChannels)
            {
                if (!found.TryGetValue(c.ChannelId, out var i)) continue;
                if (i.AvatarUrl is { Length: > 0 } logo) c.AvatarUrl = logo;
                if (c.Handle.Length == 0) c.Handle = i.Handle ?? "";
                if ((c.Name.Length == 0 || c.Name == "YouTube channel") && i.Name.Length > 0) c.Name = i.Name;
            }
        });
    }

    private void OnChannelKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAddChannel(sender, e);
    }

    /// <summary>
    /// Accepts youtube.com/@name, @name, /channel/UC…, /c/ and /user/ links, video links or a bare UC… ID, and looks the
    /// channel up for its ID, name and logo.
    /// </summary>
    private async void OnAddChannel(object sender, RoutedEventArgs e)
    {
        var input = NewChannelId.Text.Trim();
        var (id, page) = YouTubeChannels.Parse(input);
        if (id is null && page is null)
        {
            ChannelError.Text = "That doesn't look like a YouTube channel — paste its link (youtube.com/@name), its @handle, a video link or its UC… ID.";
            return;
        }
        if (id is not null && _s.Social.YouTubeChannels.Any(c => c.ChannelId == id)) { ChannelError.Text = "That channel is already added."; return; }
        AddChannelButton.IsEnabled = false;
        ChannelError.Text = "Looking up the channel…";
        YouTubeChannelInfo? info = null;
        try { info = await YouTubeChannels.ResolveAsync(Hub.Core.Http, input); }
        catch (Exception ex) { Core.Util.Log.Warn("settings", "YouTube lookup failed", ex); }
        finally { AddChannelButton.IsEnabled = true; }
        if (info is null && id is null)
        {
            ChannelError.Text = "Couldn't find that channel. Check the link, or paste the channel's UC… ID (on the channel page: About › Share › Copy channel ID).";
            return;
        }
        var channelId = info?.ChannelId ?? id!;
        if (_s.Social.YouTubeChannels.Any(c => c.ChannelId == channelId)) { ChannelError.Text = "That channel is already added."; return; }
        _s.Social.YouTubeChannels.Add(new ChannelRef
        {
            ChannelId = channelId,
            Name = info?.Name is { Length: > 0 } name ? name : "YouTube channel",
            Handle = info?.Handle ?? "",
            AvatarUrl = info?.AvatarUrl ?? "",
        });
        ChannelError.Text = info is null ? "Added — the name couldn't be looked up right now (offline?), so it shows as “YouTube channel”." : "";
        NewChannelId.Text = "";
        RefreshLists();
        Save();
        Hub.Core.Agents.RunNow("social-scout");
    }

    private void OnRemoveChannel(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        _s.Social.YouTubeChannels.RemoveAll(c => c.ChannelId == id);
        RefreshLists();
        Save();
    }

    private void OnAddExtraFeed(object sender, RoutedEventArgs e)
    {
        var url = NewFeedUrl.Text.Trim();
        if (!SettingsStore.IsSafeHttpUrl(url)) { ExtraFeedError.Text = "Please enter an https:// RSS or Atom feed URL."; return; }
        if (_s.Social.ExtraFeeds.Any(f => f.Url == url)) { ExtraFeedError.Text = "That feed is already added."; return; }
        var name = NewFeedName.Text.Trim() is { Length: > 0 } n ? n : new Uri(url).Host.Replace("www.", "");
        _s.Social.ExtraFeeds.Add(new NewsSource { Id = "feed-" + Hash.Short(url)[..8], Name = name, Url = url, Category = "social", Tier = 3, Local = NewFeedLocal.IsChecked == true });
        ExtraFeedError.Text = "";
        NewFeedUrl.Text = NewFeedName.Text = "";
        NewFeedLocal.IsChecked = false;
        RefreshLists();
        Save();
        Hub.Core.Agents.RunNow("social-scout");
    }

    private void OnRemoveExtraFeed(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        _s.Social.ExtraFeeds.RemoveAll(f => f.Id == id);
        RefreshLists();
        Save();
    }

    private void OnSaveAiKey(object sender, RoutedEventArgs e)
    {
        Hub.Core.Secrets.Set(SecretKeys.OpenAiCompatibleKey, AiKey.Password.Trim());
        SavedText.Text = "API key saved to Credential Manager";
    }

    private void OnSaveBraveKey(object sender, RoutedEventArgs e)
    {
        Hub.Core.Secrets.Set(SecretKeys.BraveSearchKey, BraveKey.Password.Trim());
        SavedText.Text = "API key saved to Credential Manager";
    }

    private void OnSearchEngineChanged(object sender, SelectionChangedEventArgs e) => UpdateSearchEngineRows();

    private void UpdateSearchEngineRows()
    {
        var engine = (SearchEngineCombo.SelectedValue as string) ?? _s.Ask.SearchEngine;
        SearxngRow.Visibility = engine == "searxng" ? Visibility.Visible : Visibility.Collapsed;
        BraveRow.Visibility = engine == "brave" ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed record AskFolderVM(string Label, string Path, string Raw);

    private void OnAddAskFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder Ask may search and read" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var folder = dialog.FolderName;
        if (LocalFiles.IsSensitiveFolder(folder)) { SavedText.Text = "That folder holds app data or credentials, which Ask never reads."; return; }
        if (_s.Ask.Folders.Any(f => string.Equals(LocalFiles.ExpandFolders(new[] { f }, Hub.Ask.Platform.KnownFolder).FirstOrDefault(), folder, StringComparison.OrdinalIgnoreCase))) return;
        _s.Ask.Folders.Add(folder);
        RefreshLists();
        Save();
    }

    private void OnRemoveAskFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string raw }) return;
        _s.Ask.Folders.RemoveAll(f => f == raw);
        RefreshLists();
        Save();
    }

    private void OnSaveFinnhub(object sender, RoutedEventArgs e)
    {
        Hub.Core.Secrets.Set(SecretKeys.FinnhubApiKey, FinnhubKey.Password.Trim());
        SavedText.Text = "API key saved to Credential Manager";
        Hub.Core.Agents.RunNow("events-scout");
    }

    // ───────────── AI ─────────────
    private void FillModels()
    {
        var installed = Hub.State.Ai?.Models.Select(m => m.Name).ToList() ?? new List<string>();
        ModelCombo.Items.Clear();
        ModelCombo.Items.Add(new ComboBoxItem { Content = "Automatic (recommended)", Tag = "auto" });
        foreach (var m in installed) ModelCombo.Items.Add(new ComboBoxItem { Content = m, Tag = m });
        ModelCombo.SelectedItem = ModelCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _s.Ai.Model) ?? ModelCombo.Items[0];

        DeepModelCombo.Items.Clear();
        DeepModelCombo.Items.Add(new ComboBoxItem { Content = "Same as main model", Tag = "" });
        foreach (var m in installed) DeepModelCombo.Items.Add(new ComboBoxItem { Content = m, Tag = m });
        DeepModelCombo.SelectedItem = DeepModelCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _s.Ai.DeepModel) ?? DeepModelCombo.Items[0];
        AiTest.Text = AiStatus(Hub.State.Ai);
    }

    /// <summary>"Connected to Ollama 0.12.3 · 3 models · using qwen3.5:9b", or why not — for the Connection row.</summary>
    private static string AiStatus(Core.Ai.LlmHealth? h) => h is null ? "" : !h.Available ? h.Error ?? "Not connected"
        : $"Connected to {(h.Provider == "ollama" ? "Ollama" : "an OpenAI-compatible server")}{(string.IsNullOrEmpty(h.Version) ? "" : " " + h.Version)}"
          + $" · {Plural.Of(h.Models.Count, "model")} · using {h.ActiveModel}";

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _s.Ai.Model = tag;
    }

    private void OnDeepModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DeepModelCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        _s.Ai.DeepModel = tag;
    }

    private async void OnTestAi(object sender, RoutedEventArgs e)
    {
        Save();
        AiTest.Text = "Testing…";
        var h = await Hub.Core.Llm.CheckAsync();
        _loading = true;
        FillModels();
        _loading = false;
        // After FillModels, which shows the last known state: this is the answer to the button.
        AiTest.Text = AiStatus(h);
    }

    // ───────────── Apps & scenes ─────────────
    private void OnRemoveApp(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        _s.Apps.RemoveAll(a => a.Id == id);
        RefreshLists();
        Save();
    }

    private void OnAddScene(object sender, RoutedEventArgs e)
    {
        _s.Scenes.Add(new Scene { Id = "scene-" + Hash.Short(DateTime.UtcNow.Ticks.ToString())[..6], Name = "New scene", Icon = "wand", Description = "" });
        BuildScenes(expandLast: true);
        Save();
    }

    /// <summary>Scenes whose editor is open (by id): rebuilding the list after an edit keeps them open.</summary>
    private readonly HashSet<string> _openScenes = new();

    private void BuildScenes(bool expandLast = false)
    {
        ScenesHost.Children.Clear();
        for (var i = 0; i < _s.Scenes.Count; i++)
        {
            var scene = _s.Scenes[i];
            if (expandLast && i == _s.Scenes.Count - 1) _openScenes.Add(scene.Id);
            var exp = new Expander { IsExpanded = _openScenes.Contains(scene.Id), Margin = new Thickness(0, 0, 0, 6) };
            exp.Expanded += (_, _) => _openScenes.Add(scene.Id);
            exp.Collapsed += (_, _) => _openScenes.Remove(scene.Id);
            exp.Header = new TextBlock { Text = $"{scene.Name}  ·  {scene.Steps.Count} steps", FontSize = 13.5 };
            var body = new StackPanel { Margin = new Thickness(20, 6, 0, 8) };

            var nameRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition());
            var name = new TextBox { Text = scene.Name, Margin = new Thickness(0, 0, 6, 0) };
            UiProps.SetPlaceholder(name, "Scene name");
            name.TextChanged += (_, _) => { scene.Name = name.Text; ((TextBlock)exp.Header).Text = $"{scene.Name}  ·  {scene.Steps.Count} steps"; };
            var desc = new TextBox { Text = scene.Description };
            UiProps.SetPlaceholder(desc, "What it does");
            desc.TextChanged += (_, _) => scene.Description = desc.Text;
            Grid.SetColumn(desc, 1);
            nameRow.Children.Add(name);
            nameRow.Children.Add(desc);
            body.Children.Add(nameRow);

            foreach (var step in scene.Steps.ToList()) body.Children.Add(StepRow(scene, step, exp));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var add = new Button { Style = (Style)FindResource("Btn.Standard"), Content = "Add step", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            add.Click += (_, _) => { scene.Steps.Add(new SceneStep { Action = "dnd", Value = "on" }); BuildScenes(); Save(); };
            var run = new Button { Style = (Style)FindResource("Btn.Standard"), Content = "Run now", Padding = new Thickness(10, 4, 10, 4), FontSize = 12, Margin = new Thickness(6, 0, 0, 0) };
            run.Click += async (_, _) => { Save(); await Hub.Actions.RunSceneAsync(scene); };
            var del = new Button { Style = (Style)FindResource("Btn.Subtle"), Content = "Delete scene", Padding = new Thickness(10, 4, 10, 4), FontSize = 12, Margin = new Thickness(6, 0, 0, 0), Foreground = Fmt.Res("B.Down") };
            del.Click += (_, _) => { _s.Scenes.Remove(scene); BuildScenes(); Save(); };
            buttons.Children.Add(add);
            buttons.Children.Add(run);
            buttons.Children.Add(del);
            body.Children.Add(buttons);
            exp.Content = body;
            ScenesHost.Children.Add(exp);
        }
    }

    private FrameworkElement StepRow(Scene scene, SceneStep step, Expander owner)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var action = new ComboBox { ItemsSource = StepActions, SelectedItem = step.Action, MinWidth = 150, Margin = new Thickness(0, 0, 6, 0) };
        FrameworkElement valueEditor;
        if (step.Action is "launch" or "close" or "focus")
        {
            var apps = new ComboBox { ItemsSource = _s.Apps, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = step.Target };
            apps.SelectionChanged += (_, _) => step.Target = apps.SelectedValue as string ?? "";
            valueEditor = apps;
        }
        else
        {
            var hint = step.Action switch
            {
                "media" => "play | pause | toggle | next | previous",
                "volume" => "0–100",
                "mute" => "true | false",
                "dnd" => "on | off",
                "ai" => "pause | resume",
                "open-url" => "https://…",
                "open-page" => "today | news | markets | …",
                "wait" => "seconds",
                "music-search" => "what to play",
                "read-brief" => "(no value needed)",
                _ => "",
            };
            var tb = new TextBox { Text = step.Value };
            UiProps.SetPlaceholder(tb, hint);
            tb.TextChanged += (_, _) => step.Value = tb.Text;
            valueEditor = tb;
        }
        action.SelectionChanged += (_, _) =>
        {
            step.Action = action.SelectedItem as string ?? step.Action;
            step.Target = "";
            step.Value = "";
            BuildScenes();
        };
        var remove = new Button { Style = (Style)FindResource("Btn.Icon"), Width = 28, Height = 28, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove step", Content = new Icon { Kind = "trash", Width = 13, Height = 13 } };
        remove.Click += (_, _) => { scene.Steps.Remove(step); BuildScenes(); Save(); };
        Grid.SetColumn(valueEditor, 1);
        Grid.SetColumn(remove, 2);
        row.Children.Add(action);
        row.Children.Add(valueEditor);
        row.Children.Add(remove);
        return row;
    }

    // ───────────── Voice ─────────────
    public sealed record MicOption(string Name, string Label)
    {
        public override string ToString() => Label;
    }
    private VoiceInput? _voiceTest;
    private bool _fillingMics;

    /// <summary>Windows' default, then each microphone by its full name; a chosen one that isn't plugged in stays listed.</summary>
    private async Task FillMicsAsync()
    {
        var mics = await Microphones.ListAsync();
        var byDefault = await Microphones.DefaultNameAsync();
        var saved = _s.Ask.Microphone;
        var options = new List<MicOption> { new("", byDefault is { Length: > 0 } d ? $"Windows default ({d})" : "Windows default") };
        var chosen = Microphones.Find(mics, saved);
        options.AddRange(mics.Select(m => new MicOption(m == chosen ? saved : m.Name, m.Name)));
        if (saved.Length > 0 && chosen is null) options.Add(new MicOption(saved, saved + " (not connected)"));
        _fillingMics = true;
        MicCombo.ItemsSource = options;
        MicCombo.SelectedItem = options.FirstOrDefault(o => o.Name.Equals(saved, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        _fillingMics = false;
    }

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingMics || _loading || MicCombo.SelectedItem is not MicOption mic) return;
        _s.Ask.Microphone = mic.Name;
    }

    private void OnSoundSettings(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl("ms-settings:sound", allowAppProtocols: true);
    private void OnSpeechSettings(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl("ms-settings:speech", allowAppProtocols: true);
    private void OnMicSetup(object sender, RoutedEventArgs e) => SpeechWizard("MicTraining");
    private void OnVoiceTraining(object sender, RoutedEventArgs e) => SpeechWizard("UserTraining");

    /// <summary>Windows' own speech wizards: microphone setup, and reading sentences aloud so the recognizer learns your voice.</summary>
    private void SpeechWizard(string page)
    {
        var wizard = Path.Combine(Environment.SystemDirectory, "Speech", "SpeechUX", "SpeechUXWiz.exe");
        if (Sandbox.Intercept("speech-wizard", page)) return;
        if (!File.Exists(wizard))
        {
            AppLauncher.OpenUrl("ms-settings:speech", allowAppProtocols: true);
            return;
        }
        try { Process.Start(new ProcessStartInfo(wizard, page) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn("voice", "Couldn't open Windows' speech wizard", ex);
            SavedText.Text = "Couldn't open Windows' speech setup: " + ex.Message;
        }
    }

    // ───────────── Whisper ─────────────
    /// <summary>A model in the list; its label is also what screen readers (and UI Automation) call it.</summary>
    public sealed record WhisperOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }
    // ───────────────────────── Updates (About) ─────────────────────────

    private void OnUpdatesChanged() => Hub.OnUi(UpdateUpdates);

    /// <summary>The Updates row: what the updater is doing, and the buttons that make sense now.</summary>
    private void UpdateUpdates()
    {
        var u = Hub.Updates;
        var stage = u.Stage;
        UpdateCheckButton.Visibility = stage == UpdateStage.Unavailable ? Visibility.Collapsed : Visibility.Visible;
        UpdateCheckButton.IsEnabled = stage is not (UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready);
        UpdateActionButton.Visibility = stage is UpdateStage.Available or UpdateStage.Ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionButton.Content = stage == UpdateStage.Ready ? "Restart now" : $"Download ({UpdatePolicy.Megabytes(u.DownloadSize)})";
        var downloading = stage == UpdateStage.Downloading;
        UpdateProgressTrack.Visibility = UpdateCancelButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressFill.Width = downloading ? UpdateProgressTrack.ActualWidth * u.Percent / 100.0 : 0;
        UpdateStatus.Text = u.Status(_s.General.CheckForUpdates);
        var notes = stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready && u.Notes.Length > 0;
        UpdateNotes.Text = notes ? $"What's new in {u.NewVersion}:\n{u.Notes}" : "";
        UpdateNotes.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        UpdateReleasesButton.Visibility = u.Repository is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnUpdateCheck(object sender, RoutedEventArgs e) => await Hub.Updates.CheckAsync(userAsked: true);

    /// <summary>Download (the user's go-ahead), then Restart now to install.</summary>
    private async void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        if (Hub.Updates.Stage == UpdateStage.Ready)
        {
            Save();
            Hub.Updates.RestartToInstall();
        }
        else await Hub.Updates.DownloadAsync();
    }

    private void OnUpdateCancel(object sender, RoutedEventArgs e) => Hub.Updates.CancelDownload();

    private void OnUpdateReleases(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl(Hub.Updates.Repository + "/releases");

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e) => UpdateUpdates();

    private string? _whisperError;
    private int _whisperUiPending;

    private void OnWhisperChanged() => Hub.OnUi(UpdateWhisper);

    /// <summary>Download progress comes many times a second; the row catches up once per turn of the UI.</summary>
    private void OnWhisperProgress(WhisperProgress progress)
    {
        if (Interlocked.Exchange(ref _whisperUiPending, 1) == 0) Hub.OnUi(() => { _whisperUiPending = 0; UpdateWhisper(); });
    }

    private void OnWhisperModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is null) return;
        _whisperError = null;
        UpdateWhisper();
    }

    private void OnVoiceEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && DataContext is not null) UpdateWhisper();
    }

    /// <summary>The Whisper row: installed or not, the download as it goes, and what its buttons can do.</summary>
    private void UpdateWhisper()
    {
        var setup = Hub.Core.Whisper;
        var busy = setup.Busy;
        var install = busy ? null : setup.Installed();
        var choice = WhisperCatalog.Choice(_s.Ask.WhisperModel);
        var wanted = choice.For(_s.Location.Language);
        var runtime = WhisperCatalog.Runtime(RuntimeInformation.OSArchitecture);
        WhisperProgressTrack.Visibility = WhisperCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        WhisperRemoveButton.Visibility = install is not null ? Visibility.Visible : Visibility.Collapsed;
        WhisperModelCombo.IsEnabled = !busy;
        if (busy)
        {
            var p = setup.Current;
            WhisperProgressFill.Width = p is { Total: > 0 } ? WhisperProgressTrack.ActualWidth * p.Done / p.Total : 0;
            WhisperStatus.Text = p is null ? "Starting…" : $"{p.Step}… {WhisperCatalog.Megabytes(p.Done)} of {WhisperCatalog.Megabytes(p.Total)}";
            WhisperInstallButton.Content = "Installing…";
            WhisperInstallButton.IsEnabled = false;
            return;
        }
        var same = install is not null && install.ChoiceId == choice.Id
                   && Path.GetFileName(install.Model).Equals(wanted.FileName, StringComparison.OrdinalIgnoreCase);
        WhisperInstallButton.Content = install is null ? "Install" : same ? "Installed" : "Switch model";
        WhisperInstallButton.IsEnabled = !same && runtime is not null;
        var download = (install is null ? runtime?.Size ?? 0 : 0) + wanted.Download.Size;
        WhisperStatus.Text = _whisperError
            ?? (runtime is null ? "Whisper isn't available for this PC's processor."
            : install is null
                ? $"Not installed. Installing downloads about {WhisperCatalog.Megabytes(download)}."
                  + (_s.Ask.Voice == "whisper" ? " Voice input is set to Whisper, so the microphone button needs it installed first." : "")
                : $"Installed: {install.Name}, whisper.cpp {install.Version}."
                  + (_s.Ask.Voice == "whisper" ? " Voice input uses it." : " Choose Whisper under Voice input to use it.")
                  + (install.EnglishOnly && !WhisperCatalog.IsEnglish(_s.Location.Language) ? " This model only knows English; “Switch model” gets one for your language." : "")
                  + (same ? "" : $" Switching downloads {WhisperCatalog.Megabytes(download)} and removes the current model."));
    }

    /// <summary>Downloads and checks Whisper (it carries on if you leave the page), then makes it the voice input.</summary>
    private async void OnWhisperInstall(object sender, RoutedEventArgs e)
    {
        if (Sandbox.Intercept("whisper-install", _s.Ask.WhisperModel))
        {
            WhisperStatus.Text = "Installing Whisper is simulated in this test session.";
            return;
        }
        _whisperError = null;
        try
        {
            var install = Hub.Core.Whisper.InstallAsync(_s.Ask.WhisperModel, _s.Location.Language);
            UpdateWhisper();
            await install;
            // Installing it is choosing it.
            if (IsLoaded) VoiceCombo.SelectedValue = "whisper";
            else Hub.Core.Settings.Update(x => x.Ask.Voice = "whisper");
        }
        catch (WhisperSetupException ex) { _whisperError = ex.Message; }
        UpdateWhisper();
    }

    private void OnWhisperCancel(object sender, RoutedEventArgs e) => Hub.Core.Whisper.Cancel();

    private void OnWhisperRemove(object sender, RoutedEventArgs e)
    {
        var question = "Remove Whisper?\n\nIts program and model are deleted from this PC. You can install it again at any time.";
        if (MessageBox.Show(Window.GetWindow(this)!, question, "Remove Whisper", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (Sandbox.Intercept("whisper-remove")) return;
        if (Hub.Ask.Voice.IsListening) Hub.Ask.Voice.Stop();
        _voiceTest?.Dispose();
        _voiceTest = null;
        try
        {
            Hub.Core.Whisper.Remove();
            _whisperError = null;
            if (_s.Ask.Voice == "whisper") VoiceCombo.SelectedValue = "offline";
        }
        catch (WhisperSetupException ex) { _whisperError = ex.Message; }
        UpdateWhisper();
    }

    /// <summary>Listens once with the settings on this page, showing the level, what was heard and the tidy-up.</summary>
    private async void OnVoiceTest(object sender, RoutedEventArgs e)
    {
        if (_voiceTest is { IsListening: true })
        {
            _voiceTest.Stop();
            return;
        }
        VoiceTestPanel.Visibility = Visibility.Visible;
        VoiceTestHeard.Visibility = VoiceTestTidied.Visibility = Visibility.Collapsed;
        VoiceLevelFill.Width = 0;
        if (_s.Ask.Voice == "off")
        {
            VoiceTestStatus.Text = "Voice input is off — choose one above first.";
            return;
        }
        if (Hub.Ask.Voice.IsListening) Hub.Ask.Voice.Stop();
        _voiceTest ??= NewVoiceTest();
        VoiceTestStatus.Text = "Starting…";
        VoiceTestButton.IsEnabled = false;
        var error = await _voiceTest.StartAsync(_s.Ask.Voice, _s.Location.Language, _s.Ask.Microphone);
        VoiceTestButton.IsEnabled = true;
        if (error is not null)
        {
            VoiceTestStatus.Text = error;
            return;
        }
        VoiceTestButton.Content = "Stop";
        VoiceTestStatus.Text = "Listening · " + _voiceTest.Describe + ". Say a sentence, e.g. “What's the weather like tomorrow?”"
                               + (_s.Ask.Voice == "whisper" ? ", then pause or click Stop — Whisper writes it down then." : "")
                               + (_voiceTest.Note is { } note ? " " + note : "")
                               + (_s.Ask.Voice == "online" ? "" : " The bar shows how loud you are: aim for the middle.");
    }

    private VoiceInput NewVoiceTest()
    {
        var voice = new VoiceInput();
        voice.Level += level => VoiceLevelFill.Width = Math.Clamp(level, 0, 1) * VoiceLevelTrack.ActualWidth;
        voice.Partial += text => ShowHeard("Hearing: " + text);
        voice.Phrase += _ => ShowHeard("Heard: " + DictationTidy.Joined(voice.Heard));
        voice.Problem += advice => VoiceTestStatus.Text = advice;
        voice.Transcribing += () =>
        {
            VoiceLevelFill.Width = 0;
            VoiceTestStatus.Text = "Writing down what you said…";
        };
        voice.Ended += error => _ = VoiceTestEndedAsync(voice, error);
        return voice;
    }

    private void ShowHeard(string text)
    {
        VoiceTestHeard.Text = text;
        VoiceTestHeard.Visibility = Visibility.Visible;
    }

    private async Task VoiceTestEndedAsync(VoiceInput voice, string? error)
    {
        VoiceTestButton.Content = "Start test";
        VoiceLevelFill.Width = 0;
        if (error is not null)
        {
            VoiceTestStatus.Text = error;
            return;
        }
        var heard = voice.Heard.ToList();
        if (heard.Count == 0)
        {
            VoiceTestStatus.Text = "Nothing was heard. If the bar didn't move when you spoke, pick another microphone or check it in Sound settings.";
            return;
        }
        if (_s.Ask.Voice == "whisper")
        {
            ShowHeard("Heard: " + DictationTidy.Joined(heard));
            VoiceTestStatus.Text = "Done — that's what Whisper wrote. It goes in the Ask box as it is.";
            return;
        }
        var sure = heard.Average(h => h.Confidence);
        ShowHeard($"Heard: {DictationTidy.Joined(heard)}   ({sure:P0} sure)");
        if (!_s.Ask.VoiceTidy || !Hub.Core.Llm.Health.Available || DictationTidy.Skip(heard) is { })
        {
            VoiceTestStatus.Text = (!_s.Ask.VoiceTidy ? "Done. Tidy-up is off."
                : DictationTidy.Skip(heard) is { } why ? $"Done. No tidy-up needed: {why}."
                : "Done. The tidy-up needs the local model, which isn't running.") + WhisperHint();
            return;
        }
        VoiceTestStatus.Text = "Tidying up…";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var tidied = await Task.Run(() => DictationTidy.TidyAsync(Hub.Core.Llm, heard, cts.Token));
        VoiceTestTidied.Text = "After tidy-up: " + (tidied ?? DictationTidy.Joined(heard) + "  (no change)");
        VoiceTestTidied.Visibility = Visibility.Visible;
        VoiceTestStatus.Text = (sure < 0.5 ? "Done. The recognizer was unsure — “Train my voice” below or a closer microphone may help." : "Done.")
                               + WhisperHint();
    }

    /// <summary>After a test with Windows' recognizers, when Whisper isn't in use: where much better results are.</summary>
    private string WhisperHint() =>
        _s.Ask.Voice == "whisper" ? ""
        : Hub.Core.Whisper.Installed() is not null ? " Whisper is installed — choose it under Voice input for much better results."
        : " Not what you said? Whisper (above) is far more accurate, and runs on this PC.";

    // ───────────── Data ─────────────
    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        if (Sandbox.Intercept("open-folder", Hub.Core.Paths.Root)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Hub.Core.Paths.Root}\"") { UseShellExecute = true });
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (Sandbox.Intercept("open-log", Log.FilePath ?? "")) return;
        if (Log.FilePath is { } path && File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // ───────────── Debug ─────────────
    public sealed record ProblemRow(string When, string Level, string Area, string Message, string Detail, Brush LevelBrush)
    {
        public bool HasDetail => Detail.Contains('\n');
    }

    private void ShowProblems()
    {
        var rows = Log.RecentProblems.Reverse().Take(30).Select(p => new ProblemRow(
            p.Time.ToString("d MMM HH:mm:ss", CultureInfo.CurrentCulture), p.Level == LogLevel.Error ? "ERROR" : "WARNING", p.Area, p.Message, p.Detail ?? "",
            Fmt.Res(p.Level == LogLevel.Error ? "B.Down" : "B.Warn"))).ToList();
        ProblemsList.ItemsSource = rows;
        NoProblems.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProblemsHeader.Text = Log.ProblemCount == 0 ? "RECENT PROBLEMS" : $"RECENT PROBLEMS · {Log.ProblemCount} SINCE AQUA STARTED";
    }

    private void OnRefreshProblems(object sender, RoutedEventArgs e) => ShowProblems();

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        if (Log.FilePath is not { } path || Path.GetDirectoryName(path) is not { } dir) return;
        if (Sandbox.Intercept("open-folder", dir)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OnOpenProblems(object sender, RoutedEventArgs e)
    {
        if (Sandbox.Intercept("open-log", Log.ProblemsPath ?? "")) return;
        if (Log.ProblemsPath is { } path)
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
    }

    private static void CopyText(string text)
    {
        if (Sandbox.Intercept("clipboard", text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Warn("settings", "Couldn't copy to the clipboard", ex); }
    }

    private void OnCopyProblems(object sender, RoutedEventArgs e)
    {
        var problems = Log.RecentProblems;
        CopyText(problems.Count == 0 ? "No warnings or errors since Aqua started." : string.Join("\n\n", problems.Reverse().Take(30).Select(p =>
            $"{p.Time:yyyy-MM-dd HH:mm:ss.fff} {p.Level.ToString().ToUpperInvariant()} [{p.Area}] {p.Message}" + (p.Detail is { Length: > 0 } d ? "\n" + d : ""))));
        SavedText.Text = $"Copied {Plural.Of(Math.Min(30, problems.Count), "problem")}";
    }

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        CopyText(DiagnosticsSummary.Build());
        SavedText.Text = "Diagnostics summary copied";
    }

    private void OnClearCaches(object sender, RoutedEventArgs e)
    {
        Hub.Core.Db.ClearCaches();
        SavedText.Text = "Caches cleared";
        Hub.Core.Agents.RunAll();
    }

    private void OnEditJson(object sender, RoutedEventArgs e)
    {
        Save();
        var path = Hub.Core.Settings.FilePath;
        if (Sandbox.Intercept("edit-json", path)) return;
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite, EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(300);
            if (Hub.Core.Settings.Reload(out var error)) { Load(); SavedText.Text = "Reloaded from settings.json"; }
            else SavedText.Text = "settings.json has an error: " + error;
        });
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
