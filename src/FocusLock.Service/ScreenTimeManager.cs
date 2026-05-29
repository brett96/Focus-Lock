using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;
using FocusLock.Core.Storage;
using FocusLock.Service.Blocking;

namespace FocusLock.Service;

/// <summary>
/// Runs a 1-second tick loop while a Focus Lock session is active: bedtimes, daily limits,
/// and per-app screen time enforcement.
/// </summary>
public sealed class ScreenTimeManager
{
    private readonly ILogger _log;
    private readonly AppBlocker _appBlocker;
    private readonly SessionNotifier _notifier;
    private readonly Func<FocusSession?> _getSession;

    private ScreenTimeConfig _config;
    private ScreenTimeState  _state  = new();

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _sessionActive;

    public ScreenTimeManager(ILogger log, AppBlocker appBlocker, Func<FocusSession?> getSession)
    {
        _log        = log;
        _appBlocker = appBlocker;
        _notifier   = new SessionNotifier(log);
        _getSession = getSession;
        _config     = ScreenTimeConfigRepository.Load();
    }

    // Tracks bedtime disconnect notification for the current login.
    private DateTime? _bedtimeNotifiedAt;
    // Upcoming bedtime warning keys (rule id + occurrence start) — same 5 / 1 minute pattern as screen time.
    private string? _bedtimeUpcomingWarn5Key;
    private string? _bedtimeUpcomingWarn1Key;

    // Tracks the "lockout notification was sent for this login" moment.
    private DateTime? _lockoutNotifiedAt;
    // Last observed console session ID; used to detect login transitions.
    private uint _lastSessionId = 0xFFFFFFFF;
    // Dirty flag: save state immediately after an important change.
    private bool _stateDirty;
    private int  _ticksSinceSave;
    private ScreenTimeStatusResponse? _lastStatusSnapshot;

    // ── Public loop ───────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ShouldRunTick())
                {
                    bool userActive = IsUserLoggedIn(out uint sessionId);
                    HashSet<string>? running = null;
                    string? foreground = null;
                    if (_sessionActive && _config.AppLimits.Count > 0)
                    {
                        running    = GetRunningExeNames();
                        foreground = GetForegroundExeName();
                    }

                    var snap = new TickSnapshot(DateTime.Now, userActive, sessionId, running, foreground);
                    if (!await _lock.WaitAsync(TimeSpan.FromSeconds(5), ct))
                    {
                        _log.LogWarning("Screen time tick skipped — could not acquire lock in time.");
                    }
                    else
                    {
                        try { ApplyTick(snap); }
                        finally { _lock.Release(); }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "ScreenTimeManager tick error."); }

