using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.Core.Services;

namespace FocusLock.Service.Blocking;

/// <summary>
/// Session protection for all modes (regular and strict):
///   1. Service DACLs deny stop/pause/change-config to Administrators and LocalSystem.
///   2. Both processes are marked critical (taskkill causes bugcheck).
///   3. Mutual watchdog services restart each other if killed.
///   4. Poll loops re-apply DACLs if tampered with.
/// </summary>
public class SessionProtectionManager(ILogger log)
{
    public AckResponse Activate(FocusSession session)
    {
        try
        {
            SessionProtectionHelper.ApplyServiceProtection();

            if (!ProcessProtection.TrySetCritical(true))
                log.LogWarning("Could not mark main service process as critical.");

            if (!WindowsServiceScm.TryStart(FocusLockServiceNames.WatchdogService))
                log.LogWarning("Watchdog service could not be started; DACL protection still applied.");

            log.LogInformation(
                "Session protection activated (DACL hardening, critical process, watchdog).");
            return new AckResponse(true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to activate session protection.");
            return new AckResponse(false, $"Session protection setup failed: {ex.Message}");
        }
    }

    public void Deactivate(FocusSession session)
    {
        try
        {
            SessionProtectionHelper.RestoreServiceProtection();
            ProcessProtection.TrySetCritical(false);
            WindowsServiceScm.TryStop(FocusLockServiceNames.WatchdogService);
            log.LogInformation("Session protection deactivated.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error deactivating session protection.");
        }
    }

}
