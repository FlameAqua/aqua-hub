using System.Globalization;

namespace AquaHub.Core.Util;

/// <summary>Numbers as people type them: "0.5" or "0,5", "1,234.50" or "1.234,50", "€150".</summary>
public static class Numbers
{
    /// <summary>
    /// Reads a typed number. With both "." and "," the last one is the decimal point and the other must group thousands;
    /// with only one kind, it groups thousands only where that's the <paramref name="culture"/>'s thousands sign, and
    /// only in a real thousands group: "1,234" is a thousand in English and "1.234" in German, while "0,125" and "0,5" are
    /// fractions anywhere, and in French, Swedish, Russian or Polish (which group with a space) "12.345" is twelve and a
    /// bit. Anything else ("1.5.2") isn't a number.
    /// </summary>
    public static bool TryParse(string? text, CultureInfo culture, out double value)
    {
        value = 0;
        var s = new string((text ?? "").Where(c => !char.IsWhiteSpace(c) && c is not ('\'' or '’' or '$' or '€' or '£' or '¥')).ToArray());
        if (s.Length == 0) return false;
        int dots = s.Count(c => c == '.'), commas = s.Count(c => c == ',');
        char? grouping = culture.NumberFormat.NumberGroupSeparator switch { "," => ',', "." => '.', _ => null };
        char? point;
        if (dots > 0 && commas > 0)
        {
            point = s.LastIndexOf('.') > s.LastIndexOf(',') ? '.' : ',';
            var other = point == '.' ? ',' : '.';
            var whole = s[..s.LastIndexOf(point.Value)];
            if (whole.Contains(point.Value) || !Grouped(whole, other)) return false;
        }
        else if (dots + commas == 0) point = null;
        else
        {
            var sep = dots > 0 ? '.' : ',';
            if (Math.Max(dots, commas) > 1)
            {
                if (!Grouped(s, sep)) return false;
                point = null;
            }
            else point = sep == grouping && s.Length - s.IndexOf(sep) - 1 == 3 && Grouped(s, sep) ? null : sep;
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

    /// <summary>
    /// Digits grouped in thousands by <paramref name="separator"/>: one to three digits that don't start with 0, then
    /// groups of three ("1,234,567"), or of two and a last three as in India ("1,00,000").
    /// </summary>
    private static bool Grouped(string number, char separator)
    {
        var parts = number.TrimStart('-', '+').Split(separator);
        if (parts[0] is not { Length: >= 1 and <= 3 } head || head[0] == '0' || !parts.All(p => p.All(char.IsAsciiDigit))) return false;
        var rest = parts.Skip(1).ToList();
        return rest.Count > 0 && rest[^1].Length == 3 && (rest.All(p => p.Length == 3) || rest.SkipLast(1).All(p => p.Length == 2));
    }

    /// <summary>A value for an input box: no thousands separators and no trailing zeros ("0.5", "1234.25").</summary>
    public static string Format(double? value, CultureInfo culture) =>
        value is { } v ? v.ToString("0.########", culture) : "";
}
