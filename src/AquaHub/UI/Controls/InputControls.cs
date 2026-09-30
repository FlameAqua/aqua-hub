using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AquaHub.UI.Controls;

/// <summary>
/// Records a global shortcut: click it and press the keys (e.g. Ctrl+Alt+H) instead of typing their names.
/// Backspace/Delete restores <see cref="DefaultGesture"/>; Esc cancels; Tab still moves focus.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty DefaultGestureProperty =
        DependencyProperty.Register(nameof(DefaultGesture), typeof(string), typeof(HotkeyBox), new PropertyMetadata(""));

    private string _before = "";

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        UiProps.SetPlaceholder(this, "Press a shortcut…");
        ToolTip = "Click, then press the keys you want (Backspace restores the default)";
        GotKeyboardFocus += (_, _) => _before = Text;
    }

    public string DefaultGesture { get => (string)GetValue(DefaultGestureProperty); set => SetValue(DefaultGestureProperty, value); }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return;
        e.Handled = true;
        switch (key)
        {
            case Key.Escape:
                Commit(_before);
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            case Key.Back or Key.Delete:
                Commit(DefaultGesture);
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin:
                return;
        }
        var name = KeyName(key);
        var mods = Keyboard.Modifiers;
        var win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        // Global shortcuts need Ctrl, Alt or Win so they don't swallow ordinary typing.
        if (name is null || ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && !win)) return;
        var parts = new List<string>();
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(name);
        Commit(string.Join("+", parts));
    }

    private void Commit(string gesture)
    {
        Text = gesture;
        GetBindingExpression(TextProperty)?.UpdateSource();
    }

    private static string? KeyName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => k.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(CultureInfo.InvariantCulture),
        >= Key.F1 and <= Key.F24 => k.ToString(),
        Key.Space => "Space",
        Key.Enter => "Enter",
        _ => null,
    };
}

/// <summary>A time-of-day picker (15-minute steps) bound to an "HH:mm" string, e.g. for quiet hours.</summary>
public sealed class TimeBox : ComboBox
{
    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(nameof(Time), typeof(string), typeof(TimeBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TimeBox)d).Sync()));

    private bool _syncing;

    public TimeBox()
    {
        SetResourceReference(StyleProperty, typeof(ComboBox));
        for (var m = 0; m < 24 * 60; m += 15) Items.Add($"{m / 60:00}:{m % 60:00}");
        MinWidth = 96;
        MaxDropDownHeight = 280;
        SelectionChanged += (_, _) =>
        {
            if (!_syncing && SelectedItem is string t) Time = t;
        };
    }

    public string Time { get => (string)GetValue(TimeProperty); set => SetValue(TimeProperty, value); }

    private void Sync()
    {
        _syncing = true;
        try
        {
            if (!TimeOnly.TryParse(Time ?? "", CultureInfo.InvariantCulture, out var t)) { SelectedItem = null; return; }
            var text = t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (!Items.Contains(text))
            {
                var at = 0;
                while (at < Items.Count && string.CompareOrdinal((string)Items[at]!, text) < 0) at++;
                Items.Insert(at, text);
            }
            SelectedItem = text;
        }
        finally { _syncing = false; }
    }
}
