using System.Text.RegularExpressions;

namespace AquaHub.E2E.Infrastructure;

/// <summary>
/// Thin, forgiving wrappers over System.Windows.Automation: conditions, searches and control patterns.
/// Every search is rooted in an element that belongs to the app under test (scoped by process id upstream).
/// </summary>
public static class Ui
{
    // ───────────── Conditions ─────────────
    public static Condition Id(string automationId) => new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
    public static Condition Name(string name) => new PropertyCondition(AutomationElement.NameProperty, name);
    public static Condition Type(ControlType type) => new PropertyCondition(AutomationElement.ControlTypeProperty, type);
    public static Condition Pid(int pid) => new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
    public static Condition And(params Condition[] conditions) => conditions.Length == 1 ? conditions[0] : new AndCondition(conditions);
    public static Condition Or(params Condition[] conditions) => conditions.Length == 1 ? conditions[0] : new OrCondition(conditions);
    public static Condition Button(string name) => And(Type(ControlType.Button), Name(name));
    public static Condition Text(string text) => And(Type(ControlType.Text), Name(text));

    public static readonly Condition Interactive = Or(
        new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true),
        new PropertyCondition(AutomationElement.IsTogglePatternAvailableProperty, true),
        new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true),
        new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true),
        new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true),
        new PropertyCondition(AutomationElement.IsRangeValuePatternAvailableProperty, true));

    // ───────────── Searches ─────────────
    public static AutomationElement? Find(AutomationElement root, Condition condition, TreeScope scope = TreeScope.Descendants)
    {
        try { return root.FindFirst(scope, condition); }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return null; }
    }

    public static List<AutomationElement> FindAll(AutomationElement root, Condition condition, TreeScope scope = TreeScope.Descendants)
    {
        try { return root.FindAll(scope, condition).Cast<AutomationElement>().ToList(); }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return new List<AutomationElement>(); }
    }

    public static AutomationElement WaitFind(AutomationElement root, Condition condition, string what, TimeSpan? timeout = null) =>
        Wait.For(() => Find(root, condition), what, timeout);

    /// <summary>First element of a control type whose name satisfies a predicate.</summary>
    public static AutomationElement? FindWhere(AutomationElement root, ControlType? type, Func<string, bool> namePredicate)
    {
        var candidates = FindAll(root, type is null ? Condition.TrueCondition : Type(type));
        foreach (var el in candidates)
        {
            var name = NameOf(el);
            if (namePredicate(name)) return el;
        }
        return null;
    }

    public static List<AutomationElement> FindAllWhere(AutomationElement root, ControlType? type, Func<string, bool> namePredicate) =>
        FindAll(root, type is null ? Condition.TrueCondition : Type(type)).Where(e => namePredicate(NameOf(e))).ToList();

    public static AutomationElement? FindByRegex(AutomationElement root, ControlType? type, string pattern) =>
        FindWhere(root, type, n => Regex.IsMatch(n, pattern, RegexOptions.IgnoreCase));

    /// <summary>
    /// Finds a button through the text it shows (for buttons whose accessible name is empty: the text is the only handle).
    /// </summary>
    public static AutomationElement? ButtonWithText(AutomationElement root, string text, ControlType? type = null)
    {
        var want = type ?? ControlType.Button;
        foreach (var t in FindAll(root, Ui.Text(text)))
        {
            var parent = t;
            for (var i = 0; i < 5; i++)
            {
                try { parent = TreeWalker.ControlViewWalker.GetParent(parent); }
                catch (Exception ex) when (Wait.IsTransient(ex)) { parent = null; }
                if (parent is null) break;
                if (TypeOf(parent) == want) return parent;
            }
        }
        // Fallback: a control whose own name is the text.
        return Find(root, And(Type(want), Name(text)));
    }

    public static AutomationElement WaitButtonWithText(AutomationElement root, string text, string? what = null, TimeSpan? timeout = null) =>
        Wait.For(() => ButtonWithText(root, text), what ?? $"button showing '{text}'", timeout);

    public static AutomationElement? Parent(AutomationElement el)
    {
        try { return TreeWalker.ControlViewWalker.GetParent(el); }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return null; }
    }

    // ───────────── Properties ─────────────
    public static string NameOf(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return ""; }
    }

    public static string IdOf(AutomationElement el)
    {
        try { return el.Current.AutomationId ?? ""; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return ""; }
    }

    public static ControlType? TypeOf(AutomationElement el)
    {
        try { return el.Current.ControlType; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return null; }
    }

    public static bool IsEnabled(AutomationElement el)
    {
        try { return el.Current.IsEnabled; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return false; }
    }

    public static bool IsOffscreen(AutomationElement el)
    {
        try { return el.Current.IsOffscreen; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return true; }
    }

    /// <summary>True while the element is still part of the live tree.</summary>
    public static bool Exists(AutomationElement? el)
    {
        if (el is null) return false;
        try
        {
            _ = el.Current.ProcessId;
            var r = el.Current.BoundingRectangle;
            return !r.IsEmpty || el.Current.ControlType == ControlType.Window;
        }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return false; }
    }

    public static string Describe(AutomationElement el)
    {
        try
        {
            var c = el.Current;
            return $"{c.ControlType.ProgrammaticName.Replace("ControlType.", "")} '{c.Name}'" + (string.IsNullOrEmpty(c.AutomationId) ? "" : $" #{c.AutomationId}");
        }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return "<stale element>"; }
    }

    /// <summary>Names of all descendant text elements (what a sighted user would read).</summary>
    public static List<string> Texts(AutomationElement root) =>
        FindAll(root, Type(ControlType.Text)).Select(NameOf).Where(n => n.Length > 0).ToList();

    /// <summary>Text of rich-text documents under <paramref name="root"/> (Ask's selectable answers), via TextPattern.</summary>
    public static List<string> DocumentTexts(AutomationElement root) =>
        FindAll(root, Type(ControlType.Document)).Select(d =>
        {
            try { return d.TryGetCurrentPattern(TextPattern.Pattern, out var p) ? ((TextPattern)p).DocumentRange.GetText(-1).Trim() : ""; }
            catch (Exception ex) when (Wait.IsTransient(ex))
            {
                Results.Log($"   (couldn't read a document's text: {ex.GetType().Name}: {ex.Message})");
                return "";
            }
        }).Where(t => t.Length > 0).ToList();

    /// <summary>Values of read-only text boxes (Ask shows your questions as selectable, copyable text).</summary>
    public static List<string> ReadOnlyEditTexts(AutomationElement root) =>
        FindAll(root, Type(ControlType.Edit)).Where(e =>
        {
            try { return e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && ((ValuePattern)p).Current.IsReadOnly; }
            catch (Exception ex) when (Wait.IsTransient(ex)) { return false; }
        }).Select(ValueOf).Where(t => t.Length > 0).ToList();

    /// <summary>Plain texts, document texts and read-only text boxes — everything a reader sees in a region.</summary>
    public static List<string> AllTexts(AutomationElement root) => Texts(root).Concat(DocumentTexts(root)).Concat(ReadOnlyEditTexts(root)).ToList();

    public static bool HasText(AutomationElement root, string text) => Find(root, Ui.Text(text)) is not null;

    public static bool HasTextMatching(AutomationElement root, string pattern) =>
        Texts(root).Any(t => Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase));

    // ───────────── Patterns ─────────────
    private static T Pattern<T>(AutomationElement el, AutomationPattern pattern) where T : BasePattern
    {
        if (el.TryGetCurrentPattern(pattern, out var p)) return (T)p;
        throw new InvalidOperationException($"{Describe(el)} does not support {pattern.ProgrammaticName}");
    }

    public static bool Supports(AutomationElement el, AutomationPattern pattern)
    {
        try { return el.TryGetCurrentPattern(pattern, out _); }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return false; }
    }

    /// <summary>Activates a control the way a user would: Invoke, else Toggle, else Select.</summary>
    public static void Invoke(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return; }
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tog)) { ((TogglePattern)tog).Toggle(); return; }
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) { ((SelectionItemPattern)sel).Select(); return; }
        throw new InvalidOperationException($"{Describe(el)} cannot be invoked");
    }

    public static void Toggle(AutomationElement el) => Pattern<TogglePattern>(el, TogglePattern.Pattern).Toggle();
    public static ToggleState ToggleStateOf(AutomationElement el) => Pattern<TogglePattern>(el, TogglePattern.Pattern).Current.ToggleState;

    /// <summary>Sets a toggle to the requested state (toggling as often as needed).</summary>
    public static void SetToggle(AutomationElement el, bool on)
    {
        var want = on ? ToggleState.On : ToggleState.Off;
        for (var i = 0; i < 3 && ToggleStateOf(el) != want; i++) Toggle(el);
        if (ToggleStateOf(el) != want) throw new InvalidOperationException($"Could not set {Describe(el)} to {want}");
    }

    public static void Select(AutomationElement el) => Pattern<SelectionItemPattern>(el, SelectionItemPattern.Pattern).Select();
    public static bool IsSelected(AutomationElement el)
    {
        try { return Pattern<SelectionItemPattern>(el, SelectionItemPattern.Pattern).Current.IsSelected; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return false; }
    }

    public static void Expand(AutomationElement el) => Pattern<ExpandCollapsePattern>(el, ExpandCollapsePattern.Pattern).Expand();
    public static void Collapse(AutomationElement el) => Pattern<ExpandCollapsePattern>(el, ExpandCollapsePattern.Pattern).Collapse();
    public static ExpandCollapseState ExpandStateOf(AutomationElement el) =>
        Pattern<ExpandCollapsePattern>(el, ExpandCollapsePattern.Pattern).Current.ExpandCollapseState;

    public static void SetValue(AutomationElement el, string value) => Pattern<ValuePattern>(el, ValuePattern.Pattern).SetValue(value);
    public static string ValueOf(AutomationElement el)
    {
        try { return Pattern<ValuePattern>(el, ValuePattern.Pattern).Current.Value ?? ""; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return ""; }
    }

    public static void SetRange(AutomationElement el, double value) => Pattern<RangeValuePattern>(el, RangeValuePattern.Pattern).SetValue(value);
    public static double RangeOf(AutomationElement el) => Pattern<RangeValuePattern>(el, RangeValuePattern.Pattern).Current.Value;

    /// <summary>Scrolls an element into view (ScrollItemPattern); returns false if unsupported.</summary>
    public static bool ScrollIntoView(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var p)) { ((ScrollItemPattern)p).ScrollIntoView(); return true; }
        }
        catch (Exception ex) when (Wait.IsTransient(ex)) { }
        return false;
    }

    /// <summary>Scrolls the nearest scrollable ancestor so the element becomes visible (for elements without ScrollItemPattern).</summary>
    public static void BringIntoView(AutomationElement el)
    {
        if (!IsOffscreen(el)) return;
        if (ScrollIntoView(el) && !IsOffscreen(el)) return;
        var p = el;
        for (var depth = 0; depth < 12 && p is not null; depth++)
        {
            p = Parent(p);
            if (p is null) break;
            ScrollIntoView(p);
            if (!IsOffscreen(el)) return;
        }
    }

    // ───────────── Selection helpers ─────────────
    /// <summary>Opens a combo box, picks the item with the given name and closes it again.</summary>
    public static void SelectComboItem(AutomationElement combo, string itemName, int ownerPid)
    {
        Wait.Retry(() => Expand(combo), "expand combo");
        var item = Wait.For(() =>
            Find(combo, And(Type(ControlType.ListItem), Name(itemName)))
            ?? AppWindows.FindInPopups(ownerPid, And(Type(ControlType.ListItem), Name(itemName))),
            $"combo item '{itemName}'");
        Select(item);
        try { if (ExpandStateOf(combo) == ExpandCollapseState.Expanded) Collapse(combo); } catch (Exception ex) when (Wait.IsTransient(ex)) { }
    }

    /// <summary>Name of the selected combo box item (via SelectionPattern).</summary>
    public static string SelectedComboItem(AutomationElement combo)
    {
        try
        {
            if (combo.TryGetCurrentPattern(SelectionPattern.Pattern, out var p))
            {
                var sel = ((SelectionPattern)p).Current.GetSelection();
                if (sel.Length > 0) return NameOf(sel[0]);
            }
            return ValueOf(combo);
        }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return ""; }
    }
}
