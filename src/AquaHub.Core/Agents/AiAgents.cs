using System.Text.Json;
using AquaHub.Core.Ai;
using AquaHub.Core.Models;
using AquaHub.Core.Util;

namespace AquaHub.Core.Agents;

internal static class AiJson
{
    public static List<string> Strings(JsonElement e, string name, int max = 8) =>
        e.Arr(name).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(max).ToList();
}

/// <summary>Writes AI summaries (TL;DR, key points, why it matters) for the top stories. Cached per story content.</summary>
public sealed class NewsEditorAgent : Agent
{
    public override string Id => "news-editor";
    public override string Name => "News Editor";
    public override string Role => "editor";
    public override bool UsesAi => true;
    public override string Description => "Writes neutral TL;DRs, key points and 'why it matters' for top stories — grounded only in the sources.";
    public override string[] After => new[] { "curator" };
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(8);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(20);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var s = ctx.S;
        var stories = ctx.State.Stories;
        if (stories.Count == 0) return AgentResult.Unchanged("No stories yet");
        var targets = stories.Take(s.News.SummarizeTop)
            .Concat(stories.Where(c => c.IsLocal).Take(4))
            .Distinct()
            .Where(c => c.Summary is not { IsAi: true } || ctx.Db.GetLlm("story:" + c.ContentKey) is null)
            .ToList();
        if (targets.Count == 0) return AgentResult.Unchanged("All top stories summarised");

        var block = ctx.Llm.BackgroundBlockReason();
        if (block is not null) return AgentResult.Wait($"{targets.Count} stories waiting · {block}", TimeSpan.FromMinutes(3));

        var done = 0;
        // Publish in small batches: the UI updates rows in place, and the snapshot isn't rewritten once per story.
        var batch = new Dictionary<string, StorySummary>();
        var lastPublish = DateTime.UtcNow;
        void Publish()
        {
            ctx.State.UpdateStorySummaries(batch);
            batch.Clear();
            lastPublish = DateTime.UtcNow;
        }
        foreach (var c in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (ctx.Llm.BackgroundBlockReason() is { } reason)
            {
                Publish();
                return AgentResult.Wait($"Summarised {done}/{targets.Count} · {reason}", TimeSpan.FromMinutes(3));
            }
            var key = "story:" + c.ContentKey;
            var cached = ctx.Db.GetLlm(key);
            StorySummary? summary = cached is null ? null : JsonUtil.Deserialize<StorySummary>(cached);
            if (summary is null)
            {
                try
                {
                    var (json, result) = await ctx.Llm.CompleteJsonAsync(Prompts.Story(c, s), ct).ConfigureAwait(false);
                    using (json)
                    {
                        var r = json.RootElement;
                        summary = new StorySummary
                        {
                            Headline = HtmlText.Truncate(r.Str("headline") ?? c.Title, 140),
                            // Models sometimes copy a feed's "Read more:" into the summary.
                            Tldr = HtmlText.Truncate(System.Text.RegularExpressions.Regex.Replace(r.Str("tldr") ?? "", @"^\W*(?:read more|read next|also read)\b\W*", "",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase), 260),
                            KeyPoints = AiJson.Strings(r, "key_points", 4).Select(p => HtmlText.Truncate(p, 180)).ToList(),
                            WhyItMatters = HtmlText.Truncate(r.Str("why_it_matters") ?? "", 220),
                            IsAi = true,
                            Model = result.Model,
                        };
                    }
                    ctx.Db.PutLlm(key, result.Model, JsonUtil.Serialize(summary));
                }
                catch (FormatException ex)
                {
                    Log.Warn("editor", $"Bad JSON for {c.Id}", ex);
                    continue;
                }
            }
            batch[c.Id] = summary;
            done++;
            if (batch.Count >= 4 || DateTime.UtcNow - lastPublish > TimeSpan.FromSeconds(20)) Publish();
        }
        Publish();
        return AgentResult.Success($"Summarised {Plural.Of(done, "story", "stories")}");
    }
}

