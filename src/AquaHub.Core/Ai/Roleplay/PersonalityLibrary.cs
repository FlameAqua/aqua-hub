using System.Text.Json;
using AquaHub.Core.Data;
using AquaHub.Core.Util;

namespace AquaHub.Core.Ai.Roleplay;

public sealed record Personality(Guid Id, string Name, string Instructions, string OpeningScenario)
{
    public override string ToString() => Name;
}

/// <summary>Local Roleplay templates. Scene drafts and conversation state never enter this store.</summary>
public sealed class PersonalityLibrary(HubDatabase database)
{
    public const int MaxEntries = 60;
    public const int MaxName = 80;
    public const int MaxInstructions = 1200;
    public const int MaxScenario = 1200;
    internal const string StorageKey = "roleplay:personalities";
    private const int Schema = 1;
    private readonly object _gate = new();

    public event Action? Changed;

    public IReadOnlyList<Personality> List()
    {
        lock (_gate) return Read().AsReadOnly();
    }

    public Personality Save(Personality draft)
    {
        var entry = Validate(draft);
        lock (_gate)
        {
            var entries = Read();
            if (entry.Id == Guid.Empty)
            {
                if (entries.Count >= MaxEntries)
                    throw new InvalidOperationException($"You can keep up to {MaxEntries} personalities. Delete one to make room.");
                entry = entry with { Id = Guid.NewGuid() };
                entries.Add(entry);
            }
            else
            {
                var index = entries.FindIndex(item => item.Id == entry.Id);
                if (index < 0) throw new InvalidOperationException("This personality was deleted. Create a new one to save these fields.");
                entries[index] = entry;
            }
            if (entries.Any(item => item.Id != entry.Id && NamesEqual(item.Name, entry.Name)))
                throw new InvalidOperationException("A personality already uses that name. Choose a different name.");
            database.PutJson(StorageKey, new Document(Schema, entries));
        }
        Changed?.Invoke();
        return entry;
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var entries = Read();
            if (entries.RemoveAll(item => item.Id == id) == 0) return false;
            database.PutJson(StorageKey, new Document(Schema, entries));
        }
        Changed?.Invoke();
        return true;
    }

    public static Personality Validate(Personality entry) => entry with
    {
        Name = Field(entry.Name, MaxName, "Name", required: true),
        Instructions = Field(entry.Instructions, MaxInstructions, "Character instructions", required: true),
        OpeningScenario = Field(entry.OpeningScenario, MaxScenario, "Opening scenario", required: false)
    };

    private List<Personality> Read()
    {
        JsonElement document;
        try { document = database.ReadJsonDocument(StorageKey, maxBytes: 1_000_000); }
        catch (JsonException) { throw new InvalidOperationException("The personality library is damaged. Its saved data has been kept."); }
        if (document.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        // Version zero was a bare list. The next successful edit writes the current envelope.
        var entries = document;
        if (document.ValueKind == JsonValueKind.Object)
        {
            if (!document.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var value) || value != Schema
                || !document.TryGetProperty("entries", out entries))
                throw new InvalidOperationException("This personality library format could not be read. Its saved data has been kept.");
        }
        if (entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The personality library could not be read. Its saved data has been kept.");

        var valid = new List<Personality>();
        foreach (var element in entries.EnumerateArray().Take(MaxEntries))
        {
            try
            {
                var entry = element.Deserialize<Personality>(JsonUtil.Options);
                if (entry is null || entry.Id == Guid.Empty) continue;
                entry = Validate(entry);
                if (valid.All(item => item.Id != entry.Id && !NamesEqual(item.Name, entry.Name))) valid.Add(entry);
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        return valid;
    }

    private static bool NamesEqual(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static string Field(string? value, int maximum, string label, bool required)
    {
        var clean = (value ?? "").Trim();
        if (required && clean.Length == 0) throw new InvalidOperationException($"Enter {label.ToLowerInvariant()}.");
        if (clean.Length > maximum) throw new InvalidOperationException($"{label} can contain up to {maximum} characters.");
        if (clean.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            || label == "Name" && clean.Any(char.IsControl))
            throw new InvalidOperationException($"{label} contains an unsupported character.");
        return clean;
    }

    private sealed record Document(int Version, List<Personality> Entries);
}
