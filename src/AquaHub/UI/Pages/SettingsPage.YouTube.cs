using System.IO;
using System.Windows;
using System.Windows.Controls;
using AquaHub.Core.Settings;
using AquaHub.Core.Sources;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public partial class SettingsPage
{
    private CancellationTokenSource? _googleCts;
    private bool _googleBusy;
    private sealed class YouTubeChoice(ChannelRef channel, bool selected, string label, Func<ChannelRef, bool, bool> choose) : ObservableObject
    {
        private bool _selected = selected;
        public string Label { get; } = label;
        public bool Selected
        {
            get => _selected;
            set { if (value == _selected) return; _selected = choose(channel, value); Raise(); }
        }
    }

    private void OnYouTubeAccountChanged() => Hub.OnUi(() => { if (_streamsActive) { ShowYouTubeAccount(); RefreshYouTubeChoices(); } });
    private void ShowYouTubeAccount()
    {
        var account = Hub.Core.YouTubeAccount;
        GoogleImport.IsEnabled = !_googleBusy;
        GoogleConnect.IsEnabled = !_googleBusy;
        GoogleConnect.Content = _googleBusy ? "Connecting Google…" : "Connect Google…";
        GoogleConnect.Visibility = account.Connected ? Visibility.Collapsed : Visibility.Visible;
        GoogleSync.Visibility = account.Connected ? Visibility.Visible : Visibility.Collapsed;
        GoogleSync.IsEnabled = !_googleBusy;
        GoogleDisconnect.Visibility = account.Connected ? Visibility.Visible : Visibility.Collapsed;
        GoogleDisconnect.IsEnabled = !_googleBusy;
        GoogleCancel.Visibility = _googleBusy && _googleCts is not null ? Visibility.Visible : Visibility.Collapsed;
        GoogleStatus.Text = account.Connected
            ? account.LastSynced is { } synced ? $"Google connected · {account.Channels.Count} subscriptions · synced {synced.ToLocalTime():g}" + (account.Truncated ? " · first 1,000 shown" : "") : "Google connected · awaiting first sync"
            : account.Configured ? "Desktop client configured" : "Desktop client configuration required";
    }
    private void OnGoogleSetup(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl("https://developers.google.com/youtube/data/guides/auth/installed-apps");
    private async void OnImportGoogleClient(object sender, RoutedEventArgs e)
    {
        if (_googleBusy || Sandbox.Intercept("google-client-import", "Desktop OAuth client configuration picker")) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Google client configuration (*.json)|*.json", Title = "Choose Google Desktop OAuth client JSON" };
        if (picker.ShowDialog() != true) return;
        await RunGoogleAsync(async ct =>
        {
            await using var file = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (file.Length > 16384) throw new GoogleYouTubeException("That file is too large for a Google Desktop client configuration.");
            var buffer = new char[16385];
            using var reader = new StreamReader(file);
            var length = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            if (length > 16384) throw new GoogleYouTubeException("That file is too large for a Google Desktop client configuration.");
            Hub.Core.YouTubeAccount.Configure(new string(buffer, 0, length));
        }, "Importing Google client configuration…", "Desktop client configured. Connect Google to import subscriptions.");
    }
    private async void OnConnectGoogle(object sender, RoutedEventArgs e)
    {
        if (_googleBusy) return;
        SaveNow();
        if (Sandbox.Intercept("google-connect", "read-only YouTube subscriptions")) { SettingsFeedback.Show("Google sign-in is intercepted in this test profile.", "google-feedback"); return; }
        await RunGoogleAsync(ct => Hub.Core.YouTubeAccount.ConnectAsync(uri => Hub.Ui.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            SettingsFeedback.Show("Finish Google sign-in in your browser.", "google-feedback", busy: true);
            if (!AppLauncher.OpenUrl(uri.AbsoluteUri)) throw new GoogleYouTubeException("Couldn't open your browser. Check your default browser, then connect Google again.");
        }).Task, ct), "Starting Google sign-in…");
    }
    private async void OnSyncGoogle(object sender, RoutedEventArgs e)
    {
        if (_googleBusy) return;
        SaveNow();
        if (Sandbox.Intercept("google-subscriptions-sync", "read-only subscriptions")) return;
        await RunGoogleAsync(ct => Hub.Core.YouTubeAccount.SyncAsync(ct), "Syncing YouTube subscriptions…");
    }
    private void OnCancelGoogle(object sender, RoutedEventArgs e) { _googleCts?.Cancel(); Hub.Core.YouTubeAccount.Cancel(); }
    private async void OnDisconnectGoogle(object sender, RoutedEventArgs e)
    {
        if (_googleBusy) return;
        SaveNow();
        await RunGoogleAsync(ct => Hub.Core.YouTubeAccount.DisconnectAsync(ct), "Disconnecting Google…", "Disconnected. Google tokens, imported subscriptions and Google-only live selections were removed from this PC.");
    }
    private async Task RunGoogleAsync(Func<CancellationToken, Task> operation, string busyText, string success = "")
    {
        _googleBusy = true;
        using var cancellation = new CancellationTokenSource();
        _googleCts = cancellation;
        ShowYouTubeAccount();
        SettingsFeedback.Show(busyText, "google-feedback", busy: true);
        try { await operation(cancellation.Token); SettingsFeedback.Show(success, "google-feedback"); }
        catch (OperationCanceledException)
        {
            if (_streamsActive) SettingsFeedback.Show(Hub.Core.YouTubeAccount.Connected ? "Sync cancelled. Your subscription list was kept." : "Google connection cancelled.", "google-feedback");
        }
        catch (GoogleYouTubeException ex) { SettingsFeedback.Show(ex.Message, "google-feedback", error: true); }
        // Provider/file exceptions can contain authorization URLs or local paths. Keep this boundary out of logs.
        catch (Exception) { SettingsFeedback.Show("Couldn't complete the Google connection. Check your connection and client configuration, then try again.", "google-feedback", error: true); }
        finally { _googleCts = null; _googleBusy = false; ShowYouTubeAccount(); RefreshYouTubeChoices(); }
    }
    private void OnFilterYouTubeChannels(object sender, TextChangedEventArgs e) { if (YouTubeLiveChoices is not null && _s is not null) RefreshYouTubeChoices(); }
    private void RefreshYouTubeChoices()
    {
        var selected = StreamSettings.SelectedYouTube(_s.Social);
        var selectedIds = selected.Select(c => c.ChannelId).ToHashSet(StringComparer.Ordinal);
        var saved = _s.Social.YouTubeChannels.Select(c => c.ChannelId).ToHashSet(StringComparer.Ordinal);
        var all = selected.Concat(_s.Social.YouTubeChannels).Concat(Hub.Core.YouTubeAccount.Channels)
            .Where(c => YouTubeStreams.ValidChannel(c.ChannelId)).DistinctBy(c => c.ChannelId).ToArray();
        var filter = YouTubeChannelFilter.Text.Trim();
        var choices = all.Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || c.ChannelId.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => selectedIds.Contains(c.ChannelId)).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new YouTubeChoice(c, selectedIds.Contains(c.ChannelId), $"{(c.Name.Length > 0 ? c.Name : c.ChannelId)} · {(saved.Contains(c.ChannelId) ? "saved channel" : "subscription")}", ChooseYouTubeLive)).ToArray();
        YouTubeLiveChoices.ItemsSource = choices;
        YouTubeSelectionStatus.Text = $"{selected.Count} of {StreamsSession.MaxYouTubeChannels} live channels selected · {choices.Length} shown." + (all.Length == 0 ? " Add a saved channel below or connect Google." : "") + (_s.Social.Streams.YouTube ? "" : " Turn on YouTube live checks above to monitor them.");
    }
    private bool ChooseYouTubeLive(ChannelRef channel, bool include)
    {
        var selected = StreamSettings.SelectedYouTube(_s.Social).ToList();
        if (include && selected.All(c => c.ChannelId != channel.ChannelId))
        {
            if (selected.Count >= StreamsSession.MaxYouTubeChannels)
            {
                SettingsFeedback.Show("Six channels are already selected. Uncheck a channel before choosing another.", "youtube-selection-feedback", error: true);
                return false;
            }
            selected.Add(channel);
        }
        else if (!include) selected.RemoveAll(c => c.ChannelId == channel.ChannelId);
        _s.Social.Streams.YouTubeSelection = selected;
        SaveNow();
        // Keep the current row/focus in place while its saved state changes.
        SettingsFeedback.Hide("youtube-selection-feedback");
        YouTubeSelectionStatus.Text = $"{selected.Count} of {StreamsSession.MaxYouTubeChannels} live channels selected.";
        return include;
    }
}
