using FocusLock.Core.Models;

namespace FocusLock.Core.Ipc;

// Wire envelope — Type is the discriminator, Payload is inner JSON.
public record PipeMessage(string Type, string Payload);

// ── Requests ──────────────────────────────────────────────────────────────────

public record StartSessionRequest(
    SessionMode Mode,
    DateTime DeadlineUtc,
    List<BlockedApp> Apps,
    List<BlockedSite> Sites,
    bool ConsentGiven);

public record EndSessionRequest(string Reason);

public record IsBlockedRequest(string ExeName);

// ── Responses ─────────────────────────────────────────────────────────────────

public record AckResponse(bool Success, string? ErrorMessage = null);

public record StatusResponse(SessionStatus Status, SessionMode Mode, DateTime? DeadlineUtc);

public record SessionInfoResponse(
    SessionStatus Status,
    SessionMode Mode,
    DateTime? StartedAtUtc,
    DateTime? DeadlineUtc,
    List<BlockedApp>? BlockedApps,
    List<BlockedSite>? BlockedSites,
    TimeSpan? TimeRemaining);

public record IsBlockedResponse(bool IsBlocked, DateTime? DeadlineUtc, string? AppDisplayName);
