using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;

namespace AquaHub.UI.Controls;

/// <summary>Exposes the same movement as dragging through the standard UI Automation Move action.</summary>
public sealed class DeckGrip : Thumb
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(DeckGrip));
    public static readonly DependencyProperty CountdownProperty = DependencyProperty.Register(nameof(Countdown), typeof(string), typeof(DeckGrip));

    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public string? Countdown { get => (string?)GetValue(CountdownProperty); set => SetValue(CountdownProperty, value); }
    internal Action<Point>? MoveRequested { get; set; }
    internal Action? ToggleRequested { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new GripPeer(this);

    private sealed class GripPeer(DeckGrip owner) : ThumbAutomationPeer(owner), ITransformProvider, IInvokeProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface is PatternInterface.Transform or PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        public void Invoke() => owner.Dispatcher.BeginInvoke(() => owner.ToggleRequested?.Invoke());

        public bool CanMove => true;
        public bool CanResize => false;
        public bool CanRotate => false;

        public void Move(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
            owner.MoveRequested?.Invoke(new(x, y));
        }

        public void Resize(double width, double height) => throw new InvalidOperationException();
        public void Rotate(double degrees) => throw new InvalidOperationException();
    }
}
