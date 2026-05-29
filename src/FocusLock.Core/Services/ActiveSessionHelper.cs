using FocusLock.Core.Models;
using FocusLock.Core.Storage;

namespace FocusLock.Core.Services;

/// <summary>
/// Reads persisted session state so the watchdog can enforce protection without IPC.
/// </summary>
public static class ActiveSessionHelper
{
    public static bool IsActiveSession()
    {
        var session = SessionRepository.Load();
        return session is not null
               && session.Status == SessionStatus.Active
               && DateTime.UtcNow < session.DeadlineUtc;
    }
}
