using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Assistant;

// Small models are unreliable at arithmetic, dates and unit conversions, and confidently wrong when they guess. These
// tools do them exactly, on this PC, with no approval needed (they touch nothing).

/// <summary>Arithmetic: + − × ÷ ^ %, parentheses, "15% of 80", common functions (sqrt, round, log…). Pure; unit-tested.</summary>
public static partial class MathEval
{
    [GeneratedRegex(@"(?<=\d),(?=\d{3}(?!\d))")]
    private static partial Regex Thousands();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*%\s*of\s+", RegexOptions.IgnoreCase)]
    private static partial Regex PercentOf();

    /// <summary>Longer than any sum a person asks for: a model stuck repeating "(((" mustn't exhaust the stack.</summary>
    public const int MaxLength = 500;

    /// <summary>The value of <paramref name="expression"/>; throws <see cref="FormatException"/> with a plain reason when it can't.</summary>
    public static double Evaluate(string expression)
    {
        if (expression.Length > MaxLength) throw new FormatException($"that's longer than {MaxLength} characters — split it into smaller sums");
        var s = Thousands().Replace(expression, "");
        s = PercentOf().Replace(s, "$1/100*");
        s = s.Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace("**", "^");
        s = Regex.Replace(s, @"(?<=[\d)])\s*[xX]\s*(?=[\d(])", "*");
        var p = new Parser(s);
        var value = p.Expression();
        p.End();
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new FormatException("the result isn't a finite number (a division by zero?)");
        return value;
    }

    /// <summary>A result for people: up to 10 significant digits, no floating-point noise.</summary>
    public static string Format(double value)
    {
        var rounded = Math.Abs(value) >= 1e15 || Math.Abs(value) < 1e-6 && value != 0 ? value : Math.Round(value, 10);
        return rounded.ToString("G12", CultureInfo.InvariantCulture);
    }

    private sealed class Parser(string text)
    {
        private const int MaxDepth = 64;
        private int _i, _depth;

        // Brackets, function calls and powers nest by recursion; past this depth the answer is a plain error, not a crash.
        private void Enter()
        {
            if (++_depth > MaxDepth) throw new FormatException("too many brackets or powers inside each other");
        }

        private char Peek() { Skip(); return _i < text.Length ? text[_i] : '\0'; }
        private void Skip() { while (_i < text.Length && char.IsWhiteSpace(text[_i])) _i++; }
        private bool Eat(char c) { if (Peek() != c) return false; _i++; return true; }

        public void End()
        {
            if (Peek() != '\0') throw new FormatException($"didn't understand “{text[_i..]}”");
        }

        public double Expression()
        {
            Enter();
            var v = Term();
            while (true)
            {
                if (Eat('+')) v += Term();
                else if (Eat('-')) v -= Term();
                else
                {
                    _depth--;
                    return v;
                }
            }
        }

        private double Term()
        {
            var v = Power();
            while (true)
            {
                if (Eat('*')) v *= Power();
                else if (Eat('/')) v /= Power();
                else if (Peek() == '(' ) v *= Power(); // 2(3+4)
                else return v;
            }
        }

        // A sign applies after the power, as in maths: -2^2 = -(2^2) = -4, and 2^-1 = 0.5. Signs are read in a loop,
        // not by recursion: "- - - 5" can be as long as the expression.
        private double Power()
        {
            var negative = false;
            while (true)
            {
                if (Eat('-')) negative = !negative;
                else if (!Eat('+')) break;
            }
            var v = Postfix();
            if (Eat('^'))
            {
                Enter();
                v = Math.Pow(v, Power()); // right-associative
                _depth--;
            }
            return negative ? -v : v;
        }

        private double Postfix()
        {
            var v = Primary();
            while (true)
            {
                if (Eat('%')) v /= 100;
                else if (Eat('!'))
                {
                    if (v < 0 || v > 170 || v != Math.Floor(v)) throw new FormatException("factorials need a whole number from 0 to 170");
                    var f = 1.0;
                    for (var k = 2; k <= (int)v; k++) f *= k;
                    v = f;
                }
                else return v;
            }
        }

