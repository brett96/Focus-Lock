using FocusLock.Core.Services;

namespace FocusLock.Watchdog;

/// <summary>
/// Restarts FocusLockService when it is killed during any active focus session.
/// Re-applies service DACLs and marks this process critical while a session is active.
/// </summary>
public sealed class WatchdogWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private readonly ILogger<WatchdogWorker> _log;

    public WatchdogWorker(ILogger<WatchdogWorker> log) => _log = log;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ActiveSessionHelper.IsActiveSession())
                {
                    SessionProtectionHelper.EnsureServiceProtection();
                    ProcessProtection.TrySetCritical(true);

                    if (!WindowsServiceScm.IsRunning(FocusLockServiceNames.MainService))
                    {
                        _log.LogWarning("Main service is not running during active session; restarting.");
                        if (!WindowsServiceScm.TryStart(FocusLockServiceNames.MainService))
                            _log.LogError("Failed to restart main Focus Lock service.");
                    }
                }
                else
                {
                    ProcessProtection.TrySetCritical(false);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Watchdog poll error.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
