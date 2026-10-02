using System.Globalization;

namespace AquaHub.Core.Util;

/// <summary>Numbers as people type them: "0.5" or "0,5", "1,234.50" or "1.234,50", "€150".</summary>
public static class Numbers
{
    /// <summary>
    /// Reads a typed number. With both "." and "," the last one is the decimal point; with only one kind, the
    /// <paramref name="culture"/> decides, except that a lone "," or "." without exactly three digits after it is
    /// always a decimal point ("0,5" anywhere, but "1,234" is a thousand in English).
    /// </summary>
    public static bool TryParse(string? text, CultureInfo culture, out double value)
    {
        value = 0;
        var s = new string((text ?? "").Where(c => !char.IsWhiteSpace(c) && c is not ('\'' or '’' or '$' or '€' or '£' or '¥')).ToArray());
        if (s.Length == 0) return false;
        int dots = s.Count(c => c == '.'), commas = s.Count(c => c == ',');
        var decimalComma = culture.NumberFormat.NumberDecimalSeparator == ",";
        char? point;
        if (dots > 0 && commas > 0) point = s.LastIndexOf('.') > s.LastIndexOf(',') ? '.' : ',';
        else if (dots + commas == 0) point = null;
        else
        {
            var sep = dots > 0 ? '.' : ',';
            var count = Math.Max(dots, commas);
            var afterLast = s.Length - s.LastIndexOf(sep) - 1;
            var looksLikeThousands = count > 1 || afterLast == 3;
            var cultureThousands = sep == (decimalComma ? '.' : ',');
            point = looksLikeThousands && cultureThousands ? null : count > 1 ? null : sep;
        }
        var normal = point switch
        {
            '.' => s.Replace(",", ""),
            ',' => s.Replace(".", "").Replace(',', '.'),
            _ => s.Replace(",", "").Replace(".", ""),
        };
        return double.TryParse(normal, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    /// <summary>A value for an input box: no thousands separators and no trailing zeros ("0.5", "1234.25").</summary>
    public static string Format(double? value, CultureInfo culture) =>
        value is { } v ? v.ToString("0.########", culture) : "";
}
