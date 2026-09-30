using System.Globalization;
using System.Text;
using AquaHub.Core.Util;

namespace AquaHub.Core.Health;

public enum HealthLevel { Good, Info, Warning, Problem }

/// <summary>
/// One result of the health check: what it looked at, how it is, what that means and what to do. <see cref="Tool"/> names a
/// Windows tool that helps (the page offers it as a button): cleanup, storage, update, security, devices, reliability,
/// startup, taskmgr, restart-info.
/// </summary>
public sealed record HealthFinding(string Area, HealthLevel Level, string Title, string Detail, string? Fix = null, string? Tool = null);

public sealed record DriveFact(string Name, string Label, double TotalGb, double FreeGb, bool System);
public sealed record PhysicalDiskFact(string Name, string Media, string Health);
public sealed record BatteryFact(double DesignWh, double FullWh, int? Cycles);
public sealed record AntivirusFact(string Name, bool Enabled, bool UpToDate);
public sealed record CrashFact(string App, int Count);

/// <summary>What the health check found out about the PC (read-only checks). Null means that check couldn't run.</summary>
public sealed record HealthFacts
{
    public DateTimeOffset Checked { get; init; } = DateTimeOffset.Now;
    public string Machine { get; init; } = "";
    public List<DriveFact> Drives { get; init; } = new();
    public List<PhysicalDiskFact>? PhysicalDisks { get; init; }
    public BatteryFact? Battery { get; init; }
    public TimeSpan Uptime { get; init; }
    public bool? RestartPending { get; init; }
    public DateTimeOffset? LastUpdate { get; init; }
    public string? LastUpdateTitle { get; init; }
    public List<string>? FailedUpdates { get; init; }
    public int? BlueScreens { get; init; }
    public int? UnexpectedShutdowns { get; init; }
    public List<CrashFact>? AppCrashes { get; init; }
    public List<AntivirusFact>? Antivirus { get; init; }
    public List<string>? DeviceProblems { get; init; }
    public List<string>? StartupApps { get; init; }
    public double? TempGb { get; init; }
    public double? RecycleBinGb { get; init; }
    public double RamPercent { get; init; }
    public double RamTotalGb { get; init; }
    public string? TopMemoryApp { get; init; }
    public double TopMemoryMb { get; init; }
    /// <summary>Checks that couldn't run, and why ("Battery wear: Windows didn't say").</summary>
    public List<string> Skipped { get; init; } = new();
}

/// <summary>A finished health check: the facts, the findings, and the local model's summary when it wrote one.</summary>
public sealed record HealthReport(HealthFacts Facts, List<HealthFinding> Findings, string? Summary)
{
    public int Problems => Findings.Count(f => f.Level == HealthLevel.Problem);
    public int Warnings => Findings.Count(f => f.Level == HealthLevel.Warning);

    /// <summary>"Good", "2 things to look at", "1 problem, 2 warnings".</summary>
    public string Verdict => (Problems, Warnings) switch
    {
        (0, 0) => "Looks healthy",
        (0, var w) => Plural.Of(w, "thing") + " to look at",
        (var p, 0) => Plural.Of(p, "problem"),
        var (p, w) => Plural.Of(p, "problem") + ", " + Plural.Of(w, "warning"),
    };
}

