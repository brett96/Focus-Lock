namespace FocusLock.Core.Blocking;

public static class SystemProcessList
{
    private static readonly HashSet<string> Exclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "lsass.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "svchost.exe", "smss.exe", "conhost.exe", "dwm.exe", "taskmgr.exe",
        "explorer.exe", "ntoskrnl.exe", "FocusLock.Service.exe", "FocusLock.UI.exe",
        "FocusLock.BlockerStub.exe"
    };

    public static bool IsSystemExe(string exeName) => Exclusions.Contains(exeName);
}
