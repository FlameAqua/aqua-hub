using AquaHub.Core.Health;
using Xunit;

namespace AquaHub.Tests;

/// <summary>This PC › Health check: the rules that turn Windows' readings into findings.</summary>
public class PcHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(1));

    private static HealthFacts Healthy() => new()
    {
        Checked = Now,
        Machine = "TEST-PC · Windows 11 Pro",
        Drives = { new DriveFact("C:", "Windows", 1000, 400, true), new DriveFact("D:", "Games", 2000, 900, false) },
        PhysicalDisks = new() { new PhysicalDiskFact("Samsung SSD 990 PRO 2TB", "SSD", "Healthy") },
        Uptime = TimeSpan.FromDays(2),
        RestartPending = false,
        LastUpdate = Now.AddDays(-9),
        LastUpdateTitle = "2026-09 Cumulative Update for Windows 11",
        FailedUpdates = new(),
        BlueScreens = 0,
        UnexpectedShutdowns = 0,
        AppCrashes = new(),
        Antivirus = new() { new AntivirusFact("Windows Defender", true, true) },
        DeviceProblems = new(),
        StartupApps = new() { "Steam", "Discord" },
        TempGb = 0.8,
        RecycleBinGb = 0.2,
        RamPercent = 40,
        RamTotalGb = 32,
    };

    [Fact]
    public void AHealthyPcHasNothingToLookAt()
    {
        var findings = PcHealth.Evaluate(Healthy());
        Assert.All(findings, f => Assert.Equal(HealthLevel.Good, f.Level));
        var report = new HealthReport(Healthy(), findings, null);
        Assert.Equal("Looks healthy", report.Verdict);
        Assert.Contains(findings, f => f.Title == "Antivirus is on and up to date");
        Assert.Contains(findings, f => f.Title == "Windows is up to date" && f.Detail.Contains("9 days ago"));
    }

    [Fact]
    public void ANearlyFullSystemDriveIsAProblemButTheSameShareOfADataDriveIsNot()
    {
        var facts = Healthy() with { Drives = new() { new DriveFact("C:", "", 500, 12, true), new DriveFact("E:", "Backup", 4000, 160, false) } };
        var findings = PcHealth.Evaluate(facts);
        var c = Assert.Single(findings, f => f.Title.StartsWith("C:"));
        Assert.Equal(HealthLevel.Problem, c.Level);   // 2.4% free on the system drive
        Assert.Equal("cleanup", c.Tool);
        var e = Assert.Single(findings, f => f.Title.StartsWith("E:"));
        Assert.Equal(HealthLevel.Warning, e.Level);   // 4% free on a data drive: getting full, not a problem yet
        Assert.Equal(HealthLevel.Problem, findings[0].Level);   // most serious first
    }

    [Fact]
    public void CrashesBlueScreensAndFailedUpdatesAreReported()
    {
        var facts = Healthy() with
        {
            BlueScreens = 2,
            UnexpectedShutdowns = 3,
            AppCrashes = new() { new CrashFact("chrome", 7), new CrashFact("Discord", 4) },
            FailedUpdates = new() { "2026-09 Cumulative Update for Windows 11 (KB5099999)" },
            RestartPending = true,
        };
        var findings = PcHealth.Evaluate(facts);
        Assert.Contains(findings, f => f.Level == HealthLevel.Problem && f.Title == "Windows crashed twice in the last 30 days" && f.Tool == "reliability");
        Assert.Contains(findings, f => f.Level == HealthLevel.Warning && f.Title.Contains("shut down unexpectedly 3 times"));
        Assert.Contains(findings, f => f.Level == HealthLevel.Warning && f.Title == "Apps crashed 11 times in two weeks" && f.Detail.Contains("chrome (7)"));
        Assert.Contains(findings, f => f.Title == "1 update failed to install lately");
        Assert.Contains(findings, f => f.Title == "A restart is waiting");
        Assert.DoesNotContain(findings, f => f.Title == "No crashes lately");
        Assert.Equal("1 problem, 4 warnings", new HealthReport(facts, findings, null).Verdict);
    }

    [Fact]
    public void AntivirusOffOrStaleIsFlagged()
    {
        Assert.Contains(PcHealth.Evaluate(Healthy() with { Antivirus = new() { new AntivirusFact("Windows Defender", false, true) } }),
            f => f.Level == HealthLevel.Problem && f.Title == "No antivirus is switched on" && f.Tool == "security");
        Assert.Contains(PcHealth.Evaluate(Healthy() with { Antivirus = new() { new AntivirusFact("Windows Defender", true, false) } }),
            f => f.Level == HealthLevel.Warning && f.Title == "Antivirus definitions are out of date");
    }

    [Fact]
    public void WindowsSecurityCentreStatesDecode()
    {
        Assert.Equal((true, true), PcHealth.AntivirusState(397568));    // 0x061100 — Defender on, up to date (read on the owner's PC, 30 Sep)
        Assert.Equal((true, false), PcHealth.AntivirusState(397584));   // 0x061110 — on, definitions old
        Assert.Equal((false, true), PcHealth.AntivirusState(393472));   // 0x060100 — off
        Assert.Equal((true, true), PcHealth.AntivirusState(266240));    // 0x041000 — a third-party antivirus, on
    }

    [Fact]
    public void DeviceErrorsInWordsAndDisabledDevicesAreNotProblems()
    {
        Assert.Equal("Realtek Audio has no driver", PcHealth.DeviceProblem("Realtek Audio", 28));
        Assert.Equal("USB Hub reported a problem, so Windows stopped it", PcHealth.DeviceProblem("USB Hub", 43));
        Assert.Null(PcHealth.DeviceProblem("Bluetooth", 22));   // turned off on purpose
        Assert.Null(PcHealth.DeviceProblem("Old phone", 45));   // not plugged in
        Assert.Equal("Thing has a problem (code 99)", PcHealth.DeviceProblem("Thing", 99));
        var findings = PcHealth.Evaluate(Healthy() with { DeviceProblems = new() { "Realtek Audio has no driver" } });
        Assert.Contains(findings, f => f.Title == "1 device with a problem" && f.Tool == "devices");
    }

    [Fact]
    public void DefenderDefinitionsDontCountAsWindowsUpdatesAndRetriedFailuresAreForgiven()
    {
        var history = new List<(DateTimeOffset, string, bool)>
        {
            (Now.AddHours(-3), "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.437.1)", true),
            (Now.AddDays(-1), "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.437.0)", true),
            (Now.AddDays(-6), "2026-09 Cumulative Update for Windows 11 Version 25H2 (KB5099999)", true),
            (Now.AddDays(-7), "2026-09 Cumulative Update for Windows 11 Version 25H2 (KB5099999)", false),   // failed, then installed
            (Now.AddDays(-8), "Intel - Display - 32.0.101.7000", false),
            (Now.AddDays(-40), "2026-08 .NET Update (KB5088888)", false),   // too long ago to mention
        };
        var (last, title, failed) = PcHealth.UpdateHistory(history, Now);
        Assert.Equal(Now.AddDays(-6), last);
        Assert.Contains("Cumulative Update", title);
        Assert.Equal(new[] { "Intel - Display - 32.0.101.7000" }, failed);
    }

    [Fact]
    public void OldUpdatesLongUptimeFullMemoryAndClutter()
    {
        var facts = Healthy() with
        {
            LastUpdate = Now.AddDays(-100),
            Uptime = TimeSpan.FromDays(33),
            RamPercent = 93,
            TopMemoryApp = "chrome",
            TopMemoryMb = 6200,
            StartupApps = Enumerable.Range(1, 15).Select(i => "App " + i).ToList(),
            TempGb = 4.5,
            RecycleBinGb = 2.5,
        };
        var findings = PcHealth.Evaluate(facts);
        Assert.Contains(findings, f => f.Level == HealthLevel.Problem && f.Title == "Windows hasn't updated in months");
        Assert.Contains(findings, f => f.Level == HealthLevel.Warning && f.Title == "Running for 33 days without a restart");
        Assert.Contains(findings, f => f.Title == "Memory is nearly full" && f.Detail.Contains("chrome uses the most (6.1 GB)"));
        Assert.Contains(findings, f => f.Level == HealthLevel.Info && f.Title == "15 apps start with Windows" && f.Tool == "startup");
        Assert.Contains(findings, f => f.Title == "7 GB of clutter" && f.Tool == "cleanup");
    }

    [Fact]
    public void ChecksThatCouldntRunAreLeftOutNotGuessed()
    {
        var facts = new HealthFacts { Checked = Now, Drives = { new DriveFact("C:", "", 500, 200, true) }, Skipped = { "Windows Update: Windows didn't say" } };
        var findings = PcHealth.Evaluate(facts);
        Assert.DoesNotContain(findings, f => f.Area is "Updates" or "Security" or "Devices" or "Stability");
        var text = PcHealth.ToText(new HealthReport(facts, findings, "All good."));
        Assert.Contains("Not checked: Windows Update: Windows didn't say", text);
        Assert.Contains("All good.", text);
    }

    [Fact]
    public void ADriveThatReportsTroubleSaysBackUp()
    {
        var findings = PcHealth.Evaluate(Healthy() with { PhysicalDisks = new() { new PhysicalDiskFact("WD Blue 1TB", "hard drive", "Unhealthy") } });
        var disk = Assert.Single(findings, f => f.Title.StartsWith("WD Blue"));
        Assert.Equal(HealthLevel.Problem, disk.Level);
        Assert.Contains("Back up", disk.Fix);
    }

    [Fact]
    public void BatteryWear()
    {
        Assert.Contains(PcHealth.Evaluate(Healthy() with { Battery = new BatteryFact(60, 33, 812) }),
            f => f.Level == HealthLevel.Warning && f.Title == "The battery has worn down" && f.Detail.Contains("55%") && f.Detail.Contains("812 charge cycles"));
        Assert.Contains(PcHealth.Evaluate(Healthy() with { Battery = new BatteryFact(60, 55, null) }), f => f.Level == HealthLevel.Good && f.Area == "Battery");
    }

    [Fact]
    public void TheReportTextListsFindingsWithWhatToDo()
    {
        var facts = Healthy() with { RestartPending = true };
        var text = PcHealth.ToText(new HealthReport(facts, PcHealth.Evaluate(facts), null), withGood: false);
        Assert.Contains("Verdict: 1 thing to look at", text);
        Assert.Contains("- [WARNING] Updates: A restart is waiting — Windows needs a restart to finish installing updates. What to do: Restart when it suits you.", text);
        Assert.DoesNotContain("[GOOD]", text);
    }
}
