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

public record IsBlockedResponse(
    bool IsBlocked,
    DateTime? DeadlineUtc,
    string? AppDisplayName,
    string? BlockMessage = null);

// ── Screen Time ────────────────────────────────────────────────────────────────

public record SetScreenTimeConfigRequest(ScreenTimeConfig Config);

public record ScreenTimeConfigResponse(ScreenTimeConfig Config);

public record BedtimeOccurrenceInfo(
    string ScheduleLabel,
    DateTime StartsAtLocal,
    DateTime EndsAtLocal);

public record ScreenTimeStatusResponse(
    bool Enabled,
    int DailyLimitMinutes,
    int TotalSecondsUsedToday,
    bool IsLockedOutForDay,
    List<AppUsageStatus> AppStatuses,
    bool DailyHasCustomSchedule = false,
    bool DailyScheduleActiveNow = true,
    bool DailyScheduleWindowEndedForToday = false,
    string? DailyScheduleLabel = null,
    DateTime? DailyScheduleResumesAtLocal = null,
    bool DailyShowingNextRuleToday = false,
    bool DailyShowingLastEndedRuleToday = false,
    bool BedtimesConfigured = false,
    bool BedtimeActiveNow = false,
    string? ActiveBedtimeLabel = null,
    List<BedtimeOccurrenceInfo>? UpcomingBedtimesDuringSession = null);

public record AppUsageStatus(
    string ExeName,
    string DisplayName,
    AppLimitType LimitType,
    int LimitMinutes,
    int TotalSecondsToday,
    bool IsBlocked,
    int? CurrentIntervalSecondsUsed,
    int? IntervalMinutes,
  /// <summary>Local time when the current interval period ends (interval limits only).</summary>
    DateTime? IntervalResetsAtLocal = null);