        private double Primary()
        {
            if (Eat('('))
            {
                var v = Expression();
                if (!Eat(')')) throw new FormatException("a bracket isn't closed");
                return v;
            }
            Skip();
            var start = _i;
            if (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] == '.'))
            {
                while (_i < text.Length && (char.IsDigit(text[_i]) || text[_i] == '.')) _i++;
                if (_i < text.Length && text[_i] is 'e' or 'E' && _i + 1 < text.Length && (char.IsDigit(text[_i + 1]) || text[_i + 1] is '-' or '+'))
                {
                    _i += 2;
                    while (_i < text.Length && char.IsDigit(text[_i])) _i++;
                }
                if (!double.TryParse(text[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) throw new FormatException($"“{text[start.._i]}” isn't a number");
                return n;
            }
            while (_i < text.Length && char.IsLetter(text[_i])) _i++;
            var name = text[start.._i].ToLowerInvariant();
            if (name.Length == 0) throw new FormatException(_i < text.Length ? $"didn't expect “{text[_i]}”" : "the expression ends too soon");
            if (name is "pi" or "π") return Math.PI;
            if (name == "e" && Peek() != '(') return Math.E;
            if (!Eat('(')) throw new FormatException($"unknown name “{name}”");
            var args = new List<double> { Expression() };
            while (Eat(',')) args.Add(Expression());
            if (!Eat(')')) throw new FormatException($"{name}( isn't closed");
            double One() => args.Count == 1 ? args[0] : throw new FormatException($"{name} takes one number");
            return name switch
            {
                "sqrt" => Math.Sqrt(One()), "cbrt" => Math.Cbrt(One()), "abs" => Math.Abs(One()),
                "round" => Math.Round(args[0], args.Count > 1 ? (int)args[1] : 0, MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(One()), "ceil" or "ceiling" => Math.Ceiling(One()),
                "min" => args.Min(), "max" => args.Max(), "avg" or "mean" or "average" => args.Average(), "sum" => args.Sum(),
                "ln" => Math.Log(One()), "log" => args.Count > 1 ? Math.Log(args[0], args[1]) : Math.Log10(args[0]), "exp" => Math.Exp(One()),
                "sin" => Math.Sin(One()), "cos" => Math.Cos(One()), "tan" => Math.Tan(One()), "pow" => args.Count == 2 ? Math.Pow(args[0], args[1]) : throw new FormatException("pow takes two numbers"),
                _ => throw new FormatException($"unknown function “{name}”"),
            };
        }
    }
}

/// <summary>Dates: parsing what people write, differences, adding periods. Pure; unit-tested.</summary>
public static partial class DateMath
{
    private static readonly string[] Formats =
    {
        "yyyy-MM-dd", "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d MMM yyyy", "d MMMM yyyy", "MMM d yyyy", "MMMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy",
        "d MMM", "d MMMM", "MMM d", "MMMM d", "dddd d MMMM yyyy", "ddd d MMM yyyy",
    };

    [GeneratedRegex(@"^(?<next>next |this |last )?(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday)$", RegexOptions.IgnoreCase)]
    private static partial Regex Weekday();

