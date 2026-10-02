using System.Text.RegularExpressions;

namespace AquaHub.E2E.Infrastructure;

/// <summary>
/// Generic monkey-style crawler: snapshots a surface, activates every enabled control that exposes Invoke / Toggle /
/// SelectionItem / ExpandCollapse exactly once (re-finding each by a signature + occurrence index after the tree changes),
/// and after every action checks that the process is alive, the log has no unhandled exception and the UI thread
/// still answers. It also records every interactive element without an accessible name.
/// </summary>
public sealed class Crawler
{
    public sealed record Touch(string Surface, string Control, string Action, string Outcome);
    public sealed record Unnamed(string Surface, string Type, string AutomationId, string ShownText, string HelpText, string Container);
    public sealed record DumpName(string Surface, string Type, string Name);
    public sealed record Problem(string Surface, string Control, string Issue);

    public sealed class SurfaceStats
    {
        public string Surface { get; init; } = "";
        public int InteractiveFound { get; set; }
        public int Actionable { get; set; }
        public int Invoked { get; set; }
        public int Skipped { get; set; }
        public int Vanished { get; set; }
        public int Navigations { get; set; }
        public int Unnamed { get; set; }
        public double Seconds { get; set; }
    }

    public static readonly List<Touch> Touches = new();
    public static readonly List<Unnamed> UnnamedControls = new();
    public static readonly List<DumpName> DumpNames = new();
    public static readonly List<Problem> Problems = new();
    public static readonly List<SurfaceStats> Stats = new();

    // Destructive or process-level actions the crawler must not trigger blindly (dedicated tests cover them).
    // File and folder pickers are system dialogs; the Ask answer actions start long model runs (A09 covers them).
    private static readonly Regex SkipNames = new(@"^(Quit|Quit Aqua Hub|Delete scene|Browse…)$|Quit|from Launchpad$|Turn off Ollama|Turn on Ollama|^Attach files|^Attach a folder|^Add a folder|^Reset the .* hotkey$|^Ask again$|Research this properly|^Delete chat$|^Delete this skill|^Forget this$|^Draft the skill|^Save and try it|^Speak your question|^Try it in a new chat|^Improve$|^Answer it instead$|^Close app$|^End task$|^Force end$|^Run health check$|^Check again$|^Remove Whisper$", RegexOptions.IgnoreCase);
    private static readonly Regex Destructive = new(@"^Remove|^Clear$|^Clear caches|^Local database \(2\)", RegexOptions.IgnoreCase);
    private static readonly Regex ObjectDump = new(@"^[A-Z][A-Za-z]+(VM|Item|Link|Source|Symbol|Entry|Match|Place|App)\s\{|^AquaHub\.|^System\.", RegexOptions.None);

    private readonly AppSession _app;
    private readonly Action<string> _log;

    public Crawler(AppSession app, Action<string> log)
    {
        _app = app;
        _log = log;
    }

    private static bool InTemplateChrome(UiNode n) =>
        n.Type == ControlType.ScrollBar || n.Type == ControlType.Thumb ||
        n.Ancestors().Any(a => a.Type == ControlType.ScrollBar) ||
        (n.Parent?.Type == ControlType.Slider && n.AutomationId is "DecreaseLarge" or "IncreaseLarge");

    private static string Label(UiNode n) =>
        $"{n.TypeName} '{(n.Name.Length > 0 ? n.Name : "∅ " + n.InnerText)}'" + (n.AutomationId.Length > 0 ? $" #{n.AutomationId}" : "");

    /// <summary>Signature plus occurrence index, so repeated template instances are each visited once.</summary>
    private static List<(string Key, UiNode Node)> Keyed(IEnumerable<UiNode> nodes)
    {
        var counts = new Dictionary<string, int>();
        var list = new List<(string, UiNode)>();
        foreach (var n in nodes)
        {
            var sig = n.Signature;
            counts.TryGetValue(sig, out var k);
            counts[sig] = k + 1;
            list.Add(($"{sig}#{k}", n));
        }
        return list;
    }

    /// <summary>Records unnamed interactive controls and object-dump names on a surface (no actions).</summary>
    public int Audit(string surface, UiNode snap)
    {
        var unnamed = 0;
        foreach (var n in snap.Descendants())
        {
            if (n.IsInteractive && !InTemplateChrome(n) && n.Name.Trim().Length == 0)
            {
                unnamed++;
                lock (UnnamedControls)
                    UnnamedControls.Add(new Unnamed(surface, n.TypeName, n.AutomationId, n.InnerText, n.HelpText,
                        n.Ancestors().FirstOrDefault(a => a.AutomationId.Length > 0)?.AutomationId ?? ""));
            }
            if (n.Type == ControlType.ListItem || n.Type == ControlType.DataItem)
                if (ObjectDump.IsMatch(n.Name))
                    lock (DumpNames) DumpNames.Add(new DumpName(surface, n.TypeName, n.Name.Length > 90 ? n.Name[..90] + "…" : n.Name));
        }
        return unnamed;
    }

