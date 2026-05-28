using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;

namespace FocusLock.Service.Blocking;

/// <summary>
/// Strict mode no longer demotes admin accounts. Instead it:
///   1. Denies SERVICE_STOP and SERVICE_PAUSE_CONTINUE to Administrators on the service DACL.
///   2. Relies on AppBlocker and WebsiteBlocker to lock IFEO keys and the hosts file.
///
/// This prevents any admin from stopping the service via SCM, while SYSTEM (LocalSystem)
/// retains the ability to stop it on system shutdown. If the service crashes it auto-restarts
/// (configured in the installer), and session.json ensures enforcement is re-applied on startup.
/// The user keeps their admin rights for other purposes, eliminating the lockout risk.
/// </summary>
public class StrictModeManager(ILogger log)
{
    private const string ServiceName = "FocusLockService";

    // Service-specific access rights (WinNT.h)
    private const uint ServiceStop          = 0x0020;
    private const uint ServicePauseContinue = 0x0040;

    // Generic access rights
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac    = 0x00040000;

    // SCM access
    private const uint ScManagerConnect = 0x0001;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceObjectSecurity(IntPtr serviceHandle, SecurityInfos secInfo,
        byte[]? lpSecDescrBuf, uint bufSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceObjectSecurity(IntPtr serviceHandle, SecurityInfos secInfo,
        byte[] lpSecDescrBuf);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    public AckResponse Activate(FocusSession session)
    {
        try
        {
            DenyServiceStop();
            log.LogInformation("Strict mode activated: administrators cannot stop the service.");
            return new AckResponse(true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to activate strict mode.");
            return new AckResponse(false, $"Strict mode setup failed: {ex.Message}");
        }
    }

    public void Deactivate(FocusSession session)
    {
        try
        {
            RestoreServiceStop();
            log.LogInformation("Strict mode deactivated: service stop access restored.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error deactivating strict mode.");
        }
    }

    private void DenyServiceStop()
    {
        ModifyServiceDacl(addDeny: true);
    }

    private void RestoreServiceStop()
    {
        ModifyServiceDacl(addDeny: false);
    }

    private void ModifyServiceDacl(bool addDeny)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new InvalidOperationException($"OpenSCManager failed: {Marshal.GetLastWin32Error()}");
        try
        {
            var svc = OpenService(scm, ServiceName, ReadControl | WriteDac);
            if (svc == IntPtr.Zero)
                throw new InvalidOperationException($"OpenService failed: {Marshal.GetLastWin32Error()}");
            try
            {
                // First call: get required buffer size.
                QueryServiceObjectSecurity(svc, SecurityInfos.DiscretionaryAcl, null, 0, out uint needed);
                var sdBytes = new byte[needed];
                if (!QueryServiceObjectSecurity(svc, SecurityInfos.DiscretionaryAcl, sdBytes, needed, out _))
                    throw new InvalidOperationException($"QueryServiceObjectSecurity failed: {Marshal.GetLastWin32Error()}");

                var sd = new RawSecurityDescriptor(sdBytes, 0);
                var dacl = sd.DiscretionaryAcl ?? new RawAcl(RawAcl.AclRevision, 1);
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                int stopPauseMask = (int)(ServiceStop | ServicePauseContinue);

                if (addDeny)
                {
                    // Insert DENY ACE at position 0 — deny ACEs must precede allow ACEs.
                    dacl.InsertAce(0, new CommonAce(
                        AceFlags.None, AceQualifier.AccessDenied,
                        stopPauseMask, admins, false, null));
                }
                else
                {
                    // Remove all deny ACEs for Administrators that we may have added.
                    var toRemove = Enumerable.Range(0, dacl.Count)
                        .Select(i => dacl[i])
                        .OfType<CommonAce>()
                        .Where(a => a.AceQualifier == AceQualifier.AccessDenied
                                 && a.SecurityIdentifier == admins
                                 && (a.AccessMask & stopPauseMask) != 0)
                        .ToList();

                    // Must remove by index in reverse to avoid shifting.
                    for (int i = dacl.Count - 1; i >= 0; i--)
                    {
                        if (dacl[i] is CommonAce ace && toRemove.Contains(ace))
                            dacl.RemoveAce(i);
                    }
                }

                sd.DiscretionaryAcl = dacl;
                var newSd = new byte[sd.BinaryLength];
                sd.GetBinaryForm(newSd, 0);

                if (!SetServiceObjectSecurity(svc, SecurityInfos.DiscretionaryAcl, newSd))
                    throw new InvalidOperationException($"SetServiceObjectSecurity failed: {Marshal.GetLastWin32Error()}");
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
