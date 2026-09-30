using System.Globalization;
using System.Text;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// A whole chat as Markdown — each question with its mode and attachments, and each answer with its plan and steps
/// (searches, pages, files), its reasoning, the answer itself, its sources and footer — for sharing, or for a bug
/// report about how Aqua handled something. Pure and unit-tested.
/// </summary>
public static class ChatExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ToMarkdown(string title, IReadOnlyList<SavedChatMessage> messages, DateTimeOffset exported)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(title.Trim().Length > 0 ? title.Trim() : "Ask Aqua chat").Append('\n');
        var questions = messages.Count(m => m.User);
        sb.Append("_Aqua Hub · exported ").Append(exported.ToString("d MMM yyyy HH:mm", Inv)).Append(" · ")
          .Append(questions).Append(questions == 1 ? " question" : " questions").Append("_\n");

        foreach (var m in messages)
        {
            sb.Append("\n---\n\n");
            var when = m.At is { } at ? at.ToString("d MMM HH:mm", Inv) : "";
            if (m.User)
            {
                sb.Append("### You").Append(when.Length > 0 ? " · " + when : "").Append(m.Mode is { Length: > 0 } mode ? " · " + mode : "").Append("\n\n");
                sb.Append(m.Text.Trim()).Append('\n');
                if (m.Attachments is { Count: > 0 } files) sb.Append("\n_Attached: ").Append(string.Join(", ", files)).Append("_\n");
                continue;
            }
            sb.Append("### Aqua").Append(when.Length > 0 ? " · " + when : "").Append("\n\n");
            if (m.Steps is { Count: > 0 } steps)
            {
                sb.Append("**What Aqua did**\n");
                foreach (var s in steps) sb.Append("- ").Append(s.Trim()).Append('\n');
                sb.Append('\n');
            }
            if (m.Reasoning is { Length: > 0 } reasoning)
                sb.Append("<details><summary>Reasoning</summary>\n\n").Append(reasoning.Trim()).Append("\n\n</details>\n\n");
            sb.Append(m.Text.Trim().Length > 0 ? m.Text.Trim() : "_(no answer)_").Append('\n');
            if (m.Citations is { Count: > 0 } cites)
            {
                sb.Append("\n**Sources**\n");
                foreach (var c in cites.OrderBy(c => c.Number))
                {
                    sb.Append(c.Number.ToString(Inv)).Append(". ");
                    if (c.Url is { Length: > 0 } url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) sb.Append('[').Append(Clean(c.Title)).Append("](").Append(url).Append(')');
                    else sb.Append(Clean(c.Title)).Append(c.Url is { Length: > 0 } path ? " (" + path + ")" : "");
                    sb.Append(" — ").Append(c.Source).Append(c.Kind == "result" ? " (search snippet only)" : "").Append('\n');
                }
            }
            if (m.Footer is { Length: > 0 } footer) sb.Append("\n_").Append(footer.Trim()).Append("_\n");
        }
        return sb.ToString();
    }

    private static string Clean(string title) => title.Replace('[', '(').Replace(']', ')').Replace('\n', ' ').Trim();
}
