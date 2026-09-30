using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AquaHub.Core.Agents;
using AquaHub.Core.Ai;
using AquaHub.Core.Health;
using AquaHub.Core.Util;
using AquaHub.Platform;
using AquaHub.Services;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.UI.Pages;

public sealed record ProcVM(string Name, string CpuText, double CpuValue, string MemText, Brush RowBrush);
public sealed record DiskVM(string Name, string Label, string Text, double Value, Brush Brush, string Note)
{
    public string OpenName => "Open " + Name;
    public string CleanName => "Clean up " + Name;
}
public sealed record NameDetail(string Name, string Detail);
public sealed record FindingVM(string Area, string Title, string Detail, string Fix, Brush Brush, string? ToolId, string ToolName)
{
    public bool HasFix => Fix.Length > 0;
    public bool HasTool => ToolId is not null;
}
public sealed record ToolkitGroupVM(string Name, List<MaintenanceTools.Tool> Tools);

public sealed class SystemVM : ObservableObject
{
    public string Machine { get; set; } = "";
    public double Cpu { get; set; }
    public double Ram { get; set; }
    public double Gpu { get; set; }
    public Brush CpuBrush { get; set; } = Brushes.Gray;
    public Brush RamBrush { get; set; } = Brushes.Gray;
    public Brush GpuBrush { get; set; } = Brushes.Gray;
    public string CpuSub { get; set; } = "";
    public bool AiReachable { get; set; }
    public bool ShowOllamaToggle { get; set; }
    public bool OllamaIdle { get; set; } = true;
    public string OllamaLabel { get; set; } = "Turn off Ollama";
    public bool ShowSetupHint { get; set; }
    public string CpuDetail { get; set; } = "last 3 minutes";
    public string RamSub { get; set; } = "";
    public string RamDetail { get; set; } = "";
    public string GpuSub { get; set; } = "";
    public string VramText { get; set; } = "";
    public double VramValue { get; set; }
    public string Down { get; set; } = "";
    public string Up { get; set; } = "";
    public string Uptime { get; set; } = "";
    public string NetName { get; set; } = "";
    public double[] CpuHistory { get; set; } = Array.Empty<double>();
    public double[] RamHistory { get; set; } = Array.Empty<double>();
    public double[] GpuHistory { get; set; } = Array.Empty<double>();
    public double[] DownHistory { get; set; } = Array.Empty<double>();
    public double[] UpHistory { get; set; } = Array.Empty<double>();

    public List<ProcVM> Processes { get; set; } = new();
    public bool SortByMemory { get; set; }
    public bool ShowMore { get; set; }
    public string MoreLabel => ShowMore ? "Show fewer" : "Show more";
    public string CpuHeader => SortByMemory ? "CPU" : "CPU ▾";
    public string MemoryHeader => SortByMemory ? "Memory ▾" : "Memory";

    // The selected app.
    public bool HasSelected { get; set; }
    public string SelTitle { get; set; } = "";
    public string SelInfo { get; set; } = "";
    public string SelPath { get; set; } = "";
    public bool SelHasPath => SelPath.Length > 0;
    public bool SelCanEnd { get; set; }
    public string SelEndTip { get; set; } = "";
    public string EndLabel { get; set; } = "End task";
    public bool ShowForceEnd { get; set; }
    public string SelStatus { get; set; } = "";
    public bool HasSelStatus => SelStatus.Length > 0;

    public List<DiskVM> Disks { get; set; } = new();
    public string StorageSub { get; set; } = "";
    public List<ToolkitGroupVM> ToolGroups { get; } = MaintenanceTools.All.GroupBy(t => t.Group).Select(g => new ToolkitGroupVM(g.Key, g.ToList())).ToList();

