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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenTime))]
    private bool _hasAppLimits;

    public ObservableCollection<AppUsageDisplayItem> AppUsages { get; } = new();

    public bool IsIdle => SessionStatus == SessionStatus.Idle;
    public bool IsActive => SessionStatus == SessionStatus.Active;
    public bool CanEndEarly => IsActive && !IsStrictMode;
    public bool IsServiceUnreachable => !IsServiceReachable;
    public bool HasSessionBlocks => BlockedApps.Count > 0 || BlockedSites.Count > 0;
    public bool HasScreenTime => ShowDailyLimit || HasAppLimits;
    public string ModeLabel => SessionMode == SessionMode.Strict ? "STRICT MODE" : "REGULAR MODE";
    public string ModeColor => SessionMode == SessionMode.Strict ? "#F38BA8" : "#89B4FA";

    private DateTime? _deadlineUtc;
    private bool _refreshing;

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
            _ = RefreshAsync();
        }
        else
        {
            TimeRemaining = remaining.ToString(@"hh\:mm\:ss");
        }
    }

    /// <summary>Call after starting a session so the dashboard shows active state immediately.</summary>
    public void PrepareForActiveSession(SessionMode mode)
    {
        SessionStatus = SessionStatus.Active;
        SessionMode   = mode;
        IsStrictMode  = mode == SessionMode.Strict;
        NotifySessionVisibility();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await RefreshCoreAsync(); }
        finally { _refreshing = false; }
    }

    private void NotifySessionVisibility()
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanEndEarly));
    }

    private async Task RefreshCoreAsync()
    {
        var info = await _client.GetSessionInfoAsync();
        if (info is null)
        {
            IsServiceReachable = false;
            return;
        }

        IsServiceReachable = true;
        SessionStatus = info.Status;
        SessionMode   = info.Mode;
        IsStrictMode  = info.Mode == SessionMode.Strict;
        _deadlineUtc  = info.DeadlineUtc;
        BlockedApps   = info.BlockedApps ?? new();
        BlockedSites  = info.BlockedSites ?? new();

        NotifySessionVisibility();
        OnPropertyChanged(nameof(IsServiceUnreachable));
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
            TimeRemaining = "--:--:--";
            DeadlineText  = string.Empty;
            ClearScreenTimeDisplay();
        }
    }

    private async Task RefreshScreenTimeAsync()
    {
        var status = await _client.GetScreenTimeStatusAsync();
        if (status is null) return;

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
            DailyRemainingText = status.IsLockedOutForDay
                ? "Limit reached"
                : FormatRemaining(limitSec - status.TotalSecondsUsedToday);
        }
        else
        {
            DailyUsageText     = string.Empty;
            DailyLimitLabel    = string.Empty;
            DailyRemainingText = string.Empty;
            DailyProgress      = 0;
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

    private void ClearScreenTimeDisplay()
    {
        ShowDailyLimit       = false;
        HasAppLimits         = false;
        IsDailyLockedOut     = false;
        DailyUsageText       = string.Empty;
        DailyLimitLabel      = string.Empty;
        DailyRemainingText   = string.Empty;
        DailyProgress        = 0;
        AppUsages.Clear();
        _appUsageByExe.Clear();
        OnPropertyChanged(nameof(HasScreenTime));
    }

    [RelayCommand]
    private void StartNew() => _nav.NavigateTo(new SetupPage());

    [RelayCommand]
    private void OpenSettings() => _nav.NavigateTo(new Views.SettingsPage());

    [RelayCommand]
    private async Task EndSessionAsync()
    {
        if (SessionMode == SessionMode.Strict) return;

        var confirm = System.Windows.MessageBox.Show(
            "End this focus session early?\n\n" +
            "App and website blocks will be removed and screen time tracking for this session will stop.",
            "End Session Early",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        _pollTimer.Stop();
        try
        {
            var result = await _client.EndSessionAsync();
            if (result?.Success == true)
                await RefreshAsync();
        }
        finally
        {
            _pollTimer.Start();
        }
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
