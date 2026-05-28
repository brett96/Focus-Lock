using FocusLock.Core.Models;

namespace FocusLock.Service;

/// <summary>
/// Kills any running blocked processes and re-applies IFEO keys that were removed
/// manually. Runs every 2 seconds, independent of the pipe server.
/// </summary>
public class AppMonitorWorker : BackgroundService
{
    private readonly ILogger<AppMonitorWorker> _log;
    private readonly SessionManager _manager;

    public AppMonitorWorker(ILogger<AppMonitorWorker> log, SessionManager manager)
    {
        _log     = log;
        _manager = manager;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = _manager.Session;
                if (s?.Status == SessionStatus.Active)
                {
                    _manager.AppBlocker.KillRunningBlockedApps(s);
                    _manager.AppBlocker.VerifyAndRepairIfeo(s);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "App monitor error.");
            }
            await Task.Delay(2000, ct);
        }
    }
}
