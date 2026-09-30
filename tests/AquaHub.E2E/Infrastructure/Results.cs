using System.Text.Json;

namespace AquaHub.E2E.Infrastructure;

/// <summary>Run artefacts written next to the screenshots: run.log, interactions.jsonl, findings.jsonl, crawler.json.</summary>
public static class Results
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string RunLogPath => Path.Combine(E2EConfig.RunDir, "run.log");

    public static void Log(string line)
    {
        lock (Gate)
        {
            try { File.AppendAllText(RunLogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { }
        }
    }

    public sealed record Interaction(string Time, string Test, string Area, string Name, bool Ok, string? Note);

    /// <summary>One exercised interaction (feeds the coverage table in REPORT.md).</summary>
    public static void RecordInteraction(string test, string area, string name, bool ok, string? note = null) =>
        Append("interactions.jsonl", new Interaction(DateTime.Now.ToString("HH:mm:ss"), test, area, name, ok, note));

    public sealed record Finding(string Time, string Test, string Kind, string Title, string Detail);

    /// <summary>Something noteworthy that is not a hard failure (accessibility gaps, suspicious behaviour).</summary>
    public static void RecordFinding(string test, string kind, string title, string detail) =>
        Append("findings.jsonl", new Finding(DateTime.Now.ToString("HH:mm:ss"), test, kind, title, detail));

    public static void WriteJson(string fileName, object value)
    {
        lock (Gate)
        {
            try { File.WriteAllText(Path.Combine(E2EConfig.ResultsDir, fileName), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        }
    }

    private static void Append(string file, object record)
    {
        lock (Gate)
        {
            try { File.AppendAllText(Path.Combine(E2EConfig.ResultsDir, file), JsonSerializer.Serialize(record, Json) + Environment.NewLine); } catch { }
        }
    }
}
