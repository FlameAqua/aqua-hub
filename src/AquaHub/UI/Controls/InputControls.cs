using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AquaHub.UI.Controls;

/// <summary>
/// Records a shortcut for any app: click it and press the keys (e.g. Ctrl+Alt+H) instead of typing their names.
/// Esc puts back what was there, Backspace or Delete leaves it empty (no shortcut), Tab still moves focus. Keys that
/// <see cref="Check"/> refuses aren't taken; <see cref="Problem"/> says why.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty DefaultGestureProperty =
        DependencyProperty.Register(nameof(DefaultGesture), typeof(string), typeof(HotkeyBox), new PropertyMetadata(""));

    /// <summary>True while a box has the keyboard, so Aqua can let go of its own shortcuts and they can be recorded too.</summary>
    public static event Action<bool>? Listening;

    private string _before = "";
    /// <summary>Keys pressed while listening, to notice a key let go whose press went to another app.</summary>
    private readonly HashSet<Key> _down = new();

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        UiProps.SetPlaceholder(this, "Press a shortcut…");
        ToolTip = "Click, then press the keys. Esc cancels, Backspace clears.";
    }

    public string DefaultGesture { get => (string)GetValue(DefaultGestureProperty); set => SetValue(DefaultGestureProperty, value); }

    /// <summary>Why the keys can't be used, or null when they can.</summary>
    public Func<string, string?>? Check { get; set; }

    /// <summary>Why the last keys weren't taken, or null once a shortcut is.</summary>
    public event Action<string?>? Problem;

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _before = Text;
        _down.Clear();
        Listening?.Invoke(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Listening?.Invoke(false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) return;
        e.Handled = true;
        _down.Add(key);
        switch (key)
        {
            case Key.Escape:
                Problem?.Invoke(null);
                Commit(_before);
                MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            case Key.Back or Key.Delete:
                Problem?.Invoke(null);
                Commit("");
                return;
        }
        if (Gesture(key) is not { } gesture) return;
        Take(gesture);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Windows hands a shortcut's press to the app that registered it, but the release still arrives here.
        if (!_down.Remove(key) && IsKeyboardFocused && Gesture(key) is { } gesture)
            Problem?.Invoke($"Windows or another app already uses {gesture}.");
        base.OnPreviewKeyUp(e);
    }

    /// <summary>Puts back <see cref="DefaultGesture"/>, if nothing else has it.</summary>
    public void Reset()
    {
        if (Text != DefaultGesture) Take(DefaultGesture);
    }

    private void Take(string gesture)
    {
        if (Check?.Invoke(gesture) is { } problem)
        {
            Problem?.Invoke(problem);
            return;
        }
        Problem?.Invoke(null);
        Commit(gesture);
    }

    private void Commit(string gesture)
    {
        Text = gesture;
        GetBindingExpression(TextProperty)?.UpdateSource();
    }

    /// <summary>"Ctrl+Alt+Shift+Win+K" for a key pressed with what's held now; null for a lone key or a modifier.</summary>
    private static string? Gesture(Key key)
    {
        var name = KeyName(key);
        var mods = Keyboard.Modifiers;
        var win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        // Shortcuts for any app need Ctrl, Alt or Win so they don't swallow ordinary typing.
        if (name is null || ((mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 && !win)) return null;
        var parts = new List<string>();
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(name);
        return string.Join("+", parts);
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
