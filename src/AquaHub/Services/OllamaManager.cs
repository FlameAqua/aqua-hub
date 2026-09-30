using System.Diagnostics;
using System.Windows.Threading;
using AquaHub.Core.Ai;
using AquaHub.Core.Util;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>
/// Starts and stops the local Ollama server for you: on demand, when a game or another app needs the GPU (and
/// back again afterwards), and after a stretch of not being used, because the server holds a lot of RAM even
/// when idle. The server is the process listening on the configured port; an idle stop never happens while a
/// model is loaded, another program is connected to the server (`ollama run`/`pull` in a terminal, a chat app)
/// or the server is downloading. A remote endpoint is never managed at all.
/// </summary>
public sealed class OllamaManager
{
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _idleSince = DateTimeOffset.Now;
    private DateTimeOffset? _contentionClearSince;
    private bool _stoppedForGpu;
    private bool _hadTrayApp;
    private int? _startedPid;
    private bool _keepOnWhileGpuBusy;
    private bool _busy;

    public OllamaManager()
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) => await TickAsync(), Hub.Ui);
    }

    /// <summary>Raised on the UI thread when the server starts or stops (or a start/stop begins).</summary>
    public event Action? Changed;

    public bool? Running { get; private set; }
    public bool Transitioning { get; private set; }
    /// <summary>Why Aqua last stopped the server ("after 15 minutes unused", "for a full-screen game"), or null.</summary>
    public string? StoppedBecause { get; private set; }

    /// <summary>You turned it off: nothing starts it again automatically until you turn it on (or restart Aqua).</summary>
    public bool UserTurnedOff { get; private set; }

    /// <summary>You turned it on — honoured even during a game, until the game ends.</summary>
    public Task<bool> TurnOnAsync()
    {
        UserTurnedOff = false;
        _keepOnWhileGpuBusy = Hub.Platform.GpuContention(serverHasModelLoaded: false) is not null;
        return StartAsync("by you");
    }

    /// <summary>You turned it off.</summary>
    public Task TurnOffAsync()
    {
        UserTurnedOff = true;
        _keepOnWhileGpuBusy = false;
        return StopAsync("by you");
    }

    /// <summary>A local Ollama endpoint (the only kind Aqua will start or stop).</summary>
    public static bool IsLocalOllama =>
        Hub.S.Ai.Provider == "ollama" && Uri.TryCreate(Hub.S.Ai.Endpoint, UriKind.Absolute, out var u) && u.IsLoopback;

    public static string? Executable
    {
        get
        {
            var configured = Hub.S.Ai.OllamaPath;
            if (configured.Length > 0) return File.Exists(configured) ? configured : null;
            var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
            if (File.Exists(standard)) return standard;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "ollama.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }
    }

    public static bool Installed => Executable is not null;

    private static int Port => Uri.TryCreate(Hub.S.Ai.Endpoint, UriKind.Absolute, out var u) ? u.Port : 11434;

    /// <summary>One line for the UI: "Running", "Stopped after 15 minutes unused", "Not installed"…</summary>
    public string StatusText =>
        !IsLocalOllama ? "Remote or non-Ollama server — managed outside Aqua"
        : Transitioning ? (Running == true ? "Stopping…" : "Starting…")
        : Running == true ? "Running"
        : !Installed ? "Not installed"
        : StoppedBecause is { } why ? $"Stopped {why}" : "Not running";

    public void Start()
    {
        if (Hub.SnapshotMode) return;
        Refresh();
        _timer.Start();
    }

    /// <summary>Re-reads whether the server is up (cheap: one TCP table lookup).</summary>
    public void Refresh()
    {
        var running = IsLocalOllama ? Native.ListenerPid(Port) is not null : (bool?)null;
        if (running != Running)
        {
            Running = running;
            if (running == true) StoppedBecause = null;
            Changed?.Invoke();
        }
    }

    public async Task<bool> StartAsync(string why, CancellationToken ct = default)
    {
        if (!IsLocalOllama || _busy || UserTurnedOff) return false;
        Refresh();
        if (Running == true) return true;
        var exe = Executable;
        if (exe is null) return false;
        _busy = true;
        Transitioning = true;
        Changed?.Invoke();
        try
        {
            if (!Sandbox.Intercept("ollama", "start · " + why))
            {
                // Bring back Ollama's own tray app if it was running before (it manages updates); otherwise run the
                // server quietly with no window.
                var tray = Path.Combine(Path.GetDirectoryName(exe)!, "ollama app.exe");
                var psi = _hadTrayApp && File.Exists(tray)
                    ? new ProcessStartInfo(tray) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! }
                    : new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                if (!psi.UseShellExecute && Port != 11434) psi.Environment["OLLAMA_HOST"] = "127.0.0.1:" + Port;
                using var process = Process.Start(psi);
                _startedPid = psi.UseShellExecute ? null : process?.Id;
                Log.Info("ollama", $"Starting Ollama ({why})");
                for (var i = 0; i < 60 && Native.ListenerPid(Port) is null; i++) await Task.Delay(250, ct);
            }
            _stoppedForGpu = false;
            StoppedBecause = null;
            _idleSince = DateTimeOffset.Now;
            await Hub.Core.Llm.CheckAsync(ct);
            return Hub.State.Ai?.Available == true || Native.ListenerPid(Port) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("ollama", "Couldn't start Ollama", ex);
            return false;
        }
        finally
        {
            _busy = false;
            Transitioning = false;
            Refresh();
            Changed?.Invoke();
        }
    }

    public async Task StopAsync(string why)
    {
        if (!IsLocalOllama || _busy) return;
        _busy = true;
        Transitioning = true;
        Changed?.Invoke();
        try
        {
            if (!Sandbox.Intercept("ollama", "stop · " + why))
            {
                // Ollama's own tray app restarts the server, so it goes too — but only the one installed beside the
                // ollama.exe Aqua uses, checked by path, never any process that merely has the same name.
                // The tray app only runs the server on Ollama's own port, so a server on another port leaves it alone.
                var trayPath = Executable is { } exe && Port == 11434 ? Path.Combine(Path.GetDirectoryName(exe)!, "ollama app.exe") : null;
                foreach (var tray in trayPath is null ? Array.Empty<Process>() : Process.GetProcessesByName("ollama app"))
                {
                    using (tray)
                    {
                        string? path = null;
                        try { path = tray.MainModule?.FileName; } catch (Exception ex) { Log.Debug("ollama", "tray app path: " + ex.Message); }
                        if (trayPath is null || !string.Equals(path, trayPath, StringComparison.OrdinalIgnoreCase)) continue;
                        _hadTrayApp = true;
                        try { tray.Kill(entireProcessTree: true); } catch (Exception ex) { Log.Debug("ollama", "tray app: " + ex.Message); }
                    }
                }
                if (Native.ListenerPid(Port) is int pid)
                {
                    try
                    {
                        using var server = Process.GetProcessById(pid);
                        // Only ever the Ollama server itself (the process serving the port), with its model runners.
                        if (server.ProcessName.StartsWith("ollama", StringComparison.OrdinalIgnoreCase)) server.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        Log.Debug("ollama", "server: " + ex.Message);
                    }
                }
                for (var i = 0; i < 20 && Native.ListenerPid(Port) is not null; i++) await Task.Delay(250);
                Log.Info("ollama", $"Stopped Ollama {why}");
            }
            StoppedBecause = why;
            await Hub.Core.Llm.CheckAsync();
        }
        finally
        {
            _busy = false;
            Transitioning = false;
            Refresh();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Quitting Aqua stops a server Aqua itself started (as "ollama serve"), since nothing would stop it when idle
    /// afterwards. A server you (or Ollama's tray app) started keeps running.
    /// </summary>
    public void OnAppExit()
    {
        if (_startedPid is not int pid || !IsLocalOllama || !Hub.S.Ai.ManageOllama || Native.ListenerPid(Port) != pid) return;
        if (Sandbox.Intercept("ollama", "stop · Aqua is quitting")) return;
        try
        {
            using var server = Process.GetProcessById(pid);
            server.Kill(entireProcessTree: true);
            Log.Info("ollama", "Stopped the Ollama server Aqua started, because Aqua is quitting");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Debug("ollama", "exit: " + ex.Message);
        }
    }

    /// <summary>
    /// Another program is using the server right now: it has a connection open to it (a terminal `ollama pull` or
    /// `ollama run`, a chat app), or the server itself is downloading (a pull in progress).
    /// </summary>
    private static bool InUseByOthers(int serverPid)
    {
        var self = Environment.ProcessId;
        foreach (var (_, remote, pid) in Native.EstablishedConnections())
        {
            if (remote == Port && pid != self && pid != serverPid) return true;
            if (pid == serverPid && remote is 443 or 80) return true;
        }
        return false;
    }

    private async Task TickAsync()
    {
        Refresh();
        var s = Hub.S.Ai;
        if (!OllamaPolicy.Applies(s) || _busy) return;
        var now = DateTimeOffset.Now;
        var running = Running == true;

        var loaded = false;
        var inUse = false;
        if (running)
        {
            try { loaded = (await Hub.Core.Llm.RunningModelsAsync()).Count > 0; } // a model loaded (by anyone) = in use
            catch { loaded = true; }
            inUse = !loaded && Native.ListenerPid(Port) is int serverPid && InUseByOthers(serverPid);
        }
        var busy = loaded || inUse || Hub.Core.Llm.IsBusy;
        if (!running || busy) _idleSince = now;
        if (Hub.Core.Llm.Stats.LastCall is { } last && last > _idleSince) _idleSince = last;

        var contention = Hub.Platform.GpuContention(serverHasModelLoaded: loaded);
        _contentionClearSince = contention is null ? _contentionClearSince ?? now : null;
        if (contention is null) _keepOnWhileGpuBusy = false; // the game you kept it on for has ended

        var action = OllamaPolicy.Decide(s, new OllamaObservation(running, busy, _idleSince, contention, _stoppedForGpu, _contentionClearSince,
            UserKeepsOn: _keepOnWhileGpuBusy, UserTurnedOff: UserTurnedOff), now);
        switch (action)
        {
            case OllamaAction.StopForGpu:
                await StopAsync($"to free the GPU for {contention}");
                _stoppedForGpu = true;
                break;
            case OllamaAction.StartAfterGpu:
                await StartAsync("the GPU is free again");
                break;
            case OllamaAction.StopIdle:
                await StopAsync($"after {Plural.Of(s.StopOllamaIdleMinutes, "minute")} unused, to free memory");
                break;
        }
    }
}
