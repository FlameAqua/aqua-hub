using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>Sensible first-run defaults: discovers well-known apps and creates starter scenes.</summary>
public static class FirstRun
{
    public static async Task EnsureDefaultsAsync()
    {
        var s = Hub.S;
        UpgradeScenes(s);
        var catalog = await Hub.Catalog.GetAppsAsync(); // also warms the catalog used to name media apps
        if (s.Apps.Count > 0 && s.Scenes.Count > 0) return;
        var apps = new List<AppEntry>();
        foreach (var (match, id, keywords, music) in AppLauncher.Suggested)
        {
            var found = catalog.FirstOrDefault(a => a.Name.Equals(match, StringComparison.OrdinalIgnoreCase))
                        ?? catalog.FirstOrDefault(a => a.Name.StartsWith(match, StringComparison.OrdinalIgnoreCase));
            if (found is null) continue;
            apps.Add(new AppEntry
            {
                Id = id,
                Name = found.Name,
                Kind = found.Kind is "uwp" ? "uwp" : "shortcut",
                Target = found.Target,
                ProcessName = GuessProcess(found, id),
                Keywords = keywords.ToList(),
                IsMusicPlayer = music,
                Pinned = apps.Count < 8,
            });
        }
        Hub.Core.Settings.Update(settings =>
        {
            if (settings.Apps.Count == 0) settings.Apps = apps;
            if (settings.Scenes.Count == 0) settings.Scenes = DefaultScenes(settings.Apps);
        });
        Log.Info("firstrun", $"Configured {apps.Count} apps and {Hub.S.Scenes.Count} scenes");
    }

    /// <summary>The starter "Wind down" scene promised to read the brief but had no step for it; add it to existing profiles.</summary>
    private static void UpgradeScenes(HubSettings s)
    {
        if (s.Scenes.FirstOrDefault(x => x.Id == "wind-down") is not { } wind) return;
        if (wind.Steps.Any(st => st.Action == "read-brief") || !wind.Description.Contains("brief", StringComparison.OrdinalIgnoreCase)) return;
        Hub.Core.Settings.Update(settings =>
        {
            if (settings.Scenes.FirstOrDefault(x => x.Id == "wind-down") is { } w && w.Steps.All(st => st.Action != "read-brief"))
                w.Steps.Add(new SceneStep { Action = "read-brief" });
        });
    }

    private static string GuessProcess(CatalogApp app, string id)
    {
        if (app.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(app.Target);
        return id switch
        {
            "vscode" => "Code",
            "edge" => "msedge",
            "teams" => "ms-teams",
            "terminal" => "WindowsTerminal",
            "calculator" => "CalculatorApp",
            "outlook" => "olk",
            _ => app.Name.Split(' ')[0],
        };
    }

    public static List<Scene> DefaultScenes(List<AppEntry> apps)
    {
        bool Has(string id) => apps.Any(a => a.Id == id);
        var focus = new Scene
        {
            Id = "focus", Name = "Focus", Icon = "focus", Description = "Silence notifications, lower the volume and open your editor.",
            Steps = { new() { Action = "dnd", Value = "on" }, new() { Action = "media", Value = "pause" }, new() { Action = "volume", Value = "25" } },
        };
        if (Has("vscode")) focus.Steps.Add(new SceneStep { Action = "launch", Target = "vscode" });

        var music = new Scene
        {
            Id = "music", Name = "Music time", Icon = "music", Description = "Start your music player and set a comfortable volume.",
            Steps = { new() { Action = "volume", Value = "45" } },
        };
        var player = apps.FirstOrDefault(a => a.IsMusicPlayer);
        if (player is not null) music.Steps.Insert(0, new SceneStep { Action = "launch", Target = player.Id });
        music.Steps.Add(new SceneStep { Action = "media", Value = "play" });

        var game = new Scene
        {
            Id = "game", Name = "Game mode", Icon = "game", Description = "Free the GPU (pause AI + unload the model) and hold notifications.",
            Steps = { new() { Action = "ai", Value = "pause" }, new() { Action = "dnd", Value = "on" } },
        };
        if (Has("steam")) game.Steps.Add(new SceneStep { Action = "launch", Target = "steam" });
        if (Has("discord")) game.Steps.Add(new SceneStep { Action = "launch", Target = "discord" });

        var wind = new Scene
        {
            Id = "wind-down", Name = "Wind down", Icon = "coffee", Description = "Pause media, resume AI, turn notifications back on and read the evening brief.",
            Steps = { new() { Action = "media", Value = "pause" }, new() { Action = "ai", Value = "resume" }, new() { Action = "dnd", Value = "off" }, new() { Action = "open-page", Value = "today" }, new() { Action = "read-brief" } },
        };
        return new List<Scene> { focus, music, game, wind };
    }
}
