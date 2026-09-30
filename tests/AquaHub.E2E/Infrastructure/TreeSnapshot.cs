using System.Text;
using System.Windows;

namespace AquaHub.E2E.Infrastructure;

/// <summary>A cached copy of one UI Automation element (one cross-process round trip for the whole tree).</summary>
public sealed class UiNode
{
    public required AutomationElement Element { get; init; }
    public UiNode? Parent { get; init; }
    public string Name { get; init; } = "";
    public string AutomationId { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string HelpText { get; init; } = "";
    public ControlType Type { get; init; } = ControlType.Custom;
    public bool Enabled { get; init; }
    public bool Offscreen { get; init; }
    public Rect Bounds { get; init; }
    public bool CanInvoke { get; init; }
    public bool CanToggle { get; init; }
    public bool CanSelect { get; init; }
    public bool CanExpand { get; init; }
    public bool HasValue { get; init; }
    public bool HasRange { get; init; }
    public List<UiNode> Children { get; } = new();

    public string TypeName => Type.ProgrammaticName.Replace("ControlType.", "");

    /// <summary>Interactive in the sense of the crawler (activatable through a pattern).</summary>
    public bool IsActionable => CanInvoke || CanToggle || CanSelect || CanExpand;

    public bool IsInteractive => IsActionable || HasValue || HasRange;

    public IEnumerable<UiNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public IEnumerable<UiNode> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent) yield return p;
    }

    /// <summary>Text shown inside the element (for elements whose accessible name is empty).</summary>
    public string InnerText => string.Join(" ", Descendants().Where(d => d.Type == ControlType.Text && d.Name.Length > 0).Select(d => d.Name).Take(4));

    public string Patterns => string.Join(",", new[]
    {
        CanInvoke ? "Invoke" : null, CanToggle ? "Toggle" : null, CanSelect ? "SelectionItem" : null,
        CanExpand ? "ExpandCollapse" : null, HasValue ? "Value" : null, HasRange ? "RangeValue" : null,
    }.Where(p => p is not null));

    /// <summary>A stable-ish locator used to find the element again after the tree was rebuilt.</summary>
    public string Signature
    {
        get
        {
            var anchor = Ancestors().FirstOrDefault(a => a.AutomationId.Length > 0 && !a.AutomationId.All(char.IsDigit));
            return $"{anchor?.AutomationId}/{TypeName}|{AutomationId}|{Name}|{(Name.Length == 0 ? InnerText : "")}";
        }
    }

    public override string ToString() =>
        $"{TypeName} '{Name}'" + (AutomationId.Length > 0 ? $" #{AutomationId}" : "") + (Patterns.Length > 0 ? $" [{Patterns}]" : "") +
        (Name.Length == 0 && InnerText.Length > 0 ? $" text=\"{InnerText}\"" : "");
}

public static class TreeSnapshot
{
    private static CacheRequest Request()
    {
        var cr = new CacheRequest
        {
            TreeScope = TreeScope.Element | TreeScope.Descendants,
            TreeFilter = Automation.ControlViewCondition,
            AutomationElementMode = AutomationElementMode.Full,
        };
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.AutomationIdProperty);
        cr.Add(AutomationElement.ClassNameProperty);
        cr.Add(AutomationElement.HelpTextProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.IsEnabledProperty);
        cr.Add(AutomationElement.IsOffscreenProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.IsInvokePatternAvailableProperty);
        cr.Add(AutomationElement.IsTogglePatternAvailableProperty);
        cr.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
        cr.Add(AutomationElement.IsExpandCollapsePatternAvailableProperty);
        cr.Add(AutomationElement.IsValuePatternAvailableProperty);
        cr.Add(AutomationElement.IsRangeValuePatternAvailableProperty);
        return cr;
    }

    /// <summary>Captures the control view below <paramref name="root"/> in one go.</summary>
    public static UiNode Capture(AutomationElement root)
    {
        AutomationElement cached;
        using (Request().Activate())
            cached = root.GetUpdatedCache(Request());
        return Build(cached, null);
    }

    private static T Get<T>(AutomationElement el, AutomationProperty p, T fallback)
    {
        try { return el.GetCachedPropertyValue(p, true) is T v ? v : fallback; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { return fallback; }
    }

    private static UiNode Build(AutomationElement el, UiNode? parent)
    {
        var node = new UiNode
        {
            Element = el,
            Parent = parent,
            Name = Get(el, AutomationElement.NameProperty, ""),
            AutomationId = Get(el, AutomationElement.AutomationIdProperty, ""),
            ClassName = Get(el, AutomationElement.ClassNameProperty, ""),
            HelpText = Get(el, AutomationElement.HelpTextProperty, ""),
            Type = Get(el, AutomationElement.ControlTypeProperty, ControlType.Custom),
            Enabled = Get(el, AutomationElement.IsEnabledProperty, false),
            Offscreen = Get(el, AutomationElement.IsOffscreenProperty, false),
            Bounds = Get(el, AutomationElement.BoundingRectangleProperty, Rect.Empty),
            CanInvoke = Get(el, AutomationElement.IsInvokePatternAvailableProperty, false),
            CanToggle = Get(el, AutomationElement.IsTogglePatternAvailableProperty, false),
            CanSelect = Get(el, AutomationElement.IsSelectionItemPatternAvailableProperty, false),
            CanExpand = Get(el, AutomationElement.IsExpandCollapsePatternAvailableProperty, false),
            HasValue = Get(el, AutomationElement.IsValuePatternAvailableProperty, false),
            HasRange = Get(el, AutomationElement.IsRangeValuePatternAvailableProperty, false),
        };
        AutomationElementCollection? children = null;
        try { children = el.CachedChildren; }
        catch (Exception ex) when (Wait.IsTransient(ex)) { }
        if (children is not null)
            foreach (AutomationElement c in children) node.Children.Add(Build(c, node));
        return node;
    }

    public static string Dump(UiNode root, int maxDepth = 60)
    {
        var sb = new StringBuilder();
        void Walk(UiNode n, int depth)
        {
            if (depth > maxDepth) return;
            sb.Append(new string(' ', depth * 2)).Append(n.ToString());
            if (!n.Enabled) sb.Append(" (disabled)");
            if (n.Offscreen) sb.Append(" (offscreen)");
            if (n.HelpText.Length > 0) sb.Append($" help=\"{n.HelpText}\"");
            sb.AppendLine();
            foreach (var c in n.Children) Walk(c, depth + 1);
        }
        Walk(root, 0);
        return sb.ToString();
    }
}