/// <summary>Turns local social chatter into a calm "what people are talking about" digest.</summary>
public sealed class PulseAgent : Agent
{
    public override string Id => "pulse";
    public override string Name => "Social Pulse";
    public override string Role => "analyst";
    public override bool UsesAi => true;
    public override string Description => "Distils your social feeds into 3–6 topics with sentiment, so you never need to doom-scroll.";
    public override string[] After => new[] { "social-scout" };
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(12);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(45);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        // Your feeds and local communities only: global Bluesky trends are shown separately, labelled as global.
        var posts = ctx.State.Social
            .Where(p => p.Published >= cutoff && p.Platform != "hackernews" && p.SourceId != "bluesky:trending")
            .GroupBy(p => p.SourceId)
            .SelectMany(g => g.OrderByDescending(p => p.Score + p.Comments).Take(12))
            .OrderByDescending(p => p.Published)
            .Take(45)
            .ToList();
        if (posts.Count < 3) return AgentResult.Unchanged("Not enough recent posts");

        var key = "pulse:" + Hash.Short(string.Join(",", posts.Select(p => p.Id).OrderBy(x => x)));
        if (ctx.State.Pulse is { IsAi: true } existing && ctx.Db.GetLlm(key) is not null)
            return AgentResult.Unchanged("Pulse is current");

        var block = ctx.Llm.BackgroundBlockReason();
        if (block is not null)
        {
            if (ctx.State.Pulse is not { IsAi: true } p || AiShelfLife.Expired(p.GeneratedAt, TimeSpan.FromHours(6)))
                ctx.State.SetPulse(Fallbacks.Pulse(posts, new[] { ctx.S.Location.City, ctx.S.Location.Region }));
            return AgentResult.Wait($"Keyword digest only · {block}", TimeSpan.FromMinutes(5));
        }

        try
        {
            var (json, result) = await ctx.Llm.CompleteJsonAsync(Prompts.Pulse(posts, ctx.S), ct).ConfigureAwait(false);
            using (json)
            {
                var r = json.RootElement;
                var topics = new List<PulseTopic>();
                foreach (var t in r.Arr("topics").Take(6))
                {
                    var title = HtmlText.Truncate(t.Str("title") ?? "Topic", 60);
                    var summary = HtmlText.Truncate(t.Str("summary") ?? "", 280);
                    var claimed = t.Arr("post_numbers").Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n) ? n : 0)
                        .Where(n => n >= 1 && n <= posts.Count).Distinct().ToList();
                    var refs = FactCheck.RelatedPosts(title, summary, claimed, posts);
                    if (refs.Count < claimed.Count) Log.Debug("pulse", $"Dropped {claimed.Count - refs.Count} unrelated post(s) from “{title}”");
                    topics.Add(new PulseTopic
                    {
                        Title = title,
                        Summary = summary,
                        Sentiment = t.Str("sentiment") ?? "neutral",
                        Heat = (int)Math.Clamp(t.Lng("heat") ?? 2, 1, 5),
                        Platforms = refs.Select(n => posts[n - 1].Platform).Distinct().ToList(),
                        ItemIds = refs.Select(n => posts[n - 1].Id).ToList(),
                    });
                }
                var pulse = new SocialPulse
                {
                    Overview = HtmlText.Truncate(r.Str("overview") ?? "", 400),
                    Topics = topics.OrderByDescending(t => t.Heat).ToList(),
                    GeneratedAt = DateTimeOffset.Now,
                    IsAi = true,
                    Model = result.Model,
                    PostCount = posts.Count,
                };
                ctx.State.SetPulse(pulse);
                ctx.Db.PutLlm(key, result.Model, "1");
                return AgentResult.Success($"{pulse.Topics.Count} topics from {posts.Count} posts");
            }
        }
        catch (FormatException)
        {
            ctx.State.SetPulse(Fallbacks.Pulse(posts, new[] { ctx.S.Location.City, ctx.S.Location.Region }));
            return AgentResult.Success("Model output unusable — keyword digest shown");
        }
    }
}

/// <summary>Market analyst: overview, per-symbol stance with rationale and risks, and a few balanced ideas.</summary>
public sealed class MarketAnalystAgent : Agent
{
    public override string Id => "market-analyst";
    public override string Name => "Market Analyst";
    public override string Role => "analyst";
    public override bool UsesAi => true;
    public override string Description => "Combines technical indicators with market headlines into a market brief, watchlist stances and balanced ideas (educational, not advice).";
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(25);

