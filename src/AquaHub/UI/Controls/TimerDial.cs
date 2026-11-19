using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AquaHub.UI.Controls;

/// <summary>Uses Slider's keyboard and UIA range support with a rotary pointer surface.</summary>
public sealed class TimerDial : Slider
{
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (CaptureMouse()) Aim(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Aim(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        Aim(e);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Aim(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var dx = p.X - ActualWidth / 2;
        var dy = p.Y - ActualHeight / 2;
        if (dx * dx + dy * dy < 64) return;
        var degrees = (Math.Atan2(dy, dx) * 180 / Math.PI - 135 + 720) % 360;
        if (degrees > 270) degrees = degrees >= 315 ? 0 : 270;
        Value = Math.Round(Minimum + (Maximum - Minimum) * degrees / 270);
    }
}
