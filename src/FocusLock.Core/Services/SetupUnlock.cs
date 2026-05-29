using FocusLock.Core.Storage;



namespace FocusLock.Core.Services;



/// <summary>

/// Restores SCM access and clears critical-process flags so MSI uninstall/upgrade can stop services.

/// Prefer IPC to the running service; offline fallback is used only when the service is not running.

/// </summary>

public static class SetupUnlock

{

    public static (bool Success, string Message) RunWithDiagnostics()

    {

        var (ipcOk, ipcMessage) = SetupUnlockClient.TryUnlockRunningService();

        if (ipcOk)

            return (true, ipcMessage);



        var offline = RunOfflineUnlock(ipcMessage);

        return offline;

    }



    public static bool Run() => RunWithDiagnostics().Success;



    private static (bool Success, string Message) RunOfflineUnlock(string ipcMessage)

    {

        try

        {

            Win32Privilege.TryEnableDebugPrivilege();

            SessionRepository.Save(null);



            var (_, endMsg) = SetupUnlockClient.TryEndSessionViaIpc();



            ForceReleaseServiceControl();



            if (!TryClearCritical(FocusLockServiceNames.WatchdogService, out string? watchdogCriticalError))

                return (false, $"{watchdogCriticalError} ({ipcMessage}; {endMsg})");



            if (!TryStopServiceWithRetries(FocusLockServiceNames.WatchdogService, out string? stopWatchdogError))

                return (false, $"{stopWatchdogError} ({ipcMessage}; {endMsg})");



            if (!TryClearCritical(FocusLockServiceNames.MainService, out string? mainCriticalError))

                return (false, $"{mainCriticalError} ({ipcMessage}; {endMsg})");



            return (true,

                $"Offline unlock completed (IPC: {ipcMessage}; EndSession: {endMsg}). " +

                "Run: sc.exe stop FocusLockService");

        }

        catch (Exception ex)

        {

            return (false, $"{ex.Message} (IPC: {ipcMessage})");

        }

    }



    /// <summary>

    /// The running service may re-apply deny ACEs every ~1.5s; reset DACL and stop in quick succession.

    /// </summary>

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



        error =

            $"Could not stop {serviceName}. Run as Administrator:\n" +

            $"  sc.exe sdset {serviceName} \"{ServiceDaclReset.DefaultServiceSddl}\"\n" +

            $"  sc.exe stop {serviceName}";

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

            $"Could not clear critical flag on {serviceName} (PID {pid}). " +

            "Reboot into Safe Mode before stopping that service.";

        return false;

    }

}


