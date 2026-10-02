using System.Text;
using System.Windows;
using System.Windows.Threading;
using AquaHub.Core;
using AquaHub.Core.Ai;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.Services;

namespace AquaHub;

/// <param name="DemoUpdate">--demo-update [ready]: a pretend release for trying the update banner (Debug builds and E2E dry runs).</param>
public sealed record AppArgs(bool Background, string? Snapshot, int SnapshotWait, string? SnapshotPages, string? DataDir, string? Page,
    bool E2e, string? Command, bool Measure = false, string? DemoUpdate = null)
{
    public static AppArgs Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        return new AppArgs(
            args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase)),
            Value("--snapshot"),
            int.TryParse(Value("--wait"), out var w) ? w : 240,
            Value("--pages"),
            Value("--data-dir"),
            Value("--page"),
            args.Any(a => a.Equals("--e2e", StringComparison.OrdinalIgnoreCase)) || Environment.GetEnvironmentVariable("AQUAHUB_E2E") == "1",
            SingleInstance.Commands.FirstOrDefault(c => args.Any(a => a.Equals("--" + c, StringComparison.OrdinalIgnoreCase))),
            args.Any(a => a.Equals("--measure", StringComparison.OrdinalIgnoreCase)),
            args.Any(a => a.Equals("--demo-update", StringComparison.OrdinalIgnoreCase))
                ? Value("--demo-update") is { } stage && stage.Equals("ready", StringComparison.OrdinalIgnoreCase) ? "ready" : "available"
                : null);
    }
}

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayController? _trayController;
    private GeneralSettings? _lastGeneral;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = AppArgs.Parse(e.Args);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        UseConsistentMonthNames();
        var paths = HubPaths.Resolve(args.DataDir);

        if (args.Snapshot is null)
        {
            _single = new SingleInstance("AquaHub", paths.Root);
            if (!_single.IsFirst)
            {
                // Already running for this profile: forward the request (e.g. --flyout from a desktop shortcut or
                // a jump-list task, --page markets from a script).
                _single.Signal(args.Command ?? (args.Page is { } page && SingleInstance.Pages.Contains(page) ? "page-" + page : "activate"));
                Shutdown();
                return;
            }
        }
        if (args.E2e) Sandbox.Enable(paths.Root);
        Autostart.UseProfile(paths.Root, isDefault: args.DataDir is null && Environment.GetEnvironmentVariable("AQUAHUB_HOME") is null);

        SessionEnding += (_, _) => Hub.MarkQuitting();
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("ui", "Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        // The process ends after this: written at once, not queued for the background writer.
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Fatal("app", "Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Warn("app", "Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        Hub.Ui = Dispatcher;
        Hub.SnapshotMode = args.Snapshot is not null;
        ISecretStore secrets = Hub.SnapshotMode || Sandbox.Enabled ? new InMemorySecretStore() : new CredentialVault();
        Hub.Platform = new PlatformServices(() => Hub.Core.Settings.Current);
        Hub.Core = new HubCore(paths, secrets, Hub.Platform);
        Hub.Core.Llm.PauseReason = () => Hub.Platform.AiPauseReason();

        Hub.Theme = new ThemeService();
        Hub.Theme.Apply(Hub.S.General);
        UI.AccessibleNames.Register();
        Hub.Launcher = new AppLauncher();
        Hub.Catalog = new AppCatalog();
        MediaController.CatalogLookup = Hub.Catalog.NameFor;
        Hub.Volume = new VolumeController();
        Hub.Media = new MediaController();
        Hub.Speech = new SpeechService();
        Hub.Actions = new ActionExecutor();
        Hub.System = new SystemMonitor();
        Hub.Windows = new WindowManager();
        Hub.Ask = new AskSession();
        Hub.Ollama = new OllamaManager();
        Hub.Updates = new UpdateService(Sandbox.Enabled, args.DataDir is null ? Array.Empty<string>() : new[] { "--data-dir", paths.Root },
            args.DemoUpdate);
        // A version downloaded earlier ("Later") installs now, before anything opens: Velopack's updater waits for this
        // process to end, swaps versions and starts the new one (in the background again if that's how we started).
        if (!Hub.SnapshotMode && Hub.Updates.ApplyPendingAtStartup(args.Background))
        {
            Shutdown();
            return;
        }
        Img.Init(paths.ImageCache, Hub.Core.Http, () => Hub.S.Privacy.LoadRemoteImages);
        Hub.System.Sampled += snap =>
        {
            Hub.Platform.RecordGpu(snap.Gpu?.Utilization ?? 0, Hub.Core.Llm.IsBusy);
            Hub.State.SetSystem(snap);
        };
        _ = FirstRun.EnsureDefaultsAsync();

        if (Hub.SnapshotMode)
        {
            WindowEffects.ForceOpaque = true;
            _ = args.Measure
                ? SnapshotRunner.MeasureAsync(args.Snapshot!, args.SnapshotWait)
                : SnapshotRunner.RunAsync(args.Snapshot!, args.SnapshotWait, args.SnapshotPages, scratchProfile: args.DataDir is not null);
            return;
        }

        // Tray, hotkeys and OS integration
        Hub.Tray = new TrayIcon();
        Hub.Platform.AttachTray(Hub.Tray);
        Hub.Hotkeys = new HotkeyManager(Hub.Tray);
        UI.Controls.HotkeyBox.Listening += PauseHotkeys;
        var firstIcon = IconFactory.CreateTrayIcon();
        Hub.Tray.Show(firstIcon, IconFactory.CreateLargeIcon(), "Aqua Hub");
        Hub.Tray.LeftClick += (_, _) => Hub.OnUi(() => Hub.Windows.ToggleFlyout(fromTrayClick: true));
        Hub.Tray.DoubleClick += () => Hub.OnUi(() => Hub.Windows.ShowMain());
        Hub.Tray.MiddleClick += () => _ = Hub.Media.PlayPauseAsync();
        // A Windows notification opens what it was about (and marks that alert read); one without an alert opens Aqua.
        Hub.Platform.ToastClicked += alert => Hub.OnUi(() => Hub.Windows.OpenAlert(alert));
        Hub.Tray.BalloonClicked += () => Hub.OnUi(() => Hub.Windows.ShowMain());
        // Any of several notifications could have been clicked: show them all (the alerts list) rather than guess.
        Hub.Tray.SeveralClicked += () => Hub.OnUi(() => Hub.Windows.OpenAlert(null));
        Hub.Tray.SettingChanged += area =>
        {
            if (area is "ImmersiveColorSet" && Hub.S.General.Theme == "system") Hub.OnUi(() => Hub.Theme.Apply(Hub.S.General));
        };
        _trayController = new TrayController(firstIcon);
        _trayController.Update();
        _single!.Listen(cmd => Hub.OnUi(() => HandleCommand(cmd)));
        TaskbarIntegration.ApplyJumpList(Autostart.ProfileArgs);
        RegisterHotkeys();
        _lastGeneral = Hub.S.General;
        Hub.Core.Settings.Changed += s => Hub.OnUi(() => OnSettingsChanged(s));
        Autostart.Sync(Hub.S.General.LaunchAtStartup, installed: Hub.Updates.IsInstalled);

        _ = Hub.Media.InitAsync();
        Hub.System.Start(TimeSpan.FromSeconds(20));
        Hub.Core.Start();
        Hub.Windows.StartIdleTrim();
        Hub.Ollama.Start();
        Hub.Updates.Start();
        // Asking something (or a scheduled brief) may start a stopped local server, if Settings allow it.
        Hub.Core.Llm.StartServer = ct => Hub.S.Ai.StartOllamaOnDemand && OllamaPolicy.Applies(Hub.S.Ai)
            ? Hub.Ui.InvokeAsync(() => Hub.Ollama.StartAsync("you asked for AI", ct)).Task.Unwrap()
            : Task.FromResult(false);

        if (!args.Background || Hub.Core.Settings.IsFirstRun) Hub.Windows.ShowMain(args.Page);
        if (args.Command is { } command and not "activate") Dispatcher.BeginInvoke(() => HandleCommand(command), DispatcherPriority.ApplicationIdle);
        Log.Info("app", $"Aqua Hub started{(Sandbox.Enabled ? " (dry-run E2E mode)" : "")}");
    }

    /// <summary>English (Ireland/UK) abbreviates September as "Sept", which looks odd beside "Oct" in compact dates.</summary>
    private static void UseConsistentMonthNames()
    {
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
        if (culture.TwoLetterISOLanguageName != "en") return;
        var invariant = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat;
        culture.DateTimeFormat.AbbreviatedMonthNames = invariant.AbbreviatedMonthNames;
        culture.DateTimeFormat.AbbreviatedMonthGenitiveNames = invariant.AbbreviatedMonthGenitiveNames;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
    }

    private void HandleCommand(string command)
    {
        switch (command)
        {
            case "flyout": Hub.Windows.ToggleFlyout(); break;
            case "palette": Hub.Windows.TogglePalette(); break;
            case "tray-menu": _trayController?.ShowMenu(); break;
            case "read-brief": _ = Hub.Actions.ReadBriefAsync(); break;
            case var page when page.StartsWith("page-", StringComparison.Ordinal): Hub.Windows.ShowMain(page[5..]); break;
            case "quit": Hub.Quit(); break;
            default: Hub.Windows.ShowMain(); break;
        }
    }

    private void OnSettingsChanged(HubSettings s)
    {
        var g = s.General;
        if (_lastGeneral is null || _lastGeneral.Theme != g.Theme || _lastGeneral.Accent != g.Accent) Hub.Theme.Apply(g);
        if (_lastGeneral is null || _lastGeneral.HotkeyFlyout != g.HotkeyFlyout || _lastGeneral.HotkeyPalette != g.HotkeyPalette) RegisterHotkeys();
        if (_lastGeneral is null || _lastGeneral.LaunchAtStartup != g.LaunchAtStartup) Autostart.Set(g.LaunchAtStartup);
        _lastGeneral = g;
    }

    public static List<string> HotkeyErrors { get; } = new();

    /// <summary>Alternatives tried (in order) when a default hotkey is already taken by another app.</summary>
    private static readonly Dictionary<string, string[]> HotkeyFallbacks = new()
    {
        ["Ctrl+Alt+H"] = new[] { "Ctrl+Alt+H", "Ctrl+Alt+Q", "Win+Alt+H" },
        ["Ctrl+Alt+Space"] = new[] { "Ctrl+Alt+Space", "Ctrl+Alt+K", "Ctrl+Alt+J", "Win+Alt+K" },
    };

    private static bool _hotkeysPaused;

    /// <summary>While a shortcut box is listening, Aqua lets go of its own shortcuts so pressing them records them.</summary>
    private static void PauseHotkeys(bool pause)
    {
        if (_hotkeysPaused == pause) return;
        _hotkeysPaused = pause;
        if (pause) Hub.Hotkeys.Clear();
        else RegisterHotkeys();
    }

    private static void RegisterHotkeys()
    {
        if (_hotkeysPaused) return;
        HotkeyErrors.Clear();
        Hub.Hotkeys.Clear();
        var g = Hub.S.General;
        var flyout = RegisterWithFallback(g.HotkeyFlyout, () => Hub.OnUi(Hub.Windows.ToggleFlyout));
        var palette = RegisterWithFallback(g.HotkeyPalette, () => Hub.OnUi(Hub.Windows.TogglePalette));
        // If a default had to move, persist the working combination so every hint in the UI is accurate.
        if ((flyout is not null && flyout != g.HotkeyFlyout) || (palette is not null && palette != g.HotkeyPalette))
        {
            Hub.Core.Settings.Update(s =>
            {
                if (flyout is not null) s.General.HotkeyFlyout = flyout;
                if (palette is not null) s.General.HotkeyPalette = palette;
            });
        }
        foreach (var err in HotkeyErrors) Log.Warn("hotkeys", err);
    }

    /// <returns>The gesture that was registered, or null if none could be.</returns>
    private static string? RegisterWithFallback(string gesture, Action action)
    {
        var candidates = HotkeyFallbacks.TryGetValue(gesture, out var list) ? list : new[] { gesture };
        string? firstError = null;
        foreach (var candidate in candidates)
        {
            var error = Hub.Hotkeys.Register(candidate, action);
            if (error is null)
            {
                if (candidate != gesture) Log.Info("hotkeys", $"{gesture} is taken by another app — using {candidate} instead");
                return candidate;
            }
            firstError ??= error;
        }
        HotkeyErrors.Add(firstError ?? $"Couldn't register {gesture}");
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Hub.Speech?.Stop();
            Hub.Ollama?.OnAppExit();
            Hub.Hotkeys?.Dispose();
            Hub.Tray?.Dispose();
            Hub.System?.Dispose();
            Hub.Core?.Dispose();
            _single?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
