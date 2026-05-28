using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
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

    /// <summary>
    /// Adds a deny-write ACL for Administrators on each IFEO key written for this session.
    /// SYSTEM (LocalSystem) is unaffected so the service can still repair keys if needed.
    /// Call after Apply() when starting a Strict session.
    /// </summary>
    public void LockIfeoAcls(FocusSession session)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        const RegistryRights denyMask = RegistryRights.SetValue | RegistryRights.Delete
                                      | RegistryRights.ChangePermissions;
        foreach (var app in session.BlockedApps)
        {
            if (IsSystemExe(app.ExeName)) continue;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"{IfeoBase}\{app.ExeName}",
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
                if (key is null) continue;

                var sec = key.GetAccessControl();
                sec.AddAccessRule(new RegistryAccessRule(
                    admins, denyMask,
                    InheritanceFlags.None, PropagationFlags.None,
                    AccessControlType.Deny));
                key.SetAccessControl(sec);
                log.LogDebug("IFEO ACL locked for {Exe}.", app.ExeName);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to lock IFEO ACL for {Exe}.", app.ExeName);
            }
        }
    }

    /// <summary>
    /// Removes all deny-write ACL entries added by LockIfeoAcls.
    /// Call before Remove() when ending a Strict session.
    /// </summary>
    public void UnlockIfeoAcls(FocusSession session)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        foreach (var app in session.BlockedApps)
        {
            if (IsSystemExe(app.ExeName)) continue;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"{IfeoBase}\{app.ExeName}",
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
                if (key is null) continue;

                var sec = key.GetAccessControl();
                var toRemove = sec
                    .GetAccessRules(true, false, typeof(SecurityIdentifier))
                    .Cast<RegistryAccessRule>()
                    .Where(r => r.AccessControlType == AccessControlType.Deny
                             && r.IdentityReference.Equals(admins))
                    .ToList();
                foreach (var rule in toRemove)
                    sec.RemoveAccessRule(rule);
                key.SetAccessControl(sec);
                log.LogDebug("IFEO ACL unlocked for {Exe}.", app.ExeName);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to unlock IFEO ACL for {Exe}.", app.ExeName);
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
