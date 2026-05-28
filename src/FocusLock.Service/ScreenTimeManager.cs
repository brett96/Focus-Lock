using System.Diagnostics;
using System.Runtime.InteropServices;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.Core.Storage;
using FocusLock.Service.Blocking;

namespace FocusLock.Service;

/// <summary>
/// Runs a 1-second tick loop that enforces all screen time limits independently of
/// Focus Lock sessions.  Owned and started by SessionWorker.
/// </summary>
public sealed class ScreenTimeManager(ILogger log)
{
    private ScreenTimeConfig _config = ScreenTimeConfigRepository.Load();
    private ScreenTimeState  _state  = ScreenTimeStateRepository.LoadOrReset();

    private readonly SemaphoreSlim _lock = new(1, 1);

    // Tracks the "lockout notification was sent for this login" moment.
    private DateTime? _lockoutNotifiedAt;
    // Last observed console session ID; used to detect login transitions.
    private uint _lastSessionId = 0xFFFFFFFF;
    // Dirty flag: save state immediately after an important change.
    private bool _stateDirty;
    private int  _ticksSinceSave;

    // ── Public loop ───────────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _lock.WaitAsync(ct);
                try { Tick(); }
                finally { _lock.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogError(ex, "ScreenTimeManager tick error."); }

            await Task.Delay(1000, ct);
        }
        ScreenTimeStateRepository.Save(_state);
    }

    // ── IPC handlers ─────────────────────────────────────────────────────────

    public async Task<AckResponse> HandleSetConfigAsync(PipeMessage msg)
    {
        var req = PipeFraming.ParsePayload<SetScreenTimeConfigRequest>(msg);
        if (req?.Config is null) return new AckResponse(false, "Invalid payload.");

        await _lock.WaitAsync();
        try
        {
            _config = req.Config;
            ScreenTimeConfigRepository.Save(_config);
            log.LogInformation("Screen time config updated.");
            return new AckResponse(true);
        }
        finally { _lock.Release(); }
    }

    public ScreenTimeConfigResponse HandleGetConfig() => new(_config);

    public ScreenTimeStatusResponse HandleGetStatus()
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
                limit.LimitType == AppLimitType.Interval ? limit.IntervalMinutes : null);
        }).ToList();

        return new ScreenTimeStatusResponse(
            _config.EnableDailyLimit,
            _config.DailyLimitMinutes,
            _state.TotalSecondsUsed,
            _state.IsLockedOutForDay,
            statuses);
    }

    // ── Tick ──────────────────────────────────────────────────────────────────

    private void Tick()
    {
        var now = DateTime.Now;

        // Reset on date change (midnight rollover).
        if (_state.Date != DateOnly.FromDateTime(now))
        {
            _state = new ScreenTimeState { Date = DateOnly.FromDateTime(now) };
            _lockoutNotifiedAt = null;
            _lastSessionId = 0xFFFFFFFF;
            _stateDirty = true;
            log.LogInformation("Screen time state reset for new day.");
        }

        bool userActive = IsUserLoggedIn(out uint sessionId);

        // Detect user login (session ID changed while a user is now present).
        if (userActive && sessionId != _lastSessionId)
        {
            log.LogDebug("User login detected (session {Id}).", sessionId);
            _lockoutNotifiedAt = null; // re-notify on each new login
        }
        _lastSessionId = userActive ? sessionId : 0xFFFFFFFF;

        if (_config.EnableDailyLimit)
            TickDailyLimit(now, userActive, sessionId);

        if (_config.AppLimits.Count > 0)
            TickAppLimits(now);

        // Persist state periodically or immediately after important changes.
        if (_stateDirty || ++_ticksSinceSave >= 10)
        {
            ScreenTimeStateRepository.Save(_state);
            _ticksSinceSave = 0;
            _stateDirty = false;
        }
    }

    private void TickDailyLimit(DateTime now, bool userActive, uint sessionId)
    {
        var schedule = _config.DailySchedule ?? ScreenTimeSchedule.Always;
        bool scheduleActive = schedule.IsActiveNow(now);

        // Accumulate active time while user is logged in and schedule is active.
        if (scheduleActive && userActive)
        {
            _state.TotalSecondsUsed++;
            if (!_state.IsLockedOutForDay && _state.TotalSecondsUsed >= _config.DailyLimitMinutes * 60)
            {
                _state.IsLockedOutForDay = true;
                _stateDirty = true;
                log.LogInformation("Daily screen time limit reached ({Min} min).", _config.DailyLimitMinutes);
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
                log.LogInformation("Showing daily limit notification and scheduling disconnect.");
                try
                {
                    var notifier = new SessionNotifier(log);
                    notifier.ShowMessage(
                        "Focus Lock — Screen Time Limit Reached",
                        $"Your daily screen time limit of {timeStr} has been reached.\n\nYou will be signed out momentarily.");
                }
                catch (Exception ex) { log.LogDebug(ex, "Failed to show lockout notification."); }
            }
            else if ((now - _lockoutNotifiedAt.Value).TotalSeconds >= 5)
            {
                WTSDisconnectSession(IntPtr.Zero, sessionId, false);
                log.LogInformation("Disconnected session {Id} (daily screen time limit).", sessionId);
                // Session is gone; reset so next login gets notified again.
                _lastSessionId = 0xFFFFFFFF;
                _lockoutNotifiedAt = null;
            }
        }
    }

    private void TickAppLimits(DateTime now)
    {
        HashSet<string>? running = null;

        foreach (var limit in _config.AppLimits)
        {
            running ??= GetRunningExeNames();

            var effectiveSchedule = limit.Schedule ?? _config.DailySchedule ?? ScreenTimeSchedule.Always;
            bool scheduleActive = effectiveSchedule.IsActiveNow(now);

            if (!_state.AppUsage.TryGetValue(limit.ExeName, out var usage))
            {
                usage = new AppUsageEntry { ExeName = limit.ExeName };
                _state.AppUsage[limit.ExeName] = usage;
            }

            bool appRunning = running.Contains(limit.ExeName);

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
            }
            return;
        }

        if (appRunning)
        {
            usage.TotalSecondsToday++;
            if (!usage.IsBlockedToday && usage.TotalSecondsToday >= limit.LimitMinutes * 60)
            {
                usage.IsBlockedToday = true;
                _stateDirty = true;
                log.LogInformation("App {Exe} daily limit reached ({Min} min).",
                    limit.ExeName, limit.LimitMinutes);
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
            usage.CurrentIntervalIndex = intervalIndex;
            usage.CurrentIntervalSecondsUsed = 0;
            usage.IsBlockedInCurrentInterval = false;
            _stateDirty = true;
        }

        if (appRunning)
        {
            usage.CurrentIntervalSecondsUsed++;
            if (!usage.IsBlockedInCurrentInterval &&
                usage.CurrentIntervalSecondsUsed >= limit.LimitMinutes * 60)
            {
                usage.IsBlockedInCurrentInterval = true;
                _stateDirty = true;
                log.LogInformation("App {Exe} interval limit reached ({Min} min per {Int} min).",
                    limit.ExeName, limit.LimitMinutes, limit.IntervalMinutes);
            }
        }

        if (usage.IsBlockedInCurrentInterval && appRunning)
            KillApp(limit.ExeName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
            try { set.Add(proc.ProcessName + ".exe"); }
            catch { }
            finally { proc.Dispose(); }
        }
        return set;
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
}
