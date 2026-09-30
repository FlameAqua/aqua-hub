using System.Collections.Concurrent;
using System.Diagnostics;
using AquaHub.Core.Ai;
using AquaHub.Core.Data;
using AquaHub.Core.Models;
using AquaHub.Core.Net;
using AquaHub.Core.Settings;
using AquaHub.Core.Util;

namespace AquaHub.Core.Agents;

/// <summary>OS integration points the core needs (implemented by the Windows app).</summary>
public interface IPlatform
{
    /// <summary>A full-screen game / presentation is running.</summary>
    bool IsFullscreenBusy { get; }
    /// <summary>No keyboard/mouse input for a while.</summary>
    bool IsUserIdle { get; }
    bool IsOnBattery { get; }
    bool IsUiVisible { get; }
    /// <summary>Deliver a notification (the platform applies quiet hours / do-not-disturb rules).</summary>
    void Notify(HubAlert alert);
}

public sealed class NullPlatform : IPlatform
{
    public bool IsFullscreenBusy => false;
    public bool IsUserIdle => false;
    public bool IsOnBattery => false;
    public bool IsUiVisible => true;
    public List<HubAlert> Delivered { get; } = new();
    public void Notify(HubAlert alert) => Delivered.Add(alert);
}

public sealed class HubContext
{
    public required SettingsStore Settings { get; init; }
    public required HubDatabase Db { get; init; }
    public required HttpFetcher Http { get; init; }
    public required LlmClient Llm { get; init; }
    public required HubState State { get; init; }
    public required ISecretStore Secrets { get; init; }
    public required IPlatform Platform { get; init; }
    public HubSettings S => Settings.Current;

    /// <summary>
    /// Adds an alert to the bell and delivers it as a notification (the platform applies quiet hours and do not
    /// disturb). With <paramref name="onceKey"/>, only the first time that key is seen.
    /// </summary>
    public bool RaiseAlert(HubAlert alert, string? onceKey = null)
    {
        if (onceKey is not null && !Db.TryMarkSeen(onceKey)) return false;
        if (!Db.AddAlert(alert)) return false;
        State.ReloadAlerts();
        Platform.Notify(alert);
        return true;
    }
}

public sealed record AgentResult(bool Ok, string Message, bool Changed = true, TimeSpan? RetryIn = null, bool Waiting = false)
{
    public static AgentResult Success(string message, bool changed = true) => new(true, message, changed);
    public static AgentResult Unchanged(string message) => new(true, message, false);
    public static AgentResult Fail(string message) => new(false, message, false);
    public static AgentResult Wait(string message, TimeSpan retry) => new(true, message, false, retry, Waiting: true);
}

public abstract class Agent
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    /// <summary>collector | curator | editor | analyst | sentinel | keeper</summary>
    public virtual string Role => "collector";
    public virtual bool UsesAi => false;
    /// <summary>Agent ids whose (changed) output should trigger this agent soon after.</summary>
    public virtual string[] After => Array.Empty<string>();
    /// <summary>Eco mode may stretch this agent's interval while the PC is idle.</summary>
    public virtual bool Stretchable => true;
    public virtual TimeSpan InitialDelay => TimeSpan.FromSeconds(1);
    public virtual bool IsEnabled(HubContext ctx) => true;
    public abstract TimeSpan Interval(HubContext ctx);
    public abstract Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct);
}

/// <summary>
/// Schedules agents: interval-based runs, dependency triggers (collector → curator → editor),
/// exponential back-off on failure, eco-mode stretching, run-now, and status/telemetry reporting.
/// Collectors run concurrently (bounded); AI agents share the single model slot via the LLM gate.
/// </summary>
public sealed class AgentRuntime : IDisposable
{
    private sealed class Slot
    {
        public required Agent Agent;
        public AgentStatus Status = null!;
        public DateTimeOffset NextRun;
        public int ConsecutiveErrors;
        public volatile bool Running;
        public bool Forced;
    }

    private readonly HubContext _ctx;
    private readonly List<Slot> _slots = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _collectors = new(3);
    private readonly SemaphoreSlim _ai = new(1);
    private readonly ConcurrentDictionary<string, Task> _inflight = new();
    private Task? _loop;

    public AgentRuntime(HubContext ctx) => _ctx = ctx;

    public event Action<AgentStatus>? StatusChanged;
    public bool Paused { get; set; }

    public IReadOnlyList<AgentStatus> Statuses
    {
        get { lock (_slots) return _slots.Select(s => s.Status).ToList(); }
    }

    public void Register(Agent agent)
    {
        lock (_slots)
        {
            _slots.Add(new Slot
            {
                Agent = agent,
                NextRun = DateTimeOffset.Now + agent.InitialDelay,
                Status = new AgentStatus
                {
                    Id = agent.Id, Name = agent.Name, Description = agent.Description, Role = agent.Role, UsesAi = agent.UsesAi,
                    State = AgentState.Idle, NextRun = DateTimeOffset.Now + agent.InitialDelay,
                },
            });
        }
    }

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    public void RunNow(string id)
    {
        lock (_slots)
            foreach (var s in _slots.Where(s => s.Agent.Id == id)) { s.NextRun = DateTimeOffset.Now; s.Forced = true; }
    }

