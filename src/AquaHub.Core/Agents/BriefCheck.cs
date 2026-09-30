using AquaHub.Core.Analysis;
using AquaHub.Core.Models;
using AquaHub.Core.Settings;

namespace AquaHub.Core.Agents;

/// <summary>
/// Keeps the model's brief sections honest: each bullet is compared with what each kind of section was actually fed
/// (world stories, local stories and the pulse, market data, agenda and crowd forecasts, weather). A bullet that
/// clearly belongs to another kind moves to that section — or is dropped when the brief has no such section — so a
/// Le Pen forecast can't sit under "Social pulse".
/// </summary>
public static class BriefCheck
{
    private static readonly string[] WeatherWords =
    {
        "weather", "rain", "sunny", "sunshine", "cloud", "wind", "temperature", "degrees", "shower", "storm", "fog",
        "snow", "frost", "humid", "°",
    };

    public static (DailyBrief Brief, int Moved) Tidy(DailyBrief brief, HubState state, HubSettings s)
    {
        HashSet<string> Words(IEnumerable<string?> texts)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in texts) if (!string.IsNullOrWhiteSpace(t)) set.UnionWith(TextTools.Signature(t));
            return set;
        }

        var vocab = new Dictionary<string, HashSet<string>>
        {
            ["news"] = Words(state.Stories.Where(c => !c.IsLocal).Take(15).Select(c => c.Title + " " + c.Summary?.Tldr)),
            ["local"] = Words(state.Stories.Where(c => c.IsLocal).Take(10).Select(c => c.Title + " " + c.Summary?.Tldr)
                .Concat(state.Pulse?.Topics.Select(t => t.Title + " " + t.Summary) ?? Enumerable.Empty<string>())
                .Append(s.Location.City)),
            ["markets"] = Words(state.Quotes.Values.Select(q => q.Name + " " + q.Symbol)
                .Append(state.MarketBrief?.Overview).Append("market stocks shares index indices")),
            ["agenda"] = Words(state.Events.Take(40).Select(e => e.Title)
                .Concat(state.Predictions.Select(m => m.Title + " " + string.Join(' ', m.Outcomes.Take(3).Select(o => o.Label))))
                .Append("crowd forecast odds prediction")),
        };

        // What a reader takes the section to be: its title when that names a kind ("Social Pulse" is local chatter
        // whatever icon the model gave it), otherwise its icon.
        string KindOf(BriefSection section)
        {
            var t = section.Title.ToLowerInvariant();
            var city = s.Location.City.ToLowerInvariant();
            if (t.Contains("weather")) return "weather";
            if (t.Contains("market") || t.Contains("stock") || t.Contains("money")) return "markets";
            if (t.Contains("agenda") || t.Contains("ahead") || t.Contains("forecast") || t.Contains("prediction") || t.Contains("crowd") ||
                t.Contains("coming up") || t.Contains("calendar") || t.Contains("upcoming")) return "agenda";
            if (t.Contains("social") || t.Contains("pulse") || t.Contains("local") || t.Contains("near you") || (city.Length > 0 && t.Contains(city))) return "local";
            if (t.Contains("world") || t.Contains("news") || t.Contains("tech") || t.Contains("stories") || t.Contains("international")) return "news";
            return section.Icon switch
            {
                "local" => "local",
                "markets" => "markets",
                "agenda" or "predictions" => "agenda",
                "weather" => "weather",
                _ => "news",
            };
        }

        double Score(string bullet, string kind)
        {
            if (kind == "weather")
            {
                var lower = bullet.ToLowerInvariant();
                return WeatherWords.Count(w => lower.Contains(w, StringComparison.Ordinal)) * 2;
            }
            var words = TextTools.Signature(bullet);
            return words.Count(vocab[kind].Contains);
        }

        var sections = brief.Sections.Select(x => new BriefSection { Title = x.Title, Icon = x.Icon, Bullets = new List<string>() }).ToList();
        var moved = 0;
        for (var i = 0; i < brief.Sections.Count; i++)
        {
            var own = KindOf(brief.Sections[i]);
            foreach (var bullet in brief.Sections[i].Bullets)
            {
                var scores = new[] { "news", "local", "markets", "agenda", "weather" }.ToDictionary(k => k, k => Score(bullet, k));
                var (bestKind, best) = scores.MaxBy(kv => kv.Value);
                if (bestKind == own || best < 2 || best < 2 * scores[own])
                {
                    sections[i].Bullets.Add(bullet);
                    continue;
                }
                moved++;
                var target = sections.FirstOrDefault(x => KindOf(x) == bestKind);
                if (target is not null && target.Bullets.Count < 5) target.Bullets.Add(bullet);
            }
        }
        return (brief with { Sections = sections.Where(x => x.Bullets.Count > 0).ToList() }, moved);
    }
}
