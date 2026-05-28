using System.Runtime.InteropServices;

namespace FocusLock.Service.Blocking;

/// <summary>
/// Spawns FocusLock.BlockerStub.exe in the active user's interactive desktop session
/// to show a native popup dialog. Called by BlockPageServer when a blocked website is visited.
/// The service runs as SYSTEM (session 0), so CreateProcessAsUser is required to place
/// the popup on the user's visible desktop.
/// </summary>
public sealed class SessionNotifier(ILogger log)
{
    private static readonly string StubPath =
        Path.Combine(AppContext.BaseDirectory, "FocusLock.BlockerStub.exe");

    private const uint CREATE_NO_WINDOW    = 0x08000000;
    private const int  STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_SHOW             = 5;

    public void ShowWebsiteBlocked(string domain, string deadline)
    {
        if (!File.Exists(StubPath))
        {
            log.LogWarning("BlockerStub not found at {Path}; skipping website popup.", StubPath);
            return;
        }

        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                log.LogDebug("No active console session; skipping website popup.");
                return;
            }

            if (!WTSQueryUserToken(sessionId, out var userToken))
            {
                log.LogDebug("WTSQueryUserToken failed ({Error}); skipping website popup.",
                    Marshal.GetLastWin32Error());
                return;
            }

            try
            {
                var si = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop = "WinSta0\\Default",
                    dwFlags   = STARTF_USESHOWWINDOW,
                    wShowWindow = SW_SHOW
                };

                // domain has no spaces; deadline may (e.g. "Fri, Dec 5 at 5:00 PM").
                string cmdLine = $"\"{StubPath}\" --notify \"{domain}\" \"{deadline}\"";

                if (!CreateProcessAsUser(userToken, null, cmdLine,
                        IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_NO_WINDOW, IntPtr.Zero, null,
                        ref si, out var pi))
                {
                    log.LogDebug("CreateProcessAsUser failed ({Error}).",
                        Marshal.GetLastWin32Error());
                    return;
                }

                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            finally
            {
                CloseHandle(userToken);
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Failed to show website blocked notification.");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string  lpCommandLine,
        IntPtr  lpProcessAttributes,
        IntPtr  lpThreadAttributes,
        bool    bInheritHandles,
        uint    dwCreationFlags,
        IntPtr  lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO         lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int    cb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpReserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitle;
        public int    dwX, dwY, dwXSize, dwYSize;
        public int    dwXCountChars, dwYCountChars, dwFillAttribute;
        public int    dwFlags;
        public short  wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int    dwProcessId, dwThreadId;
    }
}
