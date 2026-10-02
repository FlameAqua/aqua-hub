using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Models;

namespace AquaHub.UI.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action run) : this(_ => run()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
}

/// <summary>Display formatting shared by all views.</summary>
public static class Fmt
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    public static string Price(double v, string currency = "")
    {
        var digits = Math.Abs(v) switch { >= 1000 => 0, >= 100 => 2, >= 1 => 2, _ => 4 };
        if (Math.Abs(v) >= 1000 && Math.Abs(v) < 100000) digits = 2;
        var s = v.ToString("N" + digits, C);
        return currency switch
        {
            "USD" => "$" + s,
            "EUR" => "€" + s,
            "GBP" => "£" + s,
            "GBp" => s + "p",
            "" => s,
            _ => s,
        };
    }

    /// <summary>
    /// One price in full, for hover read-outs: always cents (€234.56, $6,512.30), four decimals below 1, pence for GBp,
    /// and the currency code for currencies without a symbol here.
    /// </summary>
    public static string PriceExact(double v, string currency = "") => Core.Util.Money.Exact(v, currency, C);

    public static string Pct(double v, int decimals = 2) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("N" + decimals, C) + "%";
    public static string Signed(double v, int decimals = 2) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("N" + decimals, C);
    public static string Prob(double p) => (p * 100).ToString(p is > 0 and < 0.01 ? "0.0" : "0", C) + "%";
    public static string Points(double? change)
    {
        if (change is null) return "";
        // Round half away from zero (0.5 pt → 1 pt) and hide anything that rounds to nothing ("▲ 0 pts").
        var pts = (int)Math.Round(Math.Abs(change.Value * 100), MidpointRounding.AwayFromZero);
        if (pts == 0) return "";
        return (change > 0 ? "▲ " : "▼ ") + pts.ToString(C) + (pts == 1 ? " pt" : " pts");
    }

    public static string Compact(double v) => Math.Abs(v) switch
    {
        >= 1e9 => (v / 1e9).ToString("0.#", C) + "B",
        >= 1e6 => (v / 1e6).ToString("0.#", C) + "M",
        >= 1e3 => (v / 1e3).ToString("0.#", C) + "K",
        _ => v.ToString("0", C),
    };

    public static string Bytes(double bytes) => bytes switch
    {
        >= 1024d * 1024 * 1024 => (bytes / 1073741824).ToString("0.0", C) + " GB",
        >= 1024 * 1024 => (bytes / 1048576).ToString("0.0", C) + " MB",
        >= 1024 => (bytes / 1024).ToString("0", C) + " KB",
        _ => bytes.ToString("0", C) + " B",
    };

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Temp(double t) => Math.Round(t).ToString("0", C) + "°";

    public static string Uptime(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m";

    public static string Clock(DateTimeOffset t, bool use24) => t.ToLocalTime().ToString(use24 ? "HH:mm" : "h:mm tt", C);

    public static string Greeting(DateTime now) => now.Hour switch
    {
        < 5 => "Good night",
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        < 22 => "Good evening",
        _ => "Good night",
    };

    public static string EventWhen(HubEvent e, bool use24)
    {
        var local = e.Start.ToLocalTime();
        var today = DateTime.Today;
        var day = local.Date == today ? "Today" : local.Date == today.AddDays(1) ? "Tomorrow" : local.ToString("ddd d MMM", C);
        return e.AllDay ? day : $"{day} · {local.ToString(use24 ? "HH:mm" : "h:mm tt", C)}";
    }

    public static string Platform(string platform) => platform switch
    {
        "reddit" => "Reddit",
        "mastodon" => "Mastodon",
        "bluesky" => "Bluesky",
        "hackernews" => "Hacker News",
        "youtube" => "YouTube",
        _ => "Feed",
    };

    public static string Category(string category) => category switch
    {
        "local" => "Local",
        "world" => "World",
        "europe" => "Europe",
        "business" => "Business",
        "tech" => "Tech",
        "gaming" => "Gaming & internet",
        "science" => "Science",
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(category),
    };

    public static Brush Res(string key, Brush? fallback = null) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback ?? Brushes.Gray;

    public static Brush UpDown(double v) => Res(v >= 0 ? "B.Up" : "B.Down");
    public static Brush UpDownSoft(double v) => Res(v >= 0 ? "B.UpSoft" : "B.DownSoft");
    public static Brush CategoryBrush(string category) => Res("B.Cat." + category, Res("B.Accent"));
    public static Brush PlatformBrush(string platform) => Res("B.Plat." + platform, Res("B.Plat.rss"));

    public static Brush StanceBrush(string stance) => Res(stance switch
    {
        "bullish" => "B.Up",
        "bearish" => "B.Down",
        "watch" => "B.Warn",
        _ => "B.Neutral",
    });

    public static Brush StanceSoft(string stance) => Res(stance switch
    {
        "bullish" => "B.UpSoft",
        "bearish" => "B.DownSoft",
        "watch" => "B.WarnSoft",
        _ => "B.NeutralSoft",
    });

    public static Brush SentimentBrush(string s) => Res(s switch
    {
        "positive" => "B.Up",
        "negative" => "B.Down",
        "mixed" => "B.Warn",
        _ => "B.Neutral",
    });

    public static Brush LoadBrush(double percent) => Res(percent switch
    {
        >= 90 => "B.Down",
        >= 75 => "B.Warn",
        _ => "B.Accent",
    });
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool x => x,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i > 0,
            System.Collections.ICollection col => col.Count > 0,
            null => false,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