    public override TimeSpan Interval(HubContext ctx)
    {
        var open = ctx.State.Quotes.Values.Any(q => q.MarketState == "open" && q.Kind is InstrumentKind.Index or InstrumentKind.Equity);
        return open ? TimeSpan.FromMinutes(60) : TimeSpan.FromHours(3);
    }

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var s = ctx.S;
        if (ctx.State.Quotes.Count == 0) return AgentResult.Wait("Waiting for quotes", TimeSpan.FromSeconds(30));
        var fallback = Fallbacks.Market(ctx.State, s.Markets);
        var block = ctx.Llm.BackgroundBlockReason();
        if (block is not null)
        {
            // Yesterday's AI view describes yesterday's session: fall back to today's indicators instead.
            if (ctx.State.MarketBrief is not { IsAi: true } mb || AiShelfLife.Expired(mb.GeneratedAt, TimeSpan.FromHours(6), sameDay: true))
                ctx.State.SetMarketBrief(fallback);
            return AgentResult.Wait($"Indicator-only view · {block}", TimeSpan.FromMinutes(5));
        }

        var quotes = s.Markets.Indices.Concat(s.Markets.Macro).Concat(s.Markets.Watchlist)
            .Select(w => ctx.State.Quotes.GetValueOrDefault(w.Symbol)).Where(q => q is not null).Cast<Quote>().ToList();
        var headlines = ctx.State.MarketHeadlines.Take(14).ToList();
        try
        {
            var (json, result) = await ctx.Llm.CompleteJsonAsync(Prompts.Market(quotes, ctx.State.Indicators, headlines, s), ct).ConfigureAwait(false);
            using (json)
            {
                var r = json.RootElement;
                var watch = new HashSet<string>(s.Markets.Watchlist.Select(w => w.Symbol), StringComparer.OrdinalIgnoreCase);
                var insights = r.Arr("insights").Select(i => new WatchInsight
                {
                    Symbol = (i.Str("symbol") ?? "").Trim().ToUpperInvariant(),
                    Stance = i.Str("stance") ?? "neutral",
                    Confidence = Math.Clamp(i.Dbl("confidence") ?? 0.5, 0, 1),
                    Summary = HtmlText.Truncate(i.Str("summary") ?? "", 240),
                    Rationale = AiJson.Strings(i, "rationale", 3),
                    Risks = AiJson.Strings(i, "risks", 2),
                }).Where(i => watch.Contains(i.Symbol)).GroupBy(i => i.Symbol).Select(g => g.First()).ToList();

                // Never drop a symbol: fill gaps with the indicator view.
                foreach (var f in fallback.Insights.Where(f => insights.All(i => i.Symbol != f.Symbol))) insights.Add(f);

                var ideas = r.Arr("ideas").Take(3).Select(i => new InvestmentIdea
                {
                    Title = HtmlText.Truncate(i.Str("title") ?? "", 80),
                    Thesis = HtmlText.Truncate(i.Str("thesis") ?? "", 360),
                    Symbols = AiJson.Strings(i, "symbols", 4).Select(x => x.ToUpperInvariant()).ToList(),
                    Risk = i.Str("risk") ?? "medium",
                    Horizon = i.Str("horizon") ?? "",
                    Kind = i.Str("kind") ?? "opportunity",
                }).Where(i => i.Title.Length > 0).ToList();

                var brief = new MarketBrief
                {
                    Overview = HtmlText.Truncate(r.Str("overview") ?? fallback.Overview, 500),
                    Highlights = AiJson.Strings(r, "highlights", 5),
                    Insights = insights,
                    Ideas = ideas,
                    GeneratedAt = DateTimeOffset.Now,
                    IsAi = true,
                    Model = result.Model,
                };
                ctx.State.SetMarketBrief(brief);
                return AgentResult.Success($"{insights.Count} watchlist insights, {ideas.Count} ideas");
            }
        }
        catch (FormatException)
        {
            ctx.State.SetMarketBrief(fallback);
            return AgentResult.Success("Model output unusable — indicator view shown");
        }
    }
}

/// <summary>Looks ahead: the week's key dates plus what prediction markets expect.</summary>
public sealed class ForesightAgent : Agent
{
    public override string Id => "foresight";
    public override string Name => "Foresight";
    public override string Role => "analyst";
    public override bool UsesAi => true;
    public override string Description => "Explains what's coming this week and what the crowd expects, using your agenda and prediction markets.";
    public override string[] After => new[] { "events-scout", "prediction-scout" };
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(30);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromHours(3);

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var events = ctx.State.Events.Where(e => e.Start > DateTimeOffset.Now.AddHours(-1) && e.Start < DateTimeOffset.Now.AddDays(7)).ToList();
        var markets = ctx.State.Predictions.ToList();
        if (events.Count == 0 && markets.Count == 0) return AgentResult.Unchanged("Nothing to analyse yet");

