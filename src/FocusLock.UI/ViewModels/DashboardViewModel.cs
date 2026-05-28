using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.UI.Services;
using FocusLock.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly NavigationService _nav;
    private readonly DispatcherTimer _timer;

    [ObservableProperty] private SessionStatus _sessionStatus = SessionStatus.Idle;
    [ObservableProperty] private SessionMode _sessionMode = SessionMode.None;
    [ObservableProperty] private string _timeRemaining = "--:--:--";
    [ObservableProperty] private string _deadlineText = string.Empty;
    [ObservableProperty] private bool _isServiceReachable = true;
    [ObservableProperty] private bool _isStrictMode;

    public bool IsIdle => SessionStatus == SessionStatus.Idle;
    public bool IsActive => SessionStatus == SessionStatus.Active;
    public bool CanEndEarly => IsActive && !IsStrictMode;
    public bool IsServiceUnreachable => !IsServiceReachable;
    public string ModeLabel => SessionMode == SessionMode.Strict ? "STRICT MODE" : "REGULAR MODE";
    public string ModeColor => SessionMode == SessionMode.Strict ? "#F38BA8" : "#89B4FA";

    private DateTime? _deadlineUtc;
    private bool _refreshing;

    public DashboardViewModel(ServiceClient client, NavigationService nav)
    {
        _client = client;
        _nav = nav;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await RefreshCoreAsync(); }
        finally { _refreshing = false; }
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
        SessionMode = info.Mode;
        IsStrictMode = info.Mode == SessionMode.Strict;
        _deadlineUtc = info.DeadlineUtc;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanEndEarly));
        OnPropertyChanged(nameof(IsServiceUnreachable));
        OnPropertyChanged(nameof(ModeLabel));
        OnPropertyChanged(nameof(ModeColor));

        if (info.Status == SessionStatus.Active && info.DeadlineUtc.HasValue)
        {
            var remaining = info.DeadlineUtc.Value - DateTime.UtcNow;
            TimeRemaining = remaining > TimeSpan.Zero
                ? remaining.ToString(@"hh\:mm\:ss")
                : "00:00:00";
            DeadlineText = info.DeadlineUtc.Value.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt");
        }
        else
        {
            TimeRemaining = "--:--:--";
            DeadlineText = string.Empty;
        }
    }

    [RelayCommand]
    private void StartNew() => _nav.NavigateTo(new SetupPage());

    [RelayCommand]
    private void OpenSettings() => _nav.NavigateTo(new Views.SettingsPage());

    [RelayCommand]
    private async Task EndSessionAsync()
    {
        if (SessionMode == SessionMode.Strict) return;
        _timer.Stop();
        try
        {
            var result = await _client.EndSessionAsync();
            if (result?.Success == true)
                await RefreshAsync();
        }
        finally
        {
            _timer.Start();
        }
    }

    public void StopPolling() => _timer.Stop();
}
