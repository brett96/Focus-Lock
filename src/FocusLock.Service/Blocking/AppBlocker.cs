using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using FocusLock.Core.Blocking;
using FocusLock.Core.Models;
using Microsoft.Win32;

namespace FocusLock.Service.Blocking;

public class AppBlocker(ILogger log)
{
    private const string DebuggerValue = "Debugger";
    private static readonly string StubPath =
        Path.Combine(AppContext.BaseDirectory, "FocusLock.BlockerStub.exe");

  /// <summary>IFEO keys written for screen-time blocks (separate from session block list).</summary>
  private readonly HashSet<string> _screenTimeIfeoKeys = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, string?> _screenTimeIfeoPrior = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsSystemExe(string exeName) =>
        SystemProcessList.IsProtectedFromBlocking(exeName);

    public void Apply(FocusSession session)
    {
        foreach (var exeName in EnumerateBlockableExeNames(session))
            WriteIfeo(exeName, session);
    }

    public void Remove(FocusSession session)
    {
        foreach (var exeName in EnumerateBlockableExeNames(session))
            RestoreIfeo(exeName, session);
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
                    string? fullPath = null;
                    try { fullPath = proc.MainModule?.FileName; } catch { }

                    var exeName = proc.ProcessName + ".exe";
                    if (IsSessionBlockedExe(session, exeName, fullPath))
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
        foreach (var exeName in EnumerateBlockableExeNames(session))
        {
            try
            {
                if (!IsIfeoStubSet(exeName))
                    WriteIfeo(exeName, session);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to verify IFEO for {Exe}.", exeName);
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
        foreach (var exeName in EnumerateBlockableExeNames(session))
        {
            foreach (var ifeoBase in IfeoRegistryPaths.All)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"{ifeoBase}\{exeName}",
                        RegistryKeyPermissionCheck.ReadWriteSubTree,
                        RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
                    if (key is null) continue;

                    var sec = key.GetAccessControl();
                    sec.AddAccessRule(new RegistryAccessRule(
                        admins, denyMask,
                        InheritanceFlags.None, PropagationFlags.None,
                        AccessControlType.Deny));
                    key.SetAccessControl(sec);
                    log.LogDebug("IFEO ACL locked for {Exe} ({Base}).", exeName, ifeoBase);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Failed to lock IFEO ACL for {Exe} ({Base}).", exeName, ifeoBase);
                }
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
        foreach (var exeName in EnumerateBlockableExeNames(session))
        {
            foreach (var ifeoBase in IfeoRegistryPaths.All)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"{ifeoBase}\{exeName}",
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
                    log.LogDebug("IFEO ACL unlocked for {Exe} ({Base}).", exeName, ifeoBase);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Failed to unlock IFEO ACL for {Exe} ({Base}).", exeName, ifeoBase);
                }
            }
        }
    }

    /// <summary>
    /// Scans every IFEO key and removes any Debugger value that points to our stub.
    /// Used by the emergency reset to clean up stale entries left by a prior session.
    /// </summary>
    /// <summary>Redirects launches to the blocker stub when an app has hit a screen-time limit.</summary>
    public void ApplyScreenTimeBlock(string exeName, FocusSession? session)
    {
        if (SystemProcessList.IsProtectedFromBlocking(exeName)) return;
        if (IsSessionBlockedExe(session, exeName)) return;
        if (!_screenTimeIfeoKeys.Add(exeName)) return;
        WriteIfeoStandalone(exeName);
    }

    public void RemoveScreenTimeBlock(string exeName, FocusSession? session)
    {
        if (!_screenTimeIfeoKeys.Remove(exeName)) return;
        if (IsSessionBlockedExe(session, exeName)) return;
        RestoreIfeoStandalone(exeName);
    }

    public void ClearAllScreenTimeBlocks(FocusSession? session)
    {
        foreach (var exe in _screenTimeIfeoKeys.ToList())
            RemoveScreenTimeBlock(exe, session);
    }

    /// <summary>
    /// Ensures IFEO matches current screen-time block state (apply missing, remove stale).
    /// </summary>
    public void SyncScreenTimeBlocks(IReadOnlySet<string> blockedExes, FocusSession? session)
    {
        foreach (var exe in _screenTimeIfeoKeys.ToList())
        {
            if (!blockedExes.Contains(exe))
                RemoveScreenTimeBlock(exe, session);
        }

        foreach (var exe in blockedExes)
            ApplyScreenTimeBlock(exe, session);
    }

