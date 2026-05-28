using System.Runtime.InteropServices;

namespace FocusLock.Service.Blocking;

/// <summary>
/// Spawns FocusLock.BlockerStub.exe in the active user's interactive desktop session
/// to show a native popup dialog. The service runs as SYSTEM (session 0), so
/// CreateProcessAsUser is required to place the popup on the user's visible desktop.
/// </summary>
public sealed class SessionNotifier(ILogger log)
{
    private static readonly string StubPath =
        Path.Combine(AppContext.BaseDirectory, "FocusLock.BlockerStub.exe");

    private const uint  CREATE_NO_WINDOW     = 0x08000000;
    private const int   STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_SHOW              = 5;

    public void ShowMessage(string title, string body)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return;
        var escaped = $"--message \"{Esc(title)}\" \"{Esc(body)}\"";
        Spawn(sessionId, escaped, waitForExitMs: 2500);
    }

    public void ShowWebsiteBlocked(string domain, string deadline)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            log.LogDebug("No active console session; skipping website notification.");
            return;
        }
        Spawn(sessionId, $"--notify \"{Esc(domain)}\" \"{Esc(deadline)}\"", waitForExitMs: 2500);
    }

    private void Spawn(uint sessionId, string stubArgs, int waitForExitMs = 0)
    {
        if (!File.Exists(StubPath))
        {
            log.LogWarning("BlockerStub not found at {Path}; skipping popup.", StubPath);
            return;
        }
        try
        {
            if (!WTSQueryUserToken(sessionId, out var userToken))
            {
                log.LogDebug("WTSQueryUserToken failed ({Error}); skipping popup.",
                    Marshal.GetLastWin32Error());
                return;
            }
            try
            {
                var si = new STARTUPINFO
                {
                    cb          = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop   = "WinSta0\\Default",
                    dwFlags     = STARTF_USESHOWWINDOW,
                    wShowWindow = SW_SHOW
                };
                string cmdLine = $"\"{StubPath}\" {stubArgs}";
                if (!CreateProcessAsUser(userToken, null, cmdLine,
                        IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_NO_WINDOW, IntPtr.Zero, null,
                        ref si, out var pi))
                {
                    log.LogDebug("CreateProcessAsUser failed ({Error}).", Marshal.GetLastWin32Error());
                    return;
                }
                if (waitForExitMs > 0)
                    WaitForSingleObject(pi.hProcess, (uint)waitForExitMs);
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            finally { CloseHandle(userToken); }
        }
        catch (Exception ex) { log.LogDebug(ex, "Spawn popup failed."); }
    }

    private static string Esc(string s) => s.Replace("\"", "\\\"");

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpReserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitle;
        public int  dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int    dwProcessId, dwThreadId;
    }
}
