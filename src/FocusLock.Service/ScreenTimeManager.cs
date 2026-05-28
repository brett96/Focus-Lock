using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;
using FocusLock.Core.Storage;
using FocusLock.Service.Blocking;

namespace FocusLock.Service;

/// <summary>
/// Runs a 1-second tick loop that enforces screen time limits only while a
/// Focus Lock session is active. Owned and started by SessionWorker.
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
        if (!_config.EnableDailyLimit)
            _config.DailySchedule = null;
    }

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
                if (_sessionActive)
                {
                    bool userActive = IsUserLoggedIn(out uint sessionId);
                    HashSet<string>? running = null;
                    string? foreground = null;
                    if (_config.AppLimits.Count > 0)
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
        if (req.Config.EnableDailyLimit && req.Config.DailyLimitMinutes < ScreenTimeConfig.MinDailyLimitMinutes)
            return new AckResponse(false,
                $"Daily screen time limit must be at least {ScreenTimeConfig.MinDailyLimitMinutes} minutes.");

        await _lock.WaitAsync();
        try
        {
            _config = req.Config;
            if (!_config.EnableDailyLimit)
                _config.DailySchedule = null;
            ScreenTimeConfigRepository.Save(_config);
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
            _lockoutNotifiedAt = null;
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

            _state.AppUsage.TryGetValue(limit.ExeName, out var usage);
            bool blocked = limit.LimitType == AppLimitType.DailyTotal
                ? usage?.IsBlockedToday ?? false
                : usage?.IsBlockedInCurrentInterval ?? false;
            if (!blocked) return null;

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
        foreach (var limit in _config.AppLimits)
        {
            _state.AppUsage.TryGetValue(limit.ExeName, out var usage);
            bool blocked = limit.LimitType == AppLimitType.DailyTotal
                ? usage?.IsBlockedToday ?? false
                : usage?.IsBlockedInCurrentInterval ?? false;
            if (blocked)
                _appBlocker.ApplyScreenTimeBlock(limit.ExeName, session);
        }
    }

    public ScreenTimeStatusResponse HandleGetStatus()
    {
        if (!_sessionActive)
            return new ScreenTimeStatusResponse(false, 0, 0, false, []);

        // Never block the pipe server for long — the tick used to hold this lock during
        // full process enumeration and starved all IPC (UI showed "service unreachable").
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
        var statuses = _config.AppLimits.Select(limit =>
        {
            _state.AppUsage.TryGetValue(limit.ExeName, out var u);
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
        }).ToList();

        var dailySchedule = _config.DailySchedule;
        var schedule = GetEffectiveDailySchedule();
        var phase = ScreenTimeScheduleHelper.GetPhase(schedule, now);
        bool scheduleActive = phase == DailySchedulePhase.Active;

        return new ScreenTimeStatusResponse(
            _config.EnableDailyLimit,
            _config.DailyLimitMinutes,
            _state.TotalSecondsUsed,
            _state.IsLockedOutForDay && scheduleActive,
            statuses,
            DailyHasCustomSchedule: _config.EnableDailyLimit && dailySchedule is not null,
            DailyScheduleActiveNow: scheduleActive,
            DailyScheduleWindowEndedForToday: phase == DailySchedulePhase.AfterWindowToday,
            DailyScheduleLabel: _config.EnableDailyLimit ? ScreenTimeScheduleHelper.Describe(schedule) : null,
            DailyScheduleResumesAtLocal: _config.EnableDailyLimit && !scheduleActive
                ? ScreenTimeScheduleHelper.GetNextActiveStart(schedule, now)
                : null);
    }

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
        }
        _lastSessionId = snap.UserActive ? snap.SessionId : 0xFFFFFFFF;

        if (_config.EnableDailyLimit)
            TickDailyLimit(now, snap.UserActive, snap.SessionId);

        if (_config.AppLimits.Count > 0)
            TickAppLimits(now, snap.RunningExeNames, snap.ForegroundExe);

        _lastStatusSnapshot = BuildStatusResponse(now);

        if (_stateDirty || ++_ticksSinceSave >= 10)
        {
            ScreenTimeStateRepository.Save(_state);
            _ticksSinceSave = 0;
            _stateDirty = false;
        }
    }

    private void TickDailyLimit(DateTime now, bool userActive, uint sessionId)
    {
        var schedule = GetEffectiveDailySchedule();
        bool scheduleActive = ScreenTimeScheduleHelper.IsEnforcementActive(schedule, now);

        if (!scheduleActive)
        {
            _lockoutNotifiedAt = null;
            if (_state.IsLockedOutForDay)
            {
                _state.IsLockedOutForDay = false;
                _stateDirty = true;
            }
        }

        // Accumulate active time while user is logged in and schedule is active.
        if (scheduleActive && userActive)
        {
            _state.TotalSecondsUsed++;
            if (!_state.IsLockedOutForDay)
            {
                int limitSec = _config.DailyLimitMinutes * 60;
                int remaining = limitSec - _state.TotalSecondsUsed;
                bool dailyWarn5 = _state.DailyWarned5Min;
                bool dailyWarn1 = _state.DailyWarned1Min;
                MaybeNotifyRemaining(
                    "Focus Lock — Daily Screen Time",
                    "your device screen time",
                    remaining,
                    limitSec,
                    ref dailyWarn5,
                    ref dailyWarn1);
                _state.DailyWarned5Min = dailyWarn5;
                _state.DailyWarned1Min = dailyWarn1;

                if (_state.TotalSecondsUsed >= limitSec)
                {
                    _state.IsLockedOutForDay = true;
                    _stateDirty = true;
                    _log.LogInformation("Daily screen time limit reached ({Min} min).", _config.DailyLimitMinutes);
                }
            }
        }

        // Enforce lockout only while schedule is active.
        if (_state.IsLockedOutForDay && scheduleActive && userActive)
        {
            if (_lockoutNotifiedAt == null)
            {
                // First tick of this login → notify, then wait 5 s before disconnecting.
                _lockoutNotifiedAt = now;
                var timeStr = FormatMinutes(_config.DailyLimitMinutes);
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
                // Session is gone; reset so next login gets notified again.
                _lastSessionId = 0xFFFFFFFF;
                _lockoutNotifiedAt = null;
            }
        }
    }

    private void TickAppLimits(DateTime now, HashSet<string>? running, string? foregroundExe)
    {
        if (running is null) return;

        foreach (var limit in _config.AppLimits)
        {
            var effectiveSchedule = GetAppSchedule(limit);
            bool scheduleActive = effectiveSchedule.IsActiveNow(now);

            if (!_state.AppUsage.TryGetValue(limit.ExeName, out var usage))
            {
                usage = new AppUsageEntry { ExeName = limit.ExeName };
                _state.AppUsage[limit.ExeName] = usage;
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
                _appBlocker.RemoveScreenTimeBlock(limit.ExeName, _getSession());
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
        if (!scheduleActive) return;

        var anchor = schedule.StartTime?.ToTimeSpan() ?? TimeSpan.Zero;
        var scheduleStartToday = now.Date + anchor;
        if (now < scheduleStartToday) return;

        double intervalSeconds = limit.IntervalMinutes * 60.0;
        long intervalIndex = (long)((now - scheduleStartToday).TotalSeconds / intervalSeconds);

        if (usage.CurrentIntervalIndex != intervalIndex)
        {
            if (usage.IsBlockedInCurrentInterval)
                _appBlocker.RemoveScreenTimeBlock(limit.ExeName, _getSession());
            usage.CurrentIntervalIndex = intervalIndex;
            usage.CurrentIntervalSecondsUsed = 0;
            usage.IsBlockedInCurrentInterval = false;
            usage.Warned5Min = false;
            usage.Warned1Min = false;
            _stateDirty = true;
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

    private ScreenTimeSchedule GetEffectiveDailySchedule()
        => _config.DailySchedule ?? ScreenTimeSchedule.Always;

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