    // Health check.
    public bool HasHealth { get; set; }
    public bool HealthRunning { get; set; }
    public bool HealthIdle => !HealthRunning;
    public string HealthButton => HealthRunning ? "Checking…" : HasHealth ? "Check again" : "Run health check";
    public string HealthStep { get; set; } = "";
    public string HealthSub { get; set; } = "Not run yet";
    public string HealthVerdict { get; set; } = "";
    public Brush HealthBrush { get; set; } = Brushes.Gray;
    public string HealthSummary { get; set; } = "";
    public bool HasSummary => HealthSummary.Length > 0;
    public List<FindingVM> Findings { get; set; } = new();
    public List<FindingVM> Passed { get; set; } = new();
    public bool HasPassed => Passed.Count > 0;
    public string PassedHeader => Plural.Of(Passed.Count, "check") + " passed";
    public string NotChecked { get; set; } = "";
    public bool HasNotChecked => NotChecked.Length > 0;

    public string AiModel { get; set; } = "";
    public string AiState { get; set; } = "";
    public string AiSub { get; set; } = "";
    public string PauseLabel { get; set; } = "Pause AI";
    public string TokensPerSecond { get; set; } = "—";
    public string Calls { get; set; } = "0";
    public string Tokens { get; set; } = "0";
    public List<NameDetail> Loaded { get; set; } = new();
    public List<NameDetail> Installed { get; set; } = new();
    public string SelfText { get; set; } = "";
    public string Status { get; set; } = "";
    public void Changed() => RaiseAll();
}

public partial class SystemPage : UserControl, IPage
{
    private readonly SystemVM _vm = new();
    private readonly UiThrottle _refresh;
    private DateTime _lastAi = DateTime.MinValue;
    private ProcessActions.Details? _selected;
    private DateTime _lastNet = DateTime.MinValue;

    public SystemPage()
    {
        InitializeComponent();
        DataContext = _vm;
        _refresh = new UiThrottle(Refresh, 300);
    }

    public void OnNavigatedTo(string? arg)
    {
        Hub.State.Changed -= OnChanged; // navigating to the page already shown must not subscribe twice
        Hub.State.Changed += OnChanged;
        Hub.Health.Progress -= OnHealthProgress;
        Hub.Health.Progress += OnHealthProgress;
        Hub.Health.Finished -= OnHealthFinished;
        Hub.Health.Finished += OnHealthFinished;
        Hub.Windows.UpdateVisibility();
        Hub.System.DetailedProcesses = true;
        Hub.System.ProcessCount = _vm.ShowMore ? 20 : 8;
        Hub.System.SetInterval(TimeSpan.FromSeconds(1.5));
        Hub.System.Sample();
        _vm.HealthRunning = Hub.Health.Running;
        ShowHealth();
        Refresh();
        _ = RefreshAiAsync();
        if (arg == "health" && !Hub.Health.Running) OnHealthCheck(this, new RoutedEventArgs());
    }

    public void OnNavigatedFrom()
    {
        Hub.State.Changed -= OnChanged;
        Hub.Health.Progress -= OnHealthProgress;
        Hub.Health.Finished -= OnHealthFinished;
        Hub.System.DetailedProcesses = false;
        Hub.System.ProcessCount = 8;
        _refresh.Stop();
    }

    private void OnChanged(string topic)
    {
        if (topic is Topics.System or Topics.Ai) _refresh.Request();
    }

