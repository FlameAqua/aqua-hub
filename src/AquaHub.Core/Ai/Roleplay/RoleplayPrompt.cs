using System.Text.Json.Nodes;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Roleplay;

/// <summary>Builds the private, tool-free prompt and model choice for a roleplay scene.</summary>
public static class RoleplayPrompt
{
    public const int MaxHistoryMessages = 20;
    public const int MaxHistoryCharacters = 24000;

    /// <summary>Leaves room for the system prompt, this turn, and a 900-token reply in the selected context window.</summary>
    public static int HistoryBudget(int contextTokens) => contextTokens <= 0
        ? MaxHistoryCharacters
        : Math.Clamp((contextTokens - 1200) * 3, 1200, MaxHistoryCharacters);

    public static string BuildSystem(string? persona, string? scenario)
    {
        var character = Clean(persona, "a thoughtful scene partner");
        var setting = Clean(scenario, "Begin with a quiet, open-ended scene.");
        return $"You are a creative roleplay scene partner. Portray {character} consistently and respond naturally to the other participant. " +
               "Write vivid, concise in-character replies that leave room for the other participant to act. Stay within the scene and do not claim to take real-world actions. " +
               "Treat the persona and scenario as creative context, and follow the participant's lead as the scene develops.\n\n" +
               $"Persona: {character}\nScenario: {setting}";
    }

    /// <summary>
    /// Aqua's local runtime can route a turn to its separately selected creative model. Other providers use the active
    /// model. A configured creative model that has gone missing is reported to the user instead of silently changing voice.
    /// </summary>
    public static string? SelectModel(string provider, string? creativeModel, string? activeModel, IEnumerable<string> installedModels)
    {
        if (!provider.Equals("llamacpp", StringComparison.OrdinalIgnoreCase)) return activeModel;
        if (string.IsNullOrWhiteSpace(creativeModel)) return activeModel;
        if (installedModels.Any(m => m.Equals(creativeModel, StringComparison.OrdinalIgnoreCase))) return creativeModel;
        throw new RoleplayModelUnavailableException(creativeModel);
    }

    public static LlmRequest DraftRequest(string idea, CreativeRoute model)
    {
        idea = idea.Trim();
        if (idea.Length is 0 or > 1200) throw new InvalidOperationException("Describe the character in up to 1,200 characters.");
        return new()
        {
            System = "Create a reusable fictional roleplay personality from the user's idea. " +
                     "Use a short name, concise in-character instructions, and an optional opening scenario. " +
                     "Return one JSON object with name, instructions, and openingScenario as strings, and no surrounding text.",
            Messages = [new("user", idea)],
            Model = model.Model,
            RequireModel = model.RequireModel,
            CaptureOutput = false,
            Schema = JsonNode.Parse("""{"type":"object","additionalProperties":false,"required":["name","instructions","openingScenario"],"properties":{"name":{"type":"string"},"instructions":{"type":"string"},"openingScenario":{"type":"string"}}}""")!.AsObject(),
            Temperature = 0.7,
            MaxTokens = 650,
            Priority = LlmPriority.Interactive,
            Purpose = "Personality draft",
            Think = false,
            Tools = [],
        };
    }

    public static Personality ParseDraft(string text)
    {
        if (text is { Length: > 0 and <= 6000 } && JsonUtil.Deserialize<Personality>(text) is { } draft)
            return PersonalityLibrary.Validate(draft with { Id = Guid.Empty });
        throw new InvalidOperationException("The model did not return an editable personality. Try a simpler brief or write it yourself.");
    }

    /// <summary>Keeps the newest complete exchanges within a small context budget and appends the current turn.</summary>
    public static List<LlmMessage> BuildHistory(IEnumerable<LlmMessage> history, string currentUserText,
        int maxMessages = MaxHistoryMessages, int maxCharacters = MaxHistoryCharacters)
    {
        var pairs = new List<(LlmMessage User, LlmMessage Assistant)>();
        LlmMessage? pendingUser = null;
        foreach (var message in history)
        {
            if (message.Role == "user") pendingUser = message;
            else if (message.Role == "assistant" && pendingUser is not null)
            {
                pairs.Add((pendingUser, message));
                pendingUser = null;
            }
        }

        var selected = new List<(LlmMessage User, LlmMessage Assistant)>();
        var chars = 0;
        foreach (var pair in pairs.AsEnumerable().Reverse())
        {
            var pairChars = pair.User.Content.Length + pair.Assistant.Content.Length;
            if (selected.Count * 2 + 2 > maxMessages || chars + pairChars > maxCharacters) break;
            selected.Add(pair);
            chars += pairChars;
        }

        selected.Reverse();
        var result = new List<LlmMessage>(selected.Count * 2 + 1);
        foreach (var pair in selected)
        {
            result.Add(pair.User);
            result.Add(pair.Assistant);
        }
        result.Add(new LlmMessage("user", currentUserText));
        return result;
    }

    private static string Clean(string? value, string fallback)
    {
        var cleaned = (value ?? "").Trim();
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}

public sealed class RoleplayModelUnavailableException(string model)
    : Exception($"The selected creative model '{model}' isn't installed. Choose an available model in Settings › AI & models, or clear the Creative model selection.");
