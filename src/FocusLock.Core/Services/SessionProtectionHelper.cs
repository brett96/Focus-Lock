namespace FocusLock.Core.Services;

/// <summary>
/// Shared service DACL hardening used by the main service and the watchdog.
/// </summary>
public static class SessionProtectionHelper
{
    public static void ApplyServiceProtection()
    {
        ServiceDaclManager.DenyStopAccess(FocusLockServiceNames.MainService);
        ServiceDaclManager.DenyStopAccess(FocusLockServiceNames.WatchdogService);
    }

    public static void RestoreServiceProtection()
    {
        ServiceDaclManager.RestoreStopAccess(FocusLockServiceNames.WatchdogService);
        ServiceDaclManager.RestoreStopAccess(FocusLockServiceNames.MainService);
    }

    public static void EnsureServiceProtection()
    {
        ServiceDaclManager.EnsureStopDenied(FocusLockServiceNames.MainService);
        ServiceDaclManager.EnsureStopDenied(FocusLockServiceNames.WatchdogService);
    }
}
