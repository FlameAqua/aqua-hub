using System.Text.RegularExpressions;

namespace AquaHub.Core.Ai.Assistant;

/// <summary>
/// Which apps Ask may start or operate. Anything that runs typed commands is off limits — shells and terminals, the Run
/// dialog and File Explorer (its address bar runs commands), code editors with a built-in terminal, script hosts — as are
/// registry and admin tools, installers, sign-in and security prompts, password managers, remote sessions and Aqua
/// itself. Pure and unit-tested; the platform applies it to windows and to the apps it launches.
/// </summary>
public static partial class DesktopRules
{
    private static readonly Dictionary<string, string> Blocked = new(StringComparer.OrdinalIgnoreCase);

    static DesktopRules()
    {
        void Add(string why, params string[] processes) { foreach (var p in processes) Blocked[p] = why; }
        Add("terminals and shells run commands",
            "cmd", "powershell", "pwsh", "powershell_ise", "WindowsTerminal", "wt", "conhost", "OpenConsole", "bash", "wsl", "wslhost", "ubuntu",
            "debian", "kali", "mintty", "git-bash", "alacritty", "wezterm-gui", "Hyper", "Tabby", "kitty", "MobaXterm", "putty", "WinSCP", "Termius",
            "SecureCRT", "cscript", "wscript", "mshta", "python", "pythonw", "py", "node", "rundll32");
        Add("File Explorer's address bar and the Run dialog run commands (Aqua opens folders itself)", "explorer");
        Add("code editors have a built-in terminal",
            "Code", "Code - Insiders", "Cursor", "Windsurf", "devenv", "idea64", "pycharm64", "rider64", "webstorm64", "clion64", "goland64",
            "phpstorm64", "rubymine64", "datagrip64", "studio64", "sublime_text", "zed");
        Add("system tools change how Windows runs",
            "regedit", "regedt32", "mmc", "taskmgr", "msconfig", "perfmon", "resmon", "gpedit", "secpol", "certmgr", "UserAccountControlSettings",
            "msiexec", "ProcessHacker", "SystemInformer", "procexp", "procexp64", "WindowsSandbox", "vmconnect", "mstsc");
        Add("sign-in and security prompts are yours alone", "consent", "CredentialUIBroker", "LogonUI", "SecHealthUI", "SecurityHealthSystray");
        Add("password managers are yours alone",
            "KeePass", "KeePassXC", "1Password", "Bitwarden", "LastPass", "Dashlane", "NordPass", "Enpass", "RoboForm", "Keeper", "ProtonPass");
        Add("Aqua never operates its own windows", "AquaHub");
    }

    /// <summary>Why Aqua won't operate this app's windows, or null when it may (with the user's OK).</summary>
    public static string? WhyBlocked(string process)
    {
        var name = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
        return Blocked.TryGetValue(name, out var why) ? why : null;
    }

    public static bool IsBlockedProcess(string process) => WhyBlocked(process) is not null;

    /// <summary>Windows Ask never even looks at (screenshots included): password managers, sign-in and security prompts.</summary>
    public static bool IsPrivateWindow(string process) =>
        WhyBlocked(process) is "password managers are yours alone" or "sign-in and security prompts are yours alone";

    /// <summary>
    /// Why Ask may not operate this app under the user's choice (null when it may): off-limits apps never; with
    /// "listed", only the apps the user named (by process name, ".exe" optional).
    /// </summary>
    public static string? NotAllowed(string process, string mode, IReadOnlyCollection<string> allowed)
    {
        if (WhyBlocked(process) is { } why) return why;
        if (mode != "listed") return null;
        var name = process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process[..^4] : process;
        return allowed.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"you've limited Ask to {(allowed.Count == 0 ? "no apps yet" : string.Join(", ", allowed))} (Settings › Ask Aqua › Apps Ask may operate)";
    }

    [GeneratedRegex(@"\b(command prompt|cmd|powershell|pwsh|terminal|bash|wsl|ubuntu|debian|kali|opensuse|registry|regedit|task manager|task scheduler|services|computer management|group policy|security policy|system configuration|msconfig|windows tools|administrative tools|recovery|reset this pc|system restore|uninstall|installer|install|setup|python|node\.?js|anaconda|miniconda|jupyter|ipython|native tools|developer (?:command|powershell)|git (?:bash|cmd|gui)|putty|remote desktop|hyper-v|virtualbox|vmware|windows sandbox|windows security|defender|firewall|credential manager|bitlocker|disk management|diskpart|memory diagnostic|disk cleanup|resource monitor|performance monitor|odbc|iscsi|event viewer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RefusedName();

    [GeneratedRegex(@"(?:^|[\\/!])(?:cmd|powershell|powershell_ise|pwsh|wt|bash|wsl|wslg|conhost|regedit|regedt32|reg|mmc|msconfig|taskmgr|perfmon|resmon|rundll32|msiexec|cscript|wscript|mshta|python|pythonw|py|node|diskpart|recoverydrive|systemreset|rstrui|mdsched|cleanmgr|shutdown|sdclt|iscsicpl|odbcad32|UserAccountControlSettings|WindowsSandbox|vmconnect|mstsc)\.exe(?:[""\s]|$)|\.(?:bat|cmd|ps1|psm1|vbs|vbe|js|jse|wsf|wsh|msi|msp|msc|reg|hta|scr|cpl)$|Microsoft\.Windows\.Shell\.RunDialog|WindowsTerminal|Microsoft\.PowerShell|CanonicalGroupLimited|TheDebianProject|KaliLinux|SUSE|WindowsSubsystemForLinux", RegexOptions.IgnoreCase)]
    private static partial Regex RefusedTarget();

    /// <summary>
    /// Why Ask won't start this app (by its name and what it runs), or null when it may: shells, terminals, the Run
    /// dialog, script hosts, installers, registry and admin tools. The user can still start them from the Launchpad.
    /// </summary>
    public static string? LaunchRefusal(string name, string target)
    {
        if (name.Trim().Equals("Run", StringComparison.OrdinalIgnoreCase) || RefusedName().IsMatch(name) || RefusedTarget().IsMatch(target.Trim().Trim('"')))
            return $"Aqua doesn't start {name}: shells, terminals, the Run dialog, installers and system tools are off limits — start it yourself if you need it.";
        return null;
    }
}