    // "2h35m" has no word boundary after the "h", so a unit ends where the letters do. A lone "m" is minutes ("mo" is months).
    [GeneratedRegex(@"(?<n>[+-]?\d+(?:\.\d+)?)\s*(?<unit>years?|yrs?|months?|mos?|weeks?|wks?|days?|d|hours?|hrs?|h|minutes?|mins?|m)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex Period();

    // A clock time at the end: "14:10", "today 14:10", "2026-09-30T14:10:00Z", "30 Sep 2026, 9:05 pm".
    [GeneratedRegex(@"(?:^|[\sT@])(?<h>\d{1,2}):(?<m>\d{2})(?::(?<s>\d{2}))?(?:\.\d+)?\s*(?<ampm>[ap]\.?m\.?)?\s*(?:z|[+-]\d{2}:?\d{2})?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"(?:^|[\s@])(?<h>\d{1,2})\s*(?<ampm>[ap]\.?m\.?)$", RegexOptions.IgnoreCase)]
    private static partial Regex HourAmPm();

    /// <summary>
    /// A date, and a time when one is given: "today", "tomorrow", "next friday", "30 Sep 2026", "2026-09-30",
    /// "30/09/2026" (day first), with an optional time ("14:10", "today at 9pm", "2026-09-30T14:10"); "now" is now.
    /// </summary>
    public static DateTime? Parse(string text, DateTime today)
    {
        var t = Regex.Replace(text.Trim().ToLowerInvariant(), @"(\d)(st|nd|rd|th)\b", "$1").Replace(",", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t is "now" or "right now") return today;
        TimeSpan? time = null;
        foreach (var match in new[] { ClockTime().Match(t), HourAmPm().Match(t) })
        {
            if (!match.Success || Clock(match) is not { } clock) continue;
            time = clock;
            t = Regex.Replace(t[..match.Index], @"(?:\s+at)?\s*$", "").Trim();
            break;
        }
        return ParseDate(t, today) is { } date ? (time is { } at ? date.Date + at : date) : null;
    }

    private static TimeSpan? Clock(Match m)
    {
        var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var s = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups["ampm"].Value is { Length: > 0 } ampm)
        {
            if (h is < 1 or > 12) return null;
            h = ampm[0] == 'p' ? h % 12 + 12 : h % 12;
        }
        return h <= 23 && min <= 59 && s <= 59 ? new TimeSpan(h, min, s) : null;
    }

    private static DateTime? ParseDate(string t, DateTime today)
    {
        if (t.Length == 0 || t == "today") return today.Date;
        if (t == "tomorrow") return today.Date.AddDays(1);
        if (t == "yesterday") return today.Date.AddDays(-1);
        if (Weekday().Match(t) is { Success: true } w)
        {
            var target = Enum.Parse<DayOfWeek>(w.Groups["day"].Value, ignoreCase: true);
            var delta = ((int)target - (int)today.DayOfWeek + 7) % 7;
            if (w.Groups["next"].Value.Trim() == "last") return today.Date.AddDays(delta == 0 ? -7 : delta - 7);
            if (delta == 0 && w.Groups["next"].Value.Trim() == "next") delta = 7;
            return today.Date.AddDays(delta);
        }
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("en-IE"), CultureInfo.InvariantCulture })
            if (DateTime.TryParseExact(t, Formats, culture, DateTimeStyles.AllowWhiteSpaces, out var d))
                return Formats.Any(f => !f.Contains('y') && DateTime.TryParseExact(t, f, culture, DateTimeStyles.None, out _)) ? new DateTime(today.Year, d.Month, d.Day) : d.Date;
        return null;
    }

    /// <summary>Adds "3 weeks", "-10 days", "2 months 5 days", "1.5 hours"; null when there's no period in it.</summary>
    public static DateTime? Add(DateTime from, string period)
    {
        var matches = Period().Matches(period);
        if (matches.Count == 0) return null;
        var result = from;
        foreach (Match m in matches)
        {
            var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups["unit"].Value.ToLowerInvariant();
            result = unit[0] switch
            {
                'y' => result.AddYears((int)n), 'w' => result.AddDays(n * 7), 'd' => result.AddDays(n), 'h' => result.AddHours(n),
                'm' when unit.StartsWith("mo", StringComparison.Ordinal) => result.AddMonths((int)n),
                'm' => result.AddMinutes(n),
                _ => result,
            };
        }
        return result;
    }

    /// <summary>Monday to Friday between two dates (the start counted, the end not).</summary>
    public static int WorkingDays(DateTime a, DateTime b)
    {
        var (from, to) = a <= b ? (a.Date, b.Date) : (b.Date, a.Date);
        var count = 0;
        for (var d = from; d < to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) count++;
        return count;
    }

    public static string Describe(DateTime d) => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "dddd d MMMM yyyy" : "dddd d MMMM yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"2 h 35 min", "10 min", "1 day 3 h"; "minus 10 min" when the end is before the start.</summary>
    public static string Span(TimeSpan span)
    {
        var sign = span < TimeSpan.Zero ? "minus " : "";
        span = span.Duration();
        var parts = new List<string>();
        if (span.Days > 0) parts.Add(Plural.Of(span.Days, "day"));
        if (span.Hours > 0) parts.Add($"{span.Hours} h");
        if (span.Minutes > 0 || parts.Count == 0) parts.Add($"{span.Minutes} min");
        return sign + string.Join(" ", parts);
    }
}

/// <summary>Unit conversion: length, mass, volume, temperature, speed, area, data, time, energy, power, pressure. Pure; unit-tested.</summary>
public static class Units
{
    private sealed record Unit(string Dimension, double Factor, double Offset = 0);

