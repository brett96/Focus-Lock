using System.Collections.ObjectModel;
using System.ComponentModel;
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
using Microsoft.Win32;

namespace FocusLock.UI.ViewModels;

public partial class SetupViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly NavigationService _nav;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegularMode))]
    [NotifyPropertyChangedFor(nameof(IsStrictMode))]
    private SessionMode _selectedMode = SessionMode.Regular;

    [ObservableProperty] private string _newSiteDomain = string.Empty;
    [ObservableProperty] private bool   _strictConsentGiven;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    // Deadline split into date + 12-hour time + AM/PM for the UI
    [ObservableProperty] private DateTime _deadlineDate   = DateTime.Today.AddDays(1);
    [ObservableProperty] private string   _deadlineHour   = "5";
    [ObservableProperty] private string   _deadlineMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAmPmAm))]
    [NotifyPropertyChangedFor(nameof(IsAmPmPm))]
    private string _deadlineAmPm = "PM";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredApps))]
    [NotifyPropertyChangedFor(nameof(HasNoFilteredApps))]
    private string _appSearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFilteredApps))]
    private bool _isLoadingApps = true;

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
        set { if (value) SelectedMode = SessionMode.Regular; }
    }
    public bool IsStrictMode
    {
        get => SelectedMode == SessionMode.Strict;
        set { if (value) SelectedMode = SessionMode.Strict; }
    }

    public bool HasError       => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsNotBusy      => !IsBusy;
    public bool HasNoFilteredApps => !IsLoadingApps && !FilteredApps.Any();

    public string AppDropdownLabel => BlockedApps.Count == 0
        ? "Select applications to block..."
        : $"{BlockedApps.Count} application{(BlockedApps.Count == 1 ? "" : "s")} selected";

    public IEnumerable<SelectableApp> FilteredApps =>
        string.IsNullOrWhiteSpace(AppSearchText)
            ? AvailableApps
            : AvailableApps.Where(a =>
                a.DisplayName.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase));

    // Populated from the App Paths registry on a background thread.
    public ObservableCollection<SelectableApp> AvailableApps { get; } = new();
    public ObservableCollection<BlockedApp>    BlockedApps   { get; } = new();
    public ObservableCollection<BlockedSite>   BlockedSites  { get; } = new();

    public SetupViewModel(ServiceClient client, NavigationService nav)
    {
        _client = client;
        _nav    = nav;

        BlockedApps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AppDropdownLabel));
        _ = LoadAvailableAppsAsync();
    }

    private async Task LoadAvailableAppsAsync()
    {
        var apps = await InstalledAppsLoader.LoadAsync();
        foreach (var app in apps)
        {
            app.PropertyChanged += OnSelectableAppChanged;
            AvailableApps.Add(app);
        }
        IsLoadingApps = false;
        OnPropertyChanged(nameof(FilteredApps));
        OnPropertyChanged(nameof(HasNoFilteredApps));
    }

    private void OnSelectableAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableApp.IsSelected) || sender is not SelectableApp app) return;
        if (app.IsSelected)
        {
            if (!BlockedApps.Any(a => string.Equals(a.ExeName, app.ExeName, StringComparison.OrdinalIgnoreCase)))
                BlockedApps.Add(new BlockedApp(app.DisplayName, app.ExeName));
        }
        else
        {
            var existing = BlockedApps.FirstOrDefault(a =>
                string.Equals(a.ExeName, app.ExeName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                BlockedApps.Remove(existing);
        }
    }


    [RelayCommand]
    private void RemoveApp(BlockedApp app)
    {
        // Uncheck in the dropdown (handler removes from BlockedApps).
        var selectable = AvailableApps.FirstOrDefault(a =>
            string.Equals(a.ExeName, app.ExeName, StringComparison.OrdinalIgnoreCase));
        if (selectable is not null)
            selectable.IsSelected = false;
        else
            BlockedApps.Remove(app); // manually browsed app not in AvailableApps
    }

    [RelayCommand]
    private void BrowseForApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe",
            Title  = "Select application to block"
        };
        if (dlg.ShowDialog() != true) return;

        var exeName = Path.GetFileName(dlg.FileName);
        if (SystemProcessList.IsSystemExe(exeName))
        {
            ErrorMessage = $"{exeName} is a system process and cannot be blocked.";
            return;
        }

        // If it's in AvailableApps, just check it; otherwise add directly to BlockedApps.
        var selectable = AvailableApps.FirstOrDefault(a =>
            string.Equals(a.ExeName, exeName, StringComparison.OrdinalIgnoreCase));
        if (selectable is not null)
        {
            selectable.IsSelected = true;
        }
        else
        {
            var display = FileVersionInfo.GetVersionInfo(dlg.FileName).ProductName
                ?? Path.GetFileNameWithoutExtension(dlg.FileName);
            if (!BlockedApps.Any(a => a.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
                BlockedApps.Add(new BlockedApp(display ?? exeName, exeName));
        }
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void AddSite()
    {
        var domain = NewSiteDomain.Trim().ToLowerInvariant()
            .Replace("https://", "").Replace("http://", "").Replace("www.", "").TrimEnd('/');
        if (string.IsNullOrEmpty(domain)) return;
        if (!BlockedSites.Any(s => s.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            BlockedSites.Add(new BlockedSite(domain));
        NewSiteDomain = string.Empty;
        ErrorMessage  = string.Empty;
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
        var deadline = ComputedDeadline();
        if (deadline <= DateTime.Now.AddMinutes(5))
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
