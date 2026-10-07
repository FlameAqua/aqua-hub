using System.Diagnostics;
using System.Text.Json;

namespace AquaHub.Services;

/// <summary>Finds and starts a user-installed ComfyUI launcher without supplying commands or arguments.</summary>
public static class ComfyServerLauncher
{
    private static readonly string[] PortableLaunchers =
    [
        "run_nvidia_gpu.bat"
    ];

    /// <summary>
    /// Checks a short list of conventional ComfyUI portable install locations. This does not search the user's
    /// directories recursively; users can select any supported launcher in the UI if it is elsewhere.
    /// </summary>
    public static string? FindInstalledLauncher()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return null;

        var roots = new[]
        {
            Path.Combine(profile, "ComfyUI_windows_portable"),
            Path.Combine(profile, "Downloads", "ComfyUI_windows_portable"),
            Path.Combine(profile, "Documents", "ComfyUI_windows_portable"),
            Path.Combine(profile, "Desktop", "ComfyUI_windows_portable"),
            Path.Combine(Path.GetPathRoot(profile) ?? "C:\\", "ComfyUI_windows_portable")
        };

        foreach (var root in roots)
        foreach (var launcher in PortableLaunchers)
        {
            var candidate = Path.Combine(root, launcher);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var installedLaunchers = new[]
            {
                // Comfy Desktop (2026's desktop app: it starts the last ComfyUI installation it ran, on port 8188 by default).
                Path.Combine(localAppData, "Programs", "Comfy Desktop", "Comfy Desktop.exe"),
                Path.Combine(localAppData, "Programs", "ComfyUI", "ComfyUI.exe"),
                Path.Combine(localAppData, "Programs", "@comfyorgcomfyui-electron", "ComfyUI.exe"),
                Path.Combine(localAppData, "ComfyUI", "ComfyUI.exe")
            };
            foreach (var candidate in installedLaunchers)
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    /// <summary>
    /// Returns the existing output folder for a recognized launcher, if present: ComfyUI/output beside a portable
    /// launcher, or the folder Comfy Desktop's own settings name (%APPDATA%\Comfy Desktop\settings.json, "outputDir").
    /// </summary>
    public static string? DetectOutputFolder(string launcherPath)
    {
        if (TryGetValidatedPath(launcherPath, out var desktop) && Path.GetFileName(desktop).Equals("Comfy Desktop.exe", StringComparison.OrdinalIgnoreCase))
            return DesktopOutputFolder();
        if (!TryGetValidatedPath(launcherPath, out var fullPath) || !IsPortableLauncher(fullPath)) return null;

        var root = Path.GetDirectoryName(fullPath);
        if (root is null) return null;
        var output = Path.GetFullPath(Path.Combine(root, "ComfyUI", "output"));
        return Directory.Exists(output) ? output : null;
    }

    /// <summary>
    /// Starts the selected executable or batch launcher with no command-line arguments and its own directory as
    /// the working directory. Batch files go through the Windows shell's registered file handler, with the path
    /// passed as the file name instead of interpolated into a command string.
    /// </summary>
    public static bool Launch(string path)
    {
        if (!TryGetValidatedPath(path, out var fullPath)) return false;
        if (Sandbox.Intercept("comfy-start", fullPath)) return true;

        var extension = Path.GetExtension(fullPath);
        var isBatch = extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = isBatch,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = !isBatch
        };

        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or ArgumentException or System.Security.SecurityException)
        {
            AquaHub.Core.Util.Log.Warn("comfy", "Couldn't start ComfyUI", ex);
            return false;
        }
    }

    /// <summary>Comfy Desktop's output folder: its settings' "outputDir" when that's a local folder that exists, else its default.</summary>
    private static string? DesktopOutputFolder()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            var settings = Path.Combine(roaming, "Comfy Desktop", "settings.json");
            if (File.Exists(settings) && new FileInfo(settings).Length < 1 << 20)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("outputDir", out var dir)
                    && dir.ValueKind == JsonValueKind.String && dir.GetString() is { Length: > 3 } output
                    && Path.IsPathFullyQualified(output) && !output.StartsWith(@"\", StringComparison.Ordinal) && Directory.Exists(output))
                    return Path.GetFullPath(output);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AquaHub.Core.Util.Log.Debug("comfy", "Comfy Desktop settings: " + ex.Message);
        }
        var fallback = Path.Combine(local, "Comfy-Desktop", "ComfyUI-Shared", "output");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static bool IsPortableLauncher(string fullPath) =>
        PortableLaunchers.Contains(Path.GetFileName(fullPath), StringComparer.OrdinalIgnoreCase);

    private static bool TryGetValidatedPath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;

        try
        {
            var candidate = Path.GetFullPath(path);
            var extension = Path.GetExtension(candidate);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) return false;
            if (!File.Exists(candidate)) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
