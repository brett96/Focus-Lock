using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FocusLock.Core.Models;

namespace FocusLock.Service.Blocking;

public class WebsiteBlocker(ILogger log)
{
    private static readonly string HostsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"drivers\etc\hosts");

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FocusLock");

    private static readonly Mutex HostsMutex = new(false, @"Global\FocusLockHostsWrite");

    public void Apply(FocusSession session)
    {
        if (session.BlockedSites.Count == 0) return;

        HostsMutex.WaitOne();
        try
        {
            Directory.CreateDirectory(DataDir);
            // Backup only if not already done (recovery case).
            string backupPath = BackupPath(session.Id);
            if (!File.Exists(backupPath))
                File.Copy(HostsFile, backupPath, overwrite: false);

            var lines = new StringBuilder();
            lines.AppendLine($"# BEGIN FocusLock session {session.Id}");
            foreach (var site in session.BlockedSites)
            {
                lines.AppendLine($"127.0.0.1 {site.Domain}");
                lines.AppendLine($"127.0.0.1 www.{site.Domain}");
            }
            lines.AppendLine($"# END FocusLock session {session.Id}");

            // Avoid writing if sentinel block already present (recovery).
            string existing = File.ReadAllText(HostsFile);
            if (!existing.Contains($"# BEGIN FocusLock session {session.Id}"))
                File.AppendAllText(HostsFile, lines.ToString());

            FlushDns();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to apply hosts file blocking.");
        }
        finally
        {
            HostsMutex.ReleaseMutex();
        }
    }

    public void Remove(FocusSession session)
    {
        HostsMutex.WaitOne();
        try
        {
            string backupPath = BackupPath(session.Id);
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, HostsFile, overwrite: true);
                File.Delete(backupPath);
            }
            else
            {
                RemoveSentinelBlock(session.Id);
            }
            FlushDns();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to restore hosts file.");
        }
        finally
        {
            HostsMutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Adds a deny-write ACL for Administrators on the hosts file.
    /// SYSTEM retains full control so the service can still write to it.
    /// Call after Apply() when starting a Strict session.
    /// </summary>
    public void LockHostsAcl(FocusSession session)
    {
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            const FileSystemRights denyMask = FileSystemRights.Write | FileSystemRights.Delete
                                            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

            var fi = new FileInfo(HostsFile);
            var sec = fi.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(
                admins, denyMask,
                InheritanceFlags.None, PropagationFlags.None,
                AccessControlType.Deny));
            fi.SetAccessControl(sec);
            log.LogInformation("Hosts file write access locked for administrators.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to lock hosts file ACL.");
        }
    }

    /// <summary>
    /// Removes all deny-write ACL entries added by LockHostsAcl.
    /// Call before Remove() when ending a Strict session.
    /// </summary>
    public void UnlockHostsAcl(FocusSession session)
    {
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var fi = new FileInfo(HostsFile);
            var sec = fi.GetAccessControl();
            var toRemove = sec
                .GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(r => r.AccessControlType == AccessControlType.Deny
                         && r.IdentityReference.Equals(admins))
                .ToList();
            foreach (var rule in toRemove)
                sec.RemoveAccessRule(rule);
            fi.SetAccessControl(sec);
            log.LogInformation("Hosts file ACL restored.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to restore hosts file ACL.");
        }
    }

    private void RemoveSentinelBlock(Guid sessionId)
    {
        string begin = $"# BEGIN FocusLock session {sessionId}";
        string end = $"# END FocusLock session {sessionId}";

        var lines = File.ReadAllLines(HostsFile).ToList();
        int startIdx = lines.FindIndex(l => l.TrimEnd() == begin);
        int endIdx = lines.FindIndex(l => l.TrimEnd() == end);

        if (startIdx >= 0 && endIdx >= startIdx)
            lines.RemoveRange(startIdx, endIdx - startIdx + 1);

        File.WriteAllLines(HostsFile, lines);
    }

    private static string BackupPath(Guid sessionId) =>
        Path.Combine(DataDir, $"hosts.backup.{sessionId}.txt");

    private void FlushDns()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "DNS flush failed.");
        }
    }
}
