using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AquaHub.E2E.Infrastructure;

/// <summary>Polling waits that tolerate the transient failures typical of cross-process UI Automation.</summary>
public static class Wait
{
    public static bool IsTransient(Exception ex) =>
        ex is ElementNotAvailableException or ElementNotEnabledException or COMException or InvalidOperationException
            or TimeoutException or IOException or UnauthorizedAccessException or ArgumentException;

    /// <summary>Polls <paramref name="condition"/> until it is true; returns false on timeout.</summary>
    public static bool Until(Func<bool> condition, TimeSpan? timeout = null, int pollMs = 150)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? E2EConfig.UiTimeout;
        while (true)
        {
            try
            {
                if (condition()) return true;
            }
            catch (Exception ex) when (IsTransient(ex)) { }
            if (sw.Elapsed > limit) return false;
            Thread.Sleep(pollMs);
        }
    }

    /// <summary>Polls until the condition holds or throws a descriptive <see cref="TimeoutException"/>.</summary>
    public static void For(Func<bool> condition, string what, TimeSpan? timeout = null, int pollMs = 150)
    {
        if (!Until(condition, timeout, pollMs))
            throw new TimeoutException($"Timed out after {(timeout ?? E2EConfig.UiTimeout).TotalSeconds:0.#}s waiting for: {what}");
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
