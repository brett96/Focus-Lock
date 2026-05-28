using System.Text.Json.Serialization;

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

    // Strict mode only — DPAPI-encrypted blob. Never sent over IPC.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? StrictPasswordBlob { get; set; }

    // Names of admin accounts demoted at session start; used to restore them on end.
    public List<string> DemotedAccountNames { get; init; } = new();

    // Tracks which IFEO keys existed before the session so we can restore them.
    public Dictionary<string, string?> IfeoPreExistingDebuggers { get; init; } = new();
}
