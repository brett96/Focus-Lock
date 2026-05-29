using System.Runtime.InteropServices;

namespace FocusLock.Core.Services;

/// <summary>
/// Marks the current process as critical (BreakOnTermination). Terminating it causes a
/// system bugcheck, which blocks casual taskkill / Process Explorer kills including from SYSTEM.
/// A skilled attacker can still clear the flag via NtSetInformationProcess on an open handle.
/// </summary>
public static class ProcessProtection
{
    private const int ProcessBreakOnTermination = 29;

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref int processInformation,
        int processInformationLength);

    public static bool TrySetCritical(bool critical)
    {
        int value = critical ? 1 : 0;
        int status = NtSetInformationProcess(
            System.Diagnostics.Process.GetCurrentProcess().Handle,
            ProcessBreakOnTermination,
            ref value,
            sizeof(int));
        return status == 0;
    }
}
