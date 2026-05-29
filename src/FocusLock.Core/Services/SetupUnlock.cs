using FocusLock.Core.Storage;

namespace FocusLock.Core.Services;

/// <summary>
/// Restores SCM access so setup can stop services. Refused while an active Strict mode session is running
/// (including MSI uninstall/upgrade). Allowed during Regular mode or when no session is active.
/// </summary>
public static class SetupUnlock
{
    public static (bool Success, string Message) RunWithDiagnostics()
    {
        if (ActiveSessionHelper.IsActiveStrictSession())
        {
            return (false,
                "Cannot unlock or uninstall while a Strict mode session is active. Wait for the deadline to pass.");
        }

        var (ipcOk, ipcMessage) = SetupUnlockClient.TryUnlockRunningService();
        if (ipcOk)
            return (true, ipcMessage);

        return RunOfflineUnlock(ipcMessage);
    }

    public static bool Run() => RunWithDiagnostics().Success;

    /// <summary>
    /// MSI / setup path: release SCM control and exit 0 unless Strict is active.
    /// Does not stop services (Windows Installer stops them after this returns).
    /// </summary>
    public static (bool Success, string Message) RunForInstaller()
    {
        if (ActiveSessionHelper.IsActiveStrictSession())
        {
            return (false,
                "Cannot unlock or uninstall while a Strict mode session is active. Wait for the deadline to pass.");
        }

        var (ipcOk, ipcMessage) = SetupUnlockClient.TryUnlockRunningService();
        if (ipcOk)
            return (true, ipcMessage);

        ReleaseServiceControlForSetup();
        TryClearCritical(FocusLockServiceNames.WatchdogService, out _);
        TryClearCritical(FocusLockServiceNames.MainService, out _);
        return (true, $"Service control released for setup (IPC: {ipcMessage}).");
    }

    private static void ReleaseServiceControlForSetup()
    {
        try
        {
            SessionRepository.Save(null);
            SetupUnlockClient.TryEndSessionViaIpc();
        }
        catch
        {
            // Best-effort before DACL reset.
        }

        for (int i = 0; i < 8; i++)
        {
            ServiceDaclReset.TryResetToDefault(FocusLockServiceNames.MainService, out _);
            ServiceDaclReset.TryResetToDefault(FocusLockServiceNames.WatchdogService, out _);
            SessionProtectionHelper.RestoreServiceProtection();
            Thread.Sleep(150);
        }
    }

    private static (bool Success, string Message) RunOfflineUnlock(string ipcMessage)
    {
        if (ActiveSessionHelper.IsActiveStrictSession())
        {
            return (false,
                "Strict mode session is active. Uninstall and unlock are blocked until the deadline.");
        }

        try
        {
            Win32Privilege.TryEnableDebugPrivilege();
            SessionRepository.Save(null);
            SetupUnlockClient.TryEndSessionViaIpc();
            ForceReleaseServiceControl();

            if (!TryClearCritical(FocusLockServiceNames.WatchdogService, out string? watchdogCriticalError))
                return (false, $"{watchdogCriticalError} ({ipcMessage})");

            if (!TryStopServiceWithRetries(FocusLockServiceNames.WatchdogService, out string? stopWatchdogError))
                return (false, $"{stopWatchdogError} ({ipcMessage})");

            TryClearCritical(FocusLockServiceNames.MainService, out _);
            return (true, $"Offline unlock completed (IPC: {ipcMessage}).");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.Message} (IPC: {ipcMessage})");
        }
    }

    private static void ForceReleaseServiceControl()
    {
        for (int i = 0; i < 8; i++)
        {
            ServiceDaclReset.TryResetToDefault(FocusLockServiceNames.MainService, out _);
            ServiceDaclReset.TryResetToDefault(FocusLockServiceNames.WatchdogService, out _);
            SessionProtectionHelper.RestoreServiceProtection();
            Thread.Sleep(150);
        }
    }

    private static bool TryStopServiceWithRetries(string serviceName, out string? error)
    {
        error = null;
        if (!WindowsServiceScm.IsRunning(serviceName))
            return true;

        for (int i = 0; i < 8; i++)
        {
            ServiceDaclReset.TryResetToDefault(serviceName, out _);
            SessionProtectionHelper.RestoreServiceProtection();

            if (WindowsServiceScm.TryStop(serviceName))
                return true;

            Thread.Sleep(150);
        }

        error = $"Could not stop {serviceName}.";
        return false;
    }

    private static bool TryClearCritical(string serviceName, out string? error)
    {
        error = null;
        var pid = WindowsServiceScm.TryGetProcessId(serviceName);
        if (pid is not > 0)
            return true;

        if (ProcessProtection.TrySetCriticalForProcess(pid.Value, critical: false))
            return true;

        error =
            $"Could not clear legacy critical flag on {serviceName} (PID {pid}). " +
            "Reboot after the Strict session deadline, then retry uninstall.";
        return false;
    }
}
