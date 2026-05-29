using System.Runtime.InteropServices;

namespace FocusLock.Core.Services;

/// <summary>
/// Minimal Service Control Manager helpers (LocalSystem only).
/// </summary>
public static class WindowsServiceScm
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;

    private const uint ServiceControlStop = 0x00000001;
    private const int ServiceRunning = 0x00000004;
    private const int ServiceStartPending = 0x00000002;
    private const int ServiceStopPending = 0x00000003;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr hService, int dwNumServiceArgs, string[]? lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    public static bool IsRunning(string serviceName)
    {
        var state = TryQueryState(serviceName);
        return state is ServiceRunning or ServiceStartPending;
    }

    public static bool TryStart(string serviceName)
    {
        if (IsRunning(serviceName))
            return true;

        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return false;

        try
        {
            var svc = OpenService(scm, serviceName, ServiceStart);
            if (svc == IntPtr.Zero)
                return false;

            try
            {
                return StartService(svc, 0, null);
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

    public static bool TryStop(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return false;

        try
        {
            var svc = OpenService(scm, serviceName, ServiceStop | ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return false;

            try
            {
                if (!QueryServiceStatus(svc, out var status))
                    return false;

                if (status.CurrentState is ServiceStopPending)
                    return true;

                if (status.CurrentState != ServiceRunning && status.CurrentState != ServiceStartPending)
                    return true;

                return ControlService(svc, ServiceControlStop, out _);
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

    private static int? TryQueryState(string serviceName)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return null;

        try
        {
            var svc = OpenService(scm, serviceName, ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return null;

            try
            {
                return QueryServiceStatus(svc, out var status) ? status.CurrentState : null;
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
}
