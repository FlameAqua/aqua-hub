namespace AquaHub.Core.Agents;

/// <summary>Read-only live stream observations, kept separate from collected posts and AI summaries.</summary>
public sealed class StreamScoutAgent : Agent
{
    public override string Id => "stream-scout";
    public override string Name => "Stream Scout";
    public override string Description => "Checks followed Twitch channels and saved YouTube channels while Social is open.";
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(1);
    public override bool IsEnabled(HubContext ctx) => ctx.Streams?.Enabled == true;
    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        if (ctx.Streams is null || !ctx.Streams.Visible || !ctx.Platform.IsUiVisible) return AgentResult.Unchanged("Streams update while Social is open");
        try { await ctx.Streams.RefreshAsync(force: ctx.Streams.TakeRequestedCheck(), ct: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !ctx.Streams.Visible)
        { return AgentResult.Unchanged("Social closed; stream check cancelled"); }
        var snapshot = ctx.State.Streams;
        return snapshot.Sources.Count > 0 && snapshot.Sources.All(s => !s.Ok)
            ? AgentResult.Fail("Stream sources couldn't be checked; the last observations are kept")
            : AgentResult.Success($"{snapshot.Sources.SelectMany(s => s.Items).DistinctBy(s => s.Id).Count()} live stream observations");
    }
}
