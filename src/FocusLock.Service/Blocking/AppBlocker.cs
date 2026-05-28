using System.Diagnostics;
using FocusLock.Core.Blocking;
using FocusLock.Core.Models;
using Microsoft.Win32;

namespace FocusLock.Service.Blocking;

public class AppBlocker(ILogger log)
{
    private const string IfeoBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string DebuggerValue = "Debugger";
    private static readonly string StubPath =
        Path.Combine(AppContext.BaseDirectory, "FocusLock.BlockerStub.exe");

    public static bool IsSystemExe(string exeName) =>
        SystemProcessList.IsSystemExe(exeName);

    public void Apply(FocusSession session)
    {
        foreach (var app in session.BlockedApps)
        {
            if (IsSystemExe(app.ExeName)) continue;
            WriteIfeo(app.ExeName, session);
        }
    }

    public void Remove(FocusSession session)
    {
        foreach (var app in session.BlockedApps)
            RestoreIfeo(app.ExeName, session);
    }

    public void KillRunningBlockedApps(FocusSession session)
    {
        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    var exeName = proc.ProcessName + ".exe";
                    if (session.BlockedApps.Any(a => string.Equals(a.ExeName, exeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        proc.Kill();
                        log.LogInformation("Killed blocked process: {Name} (PID {Pid})", proc.ProcessName, proc.Id);
                    }
                }
                catch { /* process may have already exited */ }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Error killing blocked processes.");
        }
    }

    public void VerifyAndRepairIfeo(FocusSession session)
    {
        foreach (var app in session.BlockedApps)
        {
            if (IsSystemExe(app.ExeName)) continue;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"{IfeoBase}\{app.ExeName}", writable: false);

                var current = key?.GetValue(DebuggerValue) as string;
                if (!string.Equals(current, StubPath, StringComparison.OrdinalIgnoreCase))
                    WriteIfeo(app.ExeName, session);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to verify IFEO for {Exe}.", app.ExeName);
            }
        }
    }

    private void WriteIfeo(string exeName, FocusSession session)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey($@"{IfeoBase}\{exeName}", writable: true);
            // Save the pre-existing Debugger value so we can restore it on session end.
            if (!session.IfeoPreExistingDebuggers.ContainsKey(exeName))
            {
                var existing = key.GetValue(DebuggerValue) as string;
                session.IfeoPreExistingDebuggers[exeName] = existing;
            }
            key.SetValue(DebuggerValue, StubPath, RegistryValueKind.String);
            log.LogDebug("IFEO set for {Exe}.", exeName);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to write IFEO for {Exe}.", exeName);
        }
    }

    private void RestoreIfeo(string exeName, FocusSession session)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{IfeoBase}\{exeName}", writable: true);
            if (key is null) return;

            if (session.IfeoPreExistingDebuggers.TryGetValue(exeName, out var prior) && prior is not null)
                key.SetValue(DebuggerValue, prior, RegistryValueKind.String);
            else
                key.DeleteValue(DebuggerValue, throwOnMissingValue: false);

            log.LogDebug("IFEO restored for {Exe}.", exeName);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to restore IFEO for {Exe}.", exeName);
        }
    }
}
