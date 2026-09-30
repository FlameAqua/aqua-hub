using System.Text.RegularExpressions;
using AquaHub.Core.Ai;
using AquaHub.Core.Health;
using AquaHub.Core.Util;
using AquaHub.Platform;

namespace AquaHub.Services;

/// <summary>
/// Runs the PC health check (This PC › Health check, or Ask's check_pc_health tool): Windows' own readings
/// (<see cref="HealthInspector"/>), the rules (<see cref="PcHealth"/>), then — when the local model is running — a short
/// summary in plain words. The last report is kept, so the page shows it again after a restart.
/// </summary>
public sealed class HealthService
{
    private const string Key = "health.last";
    private readonly SemaphoreSlim _one = new(1, 1);
    private HealthReport? _last;
    private bool _loaded;

    /// <summary>A step of a check in progress, or a finished report (UI thread not guaranteed).</summary>
    public event Action<string>? Progress;
    public event Action<HealthReport>? Finished;

    public bool Running => _one.CurrentCount == 0;

    public HealthReport? Last
    {
        get
        {
            if (!_loaded)
            {
                _loaded = true;
                try { _last = Hub.Core.Db.GetJson<HealthReport>(Key); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Debug("health", "Couldn't read the last report: " + ex.Message); }
            }
            return _last;
        }
    }

    /// <summary>
    /// Runs a check (one at a time; a second caller waits for the first and gets its report). With
    /// <paramref name="reuseFor"/>, a report that recent is returned instead of checking again.
    /// </summary>
    public async Task<HealthReport> RunAsync(bool summarise, CancellationToken ct, TimeSpan? reuseFor = null)
    {
        var started = DateTimeOffset.Now;
        await _one.WaitAsync(ct);
        try
        {
            if (Last is { } fresh && (fresh.Facts.Checked >= started || (reuseFor is { } age && DateTimeOffset.Now - fresh.Facts.Checked < age))) return fresh;
            var facts = await HealthInspector.InspectAsync(step => Progress?.Invoke(step), ct);
            var report = new HealthReport(facts, PcHealth.Evaluate(facts), null);
            Log.Info("health", $"Health check: {report.Verdict} ({report.Findings.Count} findings, {facts.Skipped.Count} not checked)");
            if (summarise && await SummariseAsync(report, ct) is { } summary) report = report with { Summary = summary };
            _last = report;
            _loaded = true;
            try { Hub.Core.Db.PutJson(Key, report); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Debug("health", "Couldn't keep the report: " + ex.Message); }
            Finished?.Invoke(report);
            return report;
        }
        finally { _one.Release(); }
    }

    /// <summary>The local model's few sentences on the findings, or null when it isn't running or says nothing useful.</summary>
    private async Task<string?> SummariseAsync(HealthReport report, CancellationToken ct)
    {
        if (!Hub.Core.Llm.Health.Available || Hub.Core.Llm.UserPaused) return null;
        Progress?.Invoke("Writing the report…");
        try
        {
            using var slow = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slow.CancelAfter(TimeSpan.FromSeconds(60));
            var result = await Hub.Core.Llm.CompleteAsync(new LlmRequest
            {
                Purpose = "health-report",
                Priority = LlmPriority.Interactive,
                System = PcHealth.SummaryInstructions,
                Messages = { new LlmMessage("user", PcHealth.SummaryPrompt(report)) },
                Temperature = 0.3,
                MaxTokens = 450,
                Think = false,
            }, slow.Token);
            // Plain sentences for the page: no markdown emphasis or headings.
            var text = Regex.Replace(result.Text, @"\*\*|__|^#+\s*", "", RegexOptions.Multiline).Trim();
            return text.Length is > 20 and < 2000 ? text : null;
        }
        catch (Exception ex) when (ex is LlmUnavailableException or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            Log.Debug("health", "No summary: " + ex.Message);
            return null;
        }
    }
}
