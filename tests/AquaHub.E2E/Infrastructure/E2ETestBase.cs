using System.Runtime.CompilerServices;
using System.Text;

namespace AquaHub.E2E.Infrastructure;

/// <summary>
/// Base class for UI tests: a lazily started app instance per class, step logging, soft checks that keep going
/// after a failure (so one defect does not hide the rest of a page), crash detection and screenshots on failure.
/// </summary>
public abstract class E2ETestBase : IClassFixture<AppFixture>
{
    public static readonly string[] AllPages = { "today", "news", "social", "markets", "upcoming", "system", "launchpad", "ask", "agents", "workbench", "settings" };

    protected readonly AppFixture Fixture;
    protected readonly ITestOutputHelper Out;
    private readonly List<string> _softFailures = new();
    private string _test = "";
    private int _shot;

    protected E2ETestBase(AppFixture fixture, ITestOutputHelper output)
    {
        Fixture = fixture;
        Out = output;
        Fixture.Configure(GetType().Name, Options);
    }

    /// <summary>Profile/launch options for this class's app instance.</summary>
    protected virtual SessionOptions Options => SessionOptions.Warm;

    protected AppSession App => Fixture.Session;
    protected int Pid => App.Pid;
    protected AutomationElement Main => App.Main;
    protected string TestName => _test;

    // ───────────── Test wrapper ─────────────
    /// <summary>Runs a test body with crash detection, soft-failure aggregation and screenshots.</summary>
    protected void Run(Action body, [CallerMemberName] string test = "")
    {
        _test = $"{GetType().Name}.{test}";
        _softFailures.Clear();
        _timeoutsInARow = 0;
        Results.Log($"=== START {_test}");
        // A test that ended the app (on purpose or not) has reported it; this one starts with a running copy.
        var app = Fixture.EnsureRunning();
        var logMark = app.Log.Mark();
        Exception? hard = null;
        try
        {
            body();
        }
        catch (Exception ex)
        {
            hard = ex;
            Shot("FAIL");
            Results.Log($"!!! {_test} failed: {ex.GetType().Name}: {ex.Message}");
        }

        var problems = new List<string>();
        // The fixture never swaps instances behind a test's back, so a dead app here died during this test (unless
        // the test replaced it itself with Fixture.Restart()).
        if (ReferenceEquals(Fixture.Current, app) && !app.IsAlive && !AllowsExit)
            problems.Add($"The app process ({app.Pid}) exited during the test (exit code {SafeExitCode(app)}).");
        var crashes = app.Log.CrashesSince(logMark);
        if (crashes.Count > 0) problems.Add("Unhandled exceptions in aquahub.log:\n  " + string.Join("\n  ", crashes.Take(8)));
        problems.AddRange(_softFailures);
        Results.Log($"=== END {_test}: {(hard is null && problems.Count == 0 ? "PASS" : "FAIL")}");

        if (hard is not null && problems.Count == 0) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(hard);
        if (hard is not null) problems.Insert(0, $"{hard.GetType().Name}: {hard.Message}");
        if (problems.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{problems.Count} problem(s) in {_test}:");
            foreach (var p in problems) sb.AppendLine(" - " + p);
            Assert.Fail(sb.ToString());
        }
    }

    /// <summary>Set by tests that deliberately end the app (quit).</summary>
    protected bool AllowsExit { get; set; }

    private static string SafeExitCode(AppSession s)
    {
        try { return s.Process.ExitCode.ToString(); } catch { return "?"; }
    }

    /// <summary>
    /// A soft check: runs one interaction, records it for the coverage table, and on failure takes screenshots,
    /// records the problem and carries on with the next interaction (the test fails at the end).
    /// </summary>
    protected bool Check(string area, string interaction, Action action)
    {
        Step($"[{area}] {interaction}");
        var page = PageOrNull();
        try
        {
            action();
            Results.RecordInteraction(_test, area, interaction, true);
            _timeoutsInARow = 0;
            return true;
        }
        catch (AppExitedException)
        {
            throw;   // nothing left to check; Run reports the exit
        }
        catch (Exception ex)
        {
            var shots = Shot($"{area}-{interaction}");
            var msg = $"[{area}] {interaction}: {ex.GetType().Name}: {ex.Message}" + (shots.Count > 0 ? $" (screenshot: {Path.GetFileName(shots[0])})" : "");
            _softFailures.Add(msg);
            Results.RecordInteraction(_test, area, interaction, false, ex.Message);
            Out.WriteLine("   FAILED: " + msg);
            Results.Log("   FAILED: " + msg);
            try { Recover(page); } catch (Exception rex) { Results.Log("   recover failed: " + rex.Message); }
            _timeoutsInARow = ex is TimeoutException ? _timeoutsInARow + 1 : 0;
            if (_timeoutsInARow >= MaxTimeoutsInARow)
                throw new InvalidOperationException(
                    $"{MaxTimeoutsInARow} checks in a row timed out, so the rest of this test was skipped (the UI is probably stuck; the first of them says where)");
            return false;
        }
    }

