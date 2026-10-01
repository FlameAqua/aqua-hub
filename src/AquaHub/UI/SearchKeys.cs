using System.Windows.Controls;
using System.Windows.Input;

namespace AquaHub.UI;

/// <summary>
/// Keyboard support for "type to search, pick from a list" popups (add symbol, city search, app picker):
/// ↓/↑ move through the results while the text box keeps focus, Enter picks (the first result if none is
/// highlighted) and Esc closes. In the list itself, Enter/Space pick the highlighted result.
/// </summary>
public static class SearchKeys
{
    public static void Attach(TextBox box, ListBox results, Action pick, Action? close = null)
    {
        box.PreviewKeyDown += (_, e) =>
        {
            var count = results.Items.Count;
            switch (e.Key)
            {
                case Key.Down when count > 0:
                    Move(results, +1);
                    e.Handled = true;
                    break;
                case Key.Up when count > 0:
                    Move(results, -1);
                    e.Handled = true;
                    break;
                case Key.Enter when count > 0 && results.IsVisible:
                    if (results.SelectedIndex < 0) results.SelectedIndex = 0;
                    pick();
                    e.Handled = true;
                    break;
                case Key.Escape when close is not null:
                    close();
                    e.Handled = true;
                    break;
            }
        };
        // With focus in the list itself (Tab, a click, or assistive tech selecting an item): Enter or Space picks the
        // highlighted result, Esc closes and returns to the search box.
        results.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter or Key.Space when results.SelectedIndex >= 0:
                    pick();
                    e.Handled = true;
                    break;
                case Key.Escape when close is not null:
                    close();
                    box.Focus();
                    e.Handled = true;
                    break;
            }
        };
        // A new result list starts with nothing highlighted.
        box.TextChanged += (_, _) => results.SelectedIndex = -1;
    }

    private static void Move(ListBox results, int delta)
    {
        var next = Math.Clamp(results.SelectedIndex + delta, 0, results.Items.Count - 1);
        results.SelectedIndex = next;
        results.ScrollIntoView(results.Items[next]);
    }
}
