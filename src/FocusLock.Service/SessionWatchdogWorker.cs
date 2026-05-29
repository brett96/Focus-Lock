using FocusLock.Core.Services;

namespace FocusLock.Service;

/// <summary>
/// During any active focus session, restarts the watchdog if stopped and re-applies service DACLs.
/// </summary>
public sealed class SessionWatchdogWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private readonly ILogger<SessionWatchdogWorker> _log;

    public SessionWatchdogWorker(ILogger<SessionWatchdogWorker> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ActiveSessionHelper.IsActiveSession())
                {
                    SessionProtectionHelper.EnsureServiceProtection();

                    if (!WindowsServiceScm.IsRunning(FocusLockServiceNames.WatchdogService))
                    {
                        _log.LogWarning("Watchdog service is not running during active session; restarting.");
                        if (!WindowsServiceScm.TryStart(FocusLockServiceNames.WatchdogService))
                            _log.LogError("Failed to restart watchdog service.");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Session watchdog monitor error.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
