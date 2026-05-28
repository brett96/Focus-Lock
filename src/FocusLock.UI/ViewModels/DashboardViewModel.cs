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

    // Ticks every second to update the countdown display — no I/O, just arithmetic.
    private readonly DispatcherTimer _countdownTimer;

    // Polls the service every 5 seconds to sync session state (active/idle, mode, deadline).
    private readonly DispatcherTimer _pollTimer;

    [ObservableProperty] private SessionStatus _sessionStatus = SessionStatus.Idle;
    [ObservableProperty] private SessionMode _sessionMode = SessionMode.None;
    [ObservableProperty] private string _timeRemaining = "--:--:--";
    [ObservableProperty] private string _deadlineText = string.Empty;
    [ObservableProperty] private bool _isServiceReachable = true;
    [ObservableProperty] private bool _isStrictMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(CanEndEarly))]
    private bool _isLoading = true;

    public bool IsIdle => !IsLoading && SessionStatus == SessionStatus.Idle;
    public bool IsActive => !IsLoading && SessionStatus == SessionStatus.Active;
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

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += (_, _) => TickCountdown();
        _countdownTimer.Start();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();
    }

    // Called every second from _countdownTimer. Pure local computation — no pipe call.
    private void TickCountdown()
    {
        if (SessionStatus != SessionStatus.Active || !_deadlineUtc.HasValue) return;

        var remaining = _deadlineUtc.Value - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            TimeRemaining = "00:00:00";
            // Deadline passed locally — trigger an immediate poll so the UI transitions
            // to the idle state as soon as the service confirms the session is over.
            _ = RefreshAsync();
        }
        else
        {
            TimeRemaining = remaining.ToString(@"hh\:mm\:ss");
        }
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
        IsLoading = false;
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
            DeadlineText = info.DeadlineUtc.Value.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt");
            // Sync the display immediately after a poll so there is no visible jump.
            TickCountdown();
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
}
