using AquaHub.Core.Ai.Roleplay;

namespace AquaHub.Services;

internal static class CreativeModels
{
    public static CreativeRoute Resolve()
    {
        var ai = Hub.S.Ai;
        var health = Hub.Core.Llm.Health;
        if (ai.Provider != "llamacpp") return new(health.ActiveModel, ai.ContextTokens, false);
        var model = Hub.Llama.Library.Find(ai.Llama.CreativeModel);
        var choice = RoleplayPrompt.SelectModel(ai.Provider, model?.Id ?? ai.Llama.CreativeModel, null,
            model is { Embedding: false } ? [model.Id] : []);
        return choice is null
            ? new(null, ai.Llama.ContextTokens > 0 ? ai.Llama.ContextTokens : 8192, false)
            : new(choice, Hub.LocalAi.Fit(model!).ContextTokens, true);
    }

    public static bool IsRemote => Hub.S.Ai.Provider != "llamacpp"
        && Uri.TryCreate(Hub.S.Ai.Endpoint, UriKind.Absolute, out var endpoint) && !endpoint.IsLoopback;

    public static string Description()
    {
        if (!Hub.S.Ai.Enabled) return "AI is off";
        try
        {
            var route = Resolve();
            var model = Hub.Core.Llm.Health.ActiveModel;
            return route.RequireModel ? $"Creative · {Hub.Llama.Library.Find(route.Model)?.Name ?? route.Model}"
                : Hub.S.Ai.Provider == "llamacpp" ? $"Main · {model ?? "Automatic"}" : model ?? "Configured AI provider";
        }
        catch (RoleplayModelUnavailableException) { return "Creative model missing"; }
    }

    public static async Task<Personality> DraftAsync(string idea, CancellationToken ct)
    {
        var result = await Hub.Core.Llm.CompleteAsync(RoleplayPrompt.DraftRequest(idea, Resolve()), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return RoleplayPrompt.ParseDraft(result.Text);
    }
}
