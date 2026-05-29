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
        var session = LoadActiveSession();
        return session is not null;
    }

    public static bool IsActiveStrictSession()
    {
        var session = LoadActiveSession();
        return session is not null && session.Mode == SessionMode.Strict;
    }

    private static FocusSession? LoadActiveSession()
    {
        var session = SessionRepository.Load();
        if (session is null
            || session.Status != SessionStatus.Active
            || DateTime.UtcNow >= session.DeadlineUtc)
        {
            return null;
        }

        return session;
    }
}
