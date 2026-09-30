using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AquaHub.Core.Util;

namespace AquaHub.Services;

/// <summary>
/// Settings › Debug › Copy summary: what someone helping with a problem needs — versions, the model and its server,
/// the profile's sizes, which features are on, and the latest problems. No places, feeds, chats, passwords or keys
/// (secrets are in Windows Credential Manager and never read here), and in the problems your user name, PC name,
/// folder and file names and web addresses are masked (<see cref="Redact"/>).
/// </summary>
internal static class DiagnosticsSummary
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Mask(string text) =>
        Redact.ForSharing(text, Environment.UserName, Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Build()
    {
        var s = Hub.S;
        var sb = new StringBuilder();
        sb.Append("Aqua Hub diagnostics — ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", Inv)).Append('\n');
        sb.Append("Version: ").Append(typeof(App).Assembly.GetName().Version?.ToString(3)).Append('\n');
        sb.Append("Windows: ").Append(RuntimeInformation.OSDescription).Append(" (").Append(RuntimeInformation.OSArchitecture).Append(")\n");
        sb.Append(".NET: ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
        var uptime = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
        sb.Append("Running for: ").Append(uptime.TotalHours >= 1 ? $"{(int)uptime.TotalHours} h {uptime.Minutes} min" : $"{uptime.Minutes} min").Append('\n');

        var h = Hub.Core.Llm.Health;
        var endpoint = Uri.TryCreate(h.Endpoint, UriKind.Absolute, out var u) ? $"{u.Host}:{u.Port}" : "(not set)";
        sb.Append("Model: ").Append(h.Available ? "available" : "not available").Append(" · ").Append(h.Provider).Append(" at ").Append(endpoint)
          .Append(h.Version is { Length: > 0 } v ? " (version " + v + ")" : "").Append(" · active ").Append(h.ActiveModel ?? "none")
          .Append(h.Error is { Length: > 0 } e ? " · error: " + Mask(e) : "").Append('\n');
        sb.Append("AI: ").Append(s.Ai.Enabled ? "on" : "off").Append(", context ").Append(s.Ai.ContextTokens.ToString("N0", Inv)).Append(" tokens, Ask window ")
          .Append(s.Ask.ContextWindow == 0 ? "automatic" : s.Ask.ContextWindow.ToString("N0", Inv)).Append('\n');
        sb.Append("Ask: web ").Append(s.Ask.Web ? "allowed" : "off").Append(" (").Append(s.Ask.SearchEngine).Append("), Use my PC ").Append(s.Ask.Computer ? "allowed" : "off")
          .Append(", operate apps ").Append(s.Ask.ControlApps ? s.Ask.OperateApps : "off").Append(", ").Append(s.Ask.Folders.Count).Append(" folders, voice ").Append(s.Ask.Voice)
          .Append(s.Ask.Voice == "off" ? "" : (s.Ask.Microphone.Length > 0 ? " (chosen microphone" : " (default microphone") + (s.Ask.VoiceTidy ? ", tidy-up on)" : ", tidy-up off)"))
          .Append(", ask before acting ").Append(s.Ask.ConfirmActions ? "on" : "off").Append('\n');

        var paths = Hub.Core.Paths;
        sb.Append("Profile: ").Append(paths.Root == DefaultRoot() ? "default" : "custom (--data-dir)").Append(" · database ").Append(Size(paths.Database))
          .Append(" · logs ").Append(Size(Log.FilePath)).Append(" + problems ").Append(Size(Log.ProblemsPath)).Append('\n');
        var failing = Hub.Core.Agents.Statuses.Where(a => a.State == Core.Models.AgentState.Error).Select(a => a.Name).ToList();
        sb.Append("Agents failing: ").Append(failing.Count == 0 ? "none" : string.Join(", ", failing)).Append('\n');

        var problems = Log.RecentProblems;
        sb.Append("Problems since start: ").Append(Log.ProblemCount).Append('\n');
        foreach (var p in problems.Reverse().Take(10))
            sb.Append("  ").Append(p.Time.ToString("MM-dd HH:mm:ss", Inv)).Append(' ').Append(p.Level.ToString().ToUpperInvariant()).Append(" [").Append(p.Area).Append("] ")
              .Append(HtmlText.Truncate(Mask(p.Message.ReplaceLineEndings(" ")), 220)).Append('\n');
        return sb.ToString();
    }

    private static string DefaultRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaHub");

    private static string Size(string? path)
    {
        try { return path is not null && File.Exists(path) ? Core.Ai.Assistant.LocalFiles.Size(new FileInfo(path).Length) : "—"; }
        catch (IOException) { return "?"; }
    }
}
