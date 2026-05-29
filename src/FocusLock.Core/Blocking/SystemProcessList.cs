namespace FocusLock.Core.Blocking;

/// <summary>
/// Executables and paths that must never be blocked — required for Windows to operate.
/// </summary>
public static class SystemProcessList
{
    private static readonly HashSet<string> CriticalExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Session / kernel bootstrap
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "lsass.exe", "services.exe",
        "svchost.exe", "ntoskrnl.exe",

        // Shell & desktop
        "explorer.exe", "sihost.exe", "dwm.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "RuntimeBroker.exe",
        "ApplicationFrameHost.exe", "SystemSettings.exe", "SystemSettingsBroker.exe",
        "TextInputHost.exe", "ctfmon.exe",

        // Console / input
        "conhost.exe", "fontdrvhost.exe", "dllhost.exe",

        // Security & health
        "MsMpEng.exe", "MpDefenderCoreService.exe", "SecurityHealthService.exe",
        "SecurityHealthSystray.exe", "SgrmBroker.exe", "smartscreen.exe",

        // Audio / print / core services
        "audiodg.exe", "spoolsv.exe", "taskhostw.exe",

        // Management (Task Manager must stay reachable)
        "taskmgr.exe", "WmiPrvSE.exe",

        // Focus Lock
        "FocusLock.Service.exe", "FocusLock.UI.exe", "FocusLock.BlockerStub.exe",
        "FocusLock.Watchdog.exe",
    };

    private static readonly string[] ProtectedDirectoryPrefixes = BuildProtectedDirectoryPrefixes();

    public static bool IsSystemExe(string exeName) =>
        CriticalExeNames.Contains(NormalizeExeName(exeName));

    /// <summary>
    /// True when the executable lives under Windows system directories or is otherwise critical.
    /// </summary>
    public static bool IsProtectedFromBlocking(string exeName, string? fullPath = null)
    {
        if (IsSystemExe(exeName))
            return true;

        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        try
        {
            fullPath = Path.GetFullPath(fullPath.Trim().Trim('"'));
        }
        catch
        {
            return false;
        }

        foreach (var prefix in ProtectedDirectoryPrefixes)
        {
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string NormalizeExeName(string exeName)
    {
        exeName = Path.GetFileName(exeName).Trim();
        return exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exeName
            : exeName + ".exe";
    }

    private static string[] BuildProtectedDirectoryPrefixes()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            return [];

        windows = Path.GetFullPath(windows);
        var sep = Path.DirectorySeparatorChar;
        return
        [
            windows + sep,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System)) + sep,
        ];
    }
}
