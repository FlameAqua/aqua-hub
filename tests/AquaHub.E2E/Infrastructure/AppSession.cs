using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AquaHub.E2E.Infrastructure;

public sealed class SessionOptions
{
    /// <summary>Start from an empty folder (first run → onboarding) instead of the warm profile.</summary>
    public bool EmptyProfile { get; init; }

    /// <summary>
    /// Clear the global hotkeys in the profile copy so the test instance does not grab system-wide
    /// shortcuts (Ctrl+Alt+Q/K fallbacks) from other apps on the desktop while the suite runs.
    /// </summary>
    public bool BlankHotkeys { get; init; } = true;

    /// <summary>Start with <c>--background</c> (no main window).</summary>
    public bool Background { get; init; }

    /// <summary>
    /// Give a profile copy without a place a fixed one (Dublin), so weather and local news don't depend on what was
    /// answered during onboarding. Tests of the no-place state turn this off.
    /// </summary>
    public bool SeedPlace { get; init; } = true;

    /// <summary>Tweaks applied to the copied settings.json before launch.</summary>
    public Action<JsonObject>? EditSettings { get; init; }

    public static SessionOptions Warm => new();
}

/// <summary>
/// One running instance of the app under test: launched with <c>--e2e --data-dir &lt;copy&gt;</c>, tracked by PID,
/// and always cleaned up. Only processes started here are ever signalled or killed.
/// </summary>
public sealed class AppSession : IDisposable
{
    private static readonly object StartedGate = new();
    private static readonly HashSet<int> StartedPids = new();

    static AppSession()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAllStarted();
    }

    public string Name { get; }
    public string ProfileDir { get; }
    public SessionOptions Options { get; }
    public Process Process { get; private set; }
    public int Pid => Process.Id;
    public Journal Journal { get; }
    public SettingsFile Settings { get; }
    public AppLog Log { get; }
    public DateTime StartedAt { get; }

    private AppSession(string name, string profileDir, SessionOptions options, Process process)
    {
        Name = name;
        ProfileDir = profileDir;
        Options = options;
        Process = process;
        Journal = new Journal(profileDir);
        Settings = new SettingsFile(profileDir);
        Log = new AppLog(profileDir);
        StartedAt = DateTime.Now;
    }

    public static AppSession Start(string name, SessionOptions options)
    {
        Win32.EnsureDpiAware();
        var exe = E2EConfig.ExePath;
        if (!File.Exists(exe)) throw new FileNotFoundException("App under test not found (set AQUAHUB_E2E_EXE)", exe);

        var profile = Path.Combine(E2EConfig.ProfilesDir, name);
        for (var n = 2; Directory.Exists(profile); n++) profile = Path.Combine(E2EConfig.ProfilesDir, $"{name}-{n}");
        if (options.EmptyProfile)
        {
            Directory.CreateDirectory(profile);
        }
        else
        {
            if (!Directory.Exists(E2EConfig.PristineProfile))
                throw new DirectoryNotFoundException("Warm profile not found (set AQUAHUB_E2E_PRISTINE): " + E2EConfig.PristineProfile);
            FileUtil.CopyDirectory(E2EConfig.PristineProfile, profile);
            File.Delete(Path.Combine(profile, "e2e-journal.log"));
            SettingsFile.Edit(profile, root =>
            {
                if (options.BlankHotkeys && root["general"] is JsonObject g)
                {
                    g["hotkeyFlyout"] = "";
                    g["hotkeyPalette"] = "";
                }
                if (options.SeedPlace) SeedDublin(root);
                options.EditSettings?.Invoke(root);
            });
        }

        var args = new List<string> { "--e2e", "--data-dir", profile };
        if (options.Background) args.Add("--background");
        var process = Launch(exe, args);
        var session = new AppSession(name, profile, options, process);
        Results.Log($"[session] started {name} pid={process.Id} profile={profile}");
        session.WaitUntilReady();
        return session;
    }

    /// <summary>A place for a profile that has none (onboarding's place step was skipped): Dublin, as the tests expect.</summary>
    private static void SeedDublin(JsonObject root)
    {
        if (root["location"] is JsonObject existing && (existing["city"]?.GetValue<string>() ?? "").Trim().Length > 0) return;
        var loc = root["location"] as JsonObject ?? new JsonObject();
        loc["city"] = "Dublin";
        loc["region"] = "Ireland";
        loc["country"] = "IE";
        loc["latitude"] = 53.35;
        loc["longitude"] = -6.26;
        loc["timezone"] = "Europe/Dublin";
        loc["language"] = "en-IE";
        loc["localKeywords"] = new JsonArray("Dublin", "Ireland", "Irish");
        root["location"] = loc;
        Results.Log("[session] the warm profile has no place; seeded Dublin");
    }

    private static Process Launch(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["AQUAHUB_E2E"] = "1";
        psi.Environment.Remove("AQUAHUB_HOME");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + exe);
        lock (StartedGate) StartedPids.Add(p.Id);
        return p;
    }

    public bool IsAlive
    {
        get
        {
            try
            {
                Process.Refresh();
                return !Process.HasExited;
            }
            catch { return false; }
        }
    }

    private void WaitUntilReady()
    {
        Wait.For(() => Log.Lines().Any(l => l.Contains("Aqua Hub started", StringComparison.Ordinal)) || !IsAlive,
            "app start-up log line", TimeSpan.FromSeconds(60), 250);
        if (!IsAlive) throw new InvalidOperationException($"App exited during start-up (exit code {Process.ExitCode})");
        if (!Options.Background)
            Wait.For(() => TryMain() is not null, "main window", TimeSpan.FromSeconds(30));
    }

    // ───────────── Windows ─────────────
    public AutomationElement? TryMain() => AppWindows.Window(Pid, AppWindows.MainTitle);
    public AutomationElement Main => Wait.For(TryMain, "main window 'Aqua Hub'", TimeSpan.FromSeconds(15));
    public AutomationElement? TryFlyout() => AppWindows.Window(Pid, AppWindows.FlyoutTitle);
    public AutomationElement? TryPalette() => AppWindows.Window(Pid, AppWindows.PaletteTitle);
    public List<AutomationElement> Windows() => AppWindows.TopLevel(Pid);

    public IntPtr HwndOf(AutomationElement window)
    {
        try { return new IntPtr(window.Current.NativeWindowHandle); }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return IntPtr.Zero; }
    }

    /// <summary>Makes sure one of our windows is in the foreground before real input is sent.</summary>
    public void EnsureForeground(AutomationElement window, bool allowActivateCommand = true)
    {
        if (AppWindows.ForegroundPid() == Pid && Win32.GetForegroundWindow() == HwndOf(window)) return;
        if (Input.TryActivate(HwndOf(window))) return;
        if (allowActivateCommand && Ui.NameOf(window) == AppWindows.MainTitle)
        {
            Command("activate");
            if (Wait.Until(() => AppWindows.ForegroundPid() == Pid, TimeSpan.FromSeconds(3))) return;
        }
        throw new InvalidOperationException($"Could not bring '{Ui.NameOf(window)}' to the foreground (foreground pid {AppWindows.ForegroundPid()}).");
    }

    // ───────────── Remote commands (single-instance signalling) ─────────────
    /// <summary>
    /// Sends a command to this instance the way desktop shortcuts do: a second process with the same
    /// --data-dir signals the first one and exits. <paramref name="command"/>: activate, flyout, palette, tray-menu, quit.
    /// </summary>
    public void Command(string command)
    {
        var args = new List<string>();
        if (command != "activate") args.Add("--" + command);
        args.AddRange(new[] { "--e2e", "--data-dir", ProfileDir });
        var p = Launch(E2EConfig.ExePath, args);
        if (!p.WaitForExit(20000))
        {
            try { p.Kill(); } catch { }
            throw new TimeoutException($"Command process for '{command}' did not exit (it may have become the primary instance)");
        }
        Results.Log($"[session] command {command} → exit {p.ExitCode}");
    }

    // ───────────── Diagnostics ─────────────
    public List<string> Screenshot(string label)
    {
        try { return Screenshots.CaptureProcess(Pid, label); }
        catch { return new List<string>(); }
    }

    public void Dispose()
    {
        try
        {
            if (IsAlive)
            {
                try { Command("quit"); } catch { }
                if (!Process.WaitForExit(15000))
                {
                    Results.Log($"[session] {Name} did not quit in time — killing pid {Pid}");
                    Process.Kill();
                    Process.WaitForExit(5000);
                }
            }
        }
        catch (Exception ex)
        {
            Results.Log($"[session] cleanup of {Name} failed: {ex.Message}");
        }
        finally
        {
            lock (StartedGate) StartedPids.Remove(Pid);
            Results.Log($"[session] disposed {Name}");
        }
    }

    /// <summary>Last-resort cleanup: kills only processes this harness started that are still running.</summary>
    public static void KillAllStarted()
    {
        int[] pids;
        lock (StartedGate) pids = StartedPids.ToArray();
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited && p.ProcessName.Equals("AquaHub", StringComparison.OrdinalIgnoreCase)) p.Kill();
            }
            catch { }
        }
    }
}

