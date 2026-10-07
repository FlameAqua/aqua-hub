using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AquaHub.UI.Controls;

/// <summary>Dismiss outside the owner without WPF closing on mouse-down and reopening on the owner's Click.</summary>
public static class PopupToggle
{
    public static void Attach(Popup popup, FrameworkElement owner)
    {
        popup.StaysOpen = true;
        var dismiss = new Dismissal(owner, () => popup.IsOpen = false);
        popup.Opened += (_, _) => dismiss.Open(popup.Child);
        popup.Closed += (_, _) => dismiss.Close();
        owner.Unloaded += (_, _) => popup.IsOpen = false;
    }

    public static void Attach(ContextMenu menu, FrameworkElement owner)
    {
        menu.StaysOpen = true;
        var dismiss = new Dismissal(owner, () => menu.IsOpen = false);
        // ContextMenu's MenuBase still captures the mouse even with StaysOpen. Consume the owner's
        // closing press before menu-mode dismissal can turn it into a fresh opening Click.
        void OnInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is not MouseButtonEventArgs { ChangedButton: MouseButton.Left, ButtonState: MouseButtonState.Pressed } mouse
                || mouse.RoutedEvent != Mouse.PreviewMouseDownEvent
                || !new Rect(owner.RenderSize).Contains(mouse.GetPosition(owner))) return;
            menu.IsOpen = false;
            e.Cancel();
        }
        menu.Opened += (_, _) => { dismiss.Open(menu); InputManager.Current.PreProcessInput += OnInput; };
        menu.Closed += (_, _) => { dismiss.Close(); InputManager.Current.PreProcessInput -= OnInput; };
        owner.Unloaded += (_, _) => menu.IsOpen = false;
    }

    private sealed class Dismissal(FrameworkElement owner, Action hide)
    {
        private Window? _window;
        private UIElement? _content;
        public void Open(UIElement? content)
        {
            Close(); _window = Window.GetWindow(owner); _content = content;
            if (_window is not null)
            {
                _window.PreviewMouseDown += OnMouseDown;
                _window.PreviewKeyDown += OnKey;
                _window.Deactivated += OnDeactivate;
                _window.LocationChanged += OnDeactivate;
            }
            if (_content is not null) _content.PreviewKeyDown += OnKey;
        }
        public void Close()
        {
            if (_window is not null)
            {
                _window.PreviewMouseDown -= OnMouseDown;
                _window.PreviewKeyDown -= OnKey;
                _window.Deactivated -= OnDeactivate;
                _window.LocationChanged -= OnDeactivate;
            }
            if (_content is not null) _content.PreviewKeyDown -= OnKey;
            _window = null; _content = null;
        }
        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!owner.IsMouseOver) hide();
        }
        private void OnDeactivate(object? sender, EventArgs e) => hide();
        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            hide(); owner.Focus(); e.Handled = true;
        }
    }
}
