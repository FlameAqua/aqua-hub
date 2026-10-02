using System.Diagnostics;
using AquaHub.Core.Util;
using AquaHub.Services;

namespace AquaHub.Platform;

/// <summary>
/// The "Doctor's toolkit": Windows' own maintenance tools, one click away. Each opens a Windows window or Settings page
/// and changes nothing by itself. <see cref="Admin"/> marks the ones Windows asks administrator permission for.
/// </summary>
public static class MaintenanceTools
{
    public sealed record Tool(string Id, string Group, string Name, string Icon, string What, string Command, string Arguments = "", bool Admin = false);

    public static readonly Tool[] All =
    {
        new("taskmgr", "Performance", "Task Manager", "bolt", "What's running and what it uses; end a stuck app.", "taskmgr.exe"),
        new("resmon", "Performance", "Resource Monitor", "pulse", "CPU, memory, disk and network use per process.", "resmon.exe"),
        new("startup", "Performance", "Startup apps", "power", "Turn off apps that start with Windows, for a quicker startup.", "ms-settings:startupapps"),
        new("services", "Performance", "Services", "settings", "Windows' background services and whether they run.", "services.msc"),
        new("storage", "Storage", "Storage settings", "disk", "What fills your drives, Storage Sense and cleanup recommendations.", "ms-settings:storagesense"),
        new("cleanup", "Storage", "Disk Cleanup", "trash", "Clear temporary files, update leftovers and the Recycle Bin.", "cleanmgr.exe"),
        new("defrag", "Storage", "Optimise drives", "layers", "Trim SSDs and defragment hard drives (Windows does this weekly).", "dfrgui.exe"),
        new("update", "Health & security", "Windows Update", "download", "Check for and install updates; see the update history.", "ms-settings:windowsupdate"),
        new("security", "Health & security", "Windows Security", "shield", "Antivirus, firewall and device security; run a scan.", "windowsdefender:"),
        new("reliability", "Health & security", "Reliability Monitor", "warning", "A timeline of crashes, failed updates and installs.", "perfmon.exe", "/rel"),
        new("events", "Health & security", "Event Viewer", "book", "Windows' detailed logs, for chasing an error message.", "eventvwr.msc"),
        new("devices", "Hardware & system", "Device Manager", "cube", "Devices, their drivers, and any with a problem.", "devmgmt.msc"),
        new("sysinfo", "Hardware & system", "System Information", "info", "Everything about the hardware and Windows version.", "msinfo32.exe"),
        new("troubleshoot", "Hardware & system", "Troubleshooters", "wrench", "Windows' guided fixes for audio, network, printers, updates and more.", "ms-settings:troubleshoot"),
        new("power", "Hardware & system", "Power & battery", "bolt", "Power mode, sleep and battery settings.", "ms-settings:powersleep"),
        new("network", "Hardware & system", "Network status", "network", "Connection status, data usage and network reset.", "ms-settings:network-status"),
    };

    public static Tool? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Opens a tool (the fixed command above; never anything passed in).</summary>
    public static bool Open(string id, string? arguments = null)
    {
        if (Find(id) is not { } tool) return false;
        var args = arguments ?? tool.Arguments;
        if (Sandbox.Intercept("maintenance-tool", tool.Name + (args.Length > 0 ? " " + args : ""))) return true;
        try
        {
            Process.Start(new ProcessStartInfo(tool.Command, args) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Cancelling Windows' administrator prompt lands here too.
            Log.Info("system", $"{tool.Name} didn't open: {ex.Message}");
            return false;
        }
    }

    /// <summary>A drive in File Explorer.</summary>
    public static void OpenDrive(string drive)
    {
        var root = drive.TrimEnd('\\', ':') + ":\\";
        if (!Directory.Exists(root) || Sandbox.Intercept("open-folder", root)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }

    /// <summary>Disk Cleanup for one drive.</summary>
    public static bool CleanDrive(string drive) => Open("cleanup", "/d " + drive.TrimEnd('\\', ':')[..1]);
}
