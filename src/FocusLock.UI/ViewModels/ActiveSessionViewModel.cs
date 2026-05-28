using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.UI.Services;
using FocusLock.UI.Views;

namespace FocusLock.UI.ViewModels;

public partial class ActiveSessionViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly NavigationService _nav;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, AppUsageDisplayItem> _appUsageByExe = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private SessionMode _mode;
    [ObservableProperty] private string _timeRemaining = "--:--:--";
    [ObservableProperty] private string _deadlineText = string.Empty;
    [ObservableProperty] private List<BlockedApp> _blockedApps = new();
    [ObservableProperty] private List<BlockedSite> _blockedSites = new();
    [ObservableProperty] private bool _canEndEarly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenTime))]
    private bool _showDailyLimit;

    [ObservableProperty] private string _dailyUsageText = string.Empty;
    [ObservableProperty] private string _dailyLimitLabel = string.Empty;
    [ObservableProperty] private double _dailyProgress;
    [ObservableProperty] private bool _isDailyLockedOut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScreenTime))]
    private bool _hasAppLimits;

    public ObservableCollection<AppUsageDisplayItem> AppUsages { get; } = new();

    public bool HasScreenTime => ShowDailyLimit || HasAppLimits;
    public bool IsStrictActive => !CanEndEarly && Mode == SessionMode.Strict;

    public ActiveSessionViewModel(ServiceClient client, NavigationService nav)
    {
        _client = client;
        _nav    = nav;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    public async Task RefreshAsync()
    {
        var info = await _client.GetSessionInfoAsync();
        if (info is null) return;

        if (info.Status == SessionStatus.Idle)
        {
            _timer.Stop();
            _nav.NavigateToDashboard();
            return;
        }

        Mode         = info.Mode;
        CanEndEarly  = info.Mode == SessionMode.Regular;
        BlockedApps  = info.BlockedApps ?? new();
        BlockedSites = info.BlockedSites ?? new();

        if (info.DeadlineUtc.HasValue)
        {
            var remaining = info.DeadlineUtc.Value - DateTime.UtcNow;
            TimeRemaining = remaining > TimeSpan.Zero
                ? remaining.ToString(@"hh\:mm\:ss")
                : "00:00:00";
            DeadlineText = info.DeadlineUtc.Value.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt");
        }

        await RefreshScreenTimeAsync();
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
            var used  = TimeSpan.FromSeconds(status.TotalSecondsUsedToday);
            var limit = TimeSpan.FromMinutes(status.DailyLimitMinutes);
            DailyUsageText  = FormatTime(used);
            DailyLimitLabel = $"of {FormatTime(limit)} today";
            DailyProgress   = status.DailyLimitMinutes > 0
                ? Math.Min(1.0, status.TotalSecondsUsedToday / (double)(status.DailyLimitMinutes * 60))
                : 0;
        }
        else
        {
            DailyUsageText  = string.Empty;
            DailyLimitLabel = string.Empty;
            DailyProgress   = 0;
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

    [RelayCommand]
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

        var result = await _client.EndSessionAsync();
        if (result?.Success == true)
        {
            _timer.Stop();
            _nav.NavigateToDashboard();
        }
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return t.Minutes > 0 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalHours}h";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m";
        return $"{t.Seconds}s";
    }
}
