using AquaHub.Core.Util;

namespace AquaHub.Core.Sources;

/// <summary>
/// How each feed has been doing, so one that keeps failing doesn't fill the log every run: a failure is logged when it
/// starts, when its error changes and then once a day. A feed that isn't at its address any more ("not found" or "gone",
/// <see cref="GoneAfter"/> runs in a row) is fetched only every <see cref="GoneRetry"/> until it works again, and its
/// collector raises one alert about it; Settings shows which feeds fail and since when. Kept in memory: after a restart
/// every feed gets a fresh try.
/// </summary>
public sealed class SourceHealth
{
    public const int GoneAfter = 3;
    public static readonly TimeSpan GoneRetry = TimeSpan.FromHours(6);
    public static readonly TimeSpan LogAgainAfter = TimeSpan.FromDays(1);

    /// <param name="Failures">Runs in a row that failed, whatever the error.</param>
    /// <param name="NotFound">Of those, the latest ones in a row that answered 404 or 410.</param>
    /// <param name="Since">When this run of failures began.</param>
    /// <param name="Logged">When the log last mentioned it.</param>
    public sealed record Problem(string Error, int HttpCode, int Failures, int NotFound, DateTimeOffset Since, DateTimeOffset LastTried, DateTimeOffset Logged)
    {
        /// <summary>Not at its address any more: fetched only every <see cref="GoneRetry"/>.</summary>
        public bool Gone => NotFound >= GoneAfter;

        /// <summary>For Settings: "Not found at this address (HTTP 404) · first failed 3h ago · Aqua tries it every 6 hours…".</summary>
        public string Describe(DateTimeOffset now) =>
            (HttpCode is 404 or 410 ? $"Not found at this address (HTTP {HttpCode})" : "Couldn't read it: " + Error)
            + " · first failed " + TimeText.AgoPhrase(Since, now)
            + (Gone ? $" · Aqua tries it every {GoneRetry.TotalHours:0} hours until it works again" : "");
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Problem> _failing = new(StringComparer.Ordinal);

    /// <summary>The feed's problem, by source id ("feed:feed-1a2b3c4d", "rte"); null while it works.</summary>
    public Problem? this[string sourceId]
    {
        get { lock (_gate) return _failing.GetValueOrDefault(sourceId); }
    }

    /// <summary>Whether to fetch the source this run: false while one that's gone waits for its next try.</summary>
    public bool Due(string sourceId, DateTimeOffset now)
    {
        lock (_gate) return !_failing.TryGetValue(sourceId, out var p) || !p.Gone || now - p.LastTried >= GoneRetry;
    }

    /// <summary>
    /// Records how a fetch went and logs it by the rules above. Returns the problem at the run that makes the source count
    /// as gone (once a run of failures), for its collector's alert; otherwise null.
    /// </summary>
    public Problem? Record(string area, FetchOutcome outcome, DateTimeOffset now)
    {
        var id = outcome.SourceId;
        string? warn = null, info = null;
        Problem? gone = null;
        lock (_gate)
        {
            _failing.TryGetValue(id, out var before);
            if (outcome.Ok)
            {
                if (before is null) return null;
                _failing.Remove(id);
                info = $"{id} works again (after {Plural.Of(before.Failures, "failed run")})";
            }
            else
            {
                var error = outcome.Error ?? "failed";
                var p = new Problem(error, outcome.HttpCode, (before?.Failures ?? 0) + 1,
                    outcome.HttpCode is 404 or 410 ? (before?.NotFound ?? 0) + 1 : 0, before?.Since ?? now, now, before?.Logged ?? now);
                if (before is null || before.Error != error) warn = $"{id}: {error}";
                else if (now - before.Logged >= LogAgainAfter) warn = $"{id}: {error} (still, after {Plural.Of(p.Failures, "run")})";
                if (p.Gone && before?.Gone != true)
                {
                    warn = $"{id}: {error} {GoneAfter} runs in a row; it's tried every {GoneRetry.TotalHours:0} hours until it works again";
                    gone = p;
                }
                if (warn is not null) p = p with { Logged = now };
                _failing[id] = p;
            }
        }
        if (warn is not null) Log.Warn(area, warn);
        if (info is not null) Log.Info(area, info);
        return gone;
    }
}
