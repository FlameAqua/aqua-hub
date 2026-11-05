using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using AquaHub.UI.Shell;
using AquaHub.UI.ViewModels;

namespace AquaHub.Services;

/// <summary>Offline Release diagnostics, explicitly selected with --e2e --data-dir --snapshot --measure --pages performance.
/// Synthetic Markdown measures WPF work, not model/GPU acceptance. No collectors, media, models or servers are started.</summary>
public static class ExperienceMeasurement
{
    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var report = new StringBuilder();
        var delays = new List<double>();
        var inputDelays = new List<double>();
        using var stop = new CancellationTokenSource();
        var dispatcher = Application.Current.Dispatcher;
        var pending = 0;
        var inputPending = 0;
        var sampler = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    if (Interlocked.CompareExchange(ref inputPending, 1, 0) == 0)
                    {
                        var inputPosted = Stopwatch.GetTimestamp();
                        _ = dispatcher.BeginInvoke(() =>
                        {
                            inputDelays.Add(Stopwatch.GetElapsedTime(inputPosted).TotalMilliseconds);
                            Interlocked.Exchange(ref inputPending, 0);
                        }, DispatcherPriority.Input);
                    }
                    if (Interlocked.CompareExchange(ref pending, 1, 0) != 0) continue;
                    var posted = Stopwatch.GetTimestamp();
                    _ = dispatcher.BeginInvoke(() =>
                    {
                        delays.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
                        Interlocked.Exchange(ref pending, 0);
                    }, DispatcherPriority.Background);
                }
            }
            catch (OperationCanceledException) { }
        });
        MainWindow? window = null;
        try
        {
            report.AppendLine($"{DateTimeOffset.UtcNow:O} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {Environment.ProcessorCount} logical CPUs");
#if DEBUG
            report.AppendLine("Configuration: Debug (not a Release baseline)");
#else
            report.AppendLine("Configuration: Release");
#endif
            report.AppendLine("Workload: empty isolated profile, offline AI, 1440 x 940 off-screen WPF window, 300 synthetic messages (150 Markdown answers), 20 theme changes, 5 navigation/show/hide cycles. No model process or VRAM measurement.");
            report.AppendLine("Theme samples end after WPF Render/Layout work plus an idle dispatcher barrier; this measures scheduled rendering, not display scanout. Dispatcher delay samples use one outstanding background callback. Budgets remain proposed: theme p95 <= 100ms, ordinary dispatcher updates p95 <= 50ms.");
            report.AppendLine("initial: " + Memory());
            window = new MainWindow { Width = 1440, Height = 940, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            window.Navigate("today");
            await Idle();
            var themes = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var settings = Hub.Core.Settings.Clone();
                settings.General.Theme = i % 2 == 0 ? "light" : "dark";
                var watch = Stopwatch.StartNew();
                Hub.Theme.Apply(settings.General);
                await Idle();
                themes.Add(watch.Elapsed.TotalMilliseconds);
            }
            report.AppendLine("theme latency: " + Distribution(themes));
            Hub.Ask.EnsureLoaded();
            Hub.Ask.NewChat();
            var markdown = string.Concat(Enumerable.Repeat("## A measured example\n\nA paragraph with **emphasis**, `code`, and a useful explanation.\n\n| Item | Value |\n|---|---|\n| Alpha | 42 |\n| Beta | 17 |\n\n- First observation\n- Second observation\n\n```csharp\nvar total = 42 + 17;\n```\n\n", 6));
            for (var i = 0; i < 300; i++) Hub.Ask.Messages.Add(new ChatMessageVM
            { IsUser = i % 2 == 0, IsChat = true, IsDone = true, Text = i % 2 == 0 ? $"Question {i / 2 + 1}" : markdown });
            var open = Stopwatch.StartNew();
            window.Navigate("ask");
            await Idle();
            report.AppendLine($"open 300-message chat: {open.Elapsed.TotalMilliseconds:0.00} ms; answer length {markdown.Length} chars; " + Memory());
            await Task.Delay(250);
            while (UI.Controls.MarkdownView.PendingRenders > 0) await Task.Delay(20);
            await Idle();
            report.AppendLine($"fully rendered chat after {open.Elapsed.TotalMilliseconds:0.00} ms: " + Memory());
            var cycles = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                window.Navigate("today");
                window.Hide();
                await Idle();
                window.Show();
                window.Navigate("ask");
                while (UI.Controls.MarkdownView.PendingRenders > 0) await Task.Delay(20);
                await Idle();
                cycles.Add(watch.Elapsed.TotalMilliseconds);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                report.AppendLine($"cycle {i + 1} after full GC (no working-set trim): " + Memory());
            }
            report.AppendLine("navigation/show/hide latency: " + Distribution(cycles));
        }
        catch (Exception ex) { report.AppendLine("FAILED: " + ex); }
        finally
        {
            stop.Cancel();
            await sampler;
            await Idle();
            report.AppendLine("dispatcher queue delay across workload: " + Distribution(delays));
            report.AppendLine("input-priority dispatcher delay across workload (includes explicit GC): " + Distribution(inputDelays));
            if (window is not null) window.Hide();
            await File.WriteAllTextAsync(Path.Combine(output, "performance.txt"), report.ToString());
            Hub.Quit();
        }
    }

    private static async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static string Distribution(List<double> values)
    {
        if (values.Count == 0) return "no samples";
        var sorted = values.Order().ToArray();
        return $"n={values.Count}; p50={sorted[(int)Math.Ceiling(values.Count * .5) - 1]:0.00} ms; p95={sorted[(int)Math.Ceiling(values.Count * .95) - 1]:0.00} ms; max={sorted[^1]:0.00} ms";
    }
    private static string Memory()
    {
        using var process = Process.GetCurrentProcess();
        return $"private bytes={process.PrivateMemorySize64 / 1048576d:0.0} MiB; working set={process.WorkingSet64 / 1048576d:0.0} MiB; managed={GC.GetTotalMemory(false) / 1048576d:0.0} MiB";
    }
}
