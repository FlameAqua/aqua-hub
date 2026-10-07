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
    private readonly TextBlock _status = new() { FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 180 };
    private bool _requested;

    public FeedRefresh()
    {
        _button.SetResourceReference(StyleProperty, "Btn.Standard");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "B.Text3");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Children.Add(_button); Children.Add(_status);
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
        if (busy) { _status.Text = "Checking your sources"; return; }
        var status = Hub.Core.Agents.Statuses.FirstOrDefault(s => s.Id == AgentId);
        _status.Text = status?.State switch
        {
            AgentState.Error => "Refresh failed",
            AgentState.Disabled => "Sources disabled",
            _ => status?.LastRun is { } at ? $"Checked {at.ToLocalTime():HH:mm}" : "",
        };
        _status.ToolTip = status?.LastMessage;
    }

    public async Task RefreshAsync()
    {
        if (_requested || Running) return;
        _requested = true; Update();
        string? message = null;
        string? error = null;
        try
        {
            var report = await Hub.Core.Agents.RunAndWaitAsync(AgentId, TimeSpan.FromSeconds(60), CancellationToken.None);
            if (report is { End: AgentRunEnd.Finished, Status.State: not AgentState.Error } && AfterId.Length > 0)
                await Hub.Core.Agents.RunAndWaitAsync(AfterId, TimeSpan.FromSeconds(30), CancellationToken.None);
            message = report?.End switch
            {
                AgentRunEnd.Paused => "Agents paused · resume in Agents",
                AgentRunEnd.Disabled => "Sources disabled",
                null => "Refresh unavailable",
                _ => null,
            };
        }
        catch (Exception ex) { message = "Refresh failed"; error = ex.Message; }
        finally
        {
            _requested = false; Update();
            if (message is not null) _status.Text = message;
            if (error is not null) _status.ToolTip = error;
        }
    }
}
