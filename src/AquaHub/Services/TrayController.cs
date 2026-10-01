using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.UI;
using AquaHub.UI.Controls;
using AquaHub.UI.ViewModels;

namespace AquaHub.Services;

/// <summary>
/// Tray icon behaviour: a glanceable multi-line tooltip, the dynamic badge/trend icon and the right-click menu.
/// Left-click toggles the quick panel, double-click opens the dashboard, middle-click plays/pauses media.
/// </summary>
public sealed class TrayController
{
    private readonly UiThrottle _update;
    private IntPtr _icon;
    private string _iconKey = "";

    /// <param name="initialIcon">The plain icon the tray was created with; this controller owns (and frees) it.</param>
    public TrayController(IntPtr initialIcon)
    {
        _icon = initialIcon;
        _iconKey = "False|False|0";
        _update = new UiThrottle(Update, 800);
        Hub.State.Changed += topic =>
        {
            if (topic is Topics.Alerts or Topics.Markets or Topics.Weather or Topics.Ai or Topics.Events) _update.Request();
        };
        Hub.Core.Settings.Changed += _ => _update.Request();
        Hub.Media.Changed += () => _update.Request();
        Hub.Tray.RightClick += (_, _) => Hub.OnUi(ShowMenu);
    }

    public void Update()
    {
        var idx = Hub.S.Markets.Indices.FirstOrDefault(i => Hub.State.Quotes.ContainsKey(i.Symbol));
        Hub.Tray.Update(RefreshIcon(idx), Tooltip(idx));
    }

    /// <summary>
    /// Windows shows up to 127 characters; lines are added in order of importance and the rest dropped.
    /// </summary>
    private static string Tooltip(WatchSymbol? idx)
    {
        var lines = new List<string> { "Aqua Hub" };
        var glance = new List<string>();
        if (Hub.State.Weather?.Now is { } w) glance.Add($"{Fmt.Temp(w.Temp)} {Core.Sources.WeatherSource.Describe(w.Code)}");
        if (idx is not null)
        {
            var q = Hub.State.Quotes[idx.Symbol];
            var close = MarketSession.LastClose(q, DateTimeOffset.Now);
            var when = q.Kind is InstrumentKind.Crypto or InstrumentKind.Fx || MarketSession.IsLive(q) ? ""
                : close == "today's close" ? " at close" : " (" + close.Replace(" close", "").Replace("'s", "") + ")";
            glance.Add($"{q.Name} {Fmt.Pct(q.ChangePercent, 1)}{when}");
        }
        if (glance.Count > 0) lines.Add(string.Join(" · ", glance));

        var status = new List<string>();
        var unread = Hub.State.UnreadAlerts;
        if (unread > 0) status.Add(Plural.Of(unread, "new alert"));
        if (Hub.S.Notifications.DoNotDisturb) status.Add("Do not disturb");
        if (Hub.Core.Llm.UserPaused) status.Add("AI paused");
        if (status.Count > 0) lines.Add(string.Join(" · ", status));

        var next = Hub.State.Events.FirstOrDefault(e => e.Start > DateTimeOffset.Now && e.Start < DateTimeOffset.Now.AddHours(12));
        if (next is not null) lines.Add($"Next: {Shorten(next.Title, 40)}, {Fmt.EventWhen(next, Hub.S.General.Use24Hour).Replace("Today · ", "")}");
        if (Hub.Media.Current is { IsPlaying: true } m) lines.Add($"▶ {Shorten(m.Title, 44)}");

        var text = lines[0];
        foreach (var line in lines.Skip(1))
            if (text.Length + 1 + line.Length <= 127) text += "\n" + line;
        return text;
    }