    public void RunAll(Func<Agent, bool>? filter = null)
    {
        lock (_slots)
            foreach (var s in _slots.Where(s => filter?.Invoke(s.Agent) ?? true)) { s.NextRun = DateTimeOffset.Now; s.Forced = true; }
    }

    public bool IsForced(string id)
    {
        lock (_slots) return _slots.Any(s => s.Agent.Id == id && s.Forced);
    }

    /// <summary>Waits until all agents have completed at least one run (used by snapshot mode).</summary>
    public async Task WaitForFirstPassAsync(TimeSpan timeout, Func<Agent, bool>? filter = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            bool done;
            lock (_slots) done = _slots.Where(s => filter?.Invoke(s.Agent) ?? true)
                .All(s => !s.Agent.IsEnabled(_ctx) || (s.Status.Runs > 0 && !s.Running));
            if (done) return;
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!Paused)
                {
                    List<Slot> due;
                    var now = DateTimeOffset.Now;
                    lock (_slots) due = _slots.Where(s => !s.Running && s.NextRun <= now).OrderBy(s => s.NextRun).ToList();
                    foreach (var slot in due)
                    {
                        if (!slot.Agent.IsEnabled(_ctx))
                        {
                            slot.NextRun = now + TimeSpan.FromMinutes(1);
                            Update(slot, slot.Status with { State = AgentState.Disabled, NextRun = null, LastMessage = "Disabled in settings" });
                            continue;
                        }
                        slot.Running = true;
                        _inflight[slot.Agent.Id] = Task.Run(() => RunSlotAsync(slot, ct), ct);
                    }
                }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error("agents", "Scheduler loop error", ex); }
        }
    }

    private TimeSpan EffectiveInterval(Agent agent)
    {
        var interval = agent.Interval(_ctx);
        if (_ctx.S.General.EcoMode && agent.Stretchable && !_ctx.Platform.IsUiVisible)
        {
            if (_ctx.Platform.IsUserIdle) interval *= 3;
            else if (_ctx.Platform.IsOnBattery) interval *= 2;
        }
        return interval;
    }

    private async Task RunSlotAsync(Slot slot, CancellationToken ct)
    {
        var agent = slot.Agent;
        var gate = agent.UsesAi ? _ai : _collectors;
        var started = DateTimeOffset.Now;
        try
        {
            Update(slot, slot.Status with { State = AgentState.Waiting, LastMessage = "Queued" });
            await gate.WaitAsync(ct).ConfigureAwait(false);
            AgentResult result;
            var sw = Stopwatch.StartNew();
            try
            {
                started = DateTimeOffset.Now;
                Update(slot, slot.Status with { State = AgentState.Running, LastMessage = "Working…" });
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(agent.UsesAi ? TimeSpan.FromMinutes(12) : TimeSpan.FromMinutes(3));
                result = await agent.RunAsync(_ctx, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = AgentResult.Fail("Timed out");
            }
            catch (LlmUnavailableException ex)
            {
                result = AgentResult.Wait(ex.Message, TimeSpan.FromMinutes(2));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("agents", $"{agent.Id} failed", ex);
                result = AgentResult.Fail(ex.Message);
            }
            finally
            {
                gate.Release();
            }
            sw.Stop();

            var now = DateTimeOffset.Now;
            TimeSpan next;
            if (!result.Ok)
            {
                slot.ConsecutiveErrors++;
                var backoff = TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(6, slot.ConsecutiveErrors - 1)));
                next = backoff < EffectiveInterval(agent) ? backoff : EffectiveInterval(agent);
            }
            else
            {
                slot.ConsecutiveErrors = 0;
                next = result.RetryIn ?? EffectiveInterval(agent);
            }
            slot.NextRun = now + next;
            slot.Forced = false;

            var state = !result.Ok ? AgentState.Error : result.Waiting ? AgentState.Paused : AgentState.Idle;
            Update(slot, slot.Status with
            {
                State = state,
                LastRun = started,
                LastDuration = sw.Elapsed,
                NextRun = slot.NextRun,
                LastMessage = result.Message,
                Runs = slot.Status.Runs + 1,
                Errors = slot.Status.Errors + (result.Ok ? 0 : 1),
            });

            try { _ctx.Db.AddRun(new AgentRunRecord(agent.Id, started, sw.Elapsed, result.Ok, result.Message)); } catch { }
            Log.Info("agents", $"{agent.Id}: {(result.Ok ? "ok" : "FAIL")} in {sw.ElapsedMilliseconds} ms — {result.Message}");

            if (result.Ok && result.Changed)
            {
                lock (_slots)
                {
                    foreach (var dependent in _slots.Where(s => s.Agent.After.Contains(agent.Id)))
                    {
                        var soon = now + TimeSpan.FromSeconds(2);
                        if (dependent.NextRun > soon) dependent.NextRun = soon;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            slot.Running = false;
            _inflight.TryRemove(agent.Id, out _);
        }
    }

    private void Update(Slot slot, AgentStatus status)
    {
        slot.Status = status;
        try { StatusChanged?.Invoke(status); } catch { }
        _ctx.State.NotifyAgents();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        _cts.Dispose();
    }
}
