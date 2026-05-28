using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.UI.Services;
using FocusLock.UI.Views;

namespace FocusLock.UI.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly NavigationService _nav;
    private readonly Dictionary<string, AppUsageDisplayItem> _appUsageByExe = new(StringComparer.OrdinalIgnoreCase);

    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _pollTimer;

    [ObservableProperty] private SessionStatus _sessionStatus = SessionStatus.Idle;
    [ObservableProperty] private SessionMode _sessionMode = SessionMode.None;
    [ObservableProperty] private string _timeRemaining = "--:--:--";
    [ObservableProperty] private string _deadlineText = string.Empty;
    [ObservableProperty] private bool _isServiceReachable = true;
    [ObservableProperty] private bool _isStrictMode;

    [ObservableProperty] private List<BlockedApp> _blockedApps = new();
    [ObservableProperty] private List<BlockedSite> _blockedSites = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenTime))]
    private bool _showDailyLimit;

    [ObservableProperty] private string _dailyUsageText = string.Empty;
    [ObservableProperty] private string _dailyLimitLabel = string.Empty;
    [ObservableProperty] private string _dailyRemainingText = string.Empty;
    [ObservableProperty] private double _dailyProgress;
    [ObservableProperty] private bool _isDailyLockedOut;
    [ObservableProperty] private string _dailyScheduleLabel = string.Empty;
    [ObservableProperty] private bool _dailyHasCustomSchedule;
    [ObservableProperty] private bool _dailyScheduleActiveNow = true;
    [ObservableProperty] private bool _dailyScheduleWindowEndedForToday;
    [ObservableProperty] private string _dailyScheduleStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenTime))]
    private bool _hasAppLimits;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEndEarly))]
    [NotifyPropertyChangedFor(nameof(ShowNewSessionButton))]
    [NotifyPropertyChangedFor(nameof(ShowActiveSession))]
    private bool _isEndingSession;

    [ObservableProperty] private string _endingSessionHeadline = "Ending session…";
    [ObservableProperty] private string _endingSessionDetail =
        "Removing blocks and restoring access. This may take a few seconds.";

    public ObservableCollection<AppUsageDisplayItem> AppUsages { get; } = new();

    public bool IsIdle => SessionStatus == SessionStatus.Idle;
    public bool IsActive => SessionStatus == SessionStatus.Active;
    public bool CanEndEarly => IsActive && !IsStrictMode && !IsEndingSession;
    public bool ShowNewSessionButton => IsIdle && !IsEndingSession;
    public bool ShowActiveSession => IsActive && !IsEndingSession;
    public bool IsServiceUnreachable => !IsServiceReachable;
    public bool HasSessionBlocks => BlockedApps.Count > 0 || BlockedSites.Count > 0;
    public bool HasScreenTime => ShowDailyLimit || HasAppLimits;
    public string ModeLabel => SessionMode == SessionMode.Strict ? "STRICT MODE" : "REGULAR MODE";
    public string ModeColor => SessionMode == SessionMode.Strict ? "#F38BA8" : "#89B4FA";

    private DateTime? _deadlineUtc;
    private bool _refreshing;
    private bool _deadlineEndTriggered;
    private int _sessionInfoFailureStreak;

    /// <summary>Consecutive failed session polls before showing the unreachable banner.</summary>
    private const int UnreachableFailureThreshold = 3;

    public DashboardViewModel(ServiceClient client, NavigationService nav)
    {
        _client = client;
        _nav    = nav;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += async (_, _) =>
        {
            TickCountdown();
            if (IsActive)
                await RefreshScreenTimeAsync();
        };
        _countdownTimer.Start();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();
    }

    private void TickCountdown()
    {
        if (SessionStatus != SessionStatus.Active || !_deadlineUtc.HasValue) return;

        var remaining = _deadlineUtc.Value - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            TimeRemaining = "00:00:00";
            if (!_deadlineEndTriggered && !IsEndingSession)
                _ = BeginSessionEndingAsync(isDeadlineEnd: true);
        }
        else
        {
            TimeRemaining = remaining.ToString(@"hh\:mm\:ss");
        }
    }

    /// <summary>Call after starting a session so the dashboard shows active state immediately.</summary>
    public void PrepareForActiveSession(SessionMode mode, DateTime deadlineLocal)
    {
        SessionStatus      = SessionStatus.Active;
        SessionMode        = mode;
        IsStrictMode       = mode == SessionMode.Strict;
        IsServiceReachable = true;
        _deadlineUtc         = deadlineLocal.ToUniversalTime();
        _deadlineEndTriggered = false;
        DeadlineText         = deadlineLocal.ToString("ddd, MMM d 'at' h:mm tt");
        TickCountdown();
        NotifySessionVisibility();
        OnPropertyChanged(nameof(IsServiceUnreachable));
    }

    public async Task RefreshAsync()
    {
        if (_refreshing || IsEndingSession) return;
        _refreshing = true;
        try { await RefreshCoreAsync(); }
        finally { _refreshing = false; }
    }

    private void NotifySessionVisibility()
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanEndEarly));
        OnPropertyChanged(nameof(ShowNewSessionButton));
        OnPropertyChanged(nameof(ShowActiveSession));
    }

    private async Task RefreshCoreAsync()
    {
        var info = await _client.GetSessionInfoAsync();
        if (info is null)
        {
            // Under load (BlockerStub, IFEO launches) a single timeout is common; probe with a lighter call.
            if (await _client.GetStatusAsync() is not null)
                MarkServiceReachable();
            else
                MarkServiceUnreachableIfPersistent();

            if (!_deadlineUtc.HasValue && _sessionInfoFailureStreak >= UnreachableFailureThreshold)
            {
                SessionStatus = SessionStatus.Idle;
                NotifySessionVisibility();
            }
            return;
        }

        MarkServiceReachable();
        var wasActive = SessionStatus == SessionStatus.Active;
        SessionStatus = info.Status;
        SessionMode   = info.Mode;
        IsStrictMode  = info.Mode == SessionMode.Strict;
        _deadlineUtc  = info.DeadlineUtc;
        BlockedApps   = info.BlockedApps ?? new();
        BlockedSites  = info.BlockedSites ?? new();

        NotifySessionVisibility();
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ModeColor));
        OnPropertyChanged(nameof(HasSessionBlocks));

        if (info.Status == SessionStatus.Active && info.DeadlineUtc.HasValue)
        {
            DeadlineText = info.DeadlineUtc.Value.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt");
            TickCountdown();
            await RefreshScreenTimeAsync();
        }
        else
        {
            if (wasActive && !IsEndingSession)
                _ = BeginSessionEndingAsync(isDeadlineEnd: true);
            else if (!IsEndingSession)
            {
                TimeRemaining = "--:--:--";
                DeadlineText  = string.Empty;
                ClearScreenTimeDisplay();
            }
        }
    }

    private async Task RefreshScreenTimeAsync()
    {
        var status = await _client.GetScreenTimeStatusAsync();
        if (status is null) return;

        MarkServiceReachable();

        ShowDailyLimit   = status.Enabled;
        IsDailyLockedOut = status.IsLockedOutForDay;
        HasAppLimits     = status.AppStatuses.Count > 0;

        if (status.Enabled)
        {
            var used     = TimeSpan.FromSeconds(status.TotalSecondsUsedToday);
            var limit    = TimeSpan.FromMinutes(status.DailyLimitMinutes);
            int limitSec = status.DailyLimitMinutes * 60;
            DailyUsageText     = FormatTime(used);
            DailyLimitLabel    = $"of {FormatTime(limit)} this session";
            DailyProgress      = limitSec > 0
                ? Math.Min(1.0, status.TotalSecondsUsedToday / (double)limitSec)
                : 0;

            DailyHasCustomSchedule            = status.DailyHasCustomSchedule;
            DailyScheduleLabel                = status.DailyScheduleLabel ?? string.Empty;
            DailyScheduleActiveNow            = status.DailyScheduleActiveNow;
            DailyScheduleWindowEndedForToday  = status.DailyScheduleWindowEndedForToday;

            if (status.DailyScheduleActiveNow)
            {
                DailyRemainingText = status.IsLockedOutForDay
                    ? "Limit reached"
                    : FormatRemaining(limitSec - status.TotalSecondsUsedToday);
                DailyScheduleStatusText = DailyHasCustomSchedule
                    ? "Daily limit tracking active now"
                    : string.Empty;
            }
            else
            {
                DailyRemainingText = status.DailyScheduleWindowEndedForToday
                    ? "Not in effect — today's window ended"
                    : FormatDailyRemainingPaused(status.DailyScheduleResumesAtLocal);
                DailyScheduleStatusText = status.DailyScheduleWindowEndedForToday
                    ? "Daily limit not in effect — today's window has ended"
                    : FormatDailyScheduleInactive(status.DailyScheduleResumesAtLocal);
            }
        }
        else
        {
            DailyUsageText     = string.Empty;
            DailyLimitLabel    = string.Empty;
            DailyRemainingText = string.Empty;
            DailyProgress      = 0;
            DailyHasCustomSchedule           = false;
            DailyScheduleLabel               = string.Empty;
            DailyScheduleStatusText          = string.Empty;
            DailyScheduleWindowEndedForToday = false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var appStatus in status.AppStatuses)
        {
            seen.Add(appStatus.ExeName);
            if (!_appUsageByExe.TryGetValue(appStatus.ExeName, out var item))
            {
                item = new AppUsageDisplayItem(appStatus.ExeName, appStatus.DisplayName);
                _appUsageByExe[appStatus.ExeName] = item;
                AppUsages.Add(item);
            }
            item.Update(appStatus);
        }

        for (int i = AppUsages.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(AppUsages[i].ExeName))
            {
                _appUsageByExe.Remove(AppUsages[i].ExeName);
                AppUsages.RemoveAt(i);
            }
        }
    }

    private void MarkServiceReachable()
    {
        _sessionInfoFailureStreak = 0;
        if (IsServiceReachable) return;
        IsServiceReachable = true;
        OnPropertyChanged(nameof(IsServiceUnreachable));
    }

    private void MarkServiceUnreachableIfPersistent()
    {
        _sessionInfoFailureStreak++;
        if (_sessionInfoFailureStreak < UnreachableFailureThreshold)
            return;
        if (!IsServiceReachable) return;
        IsServiceReachable = false;
        OnPropertyChanged(nameof(IsServiceUnreachable));
    }

    private void ClearScreenTimeDisplay()
    {
        ShowDailyLimit       = false;
        HasAppLimits         = false;
        IsDailyLockedOut     = false;
        DailyUsageText       = string.Empty;
        DailyLimitLabel      = string.Empty;
        DailyRemainingText   = string.Empty;
        DailyProgress        = 0;
        DailyScheduleLabel               = string.Empty;
        DailyScheduleStatusText          = string.Empty;
        DailyHasCustomSchedule           = false;
        DailyScheduleWindowEndedForToday = false;
        AppUsages.Clear();
        _appUsageByExe.Clear();
        OnPropertyChanged(nameof(HasScreenTime));
    }

    [RelayCommand]
    private void StartNew() => _nav.NavigateTo(new SetupPage());

    [RelayCommand]
    private void OpenSettings() => _nav.NavigateTo(new Views.SettingsPage());

    [RelayCommand(CanExecute = nameof(CanEndEarly))]
    private async Task EndSessionAsync()
    {
        if (!CanEndEarly) return;

        var confirm = System.Windows.MessageBox.Show(
            "End this focus session early?\n\n" +
            "App and website blocks will be removed and screen time tracking for this session will stop.",
            "End Session Early",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        await BeginSessionEndingAsync(isDeadlineEnd: false, requestEndFromService: true);
    }

    private async Task BeginSessionEndingAsync(bool isDeadlineEnd, bool requestEndFromService = false)
    {
        if (IsEndingSession)
            return;

        if (isDeadlineEnd)
            _deadlineEndTriggered = true;

        EndingSessionHeadline = isDeadlineEnd ? "Session complete" : "Ending session…";
        EndingSessionDetail   = isDeadlineEnd
            ? "Your focus session has ended. Removing blocks and restoring access…"
            : "Removing blocks and restoring access. This may take a few seconds.";

        IsEndingSession = true;
        EndSessionCommand.NotifyCanExecuteChanged();
        _pollTimer.Stop();
        _countdownTimer.Stop();

        try
        {
            if (requestEndFromService)
                await RequestEndSessionUntilAcknowledgedAsync();
            await WaitForSessionIdleAsync();
        }
        finally
        {
            IsEndingSession = false;
            _deadlineEndTriggered = false;
            EndSessionCommand.NotifyCanExecuteChanged();
            _pollTimer.Start();
            _countdownTimer.Start();
        }
    }

    private async Task RequestEndSessionUntilAcknowledgedAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var result = await _client.EndSessionAsync();
            if (result?.Success == true)
                return;
            if (result?.ErrorMessage?.Contains("No active session", StringComparison.OrdinalIgnoreCase) == true)
                return;

            await Task.Delay(400);
        }
    }

    private async Task WaitForSessionIdleAsync()
    {
        var timeout = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < timeout)
        {
            var info = await _client.GetSessionInfoAsync();
            if (info is null)
            {
                await Task.Delay(400);
                continue;
            }

            IsServiceReachable = true;
            OnPropertyChanged(nameof(IsServiceUnreachable));

            if (info.Status == SessionStatus.Idle)
            {
                ApplyIdleFromService(info);
                return;
            }

            // Session still active — retry end once in case the first IPC was lost.
            await _client.EndSessionAsync();
            await Task.Delay(400);
        }

        // Timed out talking to the service; still move UI to idle locally.
        ApplyIdleFromService(null);
    }

    private void ApplyIdleFromService(SessionInfoResponse? info)
    {
        SessionStatus = SessionStatus.Idle;
        SessionMode   = SessionMode.None;
        IsStrictMode  = false;
        _deadlineUtc  = null;
        TimeRemaining = "--:--:--";
        DeadlineText  = string.Empty;
        BlockedApps   = info?.BlockedApps ?? new();
        BlockedSites  = info?.BlockedSites ?? new();
        ClearScreenTimeDisplay();
        NotifySessionVisibility();
        OnPropertyChanged(nameof(ShowNewSessionButton));
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ModeColor));
        OnPropertyChanged(nameof(HasSessionBlocks));
    }

    public void StopPolling()
    {
        _countdownTimer.Stop();
        _pollTimer.Stop();
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return t.Minutes > 0 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalHours}h";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m";
        return $"{t.Seconds}s";
    }

    private static string FormatDailyRemainingPaused(DateTime? resumesAtLocal)
    {
        if (resumesAtLocal is not { } at)
            return "Outside schedule";

        if (at.Date == DateTime.Today)
            return $"Not active — resumes {at:h:mm tt}";

        return $"Not active — resumes {at:ddd h:mm tt}";
    }

    private static string FormatDailyScheduleInactive(DateTime? resumesAtLocal)
    {
        if (resumesAtLocal is not { } at)
            return "Daily limit not active — outside scheduled hours";

        if (at.Date == DateTime.Today)
            return $"Daily limit not active — resumes at {at:h:mm tt}";

        return $"Daily limit not active — resumes {at:ddd} at {at:h:mm tt}";
    }

    private static string FormatRemaining(int secondsRemaining)
    {
        if (secondsRemaining <= 0) return "0s left";
        var t = TimeSpan.FromSeconds(secondsRemaining);
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m left";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m {t.Seconds}s left";
        return $"{t.Seconds}s left";
    }
}
