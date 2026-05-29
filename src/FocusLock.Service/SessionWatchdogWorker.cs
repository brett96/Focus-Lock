using FocusLock.Core.Models;
using FocusLock.Core.Services;
namespace FocusLock.Service;

/// <summary>
/// During any active focus session, restarts the watchdog if stopped and re-applies service DACLs.
/// </summary>
public sealed class SessionWatchdogWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private readonly ILogger<SessionWatchdogWorker> _log;
    private readonly SessionManager _manager;

    public SessionWatchdogWorker(ILogger<SessionWatchdogWorker> log, SessionManager manager)
    {
        _log     = log;
        _manager = manager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsActiveSession())
                {
                    SessionProtectionHelper.EnsureServiceProtection();
                    ProcessProtection.TrySetCritical(true);

                    if (!WindowsServiceScm.IsRunning(FocusLockServiceNames.WatchdogService))
                    {
                        _log.LogWarning("Watchdog service is not running during active session; restarting.");
                        if (!WindowsServiceScm.TryStart(FocusLockServiceNames.WatchdogService))
                            _log.LogError("Failed to restart watchdog service.");
                    }
                }
                else
                {
                    ProcessProtection.TrySetCritical(false);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Session watchdog monitor error.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private bool IsActiveSession()
    {
        var session = _manager.Session;
        return session is not null
               && session.Status == SessionStatus.Active
               && DateTime.UtcNow < session.DeadlineUtc;
    }
}
