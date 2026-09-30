using System.Globalization;
using System.Windows.Data;

namespace AquaHub.UI;

/// <summary>Converts a 0–100 percentage into a 0–1 fraction for bars.</summary>
public sealed class Percent : IValueConverter
{
    public static readonly Percent ToFraction = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? Math.Clamp(d / 100.0, 0, 1) : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
