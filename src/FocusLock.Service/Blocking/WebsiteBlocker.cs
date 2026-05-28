using System.Diagnostics;
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
                lines.AppendLine($"0.0.0.0 {site.Domain}");
                lines.AppendLine($"0.0.0.0 www.{site.Domain}");
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
