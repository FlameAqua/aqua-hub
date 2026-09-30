using System.Globalization;
using System.Text.RegularExpressions;
using AquaHub.Core.Models;

namespace AquaHub.Core.Updates;

/// <summary>
/// The rules behind Aqua's updates; the app's UpdateService does the Velopack work. Where releases come from, when to
/// look, and what to tell the user. Nothing here touches the network.
/// </summary>
public static partial class UpdatePolicy
{
    /// <summary>The first automatic check waits until start-up has settled.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(3);

    /// <summary>Automatic checks happen about once a day (a little under, so a PC used at the same time daily gets one).</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);

    /// <summary>After a check that failed (GitHub busy, a flaky connection), the next one comes sooner.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(2);

    /// <summary>Settings › About › Updates: where update alerts lead.</summary>
    public const string Target = "settings:about:updates";

    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex GithubRepo();

    /// <summary>
    /// The GitHub repository a release build was made from (https://github.com/owner/repo), normalised, or null when
    /// the value isn't one. Updates then stay off rather than asking some other server.
    /// </summary>
    public static string? Repository(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        if (!GithubRepo().IsMatch(u)) return null;
        var repo = u[(u.LastIndexOf('/') + 1)..];
        return repo is "." or ".." ? null : u;
    }

    /// <summary>An automatic check is due if there hasn't been one yet, about a day after one that worked, or a couple
    /// of hours after one that failed.</summary>
    public static bool CheckDue(DateTimeOffset? lastAttempt, bool lastFailed, DateTimeOffset now) =>
        lastAttempt is not { } last || now - last >= (lastFailed ? RetryAfter : CheckEvery);

    /// <summary>The alert for a new version (raised once per version, and only by the automatic check).</summary>
    public static HubAlert Available(string version, long size, DateTimeOffset now) => new()
    {
        Id = "update:" + version,
        Kind = "update",
        Severity = AlertSeverity.Notice,
        Title = $"Aqua Hub {version} is available",
        Body = $"Download it from Settings › About ({Megabytes(size)}). It installs when you restart Aqua Hub.",
        Target = Target,
        Created = now,
    };

    /// <summary>The alert once a download has finished.</summary>
    public static HubAlert Ready(string version, DateTimeOffset now) => new()
    {
        Id = "update-ready:" + version,
        Kind = "update",
        Severity = AlertSeverity.Notice,
        Title = $"Aqua Hub {version} is ready to install",
        Body = "Restart Aqua Hub to finish (Settings › About), or it installs itself the next time Aqua Hub starts.",
        Target = Target,
        Created = now,
    };

    public static string Megabytes(long bytes) =>
        bytes <= 0 ? "size unknown" : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.#} MB");

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();
    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]*", RegexOptions.Multiline)]
    private static partial Regex Heading();
    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+", RegexOptions.Multiline)]
    private static partial Regex Bullet();
    [GeneratedRegex(@"\*\*|__|`|<[^>]{1,40}>")]
    private static partial Regex Markup();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    /// <summary>A release's notes (Markdown) as short plain text for the Updates card.</summary>
    public static string Notes(string? markdown, int max = 700)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var t = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        t = Link().Replace(t, "$1");
        t = Heading().Replace(t, "");
        t = Bullet().Replace(t, "• ");
        t = Markup().Replace(t, "");
        t = BlankLines().Replace(t, "\n\n").Trim();
        if (t.Length <= max) return t;
        var cut = t.LastIndexOf('\n', max);
        return (cut > max / 2 ? t[..cut] : t[..max]).TrimEnd() + " …";
    }
}