    private static readonly Dictionary<string, Unit> Table = Build();

    private static Dictionary<string, Unit> Build()
    {
        var t = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
        void Add(string dimension, double factor, params string[] names) { foreach (var n in names) t[n] = new Unit(dimension, factor); }
        Add("length", 0.001, "mm", "millimetre", "millimetres", "millimeter", "millimeters");
        Add("length", 0.01, "cm", "centimetre", "centimetres", "centimeter", "centimeters");
        Add("length", 1, "m", "metre", "metres", "meter", "meters");
        Add("length", 1000, "km", "kilometre", "kilometres", "kilometer", "kilometers");
        Add("length", 0.0254, "in", "inch", "inches", "\"");
        Add("length", 0.3048, "ft", "foot", "feet", "'");
        Add("length", 0.9144, "yd", "yard", "yards");
        Add("length", 1609.344, "mi", "mile", "miles");
        Add("length", 1852, "nmi", "nautical mile", "nautical miles");
        Add("mass", 1e-6, "mg", "milligram", "milligrams");
        Add("mass", 0.001, "g", "gram", "grams");
        Add("mass", 1, "kg", "kilo", "kilos", "kilogram", "kilograms");
        Add("mass", 1000, "t", "tonne", "tonnes", "metric ton", "metric tons");
        Add("mass", 0.028349523125, "oz", "ounce", "ounces");
        Add("mass", 0.45359237, "lb", "lbs", "pound", "pounds");
        Add("mass", 6.35029318, "st", "stone", "stones");
        Add("volume", 0.001, "ml", "millilitre", "millilitres", "milliliter", "milliliters");
        Add("volume", 0.01, "cl", "centilitre", "centilitres");
        Add("volume", 1, "l", "litre", "litres", "liter", "liters");
        Add("volume", 1000, "m3", "m³", "cubic metre", "cubic metres");
        Add("volume", 0.00492892159375, "tsp", "teaspoon", "teaspoons");
        Add("volume", 0.01478676478125, "tbsp", "tablespoon", "tablespoons");
        Add("volume", 0.2365882365, "cup", "cups");
        Add("volume", 0.0295735295625, "fl oz", "fluid ounce", "fluid ounces");
        Add("volume", 0.56826125, "pint", "pints", "uk pint", "uk pints");
        Add("volume", 0.473176473, "us pint", "us pints");
        Add("volume", 4.54609, "gallon", "gallons", "uk gallon", "uk gallons");
        Add("volume", 3.785411784, "us gallon", "us gallons");
        Add("speed", 1, "m/s", "metres per second", "meters per second");
        Add("speed", 1 / 3.6, "km/h", "kph", "kmh", "kilometres per hour", "kilometers per hour");
        Add("speed", 0.44704, "mph", "miles per hour");
        Add("speed", 0.514444, "kn", "knot", "knots");
        Add("area", 1, "m2", "m²", "sq m", "square metre", "square metres", "square meter", "square meters");
        Add("area", 1e6, "km2", "km²", "square kilometre", "square kilometres");
        Add("area", 10000, "ha", "hectare", "hectares");
        Add("area", 4046.8564224, "acre", "acres");
        Add("area", 0.09290304, "sq ft", "ft2", "ft²", "square foot", "square feet");
        Add("data", 1, "b", "byte", "bytes");
        Add("data", 0.125, "bit", "bits");
        Add("data", 1000, "kb", "kilobyte", "kilobytes");
        Add("data", 1e6, "mb", "megabyte", "megabytes");
        Add("data", 1e9, "gb", "gigabyte", "gigabytes");
        Add("data", 1e12, "tb", "terabyte", "terabytes");
        Add("data", 1024, "kib", "kibibyte", "kibibytes");
        Add("data", 1048576, "mib", "mebibyte", "mebibytes");
        Add("data", 1073741824, "gib", "gibibyte", "gibibytes");
        Add("data", 1099511627776, "tib", "tebibyte", "tebibytes");
        Add("data", 125000, "mbit", "mbps", "megabit", "megabits");
        Add("data", 1.25e8, "gbit", "gbps", "gigabit", "gigabits");
        Add("time", 1, "s", "sec", "secs", "second", "seconds");
        Add("time", 60, "min", "mins", "minute", "minutes");
        Add("time", 3600, "h", "hr", "hrs", "hour", "hours");
        Add("time", 86400, "day", "days");
        Add("time", 604800, "week", "weeks");
        Add("energy", 1, "j", "joule", "joules");
        Add("energy", 1000, "kj", "kilojoule", "kilojoules");
        Add("energy", 4184, "kcal", "calorie", "calories", "kilocalorie", "kilocalories");
        Add("energy", 3.6e6, "kwh", "kilowatt hour", "kilowatt hours");
        Add("power", 1, "w", "watt", "watts");
        Add("power", 1000, "kw", "kilowatt", "kilowatts");
        Add("power", 745.699872, "hp", "horsepower");
        Add("pressure", 1, "pa", "pascal", "pascals");
        Add("pressure", 1000, "kpa", "kilopascal", "kilopascals");
        Add("pressure", 100000, "bar");
        Add("pressure", 6894.757293168, "psi");
        Add("pressure", 101325, "atm", "atmosphere", "atmospheres");
        foreach (var c in new[] { "c", "°c", "celsius", "centigrade" }) t[c] = new Unit("temperature", 1, 0);
        foreach (var f in new[] { "f", "°f", "fahrenheit" }) t[f] = new Unit("temperature", 5.0 / 9, -32);
        foreach (var k in new[] { "k", "kelvin" }) t[k] = new Unit("temperature", 1, -273.15);
        return t;
    }