        var key = "foresight:" + Hash.Short(string.Join(",", events.Select(e => e.Id)), string.Join(",", markets.Take(14).Select(m =>
            m.Id + ":" + Math.Round((m.Lead?.Probability ?? 0) * 20))));
        if (ctx.State.Foresight is { IsAi: true } current && ctx.Db.GetLlm(key) is not null)
        {
            // Nothing new to write — but re-check what's on screen against today's odds (and heal anything an older
            // version of the checks let through), without asking the model again.
            var (rechecked, fixes) = FactCheck.Foresight(current, events, markets, Fallbacks.Foresight(events, markets).Overview);
            if (fixes == 0) return AgentResult.Unchanged("Outlook is current");
            ctx.State.SetForesight(rechecked);
            return AgentResult.Success($"Outlook re-checked · {Plural.Of(fixes, "figure")} corrected");
        }

        var block = ctx.Llm.BackgroundBlockReason();
        if (block is not null)
        {
            if (ctx.State.Foresight is not { IsAi: true } fs || AiShelfLife.Expired(fs.GeneratedAt, TimeSpan.FromHours(12)))
                ctx.State.SetForesight(Fallbacks.Foresight(events, markets));
            return AgentResult.Wait($"List view only · {block}", TimeSpan.FromMinutes(5));
        }
        try
        {
            var (json, result) = await ctx.Llm.CompleteJsonAsync(Prompts.Foresight(events, markets, ctx.S), ct).ConfigureAwait(false);
            using (json)
            {
                var r = json.RootElement;
                var f = new Foresight
                {
                    Overview = HtmlText.Truncate(r.Str("overview") ?? "", 400),
                    Items = r.Arr("items").Take(8).Select(i => new ForesightItem
                    {
                        Title = HtmlText.Truncate(i.Str("title") ?? "", 90),
                        When = HtmlText.Truncate(i.Str("when") ?? "", 40),
                        Detail = HtmlText.Truncate(i.Str("detail") ?? "", 240),
                        Kind = i.Str("kind") ?? "event",
                        Importance = (int)Math.Clamp(i.Lng("importance") ?? 1, 1, 3),
                    }).Where(i => i.Title.Length > 0).ToList(),
                    GeneratedAt = DateTimeOffset.Now,
                    IsAi = true,
                    Model = result.Model,
                };
                // Odds the model quotes must match the markets it was shown (a small model once swapped 34% and 65%).
                var (checkedForesight, fixes) = FactCheck.Foresight(f, events, markets, Fallbacks.Foresight(events, markets).Overview);
                if (fixes > 0) Log.Info("foresight", $"Corrected {fixes} unsupported figure(s) in the model's outlook");
                ctx.State.SetForesight(checkedForesight);
                ctx.Db.PutLlm(key, result.Model, "1");
                return AgentResult.Success($"{Plural.Of(checkedForesight.Items.Count, "thing")} to watch" + (fixes > 0 ? $" · {Plural.Of(fixes, "figure")} corrected" : ""));
            }
        }
        catch (FormatException)
        {
            ctx.State.SetForesight(Fallbacks.Foresight(events, markets));
            return AgentResult.Success("Model output unusable — list view shown");
        }
    }
}

/// <summary>Composes the morning/evening brief from every other agent's output.</summary>
public sealed class BriefingAgent : Agent
{
    public override string Id => "briefing";
    public override string Name => "Chief of Staff";
    public override string Role => "editor";
    public override bool UsesAi => true;
    public override string Description => "Composes your personal morning and evening brief from every other agent, at the times you choose.";
    public override TimeSpan InitialDelay => TimeSpan.FromSeconds(45);
    public override TimeSpan Interval(HubContext ctx) => TimeSpan.FromMinutes(5);

    private DateTimeOffset? LastScheduledSlot(HubContext ctx, DateTimeOffset now)
    {
        DateTimeOffset? latest = null;
        foreach (var t in ctx.S.Ai.BriefTimes)
        {
            if (!TimeOnly.TryParse(t, out var time)) continue;
            foreach (var day in new[] { now.Date.AddDays(-1), now.Date })
            {
                var slot = new DateTimeOffset(day + time.ToTimeSpan(), now.Offset);
                if (slot <= now && (latest is null || slot > latest)) latest = slot;
            }
        }
        return latest;
    }

