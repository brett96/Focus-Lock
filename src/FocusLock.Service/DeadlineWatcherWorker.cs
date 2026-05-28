using FocusLock.Core.Models;

namespace FocusLock.Service;

/// <summary>
/// Checks every 5 seconds whether the active session's deadline has passed and
/// ends it automatically when it has.
/// </summary>
public class DeadlineWatcherWorker : BackgroundService
{
    private readonly ILogger<DeadlineWatcherWorker> _log;
    private readonly SessionManager _manager;

    public DeadlineWatcherWorker(ILogger<DeadlineWatcherWorker> log, SessionManager manager)
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
                if (s?.Status == SessionStatus.Active && DateTime.UtcNow >= s.DeadlineUtc)
                {
                    _log.LogInformation("Session {Id} deadline reached.", s.Id);
                    await _manager.SessionLock.WaitAsync(ct);
                    try { _manager.EndSession(s); }
                    finally { _manager.SessionLock.Release(); }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Deadline watcher error.");
            }
            await Task.Delay(5000, ct);
        }
    }
}