/// <summary>
/// The rules of the PC health check: turns what was found (<see cref="HealthFacts"/>) into plain findings, most serious
/// first. Pure; unit-tested. Thresholds follow what Windows itself warns about (a nearly full system drive, pending
/// restarts, antivirus off) plus what support technicians look at first (crashes, failed updates, device errors).
/// </summary>
public static class PcHealth
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<HealthFinding> Evaluate(HealthFacts f)
    {
        var list = new List<HealthFinding>();

        // Drives: a nearly full system drive slows everything down and stops updates installing.
        foreach (var d in f.Drives.Where(d => d.TotalGb > 1))
        {
            var share = d.FreeGb / d.TotalGb;
            var name = d.Name + (d.Label.Length > 0 ? " (" + d.Label + ")" : "");
            var free = $"{Gb(d.FreeGb)} free of {Gb(d.TotalGb)}";
            if (d.System ? share < 0.05 || d.FreeGb < 5 : share < 0.03)
                list.Add(new("Storage", HealthLevel.Problem, $"{name} is almost full", free + (d.System ? " — Windows slows down and updates fail when the system drive fills up." : "."),
                    "Free up space with Disk Cleanup or Storage settings (Temporary files, Downloads, the Recycle Bin).", "cleanup"));
            else if (d.System ? share < 0.12 || d.FreeGb < 15 : share < 0.08)
                list.Add(new("Storage", HealthLevel.Warning, $"{name} is getting full", free + ".", "Clear temporary files and old downloads before it fills up.", "storage"));
            else
                list.Add(new("Storage", HealthLevel.Good, $"{name} has room", free + "."));
        }

        if (f.PhysicalDisks is { } disks)
        {
            foreach (var d in disks.Where(d => !d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase)))
            {
                var bad = d.Health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase);
                list.Add(new("Storage", bad ? HealthLevel.Problem : HealthLevel.Warning, $"{d.Name} reports it is {d.Health.ToLowerInvariant()}",
                    $"Windows' storage health for this {d.Media} says {d.Health}. A drive that reports trouble can fail without more warning.",
                    "Back up anything important on it now, then check it with the maker's tool.", "devices"));
            }
            if (disks.Count > 0 && disks.All(d => d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase)))
                list.Add(new("Storage", HealthLevel.Good, disks.Count == 1 ? "The drive reports healthy" : $"All {disks.Count} drives report healthy",
                    string.Join(", ", disks.Select(d => $"{d.Name} ({d.Media})")) + "."));
        }

        // Updates and restarts.
        if (f.RestartPending == true)
            list.Add(new("Updates", HealthLevel.Warning, "A restart is waiting", "Windows needs a restart to finish installing updates.", "Restart when it suits you.", "update"));
        if (f.LastUpdate is { } last)
        {
            var days = (f.Checked - last).TotalDays;
            var when = $"The last update installed {On(last, f.Checked)}" + (f.LastUpdateTitle is { Length: > 0 } t ? $": {HtmlText.Truncate(t, 90)}." : ".");
            if (days > 90) list.Add(new("Updates", HealthLevel.Problem, "Windows hasn't updated in months", when + " Security fixes come every month.", "Open Windows Update and check for updates.", "update"));
            else if (days > 45) list.Add(new("Updates", HealthLevel.Warning, "Windows is behind on updates", when, "Open Windows Update and check for updates.", "update"));
            else list.Add(new("Updates", HealthLevel.Good, "Windows is up to date", when));
        }
        if (f.FailedUpdates is { Count: > 0 } failed)
            list.Add(new("Updates", HealthLevel.Warning, Plural.Of(failed.Count, "update") + " failed to install lately",
                string.Join("; ", failed.Take(3).Select(x => HtmlText.Truncate(x, 90))) + (failed.Count > 3 ? "…" : "") + ".",
                "Windows Update usually retries on its own; if it keeps failing, run the Windows Update troubleshooter.", "update"));

        // Stability.
        if (f.BlueScreens is > 0)
            list.Add(new("Stability", HealthLevel.Problem, $"Windows crashed {Times(f.BlueScreens.Value)} in the last 30 days",
                "A blue screen means Windows itself stopped — usually a driver, overheating or failing hardware.",
                "Reliability Monitor shows when and what; updating graphics, chipset and storage drivers fixes most.", "reliability"));
        if (f.UnexpectedShutdowns is >= 2)
            list.Add(new("Stability", HealthLevel.Warning, $"The PC shut down unexpectedly {Times(f.UnexpectedShutdowns.Value)} in 30 days",
                "Power cuts, holding the power button, or the PC freezing or crashing.", "If you didn't cause them, Reliability Monitor shows what happened around then.", "reliability"));
        if (f.AppCrashes is { } crashes)
        {
            var total = crashes.Sum(c => c.Count);
            var top = string.Join(", ", crashes.OrderByDescending(c => c.Count).Take(3).Select(c => $"{c.App} ({c.Count})"));
            if (total >= 10)
                list.Add(new("Stability", HealthLevel.Warning, $"Apps crashed {total} times in two weeks", "Most: " + top + ".",
                    "Update or reinstall the app that crashes most; Reliability Monitor has the details.", "reliability"));
            else if (total > 0)
                list.Add(new("Stability", HealthLevel.Info, Plural.Of(total, "app crash", "app crashes") + " in two weeks", top + "."));
            else if (f.BlueScreens is 0)
                list.Add(new("Stability", HealthLevel.Good, "No crashes lately", "No blue screens in 30 days and no app crashes in two weeks."));
        }

        // Security.
        if (f.Antivirus is { } av)
        {
            var on = av.Where(a => a.Enabled).ToList();
            if (on.Count == 0)
                list.Add(new("Security", HealthLevel.Problem, "No antivirus is switched on",
                    av.Count == 0 ? "Windows doesn't report any antivirus." : "Installed but off: " + string.Join(", ", av.Select(a => a.Name)) + ".",
                    "Turn on Microsoft Defender in Windows Security.", "security"));
            else if (on.Any(a => !a.UpToDate))
                list.Add(new("Security", HealthLevel.Warning, "Antivirus definitions are out of date", string.Join(", ", on.Where(a => !a.UpToDate).Select(a => a.Name)) + " hasn't updated its definitions.",
                    "Check for protection updates in Windows Security.", "security"));
            else
                list.Add(new("Security", HealthLevel.Good, "Antivirus is on and up to date", string.Join(", ", on.Select(a => a.Name)) + "."));
        }

        // Devices.
        if (f.DeviceProblems is { Count: > 0 } devices)
            list.Add(new("Devices", HealthLevel.Warning, Plural.Of(devices.Count, "device") + " with a problem", string.Join("; ", devices.Take(4)) + (devices.Count > 4 ? "…" : "") + ".",
                "Device Manager shows each one; a driver update or reinstall usually fixes it.", "devices"));
        else if (f.DeviceProblems is not null)
            list.Add(new("Devices", HealthLevel.Good, "All devices are working", "Device Manager reports no problems."));

        // Battery.
        if (f.Battery is { DesignWh: > 0, FullWh: > 0 } battery)
        {
            var health = battery.FullWh / battery.DesignWh;
            var detail = $"It holds {health:P0} of the charge it did new ({battery.FullWh:0.#} of {battery.DesignWh:0.#} Wh)" + (battery.Cycles is > 0 ? $", after {battery.Cycles} charge cycles." : ".");
            list.Add(health < 0.6
                ? new("Battery", HealthLevel.Warning, "The battery has worn down", detail, "Expect shorter battery life; a replacement battery restores it.")
                : new("Battery", HealthLevel.Good, "The battery is in good shape", detail));
        }

        // Upkeep.
        if (f.Uptime.TotalDays > 14)
            list.Add(new("Upkeep", f.Uptime.TotalDays > 30 ? HealthLevel.Warning : HealthLevel.Info, $"Running for {(int)f.Uptime.TotalDays} days without a restart",
                "A restart finishes updates and clears apps that slowly use up memory.", "Restart when it suits you."));
        if (f.RamPercent >= 90)
            list.Add(new("Upkeep", HealthLevel.Warning, "Memory is nearly full", $"{f.RamPercent:0}% of {Gb(f.RamTotalGb)} in use" + (f.TopMemoryApp is { } app ? $"; {app} uses the most ({Fmt(f.TopMemoryMb)})." : "."),
                "Close apps you aren't using; Task Manager shows what uses it.", "taskmgr"));
        if (f.StartupApps is { Count: > 12 } startup)
            list.Add(new("Upkeep", HealthLevel.Info, $"{startup.Count} apps start with Windows", string.Join(", ", startup.Take(6)) + "…",
                "Turning off the ones you don't need makes startup quicker.", "startup"));
        var clutter = (f.TempGb ?? 0) + (f.RecycleBinGb ?? 0);
        if (clutter >= 5)
            list.Add(new("Upkeep", HealthLevel.Info, $"{Gb(clutter)} of clutter",
                $"Temporary files: {Gb(f.TempGb ?? 0)}; Recycle Bin: {Gb(f.RecycleBinGb ?? 0)}.", "Disk Cleanup or Storage Sense clears them safely.", "cleanup"));

        return list.OrderByDescending(x => x.Level).ToList();
    }

    /// <summary>The report as text: for copying, for Ask, and for the local model to summarise.</summary>
    public static string ToText(HealthReport report, bool withGood = true)
    {
        var f = report.Facts;
        var sb = new StringBuilder();
        sb.Append("PC health check · ").Append(f.Machine).Append(" · ").Append(f.Checked.ToString("d MMM yyyy HH:mm", Inv)).Append('\n');
        sb.Append("Verdict: ").Append(report.Verdict).Append('\n');
        if (report.Summary is { Length: > 0 } summary) sb.Append('\n').Append(summary.Trim()).Append('\n');
        sb.Append('\n');
        foreach (var x in report.Findings.Where(x => withGood || x.Level != HealthLevel.Good))
        {
            sb.Append("- [").Append(x.Level.ToString().ToUpperInvariant()).Append("] ").Append(x.Area).Append(": ").Append(x.Title).Append(" — ").Append(x.Detail);
            if (x.Fix is { Length: > 0 } fix) sb.Append(" What to do: ").Append(fix);
            sb.Append('\n');
        }
        if (f.Skipped.Count > 0) sb.Append("\nNot checked: ").Append(string.Join("; ", f.Skipped)).Append('\n');
        return sb.ToString();
    }

    public const string SummaryInstructions = """
        You are Aqua, the owner's assistant on their Windows PC. Write a short health report from the check results below:
        3 to 6 sentences in plain words, the most important thing first, saying what to do about each problem or warning
        (use the "What to do" given). If everything is fine, say so in a sentence or two, and sum up what passed in one short
        sentence rather than listing it. Only use what the results say, in their own words (a drive that "has room" isn't "full";
        a "restart" isn't a "shutdown") —
        don't guess at causes they don't mention, don't add numbers, and don't recommend "PC cleaner" or "driver updater" apps.
        """;

    /// <summary>The findings for the local model to summarise.</summary>
    public static string SummaryPrompt(HealthReport report) => ToText(report with { Summary = null });

    /// <summary>
    /// Windows Security Center's productState for an antivirus: the middle byte says whether it is on (0x10, or 0x11
    /// snoozed), the low byte whether its definitions are up to date (0x00) or old (0x10).
    /// </summary>
    public static (bool Enabled, bool UpToDate) AntivirusState(int productState) =>
        (((productState >> 8) & 0xFF) is 0x10 or 0x11, (productState & 0xFF) == 0x00);

    /// <summary>What a Device Manager error code means, or null when it isn't a problem (22 = you turned it off; 45 = unplugged).</summary>
    public static string? DeviceProblem(string name, int code) => code switch
    {
        0 or 22 or 45 => null,
        1 => name + " isn't set up",
        10 => name + " can't start",
        18 or 28 => name + " has no driver",
        19 or 38 or 39 or 41 => name + " has a damaged or missing driver",
        24 => name + " isn't working or isn't all there",
        31 => name + " isn't working properly",
        43 => name + " reported a problem, so Windows stopped it",
        52 => name + " has a driver Windows can't verify",
        _ => $"{name} has a problem (code {code})",
    };

    /// <summary>Microsoft Defender's daily definition downloads aren't Windows updates: they don't show whether Windows is current.</summary>
    public static bool IsDefinitionUpdate(string title) =>
        title.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase) || title.Contains("Definition Update", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Antimalware Platform", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// From Windows Update's history (newest first or in any order): the latest real update installed, and the updates that
    /// failed in the last <paramref name="days"/> days and haven't installed since.
    /// </summary>
    public static (DateTimeOffset? Last, string? LastTitle, List<string> Failed) UpdateHistory(IEnumerable<(DateTimeOffset Date, string Title, bool Succeeded)> history, DateTimeOffset now, int days = 14)
    {
        var real = history.Where(h => !IsDefinitionUpdate(h.Title)).OrderByDescending(h => h.Date).ToList();
        var last = real.FirstOrDefault(h => h.Succeeded);
        var failed = real.Where(h => !h.Succeeded && now - h.Date < TimeSpan.FromDays(days))
            .Where(h => !real.Any(ok => ok.Succeeded && ok.Date > h.Date && ok.Title.Equals(h.Title, StringComparison.OrdinalIgnoreCase)))
            .Select(h => h.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return (last.Title is null ? null : last.Date, last.Title, failed);
    }

    /// <summary>"today", "yesterday" or "on 21 Sep 2026 (9 days ago)".</summary>
    private static string On(DateTimeOffset time, DateTimeOffset now)
    {
        var days = (int)(now.ToLocalTime().Date - time.ToLocalTime().Date).TotalDays;
        return days <= 0 ? "today" : days == 1 ? "yesterday" : $"on {time.ToLocalTime().ToString("d MMM yyyy", Inv)} ({days} days ago)";
    }

    private static string Times(int n) => n == 1 ? "once" : n == 2 ? "twice" : n + " times";
    private static string Gb(double gb) => gb >= 1000 ? (gb / 1024).ToString("0.0", Inv) + " TB" : gb >= 10 ? gb.ToString("0", Inv) + " GB" : gb.ToString("0.#", Inv) + " GB";
    private static string Fmt(double mb) => mb >= 1024 ? (mb / 1024).ToString("0.0", Inv) + " GB" : mb.ToString("0", Inv) + " MB";
}