/// <summary>Per-test-class fixture: one app instance (fresh profile copy) per class, started lazily.</summary>
public sealed class AppFixture : IDisposable
{
    private AppSession? _session;
    private string _name = "app";
    private SessionOptions _options = SessionOptions.Warm;
    private int _restarts;

    public void Configure(string name, SessionOptions options)
    {
        if (_session is not null) return;
        _name = name;
        _options = options;
    }

    public AppSession? Current => _session;

    /// <summary>
    /// The running instance, started on first use. If it has died this throws instead of quietly starting another:
    /// a fresh copy mid-test would hide the crash and let later checks pass against a different app.
    /// </summary>
    public AppSession Session
    {
        get
        {
            if (_session is null) _session = AppSession.Start(_name, _options);
            else if (!_session.IsAlive) throw new AppExitedException(_session);
            return _session;
        }
    }

    /// <summary>
    /// At the start of a test: the instance, replaced by a fresh one if an earlier test ended it (that test has
    /// already reported the exit, or meant it, like a quit test).
    /// </summary>
    public AppSession EnsureRunning()
    {
        if (_session is { } s && !s.IsAlive)
        {
            Results.Log($"[fixture] {_name}: instance pid {s.Pid} ended in an earlier test — starting a fresh one");
            s.Dispose();
            _session = AppSession.Start($"{_name}-restart{++_restarts}", _options);
        }
        return Session;
    }

    /// <summary>Replaces the running instance (fresh profile copy).</summary>
    public AppSession Restart(SessionOptions? options = null)
    {
        _session?.Dispose();
        _session = AppSession.Start($"{_name}-restart{++_restarts}", options ?? _options);
        return _session;
    }

    public void Dispose() => _session?.Dispose();
}

/// <summary>The app under test is no longer running (never a transient error: waits stop at once).</summary>
public sealed class AppExitedException(AppSession session) : Exception(
    $"The app (pid {session.Pid}) is no longer running (exit code {ExitCodeOf(session)}); the rest of this test can't run")
{
    private static string ExitCodeOf(AppSession s)
    {
        try { return s.Process.ExitCode.ToString(); } catch { return "?"; }
    }
}
