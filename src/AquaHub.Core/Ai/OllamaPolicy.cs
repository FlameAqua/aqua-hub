using AquaHub.Core.Settings;

namespace AquaHub.Core.Ai;

public enum OllamaAction { None, StopIdle, StopForGpu, StartAfterGpu }

/// <summary>What Aqua observed about the local Ollama server at one check.</summary>
public sealed record OllamaObservation(
    bool Running,
    bool Busy,
    DateTimeOffset IdleSince,
    string? GpuContention,
    bool StoppedForGpu,
    DateTimeOffset? ContentionClearSince,
    bool UserKeepsOn = false,
    bool UserTurnedOff = false);

/// <summary>
/// When Aqua stops or restarts a local Ollama server. Pure decision logic (the app does the process work):
/// a game or another GPU-heavy app stops it and it comes back two minutes after the GPU frees up; with no model
/// loaded and no requests for the configured time it stops to give its memory back. Remote endpoints are never touched.
/// </summary>
public static class OllamaPolicy
{
    public static readonly TimeSpan RestartDelay = TimeSpan.FromMinutes(2);

    public static bool Applies(AiSettings s) =>
        s.Enabled && s.ManageOllama && s.Provider == "ollama" &&
        Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var u) && u.IsLoopback;

    public static OllamaAction Decide(AiSettings s, OllamaObservation o, DateTimeOffset now)
    {
        if (!Applies(s)) return OllamaAction.None;
        // Your choices win: turned on during a game stays on until the game ends; turned off stays off.
        if (o.Running && s.StopOllamaForGpu && o.GpuContention is not null && !o.UserKeepsOn) return OllamaAction.StopForGpu;
        if (!o.Running && o.StoppedForGpu && !o.UserTurnedOff && s.StopOllamaForGpu && o.GpuContention is null &&
            o.ContentionClearSince is { } clear && now - clear >= RestartDelay)
            return OllamaAction.StartAfterGpu;
        if (o.Running && !o.Busy && s.StopOllamaIdleMinutes > 0 && now - o.IdleSince >= TimeSpan.FromMinutes(s.StopOllamaIdleMinutes))
            return OllamaAction.StopIdle;
        return OllamaAction.None;
    }
}