    private int _timeoutsInARow;
    private const int MaxTimeoutsInARow = 3;

    private string? PageOrNull()
    {
        try { return CurrentPage(); }
        catch (Exception ex) when (ex is AppExitedException || Wait.IsTransient(ex)) { return null; }
    }

    /// <summary>Records a noteworthy observation (goes to findings.jsonl for the report).</summary>
    protected void Finding(string kind, string title, string detail)
    {
        Out.WriteLine($"   FINDING [{kind}] {title}: {detail}");
        Results.RecordFinding(_test, kind, title, detail);
    }

    protected void Step(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        try { Out.WriteLine(line); } catch { }
        Results.Log("   " + text);
    }

    protected List<string> Shot(string label)
    {
        var s = Fixture.Current;
        if (s is null || !s.IsAlive) return new List<string>();
        var files = s.Screenshot($"{Sanitize(_test)}-{++_shot:00}-{Sanitize(label)}");
        foreach (var f in files) Step("   screenshot: " + f);
        return files;
    }

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').Take(60).ToArray());

    /// <summary>
    /// Best-effort return to where the failed check started: popups, menus and dialogs closed, the main window shown,
    /// and the page it was on (so one failure doesn't make the following checks time out on the wrong page).
    /// </summary>
    protected virtual void Recover(string? page = null)
    {
        DismissTransients();
        CloseDialogs();
        var main = App.TryMain();
        if (main is null) App.Command("activate");
        if (page is not null && PageOrNull() != page)
        {
            Results.Log($"   back to {page}");
            GoTo(page);
        }
    }

