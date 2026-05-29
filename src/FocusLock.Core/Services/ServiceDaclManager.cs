using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FocusLock.Core.Services;

/// <summary>
/// Denies BUILTIN\Administrators and NT AUTHORITY\SYSTEM the ability to stop, pause, or
/// reconfigure a Windows service via SCM. LocalSystem still owns the object and can restore
/// DACLs during session teardown; mutual watchdog + critical-process flags cover direct kills.
/// </summary>
public static class ServiceDaclManager
{
    private const uint ServiceStop = 0x0020;
    private const uint ServicePauseContinue = 0x0040;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint ScManagerConnect = 0x0001;

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    private static readonly SecurityIdentifier[] ProtectedPrincipals =
        [Administrators, LocalSystem];

    private static int DenyAccessMask =>
        (int)(ServiceStop | ServicePauseContinue | ServiceChangeConfig);

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

    public static void DenyStopAccess(string serviceName)
        => ModifyServiceDacl(serviceName, addDeny: true);

    public static void RestoreStopAccess(string serviceName)
        => ModifyServiceDacl(serviceName, addDeny: false);

    public static void EnsureStopDenied(string serviceName)
    {
        if (!HasStopDenials(serviceName))
            DenyStopAccess(serviceName);
    }

    public static bool HasStopDenials(string serviceName)
    {
        var dacl = TryReadDacl(serviceName);
        if (dacl is null)
            return false;

        foreach (var principal in ProtectedPrincipals)
        {
            if (!HasDenyAce(dacl, principal))
                return false;
        }

        return true;
    }

    private static bool HasDenyAce(RawAcl dacl, SecurityIdentifier principal)
    {
        for (int i = 0; i < dacl.Count; i++)
        {
            if (dacl[i] is not CommonAce ace)
                continue;

            if (ace.AceQualifier == AceQualifier.AccessDenied
                && ace.SecurityIdentifier == principal
                && (ace.AccessMask & DenyAccessMask) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static RawAcl? TryReadDacl(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return null;

        try
        {
            var svc = OpenService(scm, serviceName, ReadControl);
            if (svc == IntPtr.Zero)
                return null;

            try
            {
                QueryServiceObjectSecurity(svc, SecurityInfos.DiscretionaryAcl, null, 0, out uint needed);
                var sdBytes = new byte[needed];
                if (!QueryServiceObjectSecurity(svc, SecurityInfos.DiscretionaryAcl, sdBytes, needed, out _))
                    return null;

                var sd = new RawSecurityDescriptor(sdBytes, 0);
                return sd.DiscretionaryAcl;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void ModifyServiceDacl(string serviceName, bool addDeny)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            throw new InvalidOperationException($"OpenSCManager failed: {Marshal.GetLastWin32Error()}");

        try
        {
            var svc = OpenService(scm, serviceName, ReadControl | WriteDac);
            if (svc == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"OpenService({serviceName}) failed: {Marshal.GetLastWin32Error()}");

            try
            {
                QueryServiceObjectSecurity(svc, SecurityInfos.DiscretionaryAcl, null, 0, out uint needed);
                var sdBytes = new byte[needed];
                if (!QueryServiceObjectSecurity(svc, SecurityInfos.DiscretionaryAcl, sdBytes, needed, out _))
                    throw new InvalidOperationException(
                        $"QueryServiceObjectSecurity failed: {Marshal.GetLastWin32Error()}");

                var sd = new RawSecurityDescriptor(sdBytes, 0);
                var dacl = sd.DiscretionaryAcl ?? new RawAcl(RawAcl.AclRevision, 2);
                int mask = DenyAccessMask;

                RemoveDenyAces(dacl, mask);

                if (addDeny)
                {
                    foreach (var principal in ProtectedPrincipals)
                    {
                        dacl.InsertAce(0, new CommonAce(
                            AceFlags.None, AceQualifier.AccessDenied,
                            mask, principal, false, null));
                    }
                }

                sd.DiscretionaryAcl = dacl;
                var newSd = new byte[sd.BinaryLength];
                sd.GetBinaryForm(newSd, 0);

                if (!SetServiceObjectSecurity(svc, SecurityInfos.DiscretionaryAcl, newSd))
                    throw new InvalidOperationException(
                        $"SetServiceObjectSecurity failed: {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static bool IsProtectedPrincipal(SecurityIdentifier sid)
    {
        foreach (var principal in ProtectedPrincipals)
        {
            if (principal.Equals(sid))
                return true;
        }

        return false;
    }

    private static void RemoveDenyAces(RawAcl dacl, int mask)
    {
        for (int i = dacl.Count - 1; i >= 0; i--)
        {
            if (dacl[i] is not CommonAce ace)
                continue;

            if (ace.AceQualifier != AceQualifier.AccessDenied)
                continue;

            if (!IsProtectedPrincipal(ace.SecurityIdentifier))
                continue;

            if ((ace.AccessMask & mask) == 0)
                continue;

            dacl.RemoveAce(i);
        }
    }
}
