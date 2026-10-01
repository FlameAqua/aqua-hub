using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AquaHub.E2E.Infrastructure;

/// <summary>Polling waits that tolerate the transient failures typical of cross-process UI Automation.</summary>
public static class Wait
{
    /// <summary>
    /// The hiccups of reading another process's UI (an element vanished mid-read, a COM call failed, a file was being
    /// written). Everything else is a real error and stops the wait at once: a nested wait's timeout, a LINQ
    /// "no elements", a test's own bug. (UI Automation's own <see cref="InvalidOperationException"/>s come from its
    /// namespaces; one thrown by test code doesn't.)
    /// </summary>
    public static bool IsTransient(Exception ex) =>
        ex is ElementNotAvailableException or ElementNotEnabledException or InputRefusedException or COMException or IOException
            or UnauthorizedAccessException
        || (ex is InvalidOperationException && FromUiAutomation(ex));

    private static bool FromUiAutomation(Exception ex) =>
        ex.TargetSite?.DeclaringType?.Namespace is { } ns &&
        (ns.StartsWith("System.Windows.Automation", StringComparison.Ordinal) || ns.StartsWith("MS.Internal.Automation", StringComparison.Ordinal));

    /// <summary>Polls <paramref name="condition"/> until it is true; returns false on timeout.</summary>
    public static bool Until(Func<bool> condition, TimeSpan? timeout = null, int pollMs = 150) => Until(condition, timeout, pollMs, out _);

    private static bool Until(Func<bool> condition, TimeSpan? timeout, int pollMs, out Exception? lastError)
    {
        lastError = null;
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? E2EConfig.UiTimeout;
        while (true)
        {
            try
            {
                if (condition()) return true;
            }
            catch (Exception ex) when (IsTransient(ex)) { lastError = ex; }
            if (sw.Elapsed > limit) return false;
            Thread.Sleep(pollMs);
        }
    }

    /// <summary>
    /// Polls until the condition holds or throws a descriptive <see cref="TimeoutException"/> (naming the last
    /// error it tolerated, if any, so a lookup that kept failing isn't mistaken for one that found nothing).
    /// </summary>
    public static void For(Func<bool> condition, string what, TimeSpan? timeout = null, int pollMs = 150)
    {
        if (!Until(condition, timeout, pollMs, out var last))
            throw new TimeoutException($"Timed out after {(timeout ?? E2EConfig.UiTimeout).TotalSeconds:0.#}s waiting for: {what}" +
                (last is null ? "" : $" (last error: {last.GetType().Name}: {FirstLine(last.Message)})"));
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    /// <summary>Polls a getter until it returns a non-null value.</summary>
    public static T For<T>(Func<T?> getter, string what, TimeSpan? timeout = null, int pollMs = 150) where T : class
    {
        T? result = null;
        For(() => (result = getter()) is not null, what, timeout, pollMs);
        return result!;
    }

    /// <summary>Retries an action that may hit a stale element (the tree is rebuilt on every navigation).</summary>
    public static void Retry(Action action, string what, int attempts = 4, int delayMs = 250)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && i < attempts)
            {
                Thread.Sleep(delayMs);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                throw new InvalidOperationException($"{what} failed after {attempts} attempts: {ex.Message}", ex);
            }
        }
    }

    public static void Ms(int ms) => Thread.Sleep(ms);
}
