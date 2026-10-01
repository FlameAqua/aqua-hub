using AquaHub.Platform;
using Velopack;
using Velopack.Logging;

namespace AquaHub;

/// <summary>
/// Entry point. Velopack goes first: Setup and the updater start this exe with their own arguments when Aqua Hub is
/// installed, updated or uninstalled, and it handles those and exits. Source builds aren't installed, so for them this
/// does nothing. An update downloaded earlier (the "Later" path) is not applied here but by the first instance in
/// <see cref="App"/>: Velopack's own start-up apply would let a second launch (a jump-list task, a shortcut) close
/// the running Aqua Hub mid-session.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        try
        {
            VelopackApp.Build()
                .SetLogger(new UpdateLog())
                .SetAutoApplyOnStartup(false)
                // Uninstalling: remove the Start with Windows entries that start this copy. The profile (settings,
                // history) stays in %LOCALAPPDATA%\AquaHub.
                .OnBeforeUninstallFastCallback(_ => Autostart.RemoveEntriesFor(Environment.ProcessPath))
                .Run();
        }
        catch (Exception ex)
        {
            // Never let the updater's start-up step keep Aqua Hub from starting.
            Core.Util.Log.Warn("update", "Velopack start-up step failed", ex);
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>Velopack's messages go to Aqua's diagnostics log, under "update".</summary>
    private sealed class UpdateLog : IVelopackLogger
    {
        public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
        {
            var text = message ?? "";
            // Every copy that isn't installed (source builds, test runs) says so at start-up: routine, not a problem.
            if (text.StartsWith("Failed to initialize WindowsVelopackLocator", StringComparison.Ordinal)) logLevel = VelopackLogLevel.Debug;
            switch (logLevel)
            {
                case VelopackLogLevel.Critical or VelopackLogLevel.Error or VelopackLogLevel.Warning:
                    Core.Util.Log.Warn("update", text, exception);
                    break;
                case VelopackLogLevel.Information:
                    Core.Util.Log.Info("update", text);
                    break;
                default:
                    Core.Util.Log.Debug("update", text);
                    break;
            }
        }
    }
}