            await Task.Delay(1000, ct);
        }

        await _lock.WaitAsync(ct);
        try { ScreenTimeStateRepository.Save(_state); }
        finally { _lock.Release(); }
    }

    private bool ShouldRunTick() => _sessionActive;

    private readonly record struct TickSnapshot(
        DateTime Now,
        bool UserActive,
        uint SessionId,
        HashSet<string>? RunningExeNames,
        string? ForegroundExe);

    // ── IPC handlers ─────────────────────────────────────────────────────────

    public async Task<AckResponse> HandleSetConfigAsync(PipeMessage msg)
    {
        var req = PipeFraming.ParsePayload<SetScreenTimeConfigRequest>(msg);
        if (req?.Config is null) return new AckResponse(false, "Invalid payload.");

        ScreenTimeConfigNormalizer.Normalize(req.Config);

        foreach (var rule in req.Config.DailyLimits)
        {
            if (rule.LimitMinutes < ScreenTimeConfig.MinDailyLimitMinutes)
                return new AckResponse(false,
                    $"Daily screen time limit must be at least {ScreenTimeConfig.MinDailyLimitMinutes} minutes.");
            if (rule.Schedule.ActiveDays == DayOfWeekFlags.None)
                return new AckResponse(false, "Each daily limit must include at least one day.");
        }

        if (ScreenTimeScheduleOverlap.HasInternalDailyLimitOverlap(req.Config.DailyLimits, out var dailyMsg))
            return new AckResponse(false, dailyMsg);

        if (ScreenTimeScheduleOverlap.HasInternalAppLimitOverlap(req.Config.AppLimits, out var appMsg))
            return new AckResponse(false, appMsg);

        foreach (var bedtime in req.Config.Bedtimes)
        {
            if (!BedtimeScheduleHelper.IsValidBedtimeSchedule(bedtime.Schedule))
                return new AckResponse(false, "Each bedtime must include at least one day and a start/end time.");
        }

        if (ScreenTimeScheduleOverlap.HasInternalBedtimeOverlap(req.Config.Bedtimes, out var bedtimeMsg))
            return new AckResponse(false, bedtimeMsg);

        if (ScreenTimeScheduleOverlap.HasBedtimeLimitConflicts(
                req.Config.Bedtimes, req.Config.DailyLimits, req.Config.AppLimits, out var conflictMsg))
            return new AckResponse(false, conflictMsg);

        await _lock.WaitAsync();
        try
        {
            _config = req.Config;
            ScreenTimeConfigRepository.Save(_config);
            _bedtimeNotifiedAt = null;
            _bedtimeUpcomingWarn5Key = null;
            _bedtimeUpcomingWarn1Key = null;
            _log.LogInformation("Screen time config updated.");
            return new AckResponse(true);
        }
        finally { _lock.Release(); }
    }

    public ScreenTimeConfigResponse HandleGetConfig() => new(_config);

    public void OnSessionStarted()
    {
        _lock.Wait();
        try
        {
            _sessionActive = true;
            _state = new ScreenTimeState { Date = DateOnly.FromDateTime(DateTime.Now) };
            _lockoutNotifiedAt = null;
            _bedtimeNotifiedAt = null;
            _bedtimeUpcomingWarn5Key = null;
            _bedtimeUpcomingWarn1Key = null;
            _lastSessionId = 0xFFFFFFFF;
            _stateDirty = true;
            _log.LogInformation("Screen time tracking started for focus session.");
        }
        finally { _lock.Release(); }
    }

    public void OnSessionEnded()
    {
        _lock.Wait();
        try
        {
            _sessionActive = false;
            foreach (var usage in _state.AppUsage.Values)
            {
                usage.IsBlockedToday = false;
                usage.IsBlockedInCurrentInterval = false;
            }
            _state.IsLockedOutForDay = false;
            _state.TotalSecondsUsed = 0;
            _state.DailyRuleUsage.Clear();
            _lockoutNotifiedAt = null;
            _bedtimeNotifiedAt = null;
            _bedtimeUpcomingWarn5Key = null;
            _bedtimeUpcomingWarn1Key = null;
            _stateDirty = true;
            _lastStatusSnapshot = null;
            ScreenTimeStateRepository.Save(_state);
            _appBlocker.ClearAllScreenTimeBlocks(_getSession());
            _log.LogInformation("Screen time tracking stopped (session ended).");
        }
        finally { _lock.Release(); }
    }

    public IsBlockedResponse? GetLaunchBlockResponse(string exeName)
    {
        if (!_sessionActive) return null;
        var session = _getSession();
        if (session is null || session.Status != SessionStatus.Active) return null;

        foreach (var limit in _config.AppLimits)
        {
            if (!string.Equals(limit.ExeName, exeName, StringComparison.OrdinalIgnoreCase))
                continue;

            _state.AppUsage.TryGetValue(limit.Id, out var usage);
            bool blocked = limit.LimitType == AppLimitType.DailyTotal
                ? usage?.IsBlockedToday ?? false
                : usage?.IsBlockedInCurrentInterval ?? false;
            if (!blocked) continue;

            string body = limit.LimitType == AppLimitType.DailyTotal
                ? $"{limit.DisplayName} has reached its {limit.LimitMinutes}-minute limit for this session and cannot be opened until the limit resets."
                : $"{limit.DisplayName} has reached its {limit.LimitMinutes}-minute allowance for this {limit.IntervalMinutes}-minute period and cannot be opened until the next interval.";

            return new IsBlockedResponse(true, session.DeadlineUtc, limit.DisplayName, body);
        }

        return null;
    }

    public void VerifyScreenTimeIfeo()
    {
        if (!_sessionActive) return;
        var session = _getSession();
        var blockedExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var limit in _config.AppLimits)
        {
            _state.AppUsage.TryGetValue(limit.Id, out var usage);
            bool blocked = limit.LimitType == AppLimitType.DailyTotal
                ? usage?.IsBlockedToday ?? false
                : usage?.IsBlockedInCurrentInterval ?? false;
            if (blocked)
                blockedExes.Add(limit.ExeName);
        }

        _appBlocker.SyncScreenTimeBlocks(blockedExes, session);
    }

    public ScreenTimeStatusResponse HandleGetStatus()
    {
        if (!ShouldRunTick())
            return new ScreenTimeStatusResponse(false, 0, 0, false, []);

        if (!_lock.Wait(TimeSpan.FromMilliseconds(500)))
        {
            if (_lastStatusSnapshot is not null)
                return _lastStatusSnapshot;
            return new ScreenTimeStatusResponse(false, 0, 0, false, []);
        }

        try
        {
            var response = BuildStatusResponse(DateTime.Now);
            _lastStatusSnapshot = response;
            return response;
        }
        finally { _lock.Release(); }
    }

    private ScreenTimeStatusResponse BuildStatusResponse(DateTime now)
    {
        var statuses = _sessionActive
            ? _config.AppLimits.Select(limit =>
            {
                _state.AppUsage.TryGetValue(limit.Id, out var u);
                bool blocked = limit.LimitType == AppLimitType.DailyTotal
                    ? u?.IsBlockedToday ?? false
                    : u?.IsBlockedInCurrentInterval ?? false;

                return new AppUsageStatus(
                    limit.ExeName, limit.DisplayName,
                    limit.LimitType, limit.LimitMinutes,
                    u?.TotalSecondsToday ?? 0, blocked,
                    limit.LimitType == AppLimitType.Interval ? u?.CurrentIntervalSecondsUsed : null,
                    limit.LimitType == AppLimitType.Interval ? limit.IntervalMinutes : null,
                    limit.LimitType == AppLimitType.Interval
                        ? ComputeIntervalResetsAt(now, limit)
                        : null);
            }).ToList()
            : new List<AppUsageStatus>();

        var dailyRules = _config.DailyLimits;
        bool enabled = _sessionActive && dailyRules.Count > 0;
        var display = enabled
            ? ScreenTimeScheduleHelper.ResolveDailyLimitDisplay(dailyRules, now)
            : new DailyLimitDisplayContext(null, false, false, false, null);
        var focusRule = display.FocusRule;
        bool scheduleActive = display.IsActiveNow;
        var usage = focusRule is not null ? GetOrCreateDailyUsage(focusRule.Id) : null;

        int dailyMinutes = focusRule?.LimitMinutes ?? 0;
        int totalSeconds = usage?.TotalSecondsUsed ?? 0;
        bool lockedOut = _sessionActive && usage?.IsLockedOut == true && scheduleActive;

        var bedtimeStatus = BuildBedtimeStatus(now);

        return new ScreenTimeStatusResponse(
            enabled,
            dailyMinutes,
            totalSeconds,
            lockedOut,
            statuses,
            DailyHasCustomSchedule: enabled && (dailyRules.Count > 1 || dailyRules.Any(HasNonDefaultDailySchedule)),
            DailyScheduleActiveNow: scheduleActive,
            DailyScheduleWindowEndedForToday: display.IsLastEndedRuleToday,
            DailyScheduleLabel: focusRule is not null
                ? ScreenTimeScheduleHelper.Describe(focusRule.Schedule)
                : enabled ? $"{dailyRules.Count} scheduled limit{(dailyRules.Count == 1 ? "" : "s")}" : null,
            DailyScheduleResumesAtLocal: display.IsActiveNow
                ? null
                : display.ReferenceTimeLocal ?? ScreenTimeScheduleHelper.GetNextActiveStart(
                    dailyRules.Select(r => r.Schedule), now),
            DailyShowingNextRuleToday: display.IsNextRuleToday,
            DailyShowingLastEndedRuleToday: display.IsLastEndedRuleToday,
            BedtimesConfigured: bedtimeStatus.Configured,
            BedtimeActiveNow: bedtimeStatus.ActiveNow,
            ActiveBedtimeLabel: bedtimeStatus.ActiveLabel,
            UpcomingBedtimesDuringSession: bedtimeStatus.Upcoming);
    }

    private readonly record struct BedtimeStatusFields(
        bool Configured,
        bool ActiveNow,
        string? ActiveLabel,
        List<BedtimeOccurrenceInfo>? Upcoming);

    private BedtimeStatusFields BuildBedtimeStatus(DateTime now)
    {
        if (!_sessionActive || _config.Bedtimes.Count == 0)
            return new BedtimeStatusFields(false, false, null, null);

        var active = BedtimeScheduleHelper.FindActiveBedtime(_config.Bedtimes, now);
        string? activeLabel = active is not null
            ? BedtimeScheduleHelper.Describe(active.Schedule)
            : null;

        List<BedtimeOccurrenceInfo>? upcoming = null;
        var session = _getSession();
        if (session?.Status == SessionStatus.Active && session.DeadlineUtc.ToLocalTime() > now)
        {
            var deadline = session.DeadlineUtc.ToLocalTime();
            upcoming = BedtimeScheduleHelper
                .GetOccurrencesInRange(_config.Bedtimes, now, deadline)
                .Where(o => o.EndsAtLocal > now)
                .Select(o => new BedtimeOccurrenceInfo(
                    BedtimeScheduleHelper.Describe(o.Rule.Schedule),
                    o.StartsAtLocal,
                    o.EndsAtLocal))
                .ToList();
        }

        return new BedtimeStatusFields(true, active is not null, activeLabel, upcoming);
    }

    private static bool HasNonDefaultDailySchedule(DailyTimeLimit rule)
        => ScreenTimeScheduleHelper.HasTimedWindow(rule.Schedule)
           || rule.Schedule.ActiveDays != (DayOfWeekFlags)127;

    /// <summary>
    /// End of the current interval window (local time), aligned with <see cref="TickIntervalApp"/>.
    /// </summary>
    private DateTime? ComputeIntervalResetsAt(DateTime now, AppTimeLimit limit)
    {
        if (limit.LimitType != AppLimitType.Interval || limit.IntervalMinutes <= 0)
            return null;

        var schedule = GetAppSchedule(limit);
        if (!schedule.IsActiveNow(now))
            return null;

        var anchor = schedule.StartTime?.ToTimeSpan() ?? TimeSpan.Zero;
        var scheduleStartToday = now.Date + anchor;
        if (now < scheduleStartToday)
            return scheduleStartToday;

        double intervalSeconds = limit.IntervalMinutes * 60.0;
        long intervalIndex = (long)((now - scheduleStartToday).TotalSeconds / intervalSeconds);
        var intervalEnd = scheduleStartToday.AddSeconds((intervalIndex + 1) * intervalSeconds);

        if (schedule.EndTime is { } endTime)
        {
            var scheduleEndToday = now.Date + endTime.ToTimeSpan();
            if (intervalEnd > scheduleEndToday)
                intervalEnd = scheduleEndToday;
            if (now >= scheduleEndToday)
                return null;
        }

        return intervalEnd;
    }

    // ── Tick ──────────────────────────────────────────────────────────────────

    private void ApplyTick(TickSnapshot snap)
    {
        var now = snap.Now;

        // Reset on date change (midnight rollover) while a session spans midnight.
        if (_state.Date != DateOnly.FromDateTime(now))
        {
            _state = new ScreenTimeState { Date = DateOnly.FromDateTime(now) };
            _lockoutNotifiedAt = null;
            _lastSessionId = 0xFFFFFFFF;
            _stateDirty = true;
            _log.LogInformation("Screen time state reset for new day.");
        }

        if (snap.UserActive && snap.SessionId != _lastSessionId)
        {
            _log.LogDebug("User login detected (session {Id}).", snap.SessionId);
            _lockoutNotifiedAt = null;
            _bedtimeNotifiedAt = null;
            _bedtimeUpcomingWarn5Key = null;
            _bedtimeUpcomingWarn1Key = null;
        }
        _lastSessionId = snap.UserActive ? snap.SessionId : 0xFFFFFFFF;

        if (_config.Bedtimes.Count > 0)
        {
            TickBedtime(now, snap.UserActive, snap.SessionId);
            if (snap.UserActive)
                TickBedtimeUpcomingWarnings(now);
        }

        if (_config.DailyLimits.Count > 0)
            TickDailyLimit(now, snap.UserActive, snap.SessionId);

        if (_config.AppLimits.Count > 0 && snap.RunningExeNames is not null)
            TickAppLimits(now, snap.RunningExeNames, snap.ForegroundExe);

        _lastStatusSnapshot = BuildStatusResponse(now);

        if (_stateDirty || ++_ticksSinceSave >= 10)
        {
            ScreenTimeStateRepository.Save(_state);
            _ticksSinceSave = 0;
            _stateDirty = false;
        }
    }

    private void TickBedtime(DateTime now, bool userActive, uint sessionId)
    {
        var activeBedtime = BedtimeScheduleHelper.FindActiveBedtime(_config.Bedtimes, now);
        if (activeBedtime is null)
        {
            _bedtimeNotifiedAt = null;
            return;
        }

        _bedtimeUpcomingWarn5Key = null;
        _bedtimeUpcomingWarn1Key = null;

        if (!userActive)
            return;

        if (_bedtimeNotifiedAt == null)
        {
            _bedtimeNotifiedAt = now;
            _log.LogInformation("Bedtime active ({Schedule}); scheduling sign-out.",
                BedtimeScheduleHelper.Describe(activeBedtime.Schedule));
            try
            {
                _notifier.ShowMessage(
                    "Focus Lock — Bedtime",
                    "Bedtime is active. You will be signed out momentarily.");
            }
            catch (Exception ex) { _log.LogDebug(ex, "Failed to show bedtime notification."); }
        }
        else if ((now - _bedtimeNotifiedAt.Value).TotalSeconds >= 5)
        {
            WTSDisconnectSession(IntPtr.Zero, sessionId, false);
            _log.LogInformation("Disconnected session {Id} (bedtime).", sessionId);
            _lastSessionId = 0xFFFFFFFF;
            _bedtimeNotifiedAt = null;
        }
    }

    private void TickBedtimeUpcomingWarnings(DateTime now)
    {
        if (BedtimeScheduleHelper.FindActiveBedtime(_config.Bedtimes, now) is not null)
            return;

        var occurrences = BedtimeScheduleHelper.GetOccurrencesInRange(_config.Bedtimes, now, now.AddDays(2));
        BedtimeOccurrence? next = null;
        foreach (var occ in occurrences)
        {
            if (occ.StartsAtLocal > now)
            {
                next = occ;
                break;
            }
        }

        if (next is null)
        {
            _bedtimeUpcomingWarn5Key = null;
            _bedtimeUpcomingWarn1Key = null;
            return;
        }

        var key   = BedtimeOccurrenceKey(next.Value);
        var label = BedtimeScheduleHelper.Describe(next.Value.Rule.Schedule);
        int secs  = (int)(next.Value.StartsAtLocal - now).TotalSeconds;

        if (secs > 300)
        {
            if (_bedtimeUpcomingWarn5Key != key) _bedtimeUpcomingWarn5Key = null;
            if (_bedtimeUpcomingWarn1Key != key) _bedtimeUpcomingWarn1Key = null;
            return;
        }

        if (secs <= 0)
            return;

        try
        {
            if (_bedtimeUpcomingWarn5Key != key && secs <= 300 && secs > 60)
            {
                _notifier.ShowMessage(
                    "Focus Lock — Bedtime",
                    $"Bedtime starts in about 5 minutes ({label}).");
                _bedtimeUpcomingWarn5Key = key;
            }
            else if (_bedtimeUpcomingWarn1Key != key && secs <= 60)
            {
                _notifier.ShowMessage(
                    "Focus Lock — Bedtime",
                    $"Bedtime starts in about 1 minute ({label}).");
                _bedtimeUpcomingWarn1Key = key;
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to show upcoming bedtime notification."); }
    }

    private static string BedtimeOccurrenceKey(BedtimeOccurrence occ)
        => $"{occ.Rule.Id}:{occ.StartsAtLocal:O}";

    private void TickDailyLimit(DateTime now, bool userActive, uint sessionId)
    {
        var activeRule = FindActiveDailyLimit(now);
        bool scheduleActive = activeRule is not null;

        if (!scheduleActive)
        {
            _lockoutNotifiedAt = null;
            foreach (var usage in _state.DailyRuleUsage.Values)
            {
                if (!usage.IsLockedOut) continue;
                usage.IsLockedOut = false;
                _stateDirty = true;
            }
            _state.IsLockedOutForDay = false;
        }

        if (scheduleActive && userActive && activeRule is not null)
        {
            var usage = GetOrCreateDailyUsage(activeRule.Id);

            if (!usage.IsLockedOut)
            {
                usage.TotalSecondsUsed++;
                _state.TotalSecondsUsed = usage.TotalSecondsUsed;

                int limitSec = activeRule.LimitMinutes * 60;
                int remaining = limitSec - usage.TotalSecondsUsed;
                bool dailyWarn5 = usage.Warned5Min;
                bool dailyWarn1 = usage.Warned1Min;
                MaybeNotifyRemaining(
                    "Focus Lock — Daily Screen Time",
                    "your device screen time",
                    remaining,
                    limitSec,
                    ref dailyWarn5,
                    ref dailyWarn1);
                usage.Warned5Min = dailyWarn5;
                usage.Warned1Min = dailyWarn1;

                if (usage.TotalSecondsUsed >= limitSec)
                {
                    usage.IsLockedOut = true;
                    _state.IsLockedOutForDay = true;
                    _stateDirty = true;
                    _log.LogInformation("Daily screen time limit reached ({Min} min).", activeRule.LimitMinutes);
                }
            }
            else
            {
                EnforceDailyLockoutDisconnect(now, sessionId, activeRule, usage);
            }
        }
    }

    private void EnforceDailyLockoutDisconnect(
        DateTime now,
        uint sessionId,
        DailyTimeLimit activeRule,
        DailyRuleUsageEntry usage)
    {
        if (!usage.IsLockedOut)
            return;

        if (_lockoutNotifiedAt == null)
        {
            _lockoutNotifiedAt = now;
            var timeStr = FormatMinutes(activeRule.LimitMinutes);
            _log.LogInformation("Showing daily limit notification and scheduling disconnect.");
            try
            {
                _notifier.ShowMessage(
                    "Focus Lock — Screen Time Limit Reached",
                    $"Your daily screen time limit of {timeStr} has been reached.\n\nYou will be signed out momentarily.");
            }
            catch (Exception ex) { _log.LogDebug(ex, "Failed to show lockout notification."); }
        }
        else if ((now - _lockoutNotifiedAt.Value).TotalSeconds >= 5)
        {
            WTSDisconnectSession(IntPtr.Zero, sessionId, false);
            _log.LogInformation("Disconnected session {Id} (daily screen time limit).", sessionId);
            _lastSessionId = 0xFFFFFFFF;
            _lockoutNotifiedAt = null;
        }
    }

    private void TickAppLimits(DateTime now, HashSet<string>? running, string? foregroundExe)
    {
        if (running is null) return;

        foreach (var limit in _config.AppLimits)
        {
            var effectiveSchedule = GetAppSchedule(limit);
            bool scheduleActive = effectiveSchedule.IsActiveNow(now);

            if (!_state.AppUsage.TryGetValue(limit.Id, out var usage))
            {
                usage = new AppUsageEntry { ExeName = limit.ExeName };
                _state.AppUsage[limit.Id] = usage;
            }

            bool appRunning = IsAppInUse(limit.ExeName, running, foregroundExe);

            switch (limit.LimitType)
            {
                case AppLimitType.DailyTotal:
                    TickDailyTotalApp(limit, usage, appRunning, scheduleActive);
                    break;
                case AppLimitType.Interval:
                    TickIntervalApp(limit, usage, appRunning, scheduleActive, now, effectiveSchedule);
                    break;
            }
        }
    }

    private void TickDailyTotalApp(AppTimeLimit limit, AppUsageEntry usage,
        bool appRunning, bool scheduleActive)
    {
        // When the schedule window ends, unblock the app (it resets per restriction window).
        if (!scheduleActive)
        {
            if (usage.IsBlockedToday)
            {
                usage.IsBlockedToday = false;
                _stateDirty = true;
                TryRemoveScreenTimeBlock(limit.ExeName);
            }
            return;
        }

        if (appRunning)
        {
            usage.TotalSecondsToday++;
            int limitSec = limit.LimitMinutes * 60;
            if (!usage.IsBlockedToday)
            {
                int remaining = limitSec - usage.TotalSecondsToday;
                bool warned5 = usage.Warned5Min;
                bool warned1 = usage.Warned1Min;
                MaybeNotifyRemaining(
                    "Focus Lock — App Time Limit",
                    limit.DisplayName,
                    remaining,
                    limitSec,
                    ref warned5,
                    ref warned1);
                usage.Warned5Min = warned5;
                usage.Warned1Min = warned1;

                if (usage.TotalSecondsToday >= limitSec)
                {
                    usage.IsBlockedToday = true;
                    _stateDirty = true;
                    _appBlocker.ApplyScreenTimeBlock(limit.ExeName, _getSession());
                    _log.LogInformation("App {Exe} daily limit reached ({Min} min).",
                        limit.ExeName, limit.LimitMinutes);
                    try
                    {
                        _notifier.ShowMessage(
                            "Focus Lock — App Time Limit",
                            $"{limit.DisplayName} has reached its {limit.LimitMinutes}-minute limit for this session and will be closed.");
                    }
                    catch (Exception ex) { _log.LogDebug(ex, "Failed to show app limit notification."); }
                }
            }
        }

        if (usage.IsBlockedToday && appRunning)
            KillApp(limit.ExeName);
    }

    private void TickIntervalApp(AppTimeLimit limit, AppUsageEntry usage,
        bool appRunning, bool scheduleActive, DateTime now, ScreenTimeSchedule schedule)
    {
        if (!scheduleActive)
        {
            if (usage.IsBlockedInCurrentInterval || usage.CurrentIntervalIndex != -1)
            {
                usage.IsBlockedInCurrentInterval = false;
                usage.CurrentIntervalIndex = -1;
                usage.CurrentIntervalSecondsUsed = 0;
                usage.Warned5Min = false;
                usage.Warned1Min = false;
                _stateDirty = true;
                TryRemoveScreenTimeBlock(limit.ExeName);
            }
            return;
        }

        var anchor = schedule.StartTime?.ToTimeSpan() ?? TimeSpan.Zero;
        var scheduleStartToday = now.Date + anchor;
        if (now < scheduleStartToday) return;

        double intervalSeconds = limit.IntervalMinutes * 60.0;
        long intervalIndex = (long)((now - scheduleStartToday).TotalSeconds / intervalSeconds);

        if (usage.CurrentIntervalIndex != intervalIndex)
        {
            var wasBlocked = usage.IsBlockedInCurrentInterval;
            usage.CurrentIntervalIndex = intervalIndex;
            usage.CurrentIntervalSecondsUsed = 0;
            usage.IsBlockedInCurrentInterval = false;
            usage.Warned5Min = false;
            usage.Warned1Min = false;
            _stateDirty = true;
            if (wasBlocked)
                TryRemoveScreenTimeBlock(limit.ExeName);
        }

        if (appRunning)
        {
            usage.CurrentIntervalSecondsUsed++;
            int limitSec = limit.LimitMinutes * 60;
            if (!usage.IsBlockedInCurrentInterval)
            {
                int remaining = limitSec - usage.CurrentIntervalSecondsUsed;
                bool warned5 = usage.Warned5Min;
                bool warned1 = usage.Warned1Min;
                MaybeNotifyRemaining(
                    "Focus Lock — App Time Limit",
                    $"{limit.DisplayName} this interval",
                    remaining,
                    limitSec,
                    ref warned5,
                    ref warned1);
                usage.Warned5Min = warned5;
                usage.Warned1Min = warned1;

                if (usage.CurrentIntervalSecondsUsed >= limitSec)
                {
                    usage.IsBlockedInCurrentInterval = true;
                    _stateDirty = true;
                    _appBlocker.ApplyScreenTimeBlock(limit.ExeName, _getSession());
                    _log.LogInformation("App {Exe} interval limit reached ({Min} min per {Int} min).",
                        limit.ExeName, limit.LimitMinutes, limit.IntervalMinutes);
                    try
                    {
                        _notifier.ShowMessage(
                            "Focus Lock — App Time Limit",
                            $"{limit.DisplayName} has reached its {limit.LimitMinutes}-minute limit for this {limit.IntervalMinutes}-minute period and will be closed.");
                    }
                    catch (Exception ex) { _log.LogDebug(ex, "Failed to show app limit notification."); }
                }
            }
        }

        if (usage.IsBlockedInCurrentInterval && appRunning)
            KillApp(limit.ExeName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ScreenTimeSchedule GetAppSchedule(AppTimeLimit limit)
        => limit.Schedule ?? ScreenTimeSchedule.Always;

    private DailyTimeLimit? FindActiveDailyLimit(DateTime now)
    {
        foreach (var rule in _config.DailyLimits)
        {
            if (ScreenTimeScheduleHelper.IsEnforcementActive(rule.Schedule, now))
                return rule;
        }
        return null;
    }

    private DailyRuleUsageEntry GetOrCreateDailyUsage(string ruleId)
    {
        if (!_state.DailyRuleUsage.TryGetValue(ruleId, out var usage))
        {
            usage = new DailyRuleUsageEntry();
            _state.DailyRuleUsage[ruleId] = usage;
        }
        return usage;
    }

    private bool IsExeBlockedByAnyRule(string exeName)
    {
        foreach (var limit in _config.AppLimits)
        {
            if (!string.Equals(limit.ExeName, exeName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_state.AppUsage.TryGetValue(limit.Id, out var usage))
                continue;
            if (usage.IsBlockedToday || usage.IsBlockedInCurrentInterval)
                return true;
        }
        return false;
    }

    private void TryRemoveScreenTimeBlock(string exeName)
    {
        if (!IsExeBlockedByAnyRule(exeName))
            _appBlocker.RemoveScreenTimeBlock(exeName, _getSession());
    }

    private void MaybeNotifyRemaining(
        string title,
        string contextLabel,
        int remainingSec,
        int limitSec,
        ref bool warned5,
        ref bool warned1)
    {
        if (limitSec < 300)
            warned5 = true;

        try
        {
            if (!warned5 && remainingSec <= 300 && remainingSec > 60)
            {
                _notifier.ShowMessage(title, $"About 5 minutes remaining for {contextLabel}.");
                warned5 = true;
                _stateDirty = true;
            }
            else if (!warned1 && remainingSec <= 60 && remainingSec > 0)
            {
                _notifier.ShowMessage(title, $"About 1 minute remaining for {contextLabel}.");
                warned1 = true;
                _stateDirty = true;
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to show remaining-time notification."); }
    }

    private static bool IsAppInUse(string exeName, HashSet<string> running, string? foregroundExe)
    {
        var normalized = NormalizeExeName(exeName);
        if (running.Contains(normalized))
            return true;
        return foregroundExe is not null
            && string.Equals(foregroundExe, normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeExeName(string name)
    {
        name = Path.GetFileName(name).Trim();
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";
        return name;
    }

    private static void KillApp(string exeName)
    {
        string baseName = Path.GetFileNameWithoutExtension(exeName);
        foreach (var proc in Process.GetProcessesByName(baseName))
        {
            try { proc.Kill(); }
            catch { }
            finally { proc.Dispose(); }
        }
    }

    private static HashSet<string> GetRunningExeNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                // ProcessName only — MainModule per process is too slow and held the screen-time
                // lock long enough to block the named-pipe server.
                set.Add(NormalizeExeName(proc.ProcessName + ".exe"));
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return set;
    }

    private static string? GetForegroundExeName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var proc = Process.GetProcessById((int)pid);
            try
            {
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                    return NormalizeExeName(path);
            }
            catch { }
            return NormalizeExeName(proc.ProcessName + ".exe");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUserLoggedIn(out uint sessionId)
    {
        sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return false;
        if (!WTSQueryUserToken(sessionId, out var token)) return false;
        CloseHandle(token);
        return true;
    }

    private static string FormatMinutes(int minutes)
    {
        int h = minutes / 60, m = minutes % 60;
        return h > 0 && m > 0 ? $"{h}h {m}m" : h > 0 ? $"{h}h" : $"{m}m";
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, uint sessionId, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