    public void RemoveAllStubIfeoKeys()
    {
        foreach (var ifeoBase in IfeoRegistryPaths.All)
        {
            try
            {
                using var ifeoRoot = Registry.LocalMachine.OpenSubKey(ifeoBase, writable: true);
                if (ifeoRoot is null) continue;
                foreach (var name in ifeoRoot.GetSubKeyNames())
                {
                    try
                    {
                        using var key = ifeoRoot.OpenSubKey(name, writable: true);
                        if (key is null) continue;
                        var debugger = key.GetValue(DebuggerValue) as string;
                        if (string.Equals(debugger, StubPath, StringComparison.OrdinalIgnoreCase))
                        {
                            key.DeleteValue(DebuggerValue, throwOnMissingValue: false);
                            log.LogInformation("Cleared stale IFEO entry for {Exe} ({Base}).", name, ifeoBase);
                        }
                    }
                    catch (Exception ex) { log.LogWarning(ex, "Could not clear IFEO for {Exe}.", name); }
                }
            }
            catch (Exception ex) { log.LogError(ex, "Failed to enumerate IFEO keys under {Base}.", ifeoBase); }
        }
    }

    private static IEnumerable<string> EnumerateBlockableExeNames(FocusSession session)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in session.BlockedApps)
        {
            foreach (var exeName in app.GetExeNamesToBlock())
            {
                if (SystemProcessList.IsProtectedFromBlocking(exeName))
                    continue;
                if (seen.Add(exeName))
                    yield return SystemProcessList.NormalizeExeName(exeName);
            }
        }
    }

    private static bool IsSessionBlockedExe(FocusSession? session, string exeName, string? fullPath = null)
        => session?.BlockedApps.Any(a => BlockedAppMatcher.IsBlocked(exeName, a, fullPath)) == true;

    private static bool IsIfeoStubSet(string exeName)
    {
        foreach (var ifeoBase in IfeoRegistryPaths.All)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ifeoBase}\{exeName}", writable: false);
            var current = key?.GetValue(DebuggerValue) as string;
            if (string.Equals(current, StubPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void WriteIfeo(string exeName, FocusSession session)
    {
        var paths = IfeoRegistryPaths.All;
        for (int i = 0; i < paths.Count; i++)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey($@"{paths[i]}\{exeName}", writable: true);
                if (i == 0 && !session.IfeoPreExistingDebuggers.ContainsKey(exeName))
                {
                    var existing = key.GetValue(DebuggerValue) as string;
                    if (string.Equals(existing, StubPath, StringComparison.OrdinalIgnoreCase))
                        existing = null;
                    session.IfeoPreExistingDebuggers[exeName] = existing;
                }

                key.SetValue(DebuggerValue, StubPath, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to write IFEO for {Exe} ({Base}).", exeName, paths[i]);
            }
        }

        log.LogDebug("IFEO set for {Exe} ({Count} registry views).", exeName, paths.Count);
    }

    private void RestoreIfeo(string exeName, FocusSession session)
    {
        foreach (var ifeoBase in IfeoRegistryPaths.All)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{ifeoBase}\{exeName}", writable: true);
                if (key is null) continue;

                if (session.IfeoPreExistingDebuggers.TryGetValue(exeName, out var prior) && prior is not null)
                    key.SetValue(DebuggerValue, prior, RegistryValueKind.String);
                else
                    key.DeleteValue(DebuggerValue, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to restore IFEO for {Exe} ({Base}).", exeName, ifeoBase);
            }
        }

        log.LogDebug("IFEO restored for {Exe}.", exeName);
    }

    private void WriteIfeoStandalone(string exeName)
    {
        var paths = IfeoRegistryPaths.All;
        for (int i = 0; i < paths.Count; i++)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey($@"{paths[i]}\{exeName}", writable: true);
                if (i == 0 && !_screenTimeIfeoPrior.ContainsKey(exeName))
                {
                    var existing = key.GetValue(DebuggerValue) as string;
                    if (string.Equals(existing, StubPath, StringComparison.OrdinalIgnoreCase))
                        existing = null;
                    _screenTimeIfeoPrior[exeName] = existing;
                }

                key.SetValue(DebuggerValue, StubPath, RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to write screen-time IFEO for {Exe} ({Base}).", exeName, paths[i]);
                _screenTimeIfeoKeys.Remove(exeName);
                return;
            }
        }

        log.LogDebug("Screen-time IFEO set for {Exe}.", exeName);
    }

    private void RestoreIfeoStandalone(string exeName)
    {
        foreach (var ifeoBase in IfeoRegistryPaths.All)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{ifeoBase}\{exeName}", writable: true);
                if (key is null)
                    continue;

                if (_screenTimeIfeoPrior.TryGetValue(exeName, out var prior) && prior is not null)
                    key.SetValue(DebuggerValue, prior, RegistryValueKind.String);
                else
                    key.DeleteValue(DebuggerValue, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to restore screen-time IFEO for {Exe} ({Base}).", exeName, ifeoBase);
            }
        }

        _screenTimeIfeoPrior.Remove(exeName);
        log.LogDebug("Screen-time IFEO restored for {Exe}.", exeName);
    }
}