    /// <summary>Closes message boxes and file dialogs a failed check left open (they belong to the app under test).</summary>
    protected void CloseDialogs()
    {
        var closed = 0;
        foreach (var h in Win32.WindowsOf(Pid))
        {
            if (!Win32.IsWindowVisible(h) || Win32.ClassOf(h) != "#32770") continue;
            Win32.PostMessage(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            closed++;
        }
        if (closed == 0) return;
        Results.Log($"   closed {closed} leftover dialog(s)");
        Thread.Sleep(300);
    }

    /// <summary>Closes context menus, popups, the quick panel and the palette if any are open.</summary>
    protected void DismissTransients()
    {
        for (var i = 0; i < 3; i++)
        {
            var popups = AppWindows.Popups(Pid);
            var palette = App.TryPalette();
            if (popups.Count == 0 && palette is null) break;
            var target = palette ?? popups[0];
            try
            {
                if (AppWindows.ForegroundPid() != Pid) Input.TryActivate(App.HwndOf(target));
                if (AppWindows.ForegroundPid() == Pid) Input.Press(Pid, VK.Escape);
            }
            catch (Exception ex) { Results.Log("   dismiss: " + ex.Message); }
            Thread.Sleep(250);
        }
        if (App.TryFlyout() is not null)
        {
            try { App.Command("flyout"); } catch { }
            Wait.Until(() => App.TryFlyout() is null, TimeSpan.FromSeconds(3));
        }
    }

    // ───────────── Navigation ─────────────
    protected AutomationElement NavItem(string page) =>
        Ui.WaitFind(Main, Ui.Id("nav-" + page), $"nav item nav-{page}");

    /// <summary>Waits for the root of a page (AutomationId page-&lt;name&gt;).</summary>
    protected AutomationElement PageRoot(string page, TimeSpan? timeout = null) =>
        Ui.WaitFind(Main, Ui.Id("page-" + page), $"page root page-{page}", timeout);

    protected AutomationElement? TryPageRoot(string page) => Ui.Find(Main, Ui.Id("page-" + page));

    /// <summary>The page currently shown in the main window (null if none).</summary>
    protected string? CurrentPage()
    {
        var main = App.TryMain();
        if (main is null) return null;
        foreach (var p in AllPages)
            if (Ui.Find(main, Ui.Id("page-" + p)) is not null) return p;
        return null;
    }

    /// <summary>Navigates with the nav rail (SelectionItem pattern) and waits for the page root.</summary>
    protected AutomationElement GoTo(string page)
    {
        if (App.TryMain() is null) App.Command("activate");
        var nav = NavItem(page);
        if (CurrentPage() != page)
        {
            Wait.Retry(() => Ui.Select(nav), $"select nav-{page}");
            Wait.For(() => CurrentPage() == page, $"navigation to {page}");
        }
        return PageRoot(page);
    }

    /// <summary>Re-navigates to a page even if it is current (forces a fresh page instance for non-Today pages).</summary>
    protected AutomationElement Reopen(string page)
    {
        GoTo(page == "today" ? "news" : "today");
        return GoTo(page);
    }

    // ───────────── Keyboard ─────────────
    protected void KeysToMain(params VK[] chord)
    {
        App.EnsureForeground(Main);
        Input.Chord(Pid, chord);
    }

    protected void PressInMain(VK key, int times = 1)
    {
        App.EnsureForeground(Main);
        Input.Press(Pid, key, times);
    }

    /// <summary>Focuses an element (UIA SetFocus) in a foreground window, then presses a key.</summary>
    protected void FocusAndPress(AutomationElement window, AutomationElement element, VK key)
    {
        App.EnsureForeground(window);
        Wait.Retry(element.SetFocus, "set focus");
        Thread.Sleep(120);
        Input.Press(Pid, key);
    }

    // ───────────── Assertions ─────────────
    protected static void Expect(bool condition, string message)
    {
        if (!condition) throw new Xunit.Sdk.XunitException(message);
    }

    protected AutomationElement Button(AutomationElement root, string name, string? what = null) =>
        Ui.WaitFind(root, Ui.Button(name), what ?? $"button '{name}'");

    protected void ExpectPage(string page, TimeSpan? timeout = null) =>
        Wait.For(() => CurrentPage() == page, $"main window showing page '{page}' (now: {CurrentPage() ?? "none"})", timeout);

    // ───────────── Journal (dry-run side effects) ─────────────
    public static readonly string[] MediaActions = { "media", "media-key" };

    /// <summary>Runs a trigger and waits for the journal entry it must produce.</summary>
    protected JournalEntry ExpectJournal(string action, Action trigger, Func<string, bool>? detail = null, string? what = null, TimeSpan? timeout = null)
    {
        var mark = App.Journal.Mark();
        trigger();
        var e = App.Journal.WaitFor(mark, action, detail, timeout, what);
        Step($"   journal: {e.Action} {e.Detail}");
        return e;
    }

    protected JournalEntry ExpectJournalAny(IReadOnlyCollection<string> actions, Action trigger, Func<JournalEntry, bool>? filter = null, TimeSpan? timeout = null)
    {
        var mark = App.Journal.Mark();
        trigger();
        var e = App.Journal.WaitForAny(mark, actions, filter, timeout);
        Step($"   journal: {e.Action} {e.Detail}");
        return e;
    }

    /// <summary>Asserts that nothing with this action was journaled within a short window after the trigger.</summary>
    protected void ExpectNoJournal(string action, Action trigger, TimeSpan window)
    {
        var mark = App.Journal.Mark();
        trigger();
        Thread.Sleep(window);
        var hit = App.Journal.Find(mark, action);
        Expect(hit is null, $"Did not expect a '{action}' journal entry but got: {hit}");
    }

    // ───────────── Context menus ─────────────
    /// <summary>Opens an element's context menu with Shift+F10 (keyboard), falling back to a right-click.</summary>
    protected AutomationElement OpenContextMenu(AutomationElement window, AutomationElement target)
    {
        AutomationElement? FindMenu() => AppWindows.Menu(Pid) ?? Ui.Find(window, Ui.Type(ControlType.Menu));
        App.EnsureForeground(window);
        Ui.BringIntoView(target);
        Wait.Retry(target.SetFocus, "focus context-menu target");
        Thread.Sleep(150);
        Input.Chord(Pid, VK.Shift, VK.F10);
        var menu = Wait.Until(() => FindMenu() is not null, TimeSpan.FromSeconds(3)) ? FindMenu() : null;
        if (menu is null)
        {
            Step("   Shift+F10 did not open a context menu — falling back to a right-click");
            App.EnsureForeground(window);
            Input.ClickElement(Pid, target, right: true);
            menu = Wait.For(FindMenu, "context menu");
        }
        return menu!;
    }

    protected static AutomationElement MenuItem(AutomationElement menu, string name) =>
        Ui.WaitFind(menu, Ui.And(Ui.Type(ControlType.MenuItem), Ui.Name(name)), $"menu item '{name}'");

    /// <summary>Opens the tray menu with --tray-menu, asking a second time if the first one didn't appear.</summary>
    protected AutomationElement TrayMenu()
    {
        for (var attempt = 1; ; attempt++)
        {
            App.Command("tray-menu");
            AutomationElement? menu = null;
            if (Wait.Until(() => (menu = AppWindows.Menu(Pid, "tray-menu")) is not null, TimeSpan.FromSeconds(5))) return menu!;
            if (attempt == 2) throw new TimeoutException("Timed out waiting for: tray menu (#tray-menu), asked twice");
            Step("   the tray menu didn't appear — asking again");
        }
    }
}
