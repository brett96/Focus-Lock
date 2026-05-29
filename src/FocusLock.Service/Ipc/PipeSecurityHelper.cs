using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FocusLock.Core.Ipc;

namespace FocusLock.Service.Ipc;

internal static class PipeSecurityHelper
{
    public static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // UI (asInvoker) and BlockerStub run in the interactive session — not WorldSid / broad AuthenticatedUser.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return security;
    }

    /// <summary>
    /// Only destructive recovery requires an elevated client; session ops run in the service as SYSTEM.
    /// </summary>
    public static bool RequiresAdministrator(string messageType) =>
        messageType is PipeConstants.ForceReset or PipeConstants.UnlockForSetup;

    public static bool TryGetClientIsAdministrator(NamedPipeServerStream pipe, out bool isAdmin)
    {
        isAdmin = false;
        if (!OperatingSystem.IsWindows())
            return false;

        if (!ImpersonateNamedPipeClient(pipe.SafePipeHandle.DangerousGetHandle()))
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return true;
        }
        finally
        {
            RevertToSelf();
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ImpersonateNamedPipeClient(IntPtr hNamedPipe);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();
}
