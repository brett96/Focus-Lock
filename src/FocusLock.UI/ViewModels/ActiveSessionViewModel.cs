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

    [ObservableProperty] private SessionMode _mode;
    [ObservableProperty] private string _timeRemaining = "--:--:--";
    [ObservableProperty] private string _deadlineText = string.Empty;
    [ObservableProperty] private List<BlockedApp> _blockedApps = new();
    [ObservableProperty] private List<BlockedSite> _blockedSites = new();
    [ObservableProperty] private bool _canEndEarly;
    public bool IsStrictActive => !CanEndEarly && Mode == SessionMode.Strict;

    public ActiveSessionViewModel(ServiceClient client, NavigationService nav)
    {
        _client = client;
        _nav = nav;

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
            _nav.NavigateTo(new DashboardPage());
            return;
        }

        Mode = info.Mode;
        CanEndEarly = info.Mode == SessionMode.Regular;
        BlockedApps = info.BlockedApps ?? new();
        BlockedSites = info.BlockedSites ?? new();

        if (info.DeadlineUtc.HasValue)
        {
            var remaining = info.DeadlineUtc.Value - DateTime.UtcNow;
            TimeRemaining = remaining > TimeSpan.Zero
                ? remaining.ToString(@"hh\:mm\:ss")
                : "00:00:00";
            DeadlineText = info.DeadlineUtc.Value.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt");
        }
    }

    [RelayCommand]
    private async Task EndSessionAsync()
    {
        if (!CanEndEarly) return;
        var result = await _client.EndSessionAsync();
        if (result?.Success == true)
        {
            _timer.Stop();
            _nav.NavigateTo(new DashboardPage());
        }
    }
}