    public override async Task<AgentResult> RunAsync(HubContext ctx, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var existing = ctx.State.Brief;
        var slot = LastScheduledSlot(ctx, now);
        var forced = ctx.State.Brief is null || (existing is not null && !existing.IsAi && ctx.Llm.BackgroundBlockReason() is null);
        var scheduledDue = slot is not null && (existing is null || existing.GeneratedAt < slot);
        var manual = ctx.State.Brief is not null && ctx.State.Brief.GeneratedAt < now.AddHours(-12);
        var requested = IsManualRequest(ctx);
        if (!forced && !scheduledDue && !manual && !requested)
        {
            // Nothing new to write, but keep what's shown honest: re-file bullets an older brief put in the wrong section.
            if (existing is { IsAi: true })
            {
                var (tidied, moved) = BriefCheck.Tidy(existing, ctx.State, ctx.S);
                if (moved > 0)
                {
                    ctx.State.SetBrief(tidied);
                    return AgentResult.Success($"Brief re-checked · {Plural.Of(moved, "bullet")} re-filed");
                }
            }
            return AgentResult.Unchanged("Brief is current");
        }
        if (ctx.State.Stories.Count == 0) return AgentResult.Wait("Waiting for stories", TimeSpan.FromSeconds(30));

        var block = ctx.Llm.BackgroundBlockReason();
        // A scheduled or requested brief may start a stopped local server; other background work never does,
        // so an idle stop really frees the memory.
        if (block == LlmClient.OfflineReason && (scheduledDue || requested) && ctx.Llm.StartServer is { } start && await start(ct).ConfigureAwait(false))
        {
            await ctx.Llm.CheckAsync(ct).ConfigureAwait(false);
            block = ctx.Llm.BackgroundBlockReason();
        }
        if (block is not null)
        {
            ctx.State.SetBrief(Fallbacks.Brief(ctx.State, ctx.S));
            return AgentResult.Wait($"Compact brief only · {block}", TimeSpan.FromMinutes(5));
        }

        var period = Digest.Period(now);
        var digest = Digest.ForBrief(ctx.State, ctx.S);
        try
        {
            var (json, result) = await ctx.Llm.CompleteJsonAsync(Prompts.Brief(digest, period, ctx.S), ct).ConfigureAwait(false);
            using (json)
            {
                var r = json.RootElement;
                var brief = new DailyBrief
                {
                    Title = HtmlText.Truncate(r.Str("title") ?? $"Your {period} brief", 80),
                    Summary = HtmlText.Truncate(r.Str("summary") ?? "", 500),
                    Sections = r.Arr("sections").Take(6).Select(sec => new BriefSection
                    {
                        Title = HtmlText.Truncate(sec.Str("title") ?? "", 40),
                        Icon = sec.Str("icon") ?? "news",
                        Bullets = AiJson.Strings(sec, "bullets", 4).Select(b => HtmlText.Truncate(b, 220)).ToList(),
                    }).Where(sec => sec.Bullets.Count > 0).ToList(),
                    GeneratedAt = now,
                    IsAi = true,
                    Model = result.Model,
                    Period = period,
                };
                var (tidied, moved) = BriefCheck.Tidy(brief, ctx.State, ctx.S);
                if (moved > 0) Log.Info("brief", $"Moved or dropped {Plural.Of(moved, "bullet")} filed under the wrong section");
                brief = tidied;
                ctx.State.SetBrief(brief);
                if (scheduledDue && ctx.S.Notifications.BriefReady)
                {
                    ctx.Platform.Notify(new HubAlert
                    {
                        Id = "brief:" + now.ToString("yyyyMMddHH"),
                        Kind = "brief",
                        Severity = AlertSeverity.Notice,
                        Title = brief.Title,
                        Body = HtmlText.Truncate(brief.Summary, 200),
                        Target = "brief",
                        Created = now,
                    });
                }
                return AgentResult.Success($"{period} brief with {brief.Sections.Count} sections");
            }
        }
        catch (FormatException)
        {
            ctx.State.SetBrief(Fallbacks.Brief(ctx.State, ctx.S));
            return AgentResult.Success("Model output unusable — compact brief shown");
        }
    }

    private static bool IsManualRequest(HubContext ctx) => ctx.Settings is not null && ManualFlag.Consume();

    /// <summary>Set by the UI when the user asks for a fresh brief.</summary>
    public static class ManualFlag
    {
        private static int _flag;
        public static void Request() => Interlocked.Exchange(ref _flag, 1);
        public static bool Consume() => Interlocked.Exchange(ref _flag, 0) == 1;
    }
}
