using System.Text.RegularExpressions;

namespace AquaHub.Core.Settings;

/// <summary>Finds a setting by what it's called or what it does, for the search box at the top of Settings.</summary>
public static class SettingsSearch
{
    public sealed record Entry(string Section, string Title, string Description = "", string Keywords = "");

    /// <summary>
    /// The best matches first (ties keep the page order). Every word typed has to start a word somewhere in the entry;
    /// a match in the title counts most, then the extra search words, the section and the description.
    /// </summary>
    public static IReadOnlyList<T> Find<T>(string query, IEnumerable<T> items, Func<T, Entry> describe, int max = 8)
    {
        var words = Words(query);
        if (words.Length == 0) return Array.Empty<T>();
        return items
            .Select((item, i) => (item, i, score: Score(words, query.Trim(), describe(item))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenBy(x => x.i)
            .Take(max)
            .Select(x => x.item)
            .ToList();
    }

    public static int Score(string query, Entry entry) => Score(Words(query), query.Trim(), entry);

    private static int Score(string[] words, string query, Entry entry)
    {
        if (words.Length == 0) return 0;
        string[] title = Words(entry.Title), keywords = Words(entry.Keywords), section = Words(entry.Section), description = Words(entry.Description);
        var total = 0;
        foreach (var word in words)
        {
            var best = Hit(word, title) ? 4 : Hit(word, keywords) ? 3 : Hit(word, section) ? 2 : Hit(word, description) ? 1 : 0;
            if (best == 0) return 0;
            total += best;
        }
        if (entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) total += 4;
        return total;
    }

    /// <summary>"noti" finds "notifications", and "updates" still finds "update".</summary>
    private static bool Hit(string word, string[] text) =>
        text.Any(t => t.StartsWith(word, StringComparison.Ordinal) || (t.Length >= 4 && word.StartsWith(t, StringComparison.Ordinal) && word.Length - t.Length <= 2));

    private static string[] Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToArray();
}
