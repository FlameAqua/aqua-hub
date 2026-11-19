using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;

namespace AquaHub.UI.Controls;

/// <summary>Exposes the same movement as dragging through the standard UI Automation Move action.</summary>
public sealed class DeckGrip : Thumb
{
    internal Action<Point>? MoveRequested { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new GripPeer(this);

    private sealed class GripPeer(DeckGrip owner) : ThumbAutomationPeer(owner), ITransformProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Transform ? this : base.GetPattern(patternInterface);

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
