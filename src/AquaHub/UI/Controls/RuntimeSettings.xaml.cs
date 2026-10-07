using System.Windows;
using System.Windows.Controls;
using AquaHub.Core.Ai.Local;
using AquaHub.Services;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Controls;

public partial class RuntimeSettings : UserControl
{
    public event Action? UseRequested;
    public event Action? SaveRequested;
    public bool WantsRuntime { get; set; }
    private bool _busy;
    private CancellationTokenSource? _devices;

    public RuntimeSettings()
    {
        InitializeComponent();
        AddHandler(LostKeyboardFocusEvent, new System.Windows.Input.KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TextBox) Dispatcher.BeginInvoke(() => SaveRequested?.Invoke());
        }));
        Loaded += (_, _) =>
        {
            Hub.LocalAi.Changed += Update;
            Hub.LocalAi.InstallProgressChanged += OnProgress;
            BuildChoice.ItemsSource = Hub.LocalAi.Builds;
            BuildChoice.SelectedItem = Hub.LocalAi.RecommendedBuild;
            Update();
        };
        Unloaded += (_, _) =>
        {
            Hub.LocalAi.Changed -= Update;
            Hub.LocalAi.InstallProgressChanged -= OnProgress;
            _devices?.Cancel();
        };
    }

    public void Update()
    {
        var ai = Hub.LocalAi;
        var build = BuildChoice.SelectedItem as LlamaBuild ?? ai.RecommendedBuild;
        Status.Text = ai.Build == "custom" ? "Using your own llama-server: " + Hub.S.Ai.Llama.ServerPath
            : ai.RuntimeReady ? $"llama.cpp {ai.Build} · {ai.Builds.FirstOrDefault(b => b.Backend == ai.Installed?.Backend)?.Label ?? ai.Installed?.Backend} · {ai.Gpu.Label}"
            : $"Aqua's runtime isn't installed. Install downloads llama.cpp {LlamaCatalog.Build} for {build.Label}, {Fmt.Bytes(build.Size)}, from llama.cpp's GitHub releases. Each file is checked before it's used.";
        InstallButton.Visibility = !ai.RuntimeReady ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !_busy && !ai.Installing;
        OtherBuilds.Visibility = !ai.RuntimeReady ? Visibility.Visible : Visibility.Collapsed;
        OtherBuilds.IsEnabled = !_busy && !ai.Installing;
        UseButton.Visibility = ai.RuntimeReady && !ai.InUse ? Visibility.Visible : Visibility.Collapsed;
        DevicesButton.Visibility = ai.RuntimeReady ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = ai.Installed is not null && ai.Build != "custom" ? Visibility.Visible : Visibility.Collapsed;
        UseButton.IsEnabled = DevicesButton.IsEnabled = RemoveButton.IsEnabled = !_busy && !ai.Installing;
        ProgressPanel.Visibility = ai.Installing ? Visibility.Visible : Visibility.Collapsed;
        if (ai.Installing && ai.InstallProgress is { } p) OnProgress(p);
    }

    private void OnBuildChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Update(); }
    private void OnProgress(LlamaProgress p)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = $"{p.Step} · {Fmt.Bytes(p.Done)} / {Fmt.Bytes(p.Total)}";
        Progress.IsIndeterminate = p.Total <= 0;
        Progress.Value = p.Total > 0 ? Math.Clamp(100d * p.Done / p.Total, 0, 100) : 0;
    }
    private void OnModels(object sender, RoutedEventArgs e) => Hub.Windows.ShowMain("models");
    private void OnUse(object sender, RoutedEventArgs e) => UseRequested?.Invoke();
    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        SaveRequested?.Invoke();
        await Run(async () =>
        {
            var result = await Hub.LocalAi.InstallRuntimeAsync(BuildChoice.SelectedItem as LlamaBuild);
            if (result is not null && WantsRuntime) UseRequested?.Invoke();
            if (Sandbox.Enabled) ErrorText.Text = "Installation recorded in the test journal.";
        });
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Hub.LocalAi.CancelInstall();
    private async void OnDevices(object sender, RoutedEventArgs e)
    {
        _devices?.Dispose(); _devices = new CancellationTokenSource();
        await Run(async () =>
        {
            DeviceText.Text = "Checking devices…";
            var devices = await Hub.LocalAi.DevicesAsync(_devices.Token);
            DeviceText.Text = devices.Count == 0 ? "It will run on the processor only. Is the graphics driver up to date?" : string.Join(Environment.NewLine, devices);
        });
    }
    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Remove Aqua's runtime? Your models are kept.", "Remove runtime", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(() => Hub.LocalAi.RemoveRuntimeAsync());
    }
    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; ErrorText.Text = ""; Update();
        try { await action(); }
        catch (OperationCanceledException) { ErrorText.Text = "Stopped."; DeviceText.Text = ""; }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { _busy = false; Update(); }
    }
}