    /// <summary><paramref name="value"/> in <paramref name="from"/> as <paramref name="to"/>; throws <see cref="FormatException"/> when the units don't fit.</summary>
    public static double Convert(double value, string from, string to)
    {
        if (!Table.TryGetValue(from.Trim(), out var a)) throw new FormatException($"unknown unit “{from}”");
        if (!Table.TryGetValue(to.Trim(), out var b)) throw new FormatException($"unknown unit “{to}”");
        if (a.Dimension != b.Dimension) throw new FormatException($"can't convert {a.Dimension} to {b.Dimension}");
        if (a.Dimension == "temperature")
        {
            var celsius = (value + a.Offset) * a.Factor;           // to °C
            return celsius / b.Factor - b.Offset;                   // from °C
        }
        return value * a.Factor / b.Factor;
    }
}

public sealed class CalculateTool : AskTool
{
    public override string Name => "calculate";
    public override string Description =>
        "Work out arithmetic exactly — never do sums in your head. + - * / ^, brackets, percentages (\"15% of 80\"), sqrt, round, min, max, log, ln, sin/cos/tan (radians), pi.";
    public override JsonObject Parameters => Schema(("expression", "string", "e.g. \"(1250 * 1.23) - 15% of 400\" or \"sqrt(2) * 10\"", true));
    public override string Icon => "hash";
    public override string Describe(JsonElement args) => "Calculate " + Quote(Arg(args, "expression"));

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var expression = Arg(args, "expression");
        try
        {
            var result = MathEval.Format(MathEval.Evaluate(expression));
            return Task.FromResult(new ToolResult($"{expression} = {result}", "= " + result));
        }
        catch (FormatException ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
    }
}

public sealed class DateMathTool : AskTool
{
    public override string Name => "date_math";
    public override string Description =>
        "Exact date and time arithmetic: the days (and weekdays) or hours and minutes between two dates or times, a date or time plus or minus a period, " +
        "or which weekday a date is. Dates like \"today\", \"next friday\", \"30 Sep 2026\", \"2026-12-25\", optionally with a time (\"14:10\", \"today 9pm\"); " +
        "periods like \"3 weeks\", \"-10 days\", \"2 months\", \"2 h 35 min\".";
    public override JsonObject Parameters => Schema(
        ("from", "string", "The start date (default: today)", false),
        ("to", "string", "Optional: the end date — to get the difference", false),
        ("add", "string", "Optional: a period to add to the start (negative to subtract)", false));
    public override string Icon => "upcoming";
    public override string Describe(JsonElement args) =>
        Arg(args, "to") is { Length: > 0 } to ? $"Count the days to {HtmlText.Truncate(to, 30)}"
        : Arg(args, "add") is { Length: > 0 } add ? $"Work out the date {HtmlText.Truncate(add, 30)} from {(Arg(args, "from") is { Length: > 0 } f ? f : "today")}"
        : "Work out the date";

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        var today = run.Now.LocalDateTime;
        var fromText = Arg(args, "from");
        if (DateMath.Parse(fromText, today) is not { } from) return Task.FromResult(ToolResult.Fail($"couldn't read the date “{fromText}”"));
        if (Arg(args, "to") is { Length: > 0 } toText)
        {
            if (DateMath.Parse(toText, today) is not { } to) return Task.FromResult(ToolResult.Fail($"couldn't read the date “{toText}”"));
            // Times of day: the exact gap ("From 13:50 to 16:55: 3 h 5 min").
            if (from.TimeOfDay != TimeSpan.Zero || to.TimeOfDay != TimeSpan.Zero)
            {
                var gap = DateMath.Span(to - from);
                return Task.FromResult(new ToolResult($"From {DateMath.Describe(from)} to {DateMath.Describe(to)}: {gap}.", gap));
            }
            var days = (to.Date - from.Date).Days;
            var weeks = Math.Abs(days) / 7;
            var text = $"From {DateMath.Describe(from)} to {DateMath.Describe(to)}: {days} days" +
                       (Math.Abs(days) >= 7 ? $" ({weeks} weeks and {Math.Abs(days) % 7} days)" : "") +
                       $"; {DateMath.WorkingDays(from, to)} working days (Mon–Fri, not counting holidays).";
            return Task.FromResult(new ToolResult(text, $"{days} days"));
        }
        if (Arg(args, "add") is { Length: > 0 } period)
        {
            if (DateMath.Add(from, period) is not { } result) return Task.FromResult(ToolResult.Fail($"couldn't read the period “{period}”"));
            return Task.FromResult(new ToolResult($"{DateMath.Describe(from)} + {period} = {DateMath.Describe(result)}", DateMath.Describe(result)));
        }
        var iso = ISOWeek.GetWeekOfYear(from);
        return Task.FromResult(new ToolResult($"{DateMath.Describe(from)} (week {iso}, day {from.DayOfYear} of the year).", from.DayOfWeek.ToString()));
    }
}

public sealed class ConvertUnitsTool : AskTool
{
    public override string Name => "convert_units";
    public override string Description =>
        "Convert between units exactly: length, weight, volume (UK and US pints/gallons), temperature, speed, area, data sizes, time, energy, power, pressure.";
    public override JsonObject Parameters => Schema(
        ("value", "number", "The amount", true),
        ("from", "string", "The unit it's in, e.g. miles, lb, °F, GB, kWh", true),
        ("to", "string", "The unit wanted, e.g. km, kg, °C, MB, kcal", true));
    public override string Icon => "layers";
    public override string Describe(JsonElement args) => $"Convert {Arg(args, "value")} {Arg(args, "from")} to {Arg(args, "to")}";

    public override Task<ToolResult> RunAsync(JsonElement args, AskRun run, CancellationToken ct)
    {
        if (!double.TryParse(Arg(args, "value").Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return Task.FromResult(ToolResult.Fail("the value isn't a number"));
        try
        {
            var result = Units.Convert(value, Arg(args, "from"), Arg(args, "to"));
            var text = $"{MathEval.Format(value)} {Arg(args, "from")} = {MathEval.Format(Math.Round(result, 6))} {Arg(args, "to")}";
            return Task.FromResult(new ToolResult(text, text));
        }
        catch (FormatException ex) { return Task.FromResult(ToolResult.Fail(ex.Message)); }
    }
}
