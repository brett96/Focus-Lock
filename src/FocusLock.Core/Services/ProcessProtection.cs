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

    private const uint ProcessSetInformation = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static bool TrySetCritical(bool critical)
        => TrySetCriticalForProcess(System.Diagnostics.Process.GetCurrentProcess().Id, critical);

    public static bool TrySetCriticalForProcess(int processId, bool critical)
    {
        if (processId <= 0)
            return false;

        IntPtr handle = OpenProcess(ProcessSetInformation, false, processId);
        if (handle == IntPtr.Zero)
            return false;

        try
        {
            int value = critical ? 1 : 0;
            return NtSetInformationProcess(handle, ProcessBreakOnTermination, ref value, sizeof(int)) == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
