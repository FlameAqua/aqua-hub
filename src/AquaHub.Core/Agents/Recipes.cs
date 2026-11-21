using System.Collections.Immutable;
using AquaHub.Core.Health;

namespace AquaHub.Core.Agents;

public sealed record RecipeStep(string CapabilityId, int? FromStep = null, RecipeFindingScope? Findings = null);
public enum RecipeFindingScope { Actionable, All }
public sealed record RecipeDraft(string Name, string Purpose, ImmutableArray<RecipeStep> Steps);
public sealed record AgentRecipe(Guid Id, int Revision, bool Paused, RecipeDraft Draft);

public static class RecipeCapabilities
{
    public const string CheckHealth = "pc.health.check";
    public const string BuildReport = "pc.health.report";
    public const int MaxName = 80;
    public const int MaxPurpose = 200;

    public static string Label(string id) => id switch
    {
        CheckHealth => "Run PC health check",
        BuildReport => "Build local report",
        _ => "Unavailable capability",
    };

    public static RecipeDraft Validate(RecipeDraft draft)
    {
        var name = draft.Name?.Trim() ?? "";
        var purpose = draft.Purpose?.Trim() ?? "";
        if (name.Length is 0 or > MaxName)
            throw new RecipeException($"Enter a name of up to {MaxName} characters.");
        if (purpose.Length > MaxPurpose)
            throw new RecipeException($"Keep the purpose within {MaxPurpose} characters.");
        if (draft.Steps.IsDefaultOrEmpty || draft.Steps.Length > 2)
            throw new RecipeException("Add one or two steps.");

        for (var i = 0; i < draft.Steps.Length; i++)
        {
            var step = draft.Steps[i];
            switch (step?.CapabilityId)
            {
                case CheckHealth when i == 0 && step.FromStep is null && step.Findings is null:
                    break;
                case BuildReport when i == 1 && step.FromStep == 1 && step.Findings is RecipeFindingScope.All or RecipeFindingScope.Actionable:
                    break;
                case CheckHealth:
                    throw new RecipeException("The health check must come first and takes no inputs.");
                case BuildReport:
                    throw new RecipeException("The local report needs the health check from step 1 and a findings filter.");
                default:
                    throw new RecipeException("Choose a supported Aqua capability for every step.");
            }
        }
        return draft with { Name = name, Purpose = purpose };
    }
}

public enum RecipeRunState { Queued, Running, Completed, Blocked, Cancelled, Failed, Interrupted }
public enum RecipeStepState { Pending, Running, Completed, Blocked, Cancelled, Failed, Interrupted }
public enum RecipeStopReason { None, AccessUnavailable, Cancelled, RecipeChanged, Shutdown, Timeout, CheckFailed, Interrupted }

public sealed record RecipeResult(DateTimeOffset Checked, int Problems, int Warnings, int IncompleteChecks,
    int IncludedFindings);
public sealed record RecipeStepRun(string CapabilityId, RecipeStepState State,
    DateTimeOffset? Started = null, DateTimeOffset? Finished = null, RecipeStopReason Reason = RecipeStopReason.None,
    RecipeResult? Result = null);
public sealed record RecipeRun(Guid Id, Guid RecipeId, int Revision, DateTimeOffset Requested,
    RecipeRunState State, ImmutableArray<RecipeStepRun> Steps, DateTimeOffset? Finished = null,
    RecipeStopReason Reason = RecipeStopReason.None)
{
    public bool Active => State is RecipeRunState.Queued or RecipeRunState.Running;
}

public sealed record RecipeFinding(string Area, HealthLevel Level, string Title);
public sealed record RecipePreview(Guid RunId, ImmutableArray<RecipeFinding> Findings, int IncludedFindings);
public sealed record RecipeState(ImmutableArray<AgentRecipe> Recipes, ImmutableArray<RecipeRun> Runs);

public sealed class RecipeException(string message) : InvalidOperationException(message);

/// <summary>A review of one saved revision. Its explicit Run action can claim only one run.</summary>
public sealed class RecipeReview
{
    internal RecipeReview(AgentRecipe recipe) => Recipe = recipe;
    public AgentRecipe Recipe { get; }
    internal Guid RunId { get; set; }
}
