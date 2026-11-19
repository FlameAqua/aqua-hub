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
    private string _googleMessage = "";
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
        if (_googleBusy) return;
        GoogleStatus.Text = _googleMessage.Length > 0 ? _googleMessage : account.Connected
            ? account.LastSynced is { } synced ? $"Google connected · {account.Channels.Count} subscriptions · synced {synced.ToLocalTime():g}." + (account.Truncated ? " Showing the first 1,000 subscriptions." : "") : "Google connected. Sync subscriptions to load your channel list."
            : account.Configured ? "Desktop client configured. Connect Google to import subscriptions." : "First enable YouTube Data API v3 in Google Cloud, create a Desktop app OAuth client, and import its JSON configuration. Add your Google account as a test user if the project is in Testing.";
    }
    private void OnGoogleSetup(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl("https://developers.google.com/youtube/data/guides/auth/installed-apps");
    private async void OnImportGoogleClient(object sender, RoutedEventArgs e)
    {
        if (_googleBusy || Sandbox.Intercept("google-client-import", "Desktop OAuth client configuration picker")) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Google client configuration (*.json)|*.json", Title = "Choose Google Desktop OAuth client JSON" };
        if (picker.ShowDialog() != true) return;
        await RunGoogleAsync(async _ =>
        {
            await using var file = new FileStream(picker.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (file.Length > 16384) throw new GoogleYouTubeException("That file is too large for a Google Desktop client configuration.");
            var buffer = new char[16385];
            using var reader = new StreamReader(file);
            var length = await reader.ReadBlockAsync(buffer.AsMemory(), _googleCts!.Token);
            if (length > 16384) throw new GoogleYouTubeException("That file is too large for a Google Desktop client configuration.");
            Hub.Core.YouTubeAccount.Configure(new string(buffer, 0, length));
        }, "Importing Google client configuration…", "Desktop client configured. Connect Google to import subscriptions.");
    }
    private async void OnConnectGoogle(object sender, RoutedEventArgs e)
    {
        if (_googleBusy) return;
        SaveNow();
        if (Sandbox.Intercept("google-connect", "read-only YouTube subscriptions")) { _googleMessage = "Google sign-in is intercepted in this test profile."; ShowYouTubeAccount(); return; }
        await RunGoogleAsync(ct => Hub.Core.YouTubeAccount.ConnectAsync(uri => Hub.Ui.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            GoogleStatus.Text = "Finish Google sign-in in your browser. Waiting for read-only YouTube approval…";
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
        _googleBusy = true; _googleMessage = "";
        using var cancellation = new CancellationTokenSource();
        _googleCts = cancellation;
        ShowYouTubeAccount(); GoogleStatus.Text = busyText;
        try { await operation(cancellation.Token); _googleMessage = success; }
        catch (OperationCanceledException) { _googleMessage = Hub.Core.YouTubeAccount.Connected ? "Sync cancelled. Google remains connected; the last successful subscription list is kept." : "Google connection cancelled."; }
        catch (GoogleYouTubeException ex) { _googleMessage = ex.Message; }
        // Provider/file exceptions can contain authorization URLs or local paths. Keep this boundary out of logs.
        catch (Exception) { _googleMessage = "Couldn't complete the Google connection. Check your connection and client configuration, then try again."; }
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
                YouTubeSelectionStatus.Text = "Six channels are already selected. Uncheck a channel before choosing another.";
                return false;
            }
            selected.Add(channel);
        }
        else if (!include) selected.RemoveAll(c => c.ChannelId == channel.ChannelId);
        _s.Social.Streams.YouTubeSelection = selected;
        SaveNow();
        // Keep the current row/focus in place while its saved state changes.
        YouTubeSelectionStatus.Text = $"{selected.Count} of {StreamsSession.MaxYouTubeChannels} live channels selected. Changes saved.";
        return include;
    }
}
