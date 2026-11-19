using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AquaHub.Core.Agents;
using AquaHub.Core.Models;
using AquaHub.Services;

namespace AquaHub.UI.Controls;

/// <summary>Joins an existing collector run and prevents repeated clicks from queuing refreshes.</summary>
public sealed class FeedRefresh : StackPanel
{
    public string AgentId { get; set; } = "";
    public string AfterId { get; set; } = "";
    public string Label { get; set; } = "feed";
    private readonly Button _button = new() { Content = "Refresh", MinWidth = 110 };
    private bool _requested;

    public FeedRefresh()
    {
        _button.SetResourceReference(StyleProperty, "Btn.Standard");
        Children.Add(_button);
        _button.Click += async (_, _) => await RefreshAsync();
        Loaded += (_, _) =>
        {
            AutomationProperties.SetAutomationId(_button, AgentId + "-refresh");
            AutomationProperties.SetName(_button, "Refresh " + Label);
            Hub.Core.Agents.StatusChanged -= OnStatus;
            Hub.Core.Agents.StatusChanged += OnStatus;
            Update();
        };
        Unloaded += (_, _) => Hub.Core.Agents.StatusChanged -= OnStatus;
    }

    private void OnStatus(AgentStatus status)
    {
        if (status.Id == AgentId || status.Id == AfterId) Hub.OnUi(() => { if (IsLoaded) Update(); });
    }

    private bool Running => Hub.Core.Agents.Statuses.Any(s => (s.Id == AgentId || s.Id == AfterId) && s.State == AgentState.Running);

    private void Update()
    {
        var busy = _requested || Running;
        _button.IsEnabled = !busy;
        _button.Content = busy ? "Refreshing…" : "Refresh";
        var status = Hub.Core.Agents.Statuses.FirstOrDefault(s => s.Id == AgentId);
        _button.ToolTip = status?.LastMessage;
        AutomationProperties.SetHelpText(_button, status?.LastMessage ?? "");
    }

    public async Task RefreshAsync()
    {
        if (_requested || Running) return;
        _requested = true; Update();
        var id = AgentId + "-refresh-notice";
        var feedback = IsVisible ? Hub.Windows.Main?.WorkspaceFeedback : null;
        feedback?.Show($"Refreshing {Label}…", id, busy: true);
        string message;
        var error = false;
        try
        {
            var report = await Hub.Core.Agents.RunAndWaitAsync(AgentId, TimeSpan.FromSeconds(60), CancellationToken.None);
            if (report is { End: AgentRunEnd.Finished, Status.State: not AgentState.Error } && AfterId.Length > 0)
                await Hub.Core.Agents.RunAndWaitAsync(AfterId, TimeSpan.FromSeconds(30), CancellationToken.None);
            error = report is null || report.Status.State == AgentState.Error;
            message = report?.End switch
            {
                AgentRunEnd.Paused => "Agents paused. Resume them in Agents.",
                AgentRunEnd.Disabled => "Sources disabled",
                AgentRunEnd.StillRunning => "Refresh is still running. Check back shortly.",
                AgentRunEnd.Finished when error => report.Status.LastMessage is { Length: > 0 } detail ? detail : "Refresh failed.",
                AgentRunEnd.Finished when report.Status.State == AgentState.Waiting => "Waiting for sources. The last available results are kept.",
                AgentRunEnd.Finished => "Sources refreshed.",
                _ => "Refresh is unavailable.",
            };
        }
        catch (Exception) { message = "Refresh failed."; error = true; }
        finally { _requested = false; Update(); }
        if (IsVisible) feedback?.Show(message, id, error: error);
        else feedback?.Hide(id);
    }
}
