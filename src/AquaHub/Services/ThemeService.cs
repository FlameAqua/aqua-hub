using System.Windows;
using System.Windows.Media;
using AquaHub.Core.Settings;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>Light/dark theme (following Windows by default) and accent colour (aqua, Windows accent or custom).</summary>
public sealed class ThemeService
{
    private ResourceDictionary? _colors;
    private static readonly string[] AccentKeys = { "B.Accent", "B.AccentHover", "B.AccentText", "B.AccentSoft", "B.OnAccent" };

    public bool IsDark { get; private set; } = true;
    public event Action? Changed;

    public void Apply(GeneralSettings g)
    {
        IsDark = g.Theme switch { "dark" => true, "light" => false, _ => OsSignals.IsDarkTheme() };
        var app = Application.Current;
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/UI/Theme/Colors.{(IsDark ? "Dark" : "Light")}.xaml") };
        var merged = app.Resources.MergedDictionaries;
        _colors ??= merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Colors.", StringComparison.Ordinal) == true);
        var index = _colors is null ? -1 : merged.IndexOf(_colors);
        if (index >= 0) merged[index] = dict; else merged.Insert(0, dict);
        _colors = dict;

        foreach (var key in AccentKeys) app.Resources.Remove(key);
        var accent = g.Accent switch
        {
            "system" => SystemAccent(),
            var hex when hex.StartsWith('#') => TryParse(hex),
            _ => null,
        };
        if (accent is { } c) ApplyAccent(app.Resources, c);

        foreach (Window w in app.Windows) WindowEffects.SetDark(w, IsDark);
        Changed?.Invoke();
    }

    private void ApplyAccent(ResourceDictionary res, Color baseColor)
    {
        // Any accent (Windows' or a custom hex) is adjusted until text on it, and accent-coloured text, meet WCAG AA (4.5:1).
        var page = IsDark ? Color.FromRgb(0x15, 0x17, 0x1B) : Color.FromRgb(0xF3, 0xF4, 0xF6);
        var fill = IsDark ? Mix(baseColor, Colors.White, 0.35) : Mix(baseColor, Colors.Black, 0.1);
        var onAccent = Luminance(fill) > 0.5 ? Color.FromRgb(0x10, 0x12, 0x14) : Colors.White;
        fill = EnsureContrast(fill, onAccent, 4.5);
        var text = EnsureContrast(IsDark ? Mix(baseColor, Colors.White, 0.45) : Mix(baseColor, Colors.Black, 0.2), page, 4.5);
        res["B.Accent"] = Frozen(fill);
        res["B.AccentHover"] = Frozen(IsDark ? Mix(fill, Colors.White, 0.15) : Mix(fill, Colors.Black, 0.1));
        res["B.AccentText"] = Frozen(text);
        res["B.AccentSoft"] = Frozen(Color.FromArgb(0x2E, fill.R, fill.G, fill.B));
        res["B.OnAccent"] = Frozen(onAccent);
    }

    /// <summary>Moves <paramref name="c"/> away from <paramref name="against"/> (towards black or white) until the WCAG ratio is met.</summary>
    internal static Color EnsureContrast(Color c, Color against, double ratio)
    {
        var towards = RelativeLuminance(against) > 0.18 ? Colors.Black : Colors.White;
        for (var i = 0; i < 20 && Contrast(c, against) < ratio; i++) c = Mix(c, towards, 0.08);
        return c;
    }

    internal static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static Color? SystemAccent()
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch { return null; }
    }

    private static Color? TryParse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
