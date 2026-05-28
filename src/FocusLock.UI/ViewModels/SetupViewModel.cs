using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Blocking;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.UI.Services;
using FocusLock.UI.Views;

namespace FocusLock.UI.ViewModels;

public partial class SetupViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly NavigationService _nav;

    [ObservableProperty] private SessionMode _selectedMode = SessionMode.Regular;
    [ObservableProperty] private string _newAppPath = string.Empty;
    [ObservableProperty] private string _newSiteDomain = string.Empty;
    [ObservableProperty] private bool _strictConsentGiven;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    // Deadline split into date + 12-hour time + AM/PM for the UI
    [ObservableProperty] private DateTime _deadlineDate = DateTime.Today.AddDays(1);
    [ObservableProperty] private string _deadlineHour = "5";
    [ObservableProperty] private string _deadlineMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAmPmAm))]
    [NotifyPropertyChangedFor(nameof(IsAmPmPm))]
    private string _deadlineAmPm = "PM";

    public bool IsAmPmAm
    {
        get => DeadlineAmPm == "AM";
        set { if (value) DeadlineAmPm = "AM"; }
    }
    public bool IsAmPmPm
    {
        get => DeadlineAmPm == "PM";
        set { if (value) DeadlineAmPm = "PM"; }
    }

    public bool IsRegularMode
    {
        get => SelectedMode == SessionMode.Regular;
        set { if (value) { SelectedMode = SessionMode.Regular; OnPropertyChanged(nameof(IsStrictMode)); } }
    }
    public bool IsStrictMode
    {
        get => SelectedMode == SessionMode.Strict;
        set { if (value) { SelectedMode = SessionMode.Strict; OnPropertyChanged(nameof(IsRegularMode)); } }
    }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsNotBusy => !IsBusy;

    public ObservableCollection<BlockedApp> BlockedApps { get; } = new();
    public ObservableCollection<BlockedSite> BlockedSites { get; } = new();

    public SetupViewModel(ServiceClient client, NavigationService nav)
    {
        _client = client;
        _nav = nav;
    }

    [RelayCommand]
    private void BrowseForApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe",
            Title = "Select application to block"
        };
        if (dlg.ShowDialog() == true)
        {
            var exeName = Path.GetFileName(dlg.FileName);
            if (SystemProcessList.IsSystemExe(exeName))
            {
                ErrorMessage = $"{exeName} is a system process and cannot be blocked.";
                return;
            }
            var display = FileVersionInfo.GetVersionInfo(dlg.FileName).ProductName
                ?? Path.GetFileNameWithoutExtension(dlg.FileName);
            if (!BlockedApps.Any(a => a.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
                BlockedApps.Add(new BlockedApp(display ?? exeName, exeName));
            ErrorMessage = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveApp(BlockedApp app) => BlockedApps.Remove(app);

    [RelayCommand]
    private void AddSite()
    {
        var domain = NewSiteDomain.Trim().ToLowerInvariant()
            .Replace("https://", "").Replace("http://", "").Replace("www.", "").TrimEnd('/');
        if (string.IsNullOrEmpty(domain)) return;
        if (!BlockedSites.Any(s => s.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            BlockedSites.Add(new BlockedSite(domain));
        NewSiteDomain = string.Empty;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void RemoveSite(BlockedSite site) => BlockedSites.Remove(site);

    private DateTime ComputedDeadline()
    {
        int.TryParse(DeadlineHour, out int h);
        int.TryParse(DeadlineMinute, out int m);
        h = Math.Clamp(h, 1, 12);
        m = Math.Clamp(m, 0, 59);
        if (DeadlineAmPm == "PM" && h != 12) h += 12;
        else if (DeadlineAmPm == "AM" && h == 12) h = 0;
        return DeadlineDate.Date + new TimeSpan(h, m, 0);
    }

    [RelayCommand]
    private async Task StartSessionAsync()
    {
        ErrorMessage = string.Empty;
        var Deadline = ComputedDeadline();
        if (Deadline <= DateTime.Now.AddMinutes(5))
        {
            ErrorMessage = "Deadline must be at least 5 minutes in the future.";
            return;
        }
        if (BlockedApps.Count == 0 && BlockedSites.Count == 0)
        {
            ErrorMessage = "Add at least one blocked app or website.";
            return;
        }
        if (SelectedMode == SessionMode.Strict && !StrictConsentGiven)
        {
            ErrorMessage = "You must confirm consent to use Strict mode.";
            return;
        }

        IsBusy = true;
        try
        {
            var req = new StartSessionRequest(
                SelectedMode,
                ComputedDeadline().ToUniversalTime(),
                BlockedApps.ToList(),
                BlockedSites.ToList(),
                StrictConsentGiven);

            var result = await _client.StartSessionAsync(req);
            if (result?.Success == true)
                _nav.NavigateTo(new DashboardPage());
            else
                ErrorMessage = result?.ErrorMessage ?? "Failed to start session.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _nav.NavigateTo(new DashboardPage());
}