    private IntPtr? RefreshIcon(WatchSymbol? idx)
    {
        var unread = Hub.State.UnreadAlerts;
        var paused = Hub.Core.Llm.UserPaused;
        // Optional market "ticker" on the icon itself: the first index's direction today.
        var trend = 0;
        if (Hub.S.General.ShowTaskbarTicker && idx is not null)
        {
            var change = Hub.State.Quotes[idx.Symbol].ChangePercent;
            trend = change >= 0.1 ? 1 : change <= -0.1 ? -1 : 0;
        }
        var key = $"{unread > 0}|{paused}|{trend}";
        if (key == _iconKey) return null;
        var old = _icon;
        _icon = IconFactory.CreateTrayIcon(unread > 0 ? Color.FromRgb(0xFF, 0x6B, 0x7A) : null, paused, trend);
        _iconKey = key;
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
        return _icon;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    private ContextMenu? _menu;

    public void ShowMenu()
    {
        // One menu at a time: another right-click (or --tray-menu) replaces the open one.
        if (_menu is { IsOpen: true } previous) previous.IsOpen = false;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _menu = menu;
        System.Windows.Automation.AutomationProperties.SetAutomationId(menu, "tray-menu");
        MenuItem Item(ItemsControl parent, string header, string icon, Action action, string? gesture = null, string? id = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            UiProps.SetIcon(mi, icon);
            if (id is not null) System.Windows.Automation.AutomationProperties.SetAutomationId(mi, id);
            mi.Click += (_, _) => action();
            parent.Items.Add(mi);
            return mi;
        }

        Item(menu, "Open Aqua Hub", "home", () => Hub.Windows.ShowMain(), "Double-click", "tray-open");
        Item(menu, "Quick panel", "launchpad", () => Hub.Windows.ShowFlyout(), Hub.S.General.HotkeyFlyout, "tray-flyout");
        Item(menu, "Ask or command…", "ask", () => Hub.Windows.ShowPalette(), Hub.S.General.HotkeyPalette, "tray-palette");
        Item(menu, "Ask about my screen", "screenshot", () => _ = Hub.Ask.AskAboutScreenAsync(), id: "tray-ask-screen");
        var unread = Hub.State.UnreadAlerts;
        if (unread > 0) Item(menu, $"Show {Plural.Of(unread, "new alert")}", "bell", () => Hub.Windows.ShowAlerts(), id: "tray-alerts");

        if (Hub.Media.Current is { } media)
        {
            menu.Items.Add(new Separator());
            Item(menu, $"{(media.IsPlaying ? "Pause" : "Play")} · {Shorten(media.Title, 34)}", media.IsPlaying ? "pause" : "play",
                () => _ = Hub.Media.PlayPauseAsync(), "Middle-click", "tray-media-toggle");
            if (media.CanNext) Item(menu, "Next track", "next", () => _ = Hub.Media.NextAsync(), id: "tray-media-next");
        }

        menu.Items.Add(new Separator());
        if (Hub.S.Scenes.Count > 0)
        {
            var scenes = new MenuItem { Header = "Run a scene" };
            UiProps.SetIcon(scenes, "wand");
            System.Windows.Automation.AutomationProperties.SetAutomationId(scenes, "tray-scenes");
            foreach (var scene in Hub.S.Scenes)
                Item(scenes, scene.Name, IconData.Get(scene.Icon) is null ? "wand" : scene.Icon, () => _ = RunScene(scene), id: "tray-scene-" + scene.Id);
            menu.Items.Add(scenes);
        }
        Item(menu, "Read my brief", "volume", () => _ = Hub.Actions.ReadBriefAsync(), id: "tray-read-brief");
        Item(menu, "Refresh everything", "refresh", () => _ = Hub.Actions.ExecuteAsync(new HubCommand("refresh")), id: "tray-refresh");
        var dnd = Hub.S.Notifications.DoNotDisturb;
        Item(menu, dnd ? "Turn notifications on" : "Do not disturb", dnd ? "bell" : "bell-off",
            () => _ = Hub.Actions.ExecuteAsync(new HubCommand(dnd ? "dnd_off" : "dnd_on")), id: "tray-dnd");
        var paused = Hub.Core.Llm.UserPaused;
        Item(menu, paused ? "Resume AI" : "Pause AI & free VRAM", "cube",
            () => _ = Hub.Actions.ExecuteAsync(new HubCommand(paused ? "resume_ai" : "pause_ai")).ContinueWith(_ => Hub.OnUi(Update)), id: "tray-ai");
        if (OllamaManager.IsLocalOllama && OllamaManager.Installed && !Hub.Ollama.Transitioning)
        {
            var on = Hub.Ollama.Running == true;
            Item(menu, on ? "Turn off Ollama (free memory)" : "Turn on Ollama", "power",
                () => _ = on ? Hub.Ollama.TurnOffAsync() : Hub.Ollama.TurnOnAsync(), id: "tray-ollama");
        }
        menu.Items.Add(new Separator());
        Item(menu, "Settings", "settings", () => Hub.Windows.ShowMain("settings"), id: "tray-settings");
        Item(menu, "Quit Aqua Hub", "power", Hub.Quit, id: "tray-quit");

        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; };
        // The menu needs its app in the foreground to keep the mouse (and close when you click elsewhere), as Windows
        // asks of notification-area menus. A right-click on the icon allows that; --tray-menu from another process
        // doesn't, so switch first. Activating the menu's own popup instead would take the mouse back and close it.
        WindowEffects.ForceForeground(Hub.Tray.Handle);
        menu.IsOpen = true;
    }

    private static async Task RunScene(Scene scene)
    {
        try { await Hub.Actions.RunSceneAsync(scene); }
        catch (Exception ex) { Log.Warn("scene", $"Scene {scene.Name} failed", ex); }
    }
}
