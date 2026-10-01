using System.Globalization;
using System.Text;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>
/// Executes allow-listed hub actions (from the command palette, flyout, scenes or the command agent).
/// Every target is resolved against user configuration — nothing here executes model-provided paths or URLs.
/// </summary>
public sealed class ActionExecutor
{
    public async Task<string> ExecuteAsync(HubCommand cmd)
    {
        var s = Hub.S;
        switch (cmd.Action)
        {
            case "launch_app":
                {
                    var app = s.Apps.FirstOrDefault(a => a.Id == cmd.Target);
                    if (app is null) return "I couldn't find that app.";
                    return Hub.Launcher.Launch(app) ? $"Opening {app.Name}" : $"Couldn't start {app.Name}";
                }
            case "close_app":
                {
                    var app = s.Apps.FirstOrDefault(a => a.Id == cmd.Target);
                    if (app is null) return "I couldn't find that app.";
                    var n = Hub.Launcher.Close(app);
                    return n > 0 ? $"Asked {app.Name} to close" : $"{app.Name} isn't running";
                }
            case "media_play":
                if (Hub.Media.HasSession) await Hub.Media.PlayAsync();
                else await WakeMusicAsync();
                return "Playing";
            case "media_pause":
                // Without a session the only tool is the play/pause key, which would *start* playback — so do nothing
                // (dry-run tests still see that the step ran).
                if (!Hub.Media.HasSession)
                {
                    Sandbox.Record("media", "pause skipped: nothing playing");
                    return "Nothing is playing";
                }
                await Hub.Media.PauseAsync();
                return "Paused";
            case "media_toggle":
                if (Hub.Media.HasSession) await Hub.Media.PlayPauseAsync(); else MediaController.SendMediaKey();
                return "Play/pause";
            case "media_next":
                if (Hub.Media.HasSession) await Hub.Media.NextAsync(); else MediaController.SendMediaKey(0xB0);
                return "Next track";
            case "media_previous":
                if (Hub.Media.HasSession) await Hub.Media.PreviousAsync(); else MediaController.SendMediaKey(0xB1);
                return "Previous track";
            case "volume_set":
                if (int.TryParse(cmd.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    Hub.Volume.SetVolume(v);
                    return $"Volume {Math.Clamp(v, 0, 100)}%";
                }
                return "Which volume level?";
            case "volume_up":
                Hub.Volume.Step(+10);
                return $"Volume {Hub.Volume.GetVolume()}%";
            case "volume_down":
                Hub.Volume.Step(-10);
                return $"Volume {Hub.Volume.GetVolume()}%";
            case "mute_toggle":
                Hub.Volume.ToggleMute();
                return Hub.Volume.IsMuted() == true ? "Muted" : "Unmuted";
            case "run_scene":
                {
                    var scene = s.Scenes.FirstOrDefault(x => x.Id == cmd.Target);
                    if (scene is null) return "I couldn't find that scene.";
                    await RunSceneAsync(scene);
                    return $"{scene.Name} is set";
                }
            case "open_page":
                Hub.Windows.ShowMain(cmd.Target);
                return "";
            case "open_ticker":
                Hub.Windows.ShowMain("markets", cmd.Target);
                return "";
            case "music_search":
                return Hub.Launcher.MusicSearch(cmd.Target, s.Apps) ? $"Searching for “{cmd.Target}”" : "No music app configured";
            case "refresh":
                Hub.Core.Agents.RunAll(a => a.Role is "collector" or "keeper");
                return "Refreshing all sources";
            case "show_brief":
                Hub.Windows.ShowMain("today");
                return "";
            case "read_brief":
                await ReadBriefAsync();
                return "Reading your brief";
            case "dnd_on":
                Hub.Core.Settings.Update(x => x.Notifications.DoNotDisturb = true);
                return "Do not disturb is on";
            case "dnd_off":
                Hub.Core.Settings.Update(x => x.Notifications.DoNotDisturb = false);
                return "Notifications are back on";
            case "pause_ai":
                Hub.Core.Llm.UserPaused = true;
                await UnloadModelAsync();
                await Hub.Core.Llm.CheckAsync();
                return "AI paused — VRAM freed";
            case "resume_ai":
                Hub.Core.Llm.UserPaused = false;
                await Hub.Core.Llm.CheckAsync();
                Hub.Core.Agents.RunAll(a => a.UsesAi);
                return "AI resumed";
            case "summarize_clipboard":
                {
                    var text = ClipboardText();
                    if (string.IsNullOrWhiteSpace(text)) return "The clipboard has no text.";
                    Hub.Windows.ShowMain("ask", "@clipboard");
                    return "";
                }
            case "ask":
                Hub.Windows.ShowMain("ask", cmd.Target);
                return "";
            default:
                return "Sorry — I can't do that yet.";
        }
    }

    private async Task WakeMusicAsync()
    {
        var player = Hub.S.Apps.FirstOrDefault(a => a.IsMusicPlayer);
        if (player is not null && !Hub.Launcher.IsRunning(player))
        {
            Hub.Launcher.Launch(player);
            await Task.Delay(3500);
        }
        // No music app in the Launchpad: Windows' default one (Media Player unless you chose another).
        else if (player is null && DefaultApps.For("music") is { } music && !DefaultApps.IsRunning(music) && DefaultApps.Launch(music))
            await Task.Delay(3500);
        MediaController.SendMediaKey();
    }

    /// <summary>Frees VRAM by unloading the local model (journaled in dry-run mode).</summary>
    public static async Task UnloadModelAsync()
    {
        if (Sandbox.Intercept("unload-model")) return;
        await Hub.Core.Llm.UnloadAsync();
    }

    public static string? ClipboardText()
    {
        if (Sandbox.Enabled) return Sandbox.ClipboardFixture;
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
        catch { return null; }
    }

    public async Task ReadBriefAsync()
    {
        if (Hub.Speech.IsSpeaking) { Hub.Speech.Stop(); return; }
        var b = Hub.State.Brief;
        if (b is null) return;
        var sb = new StringBuilder();
        sb.Append(b.Summary).Append(' ');
        foreach (var sec in b.Sections)
        {
            sb.Append(sec.Title).Append(". ");
            foreach (var bullet in sec.Bullets) sb.Append(bullet.TrimEnd('.')).Append(". ");
        }
        await Hub.Speech.SpeakAsync(sb.ToString(), Hub.S.Location.Language);
    }

    public async Task RunSceneAsync(Scene scene, IProgress<string>? progress = null)
    {
        var s = Hub.S;
        foreach (var step in scene.Steps)
        {
            try
            {
                progress?.Report(Describe(step, s));
                switch (step.Action)
                {
                    case "launch":
                        if (s.Apps.FirstOrDefault(a => a.Id == step.Target) is { } la) Hub.Launcher.Launch(la);
                        break;
                    case "close":
                        if (s.Apps.FirstOrDefault(a => a.Id == step.Target) is { } ca) Hub.Launcher.Close(ca);
                        break;
                    case "focus":
                        if (s.Apps.FirstOrDefault(a => a.Id == step.Target) is { } fa) Hub.Launcher.Focus(fa);
                        break;
                    case "media":
                        await ExecuteAsync(new HubCommand(step.Value switch
                        {
                            "play" => "media_play", "pause" => "media_pause", "next" => "media_next", "previous" => "media_previous", _ => "media_toggle",
                        }));
                        break;
                    case "volume":
                        if (int.TryParse(step.Value, out var vol)) Hub.Volume.SetVolume(vol);
                        break;
                    case "mute":
                        if ((Hub.Volume.IsMuted() == true) != (step.Value == "true")) Hub.Volume.ToggleMute();
                        break;
                    case "dnd":
                        Hub.Core.Settings.Update(x => x.Notifications.DoNotDisturb = step.Value is "on" or "true");
                        break;
                    case "ai":
                        await ExecuteAsync(new HubCommand(step.Value == "pause" ? "pause_ai" : "resume_ai"));
                        break;
                    case "open-url":
                        AppLauncher.OpenUrl(step.Value);
                        break;
                    case "open-page":
                        Hub.Windows.ShowMain(step.Value);
                        break;
                    case "wait":
                        if (double.TryParse(step.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
                            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(secs, 0, 30)));
                        break;
                    case "music-search":
                        Hub.Launcher.MusicSearch(step.Value, s.Apps);
                        break;
                    case "read-brief":
                        await ReadBriefAsync();
                        break;
                }
                await Task.Delay(250);
            }
            catch (Exception ex)
            {
                Log.Warn("scene", $"Step {step.Action} failed", ex);
            }
        }
        Log.Info("scene", $"Ran scene {scene.Name}");
    }

    public static string Describe(SceneStep step, HubSettings s)
    {
        string App(string id) => s.Apps.FirstOrDefault(a => a.Id == id)?.Name ?? id;
        return step.Action switch
        {
            "launch" => $"Open {App(step.Target)}",
            "close" => $"Close {App(step.Target)}",
            "focus" => $"Focus {App(step.Target)}",
            "media" => step.Value switch { "play" => "Play music", "pause" => "Pause media", "next" => "Next track", _ => "Play/pause" },
            "volume" => $"Volume {step.Value}%",
            "mute" => step.Value == "true" ? "Mute" : "Unmute",
            "dnd" => step.Value is "on" or "true" ? "Do not disturb on" : "Do not disturb off",
            "ai" => step.Value == "pause" ? "Pause AI & free VRAM" : "Resume AI",
            "open-url" => $"Open {step.Value}",
            "open-page" => $"Show {step.Value}",
            "wait" => $"Wait {step.Value}s",
            "music-search" => $"Play “{step.Value}”",
            "read-brief" => "Read the brief aloud",
            _ => step.Action,
        };
    }
}
