using System.Collections.Concurrent;
using System.Text;

namespace AquaHub.Core.Util;

public enum LogLevel { Debug, Info, Warn, Error }

/// <param name="Detail">For warnings and errors: the thread and the exception in full (type, message, stack, inner exceptions).</param>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Area, string Message, string? Detail = null);

/// <summary>
/// Tiny dependency-free rolling file logger. Writes are queued and flushed on a background task so callers never block
/// on disk I/O. Two files, each capped while the app runs (not only at start-up — Aqua can run for weeks from the tray):
/// <c>aquahub.log</c> (everything at the chosen level; 2 MB, then it becomes <c>aquahub.old.log</c>) and
/// <c>aquahub-problems.log</c> (warnings and errors only, with full exception details for debugging; 1 MB, then
/// <c>aquahub-problems.old.log</c>). Keeps the last 200 entries and the last 100 problems in memory for the Debug view.
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const long MaxProblemBytes = 1024 * 1024;
    private static readonly BlockingCollection<LogEntry> Queue = new(new ConcurrentQueue<LogEntry>(), 4096);
    private static readonly ConcurrentQueue<LogEntry> Recent = new();
    private static readonly ConcurrentQueue<LogEntry> Problems = new();
    private static string? _path, _problemsPath;
    private static long _size, _problemsSize;
    private static int _problemCount;
    private static Task? _pump;

    /// <summary>The main log's level (warnings and errors are always recorded).</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static event Action<LogEntry>? Written;

    public static IReadOnlyList<LogEntry> RecentEntries => Recent.ToArray();

    /// <summary>The last 100 warnings and errors (newest last), with their details.</summary>
    public static IReadOnlyList<LogEntry> RecentProblems => Problems.ToArray();

    /// <summary>Warnings and errors since the app started.</summary>
    public static int ProblemCount => Volatile.Read(ref _problemCount);

    public static string? FilePath => _path;
    public static string? ProblemsPath => _problemsPath;

    public static void Init(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "aquahub.log");
        _problemsPath = Path.Combine(directory, "aquahub-problems.log");
        _size = Roll(_path, MaxBytes, 0);
        _problemsSize = Roll(_problemsPath, MaxProblemBytes, 0);
        _pump ??= Task.Factory.StartNew(Pump, TaskCreationOptions.LongRunning);
    }

    public static void Debug(string area, string message) => Write(LogLevel.Debug, area, message, null);
    public static void Info(string area, string message) => Write(LogLevel.Info, area, message, null);
    public static void Warn(string area, string message, Exception? ex = null) => Write(LogLevel.Warn, area, Format(message, ex), ex);
    public static void Error(string area, string message, Exception? ex = null) => Write(LogLevel.Error, area, Format(message, ex), ex);

    private static string Format(string message, Exception? ex) =>
        ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}";

    /// <summary>What the problems log keeps about a warning or error: the thread, and the exception in full.</summary>
    internal static string Details(Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append("thread ").Append(Environment.CurrentManagedThreadId);
        if (Thread.CurrentThread.Name is { Length: > 0 } name) sb.Append(" (").Append(name).Append(')');
        if (ex is null) return sb.ToString();
        sb.Append('\n').Append(ex.ToString());
        if (ex.HResult != 0) sb.Append("\nHRESULT 0x").Append(ex.HResult.ToString("X8"));
        foreach (System.Collections.DictionaryEntry d in ex.Data) sb.Append('\n').Append(d.Key).Append(" = ").Append(d.Value);
        return sb.ToString();
    }

    private static void Write(LogLevel level, string area, string message, Exception? ex)
    {
        var problem = level >= LogLevel.Warn;
        if (level < MinLevel && !problem) return;
        var entry = new LogEntry(DateTimeOffset.Now, level, area, message, problem ? Details(ex) : null);
        Recent.Enqueue(entry);
        while (Recent.Count > 200 && Recent.TryDequeue(out _)) { }
        if (problem)
        {
            Problems.Enqueue(entry);
            while (Problems.Count > 100 && Problems.TryDequeue(out _)) { }
            Interlocked.Increment(ref _problemCount);
        }
        Queue.TryAdd(entry);
        try { Written?.Invoke(entry); } catch { }
    }

    /// <summary>A file over its cap becomes ".old" (replacing the previous one); returns the current file's size.</summary>
    private static long Roll(string path, long max, long size)
    {
        try
        {
            var info = new FileInfo(path);
            var current = info.Exists ? Math.Max(size, info.Length) : 0;
            if (current <= max) return current;
            File.Move(path, Path.ChangeExtension(path, null) + ".old.log", overwrite: true);
            return 0;
        }
        catch { return size; /* logging must never crash the app */ }
    }

    private static void Line(StringBuilder sb, LogEntry e) =>
        sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(e.Level.ToString().ToUpperInvariant().PadRight(5))
          .Append(" [").Append(e.Area).Append("] ").Append(e.Message).Append(Environment.NewLine);

    private static readonly object FileGate = new();

    /// <summary>
    /// A crash that ends the app: what's still queued, then this, written straight to both files — the background writer
    /// may never get another turn, and this is the entry a bug report needs most.
    /// </summary>
    public static void Fatal(string area, string message, Exception? ex)
    {
        var entry = new LogEntry(DateTimeOffset.Now, LogLevel.Error, area, Format(message, ex), Details(ex));
        Problems.Enqueue(entry);
        Interlocked.Increment(ref _problemCount);
        var batch = new List<LogEntry>();
        while (Queue.TryTake(out var pending)) batch.Add(pending);
        batch.Add(entry);
        WriteAll(batch);
    }

    private static void Pump()
    {
        foreach (var entry in Queue.GetConsumingEnumerable())
        {
            var batch = new List<LogEntry> { entry };
            // Drain any additional queued lines into the same write.
            while (batch.Count < 200 && Queue.TryTake(out var more)) batch.Add(more);
            WriteAll(batch);
        }
    }

    private static void WriteAll(List<LogEntry> batch)
    {
        if (_path is null) return;
        var main = new StringBuilder(160 * batch.Count);
        var problems = new StringBuilder();
        foreach (var e in batch)
        {
            Line(main, e);
            if (e.Level < LogLevel.Warn) continue;
            Line(problems, e);
            foreach (var detail in (e.Detail ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                problems.Append("    ").Append(detail.TrimEnd('\r')).Append(Environment.NewLine);
        }
        lock (FileGate)
        {
            try
            {
                File.AppendAllText(_path, main.ToString());
                _size = Roll(_path, MaxBytes, _size + main.Length);
                if (problems.Length > 0 && _problemsPath is not null)
                {
                    File.AppendAllText(_problemsPath, problems.ToString());
                    _problemsSize = Roll(_problemsPath, MaxProblemBytes, _problemsSize + problems.Length);
                }
            }
            catch { /* logging must never crash the app */ }
        }
    }
}
