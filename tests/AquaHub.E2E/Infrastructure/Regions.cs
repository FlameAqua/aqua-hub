namespace AquaHub.E2E.Infrastructure;

/// <summary>
/// Locates groups of templated rows by the named header controls that surround them (e.g. story rows sit between
/// the "All news" and "Details" links on Today). Rows are the buttons that are direct children of list items.
/// </summary>
public static class Regions
{
    /// <summary>Names of row buttons found (in document order) between two anchors identified by name or inner text.</summary>
    public static List<string> RowButtonNames(AutomationElement root, string startAnchor, string? endAnchor)
    {
        var snap = TreeSnapshot.Capture(root);
        var flat = snap.Descendants().ToList();
        bool IsAnchor(UiNode n, string anchor) =>
            n.Type == ControlType.Button && (n.Name == anchor || (n.Name.Length == 0 && n.InnerText.StartsWith(anchor, StringComparison.Ordinal)));
        var start = flat.FindIndex(n => IsAnchor(n, startAnchor));
        if (start < 0) throw new InvalidOperationException($"anchor '{startAnchor}' not found");
        var end = endAnchor is null ? flat.Count : flat.FindIndex(start + 1, n => IsAnchor(n, endAnchor));
        if (end < 0) end = flat.Count;
        return flat.Skip(start + 1).Take(end - start - 1)
            .Where(n => n.Type == ControlType.Button && n.Name.Length > 0 && n.Parent is { } p &&
                        (p.Type == ControlType.DataItem || p.Type == ControlType.ListItem))
            .Select(n => n.Name)
            .Distinct()
            .ToList();
    }

    /// <summary>Texts shown inside a row button (e.g. quote rows: name, symbol, price, change).</summary>
    public static List<string> TextsOf(AutomationElement element) => Ui.Texts(element);
}
