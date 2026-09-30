using AquaHub.Platform;
using Velopack;
using Velopack.Logging;

namespace AquaHub;

/// <summary>
/// Entry point. Velopack goes first: Setup and the updater start this exe with their own arguments when Aqua Hub is
/// installed, updated or uninstalled, and it handles those and exits. It also installs an update that was downloaded
/// earlier but not yet applied (the "Later" path) before the app starts. Source builds aren't installed, so for them
/// this does nothing.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .SetLogger(new UpdateLog())
            // Uninstalling: remove the Start with Windows entries that start this copy. The profile (settings, history)
            // stays in %LOCALAPPDATA%\AquaHub.
            .OnBeforeUninstallFastCallback(_ => Autostart.RemoveEntriesFor(Environment.ProcessPath))
            .Run();

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