    /// <summary>Crawls one surface. <paramref name="root"/> re-finds the surface; <paramref name="restore"/> brings it back.</summary>
    public SurfaceStats Crawl(string surface, Func<AutomationElement> root, Action restore, Func<UiNode, bool>? extraSkip = null, Action? afterEach = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = new SurfaceStats { Surface = surface };
        restore();
        var snap = TreeSnapshot.Capture(root());
        var interactive = snap.Descendants().Where(n => n.IsInteractive && !InTemplateChrome(n)).ToList();
        stats.InteractiveFound = interactive.Count;
        stats.Unnamed = Audit(surface, snap);
        var candidates = Keyed(snap.Descendants().Where(n => n.IsActionable && !InTemplateChrome(n))).ToList();
        stats.Actionable = candidates.Count;
        _log($"[crawl] {surface}: {stats.InteractiveFound} interactive, {stats.Actionable} actionable, {stats.Unnamed} unnamed");

        var destructiveSeen = new HashSet<string>();
        foreach (var (key, original) in candidates)
        {
            var label = Label(original);
            if (!original.Enabled || SkipNames.IsMatch(original.Name) || (extraSkip?.Invoke(original) ?? false))
            {
                stats.Skipped++;
                Record(surface, label, "skip", original.Enabled ? "skipped by rule" : "disabled");
                continue;
            }
            // One of each kind per surface ("Remove …" in the watchlist once, not once per symbol): the key leaves out
            // the item's own name.
            if (Destructive.IsMatch(original.Name) &&
                !destructiveSeen.Add(original.Name.Split(' ')[0] + "|" + original.Signature.Split('|')[0] + "|" + original.AutomationId))
            {
                stats.Skipped++;
                Record(surface, label, "skip", "destructive — one instance only");
                continue;
            }
            if (!_app.IsAlive) break;
            UiNode? node;
            try
            {
                restore();
                node = Keyed(TreeSnapshot.Capture(root()).Descendants().Where(n => n.IsActionable && !InTemplateChrome(n))).FirstOrDefault(k => k.Key == key).Node;
            }
            catch (Exception ex)
            {
                Record(surface, label, "find", "error: " + ex.Message);
                stats.Vanished++;
                continue;
            }
            if (node is null || !node.Enabled)
            {
                stats.Vanished++;
                Record(surface, label, "find", node is null ? "vanished (tree changed)" : "disabled now");
                continue;
            }

            var logMark = _app.Log.Mark();
            var action = "";
            string outcome;
            try
            {
                action = Act(node);
                Thread.Sleep(350);
                outcome = "ok";
            }
            catch (Exception ex) when (Wait.IsTransient(ex))
            {
                outcome = "pattern error: " + ex.GetType().Name + ": " + ex.Message;
            }
            stats.Invoked++;

            if (!_app.IsAlive)
            {
                AddProblem(surface, label, $"process exited after {action}");
                Record(surface, label, action, "PROCESS EXITED");
                break;
            }
            var crashes = _app.Log.CrashesSince(logMark);
            if (crashes.Count > 0)
            {
                AddProblem(surface, label, "unhandled exception: " + crashes[0]);
                outcome += " | CRASH LOGGED";
            }
            if (!Responsive())
            {
                AddProblem(surface, label, "UI thread did not answer UI Automation within 5 s");
                outcome += " | UNRESPONSIVE";
            }
            try { afterEach?.Invoke(); } catch (Exception ex) { outcome += " | cleanup: " + ex.Message; }
            Record(surface, label, action, outcome);
        }
        stats.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        lock (Stats) Stats.Add(stats);
        _log($"[crawl] {surface}: invoked {stats.Invoked}, skipped {stats.Skipped}, vanished {stats.Vanished} in {stats.Seconds}s");
        return stats;
    }

    private static string Act(UiNode node)
    {
        var el = node.Element;
        if (node.CanInvoke) { ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return "Invoke"; }
        if (node.CanToggle)
        {
            // Flip and flip back, so the crawl leaves the profile's switches (close to tray, start with Windows…) as it
            // found them for the tests that run after it.
            var toggle = (TogglePattern)el.GetCurrentPattern(TogglePattern.Pattern);
            var before = toggle.Current.ToggleState;
            toggle.Toggle();
            Thread.Sleep(300);
            try
            {
                if (toggle.Current.ToggleState != before) toggle.Toggle();
            }
            catch (Exception ex) when (Wait.IsTransient(ex)) { return "Toggle (the switch was rebuilt; not toggled back)"; }
            return "Toggle+back";
        }
        if (node.CanExpand)
        {
            var p = (ExpandCollapsePattern)el.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (p.Current.ExpandCollapseState == ExpandCollapseState.Expanded) { p.Collapse(); Thread.Sleep(250); p.Expand(); }
            else { p.Expand(); Thread.Sleep(350); p.Collapse(); }
            return "Expand+Collapse";
        }
        if (node.CanSelect) { ((SelectionItemPattern)el.GetCurrentPattern(SelectionItemPattern.Pattern)).Select(); return "Select"; }
        return "none";
    }

    private bool Responsive()
    {
        var task = Task.Run(() =>
        {
            var main = _app.TryMain() ?? _app.TryFlyout() ?? _app.TryPalette();
            return main is null || Ui.NameOf(main).Length >= 0;
        });
        return task.Wait(TimeSpan.FromSeconds(5));
    }

    private static void Record(string surface, string control, string action, string outcome)
    {
        lock (Touches) Touches.Add(new Touch(surface, control, action, outcome));
    }

    private static void AddProblem(string surface, string control, string issue)
    {
        lock (Problems) Problems.Add(new Problem(surface, control, issue));
    }

    public static void WriteResults()
    {
        Results.WriteJson("crawler.json", new { Stats, Problems, Touches });
        Results.WriteJson("a11y-unnamed.json", UnnamedControls);
        Results.WriteJson("a11y-object-dump-names.json", DumpNames);
    }
}
