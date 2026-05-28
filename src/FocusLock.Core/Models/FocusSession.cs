namespace FocusLock.Core.Models;

public class FocusSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public SessionMode Mode { get; init; }
    public SessionStatus Status { get; set; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime DeadlineUtc { get; init; }
    public List<BlockedApp> BlockedApps { get; init; } = new();
    public List<BlockedSite> BlockedSites { get; init; } = new();

    // Tracks which IFEO keys existed before the session so we can restore them.
    public Dictionary<string, string?> IfeoPreExistingDebuggers { get; init; } = new();
}
