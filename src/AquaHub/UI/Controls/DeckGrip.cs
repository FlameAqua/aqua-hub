using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;

namespace AquaHub.UI.Controls;

/// <summary>Exposes the same movement as dragging through the standard UI Automation Move action.</summary>
public sealed class DeckGrip : Thumb
{
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
