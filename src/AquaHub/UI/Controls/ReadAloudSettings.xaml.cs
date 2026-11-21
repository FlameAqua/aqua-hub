using System.Windows;
using System.Windows.Controls;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Speech;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Controls;

public partial class ReadAloudSettings : UserControl
{
    private bool _attached, _syncing, _installing;
    public Control BackendPicker => Backend;

    public ReadAloudSettings()
    {
        InitializeComponent();
        Voice.ItemsSource = KokoroSetup.Voices;
        Loaded += (_, _) => Observe();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) Observe(); else Detach(); };
    }

    private void Observe()
    {
        if (!_attached)
        {
            _attached = true;
            Hub.Core.Settings.Changed += OnSettingsChanged;
            Hub.Speech.StateChanged += Refresh;
        }
        Refresh();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        Hub.Core.Settings.Changed -= OnSettingsChanged;
        Hub.Speech.StateChanged -= Refresh;
    }

    private void OnSettingsChanged(HubSettings _) => Hub.OnUi(() => { if (_attached) Refresh(); });

    private void Refresh()
    {
        _syncing = true;
        Backend.SelectedValue = Hub.S.Ask.ReadAloudBackend;
        Voice.SelectedValue = Hub.S.Ask.ReadAloudVoice;
        _syncing = false;
        var kokoro = Hub.S.Ask.ReadAloudBackend == "kokoro";
        var installed = Hub.Speech.Kokoro.Installed() is not null;
        KokoroOptions.Visibility = kokoro ? Visibility.Visible : Visibility.Collapsed;
        Install.Visibility = KokoroSetup.Supported ? Visibility.Visible : Visibility.Collapsed;
        Install.Content = _installing ? "Cancel" : installed ? "Repair" : "Install";
        PackageStatus.Text = !KokoroSetup.Supported ? "This Kokoro build needs 64-bit Windows and AVX-512."
            : installed ? "Installed · English · Uses your processor"
            : $"English · {KokoroSetup.DownloadSize / 1_000_000d:0} MB from GitHub, Hugging Face and PyPI";
        var active = Hub.Speech.IsActive(SpeechService.Preview);
        PreviewButton.Content = active ? "Stop preview" : "Preview";
        PreviewButton.IsEnabled = active || !kokoro || installed && !_installing;
    }

    private void OnBackend(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !_attached || Backend.SelectedValue is not string backend || backend == Hub.S.Ask.ReadAloudBackend) return;
        if (Hub.Speech.IsActive(SpeechService.Preview)) Hub.Speech.Stop();
        Hub.Core.Settings.Update(s => s.Ask.ReadAloudBackend = backend);
    }

    private void OnVoice(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || !_attached || Voice.SelectedValue is not string id || id == Hub.S.Ask.ReadAloudVoice) return;
        if (Hub.Speech.IsActive(SpeechService.Preview)) Hub.Speech.Stop();
        Hub.Core.Settings.Update(s => s.Ask.ReadAloudVoice = id);
    }

    private void OnPreview(object sender, RoutedEventArgs e) => Hub.Speech.Toggle(SpeechService.Preview,
        "Hello from Aqua. This is your read-aloud voice.",
        Hub.S.Ask.ReadAloudBackend == "kokoro" ? "en" : Hub.S.Location.Language);

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_installing) { Hub.Speech.CancelInstall(); return; }
        var repair = Hub.Speech.Kokoro.Installed() is not null;
        _installing = true;
        Refresh();
        var notices = Hub.Windows.Main?.WorkspaceFeedback;
        notices?.Show("Downloading Kokoro…", "read-aloud-install-feedback", busy: true);
        try
        {
            await Hub.Speech.InstallAsync(new Progress<long>(bytes =>
            {
                if (_attached && _installing)
                    PackageStatus.Text = $"{Fmt.Bytes(bytes)} / {Fmt.Bytes(KokoroSetup.DownloadSize)}";
            }), repair);
            if (_attached && Hub.Speech.Kokoro.Installed() is not null)
                notices?.Show("Kokoro is ready to preview.", "read-aloud-install-feedback");
            else notices?.Hide("read-aloud-install-feedback");
        }
        catch (OperationCanceledException) { notices?.Hide("read-aloud-install-feedback"); }
        catch (Exception ex)
        {
            if (_attached) notices?.Show(ex is DownloadException or ReadAloudException ? ex.Message : "Kokoro couldn't be installed. Try again.",
                "read-aloud-install-feedback", error: true);
        }
        finally { _installing = false; if (_attached) Refresh(); }
    }
}