    private void Refresh()
    {
        var s = Hub.State.System;
        if (s is null) return;
        _vm.Machine = $"{s.MachineName}  ·  {s.OsName}";
        _vm.Cpu = s.CpuPercent;
        _vm.Ram = s.RamPercent;
        _vm.Gpu = s.Gpu?.Utilization ?? 0;
        _vm.CpuBrush = Fmt.LoadBrush(s.CpuPercent);
        _vm.RamBrush = Fmt.LoadBrush(s.RamPercent);
        _vm.GpuBrush = Fmt.LoadBrush(_vm.Gpu);
        _vm.CpuSub = ShortCpuName(s.CpuName);
        _vm.CpuDetail = $"{s.LogicalCores} threads · last 3 minutes";
        _vm.RamSub = $"{s.RamTotalGb:0} GB";
        _vm.RamDetail = $"{s.RamUsedGb:0.0} GB in use";
        _vm.GpuSub = s.Gpu is null ? "No GPU data" : TodayVM.ShortGpu(s.Gpu.Name);
        _vm.VramText = s.Gpu is { VramTotalGb: > 0 } g ? $"{g.VramUsedGb:0.0} / {g.VramTotalGb:0} GB" : "—";
        _vm.VramValue = s.Gpu is { VramTotalGb: > 0 } g2 ? g2.VramUsedGb / g2.VramTotalGb : 0;
        _vm.Down = Fmt.Rate(s.NetDownBps);
        _vm.Up = Fmt.Rate(s.NetUpBps);
        _vm.Uptime = "Up " + Fmt.Uptime(s.Uptime);
        if (DateTime.UtcNow - _lastNet > TimeSpan.FromSeconds(20))
        {
            _lastNet = DateTime.UtcNow;
            _vm.NetName = Connection();
        }
        lock (Hub.System.History)
        {
            var h = Hub.System.History.ToArray();
            _vm.CpuHistory = h.Select(x => x.Cpu).ToArray();
            _vm.RamHistory = h.Select(x => x.Ram).ToArray();
            _vm.GpuHistory = h.Select(x => x.Gpu).ToArray();
            _vm.DownHistory = h.Select(x => x.NetDown).ToArray();
            _vm.UpHistory = h.Select(x => x.NetUp).ToArray();
        }
        // Hold the list still while the pointer is on it: rows don't jump away from a click.
        if (!ProcessList.IsMouseOver)
        {
            var ordered = _vm.SortByMemory
                ? s.TopProcesses.OrderByDescending(p => p.MemoryMb).ThenByDescending(p => p.Cpu)
                : s.TopProcesses.OrderByDescending(p => Math.Round(p.Cpu, 1)).ThenByDescending(p => p.MemoryMb);
            var selected = Fmt.Res("B.AccentSoft");
            _vm.Processes = ordered.Take(_vm.ShowMore ? 20 : 8).Select(p => new ProcVM(p.Name, $"{p.Cpu:0.0}%", Math.Min(1, p.Cpu / 25), Fmt.Bytes(p.MemoryMb * 1048576),
                _selected?.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) == true ? selected : Brushes.Transparent)).ToList();
        }
        if (_selected is { } sel) _vm.SelInfo = SelectedInfo(sel, s);
        var systemRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        var health = Hub.Health.Last?.Facts.PhysicalDisks;
        _vm.Disks = s.Disks.Select(d => new DiskVM(d.Name, d.Label, $"{d.FreeGb:0} GB free of {d.TotalGb:0} GB", d.UsedPercent / 100,
            Fmt.Res(d.UsedPercent >= 90 ? "B.Down" : d.UsedPercent >= 75 ? "B.Warn" : "B.Info"),
            d.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase) ? "Windows is on this drive" : "")).ToList();
        _vm.StorageSub = health is { Count: > 0 }
            ? (health.All(h => h.Health == "Healthy") ? Plural.Of(health.Count, "drive") + " healthy at the last check" : "a drive reported trouble — see Health check")
            : "";
        using var self = Process.GetCurrentProcess();
        var mem = MemoryStats.Current();
        _vm.SelfText = $"{mem.PrivateWorkingSetMb:0} MB of memory (as Task Manager counts it; {mem.CommitMb:0} MB committed) · " +
                       $"{self.Threads.Count} threads · {Hub.Core.Agents.Statuses.Count} agents on duty.";
        UpdateAiStats();
        _vm.Changed();
        if (DateTime.UtcNow - _lastAi > TimeSpan.FromSeconds(10)) _ = RefreshAiAsync();
    }

    /// <summary>"Ethernet · 1 Gbps" or "Wi-Fi · 866 Mbps": the connection carrying the traffic.</summary>
    private static string Connection()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && n.GetIPProperties().GatewayAddresses.Count > 0)
                .OrderByDescending(n => n.GetIPStatistics().BytesReceived).FirstOrDefault();
            if (nic is null) return "Not connected";
            var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet" : nic.Name;
            var speed = nic.Speed >= 1_000_000_000 ? $"{nic.Speed / 1e9:0.#} Gbps" : nic.Speed > 0 ? $"{nic.Speed / 1e6:0} Mbps" : "";
            return speed.Length > 0 ? $"{kind} · {speed}" : kind;
        }
        catch (NetworkInformationException) { return ""; }
    }

    private void UpdateAiStats()
    {
        var ai = Hub.State.Ai;
        var stats = Hub.Core.Llm.Stats;
        _vm.AiModel = ai?.ActiveModel ?? "No model";
        _vm.AiReachable = ai?.Available == true;
        _vm.ShowOllamaToggle = OllamaManager.IsLocalOllama && OllamaManager.Installed;
        _vm.OllamaIdle = !Hub.Ollama.Transitioning;
        _vm.OllamaLabel = Hub.Ollama.Transitioning ? (Hub.Ollama.Running == true ? "Stopping…" : "Starting…")
            : Hub.Ollama.Running == true ? "Turn off Ollama" : "Turn on Ollama";
        _vm.ShowSetupHint = ai is { Enabled: true, Available: false };
        _vm.AiState = ai is null ? "Checking…" : !ai.Enabled ? "AI is turned off in Settings" : !ai.Available ? ai.Error ?? "Offline" :
            ai.Paused ? $"Paused — {ai.PauseReason}" : "Ready · runs entirely on this PC";
        _vm.AiSub = ai is null ? "" : $"{(ai.Provider == "ollama" ? "Ollama" : "OpenAI-compatible")} {ai.Version} · {ai.Endpoint}";
        _vm.PauseLabel = Hub.Core.Llm.UserPaused ? "Resume AI" : "Pause AI";
        _vm.TokensPerSecond = stats.LastTokensPerSecond > 0 ? $"{stats.LastTokensPerSecond:0} tok/s" : "—";
        _vm.Calls = stats.Calls.ToString();
        _vm.Tokens = Fmt.Compact(stats.OutputTokens);
        _vm.Installed = ai?.Models.Select(m => new NameDetail(m.Name, $"{m.Parameters} {m.Quantization} · {Fmt.Bytes(m.SizeBytes)}".Trim())).ToList() ?? new();
    }

    private async Task RefreshAiAsync()
    {
        _lastAi = DateTime.UtcNow;
        var running = await Hub.Core.Llm.RunningModelsAsync();
        _vm.Loaded = running.Select(r => new NameDetail(r.Name, $"{Fmt.Bytes(r.VramBytes)} VRAM" +
            (r.ExpiresAt is { } exp ? $" · unloads {TimeText.Until(exp)}" : ""))).ToList();
        _vm.Changed();
    }

    /// <summary>"AMD Ryzen 7 9700X 8-Core Processor" → "Ryzen 7 9700X"; "Intel(R) Core(TM) i7-12700K" → "Core i7-12700K".</summary>
    internal static string ShortCpuName(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name, @"\((R|TM|C)\)", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b\d+(st|nd|rd|th) Gen\b|\bProcessor\b|\bCPU\b|\b\d+-Core\b|@.*$|\b(AMD|Intel)\b", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s.Length > 0 ? s : name.Trim();
    }

    // ───────────── Tools ─────────────

    private void OnTool(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && !MaintenanceTools.Open(id))
            SetStatusFor(id);
    }

    private void SetStatusFor(string id)
    {
        if (MaintenanceTools.Find(id) is { } tool) Say($"{tool.Name} didn't open (or Windows' permission prompt was cancelled).");
    }

    private int _said;

    /// <summary>A short confirmation under the page title, cleared after a few seconds.</summary>
    private async void Say(string text)
    {
        var mine = ++_said;
        _vm.Status = text;
        _vm.Changed();
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (mine != _said) return;
        _vm.Status = "";
        _vm.Changed();
    }

    private void OnGraphicsSettings(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl("ms-settings:display-advancedgraphics", allowAppProtocols: true);
    private void OnGetOllama(object sender, RoutedEventArgs e) => AppLauncher.OpenUrl("https://ollama.com/download");

    // ───────────── Storage ─────────────

    private void OnOpenDrive(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string drive }) MaintenanceTools.OpenDrive(drive);
    }

    private void OnCleanDrive(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string drive }) MaintenanceTools.CleanDrive(drive);
    }

    // ───────────── Processes ─────────────

    private void OnSortCpu(object sender, RoutedEventArgs e) => SortBy(memory: false);
    private void OnSortMemory(object sender, RoutedEventArgs e) => SortBy(memory: true);

    private void SortBy(bool memory)
    {
        _vm.SortByMemory = memory;
        Hub.System.Sample();
    }

    private void OnMoreProcesses(object sender, RoutedEventArgs e)
    {
        _vm.ShowMore = !_vm.ShowMore;
        Hub.System.ProcessCount = _vm.ShowMore ? 20 : 8;
        Hub.System.Sample();
    }

    /// <summary>Snapshot QA: shows an app's details as if it had been clicked.</summary>
    internal void SelectForSnapshot(string name) => Select(new FrameworkElement { Tag = name });

    // Selected on press, not on release: the list may refresh between the two.
    private void OnProcessPressed(object sender, MouseButtonEventArgs e) => Select(sender);
    private void OnProcessClicked(object sender, RoutedEventArgs e) => Select(sender);

    private async void Select(object sender)
    {
        if (sender is not FrameworkElement { Tag: string name } || _selected?.Name == name && _vm.HasSelected) return;
        _selected = new ProcessActions.Details(name, null, null, null, 0, false, false, ProcessActions.WhyKept(name));
        ShowSelected("");
        var details = await Task.Run(() => ProcessActions.Describe(name));
        if (_selected?.Name != name) return;
        _selected = details;
        ShowSelected("");
    }

    private void ShowSelected(string status)
    {
        if (_selected is not { } d)
        {
            _vm.HasSelected = false;
            _vm.Changed();
            return;
        }
        _vm.HasSelected = true;
        _vm.SelTitle = d.Title + (d.Title.Equals(d.Name, StringComparison.OrdinalIgnoreCase) ? "" : $"  ({d.Name})");
        _vm.SelPath = d.Path ?? "";
        _vm.SelInfo = Hub.State.System is { } s ? SelectedInfo(d, s) : "";
        _vm.SelCanEnd = d.Protected is null && d.Count > 0;
        _vm.SelEndTip = d.Protected is { } why ? "Aqua won't end it: " + why
            : d.HasWindow ? "Asks it to close, as if you clicked × — it can offer to save your work" : "Stops it straight away";
        _vm.EndLabel = d.HasWindow ? "Close app" : "End task";
        _vm.ShowForceEnd = false;
        _vm.SelStatus = status;
        Refresh();
    }

    private static string SelectedInfo(ProcessActions.Details d, Core.Models.SystemSnapshot s)
    {
        var usage = s.TopProcesses.FirstOrDefault(p => p.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
        var parts = new List<string>();
        if (d.Company is { } company) parts.Add(company);
        else if (d.PartOfWindows) parts.Add("Part of Windows");
        if (d.Count > 0) parts.Add(Plural.Of(d.Count, "process", "processes"));
        if (usage is not null) parts.Add($"{usage.Cpu:0.0}% CPU · {Fmt.Bytes(usage.MemoryMb * 1048576)}");
        if (d.Count == 0) parts.Add("no longer running");
        return string.Join(" · ", parts);
    }

    private void OnCloseProcess(object sender, RoutedEventArgs e)
    {
        _selected = null;
        ShowSelected("");
    }

    private void OnRevealProcess(object sender, RoutedEventArgs e)
    {
        if (_selected?.Path is { } path) ProcessActions.Reveal(path);
    }

    private void OnSearchProcess(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } d) return;
        var q = Uri.EscapeDataString(d.Name + ".exe" + (d.Description is { } desc && !desc.Equals(d.Name, StringComparison.OrdinalIgnoreCase) ? " " + desc : ""));
        var engine = Hub.S.Ask;
        var url = engine.SearchEngine == "brave" ? "https://search.brave.com/search?q=" + q
            : engine.SearchEngine == "searxng" && Uri.TryCreate(engine.SearxngUrl.TrimEnd('/') + "/search?q=" + q, UriKind.Absolute, out var searx) && searx.Scheme == Uri.UriSchemeHttps ? searx.AbsoluteUri
            : "https://duckduckgo.com/?q=" + q;
        AppLauncher.OpenUrl(url);
    }

    private void OnAskProcess(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } d) return;
        var what = d.Description is { } desc && !desc.Equals(d.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{d.Name}.exe (“{desc}”{(d.Company is { } c ? " by " + c : "")})" : d.Name + ".exe";
        var usage = Hub.State.System?.TopProcesses.FirstOrDefault(p => p.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
        var use = usage is null ? "" : $", and is it normal for it to use {usage.Cpu:0.#}% of the processor and {Fmt.Bytes(usage.MemoryMb * 1048576)} of memory";
        Hub.Windows.ShowMain("ask", $"What is {what} on my PC{use}? Do I need it?");
    }

    private async void OnEndProcess(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } d || d.Protected is not null) return;
        var question = d.HasWindow
            ? $"Close {d.Title}?\n\n" + (d.PartOfWindows ? "It's part of Windows. " : "") + "Aqua asks it to close, as if you clicked ×, so it can offer to save your work."
            : $"End {d.Title}?\n\nIt runs in the background ({Plural.Of(d.Count, "process", "processes")}). Ending it stops it straight away; anything it was in the middle of is lost.";
        if (MessageBox.Show(Window.GetWindow(this)!, question, d.HasWindow ? "Close app" : "End task", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await EndAsync(d, force: !d.HasWindow);
    }

    private async void OnForceEndProcess(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } d || !d.CanForce) return;
        if (MessageBox.Show(Window.GetWindow(this)!, $"Force {d.Title} to end?\n\nAnything unsaved in it is lost.", "Force end", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await EndAsync(d, force: true);
    }

    private async Task EndAsync(ProcessActions.Details d, bool force)
    {
        _vm.SelStatus = force ? "Ending…" : "Asking it to close…";
        _vm.SelCanEnd = false;
        _vm.Changed();
        var (left, error) = await ProcessActions.EndAsync(d.Name, force);
        Hub.System.Sample();
        if (_selected?.Name != d.Name) return;
        _selected = await Task.Run(() => ProcessActions.Describe(d.Name));
        ShowSelected(error ?? (left == 0 ? $"{d.Title} has ended."
            : force || !d.CanForce ? $"{d.Title} is still running ({Plural.Of(left, "process", "processes")}) — it may be asking you something. Task Manager can end it."
            : $"{d.Title} is still running — it may be asking you to save something. Force end stops it without saving."));
        _vm.ShowForceEnd = error is null && left > 0 && !force && d.CanForce;
        _vm.Changed();
    }

    // ───────────── Health check ─────────────

    private async void OnHealthCheck(object sender, RoutedEventArgs e)
    {
        if (Hub.Health.Running) return;
        _vm.HealthRunning = true;
        _vm.HealthStep = "Starting…";
        _vm.Changed();
        try { await Hub.Health.RunAsync(summarise: true, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Warn("health", "The health check failed", ex);
            _vm.HealthStep = "The check stopped: " + ex.Message;
        }
        finally
        {
            _vm.HealthRunning = false;
            ShowHealth();
        }
    }

    private void OnHealthProgress(string step) => Hub.OnUi(() =>
    {
        _vm.HealthRunning = true;
        _vm.HealthStep = step;
        _vm.Changed();
    });

    private void OnHealthFinished(HealthReport report) => Hub.OnUi(() =>
    {
        _vm.HealthRunning = Hub.Health.Running;
        ShowHealth();
    });

    private void ShowHealth()
    {
        var r = Hub.Health.Last;
        _vm.HasHealth = r is not null;
        if (r is null)
        {
            _vm.Changed();
            return;
        }
        _vm.HealthSub = "Checked " + TimeText.Ago(r.Facts.Checked);
        _vm.HealthVerdict = r.Verdict;
        _vm.HealthBrush = Fmt.Res(r.Problems > 0 ? "B.Down" : r.Warnings > 0 ? "B.Warn" : "B.Up");
        _vm.HealthSummary = r.Summary ?? "";
        _vm.Findings = r.Findings.Where(f => f.Level != HealthLevel.Good).Select(ToVM).ToList();
        _vm.Passed = r.Findings.Where(f => f.Level == HealthLevel.Good).Select(ToVM).ToList();
        _vm.NotChecked = r.Facts.Skipped.Count > 0 ? "Not checked: " + string.Join("; ", r.Facts.Skipped) + "." : "";
        _vm.Changed();
        Refresh();   // the Storage card shows the drives' health from the check
    }

    private static FindingVM ToVM(HealthFinding f)
    {
        var tool = f.Tool is { } id ? MaintenanceTools.Find(id) : null;
        var brush = Fmt.Res(f.Level switch { HealthLevel.Problem => "B.Down", HealthLevel.Warning => "B.Warn", HealthLevel.Info => "B.Info", _ => "B.Up" });
        return new FindingVM(f.Area, f.Title, f.Detail, f.Fix ?? "", brush, tool?.Id, tool is null ? "" : "Open " + tool.Name);
    }

    private void OnCopyHealth(object sender, RoutedEventArgs e)
    {
        if (Hub.Health.Last is not { } report) return;
        var text = PcHealth.ToText(report);
        if (Sandbox.Intercept("clipboard", text)) return;
        try
        {
            Clipboard.SetText(text);
            Say("Health report copied — paste it anywhere.");
        }
        catch (System.Runtime.InteropServices.COMException ex) { Log.Warn("health", "Couldn't copy the report", ex); }
    }

    private void OnAskHealth(object sender, RoutedEventArgs e) =>
        Hub.Windows.ShowMain("ask", "@pc:Go through my PC's latest health check with me: what should I do first, and how?");

    // ───────────── AI runtime ─────────────

    private async void OnFreeVram(object sender, RoutedEventArgs e)
    {
        await ActionExecutor.UnloadModelAsync();
        await RefreshAiAsync();
    }

    private async void OnToggleOllama(object sender, RoutedEventArgs e)
    {
        if (Hub.Ollama.Running == true) await Hub.Ollama.TurnOffAsync();
        else await Hub.Ollama.TurnOnAsync();
        UpdateAiStats();
        _vm.Changed();
        await RefreshAiAsync();
    }

    private async void OnToggleAi(object sender, RoutedEventArgs e)
    {
        await Hub.Actions.ExecuteAsync(new HubCommand(Hub.Core.Llm.UserPaused ? "resume_ai" : "pause_ai"));
        UpdateAiStats();
        await RefreshAiAsync();
    }
}
