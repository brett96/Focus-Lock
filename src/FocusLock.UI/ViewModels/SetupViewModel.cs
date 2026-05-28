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
using FocusLock.Core.ScreenTime;
using FocusLock.UI.Services;
using FocusLock.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace FocusLock.UI.ViewModels;

public partial class SetupViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly NavigationService _nav;

    // ── Session mode ──────────────────────────────────────────────────────────

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

    // ── Deadline ──────────────────────────────────────────────────────────────

    [ObservableProperty] private DateTime _deadlineDate;
    [ObservableProperty] private string   _deadlineHour   = "12";
    [ObservableProperty] private string   _deadlineMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAmPmAm))]
    [NotifyPropertyChangedFor(nameof(IsAmPmPm))]
    private string _deadlineAmPm = "AM";

    // ── App picker ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredApps))]
    [NotifyPropertyChangedFor(nameof(HasNoFilteredApps))]
    [NotifyPropertyChangedFor(nameof(StFilteredDraftApps))]
    [NotifyPropertyChangedFor(nameof(StHasNoDraftApps))]
    private string _appSearchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFilteredApps))]
    [NotifyPropertyChangedFor(nameof(StHasNoDraftApps))]
    private bool _isLoadingApps = true;

    public bool IsAmPmAm { get => DeadlineAmPm == "AM"; set { if (value) DeadlineAmPm = "AM"; } }
    public bool IsAmPmPm { get => DeadlineAmPm == "PM"; set { if (value) DeadlineAmPm = "PM"; } }

    public bool IsRegularMode { get => SelectedMode == SessionMode.Regular; set { if (value) SelectedMode = SessionMode.Regular; } }
    public bool IsStrictMode  { get => SelectedMode == SessionMode.Strict;  set { if (value) SelectedMode = SessionMode.Strict; } }

    public DateTime TodayDate => DateTime.Today;
    public DateTime MaxDeadlineDate => DateTime.Today.AddYears(1);

    public bool HasError        => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsNotBusy       => !IsBusy;
    public bool HasScreenTimeLimits => StDailyLimits.Count > 0 || StAppLimits.Count > 0;
    public bool HasNoFilteredApps => !IsLoadingApps && !FilteredApps.Any();

    public string AppDropdownLabel => BlockedApps.Count == 0
        ? "Select applications to block..."
        : $"{BlockedApps.Count} application{(BlockedApps.Count == 1 ? "" : "s")} selected";

    public IEnumerable<SelectableApp> FilteredApps =>
        string.IsNullOrWhiteSpace(AppSearchText)
            ? AvailableApps
            : AvailableApps.Where(a => a.DisplayName.Contains(AppSearchText, StringComparison.OrdinalIgnoreCase));

    public ObservableCollection<SelectableApp> AvailableApps { get; } = new();
    public ObservableCollection<BlockedApp>    BlockedApps   { get; } = new();
    public ObservableCollection<BlockedSite>   BlockedSites  { get; } = new();
    public ObservableCollection<SelectableWebsiteCategory> WebsiteCategories { get; } = new();

    // ── Screen Time — daily limits list ───────────────────────────────────────

    public ObservableCollection<DailyTimeLimitViewModel> StDailyLimits { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StIsNotAddingDaily))]
    private bool _stIsAddingDailyLimit;

    public bool StIsNotAddingDaily => !StIsAddingDailyLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStDailyLimitAddError))]
    private string _stDailyLimitAddError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStAppLimitAddError))]
    private string _stAppLimitAddError = string.Empty;

    public bool HasStDailyLimitAddError => !string.IsNullOrEmpty(StDailyLimitAddError);
    public bool HasStAppLimitAddError   => !string.IsNullOrEmpty(StAppLimitAddError);

    [ObservableProperty] private string _stDraftDailyLimitHours = "2";
    [ObservableProperty] private string _stDraftDailyLimitMin   = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StShowDailyDraftSchedulePicker))]
    private bool _stDraftDailyUseCustomSchedule;

    public bool StShowDailyDraftSchedulePicker => StDraftDailyUseCustomSchedule;

    private DayOfWeekFlags _stDailyDraftDays = (DayOfWeekFlags)127;

    public bool StDailyDraftMon { get => GetDay(_stDailyDraftDays, DayOfWeekFlags.Monday);    set => SetDay(ref _stDailyDraftDays, DayOfWeekFlags.Monday,    value, nameof(StDailyDraftMon)); }
    public bool StDailyDraftTue { get => GetDay(_stDailyDraftDays, DayOfWeekFlags.Tuesday);   set => SetDay(ref _stDailyDraftDays, DayOfWeekFlags.Tuesday,   value, nameof(StDailyDraftTue)); }
    public bool StDailyDraftWed { get => GetDay(_stDailyDraftDays, DayOfWeekFlags.Wednesday); set => SetDay(ref _stDailyDraftDays, DayOfWeekFlags.Wednesday, value, nameof(StDailyDraftWed)); }
    public bool StDailyDraftThu { get => GetDay(_stDailyDraftDays, DayOfWeekFlags.Thursday);  set => SetDay(ref _stDailyDraftDays, DayOfWeekFlags.Thursday,  value, nameof(StDailyDraftThu)); }
    public bool StDailyDraftFri { get => GetDay(_stDailyDraftDays, DayOfWeekFlags.Friday);    set => SetDay(ref _stDailyDraftDays, DayOfWeekFlags.Friday,    value, nameof(StDailyDraftFri)); }
    public bool StDailyDraftSat { get => GetDay(_stDailyDraftDays, DayOfWeekFlags.Saturday);  set => SetDay(ref _stDailyDraftDays, DayOfWeekFlags.Saturday,  value, nameof(StDailyDraftSat)); }
    public bool StDailyDraftSun { get => GetDay(_stDailyDraftDays, DayOfWeekFlags.Sunday);    set => SetDay(ref _stDailyDraftDays, DayOfWeekFlags.Sunday,    value, nameof(StDailyDraftSun)); }

    [ObservableProperty] private string _stDailyDraftStartHour   = "9";
    [ObservableProperty] private string _stDailyDraftStartMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StDailyDraftStartIsAm))]
    [NotifyPropertyChangedFor(nameof(StDailyDraftStartIsPm))]
    private string _stDailyDraftStartAmPm = "AM";

    public bool StDailyDraftStartIsAm { get => StDailyDraftStartAmPm == "AM"; set { if (value) StDailyDraftStartAmPm = "AM"; } }
    public bool StDailyDraftStartIsPm { get => StDailyDraftStartAmPm == "PM"; set { if (value) StDailyDraftStartAmPm = "PM"; } }

    [ObservableProperty] private string _stDailyDraftEndHour   = "5";
    [ObservableProperty] private string _stDailyDraftEndMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StDailyDraftEndIsAm))]
    [NotifyPropertyChangedFor(nameof(StDailyDraftEndIsPm))]
    private string _stDailyDraftEndAmPm = "PM";

    public bool StDailyDraftEndIsAm { get => StDailyDraftEndAmPm == "AM"; set { if (value) StDailyDraftEndAmPm = "AM"; } }
    public bool StDailyDraftEndIsPm { get => StDailyDraftEndAmPm == "PM"; set { if (value) StDailyDraftEndAmPm = "PM"; } }

    // ── Screen Time — app limits list ─────────────────────────────────────────

    public ObservableCollection<AppTimeLimitViewModel> StAppLimits { get; } = new();

    // ── Screen Time — draft add-app form ─────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StIsNotAdding))]
    private bool _stIsAddingAppLimit;

    public bool StIsNotAdding => !StIsAddingAppLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StFilteredDraftApps))]
    [NotifyPropertyChangedFor(nameof(StHasNoDraftApps))]
    private string _stDraftAppSearch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StHasDraftExe))]
    private string _stDraftExeName = string.Empty;

    [ObservableProperty] private string _stDraftDisplayName = string.Empty;

    public bool StHasDraftExe => !string.IsNullOrEmpty(StDraftExeName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StIsDailyTotalDraft))]
    [NotifyPropertyChangedFor(nameof(StIsIntervalDraft))]
    private AppLimitType _stDraftLimitType = AppLimitType.DailyTotal;

    public bool StIsDailyTotalDraft { get => StDraftLimitType == AppLimitType.DailyTotal; set { if (value) StDraftLimitType = AppLimitType.DailyTotal; } }
    public bool StIsIntervalDraft   { get => StDraftLimitType == AppLimitType.Interval;   set { if (value) StDraftLimitType = AppLimitType.Interval; } }

    [ObservableProperty] private string _stDraftLimitHours     = "1";
    [ObservableProperty] private string _stDraftLimitMin       = "0";
    [ObservableProperty] private string _stDraftIntervalLimit  = "20";
    [ObservableProperty] private string _stDraftIntervalPeriod = "60";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StShowDraftSchedulePicker))]
    private bool _stDraftUseCustomSchedule;

    public bool StShowDraftSchedulePicker => StDraftUseCustomSchedule;

    private DayOfWeekFlags _stDraftDays = (DayOfWeekFlags)127;

    public bool StDraftMon { get => GetDay(_stDraftDays, DayOfWeekFlags.Monday);    set => SetDay(ref _stDraftDays, DayOfWeekFlags.Monday,    value, nameof(StDraftMon)); }
    public bool StDraftTue { get => GetDay(_stDraftDays, DayOfWeekFlags.Tuesday);   set => SetDay(ref _stDraftDays, DayOfWeekFlags.Tuesday,   value, nameof(StDraftTue)); }
    public bool StDraftWed { get => GetDay(_stDraftDays, DayOfWeekFlags.Wednesday); set => SetDay(ref _stDraftDays, DayOfWeekFlags.Wednesday, value, nameof(StDraftWed)); }
    public bool StDraftThu { get => GetDay(_stDraftDays, DayOfWeekFlags.Thursday);  set => SetDay(ref _stDraftDays, DayOfWeekFlags.Thursday,  value, nameof(StDraftThu)); }
    public bool StDraftFri { get => GetDay(_stDraftDays, DayOfWeekFlags.Friday);    set => SetDay(ref _stDraftDays, DayOfWeekFlags.Friday,    value, nameof(StDraftFri)); }
    public bool StDraftSat { get => GetDay(_stDraftDays, DayOfWeekFlags.Saturday);  set => SetDay(ref _stDraftDays, DayOfWeekFlags.Saturday,  value, nameof(StDraftSat)); }
    public bool StDraftSun { get => GetDay(_stDraftDays, DayOfWeekFlags.Sunday);    set => SetDay(ref _stDraftDays, DayOfWeekFlags.Sunday,    value, nameof(StDraftSun)); }

    [ObservableProperty] private string _stDraftStartHour   = "9";
    [ObservableProperty] private string _stDraftStartMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StDraftStartIsAm))]
    [NotifyPropertyChangedFor(nameof(StDraftStartIsPm))]
    private string _stDraftStartAmPm = "AM";

    public bool StDraftStartIsAm { get => StDraftStartAmPm == "AM"; set { if (value) StDraftStartAmPm = "AM"; } }
    public bool StDraftStartIsPm { get => StDraftStartAmPm == "PM"; set { if (value) StDraftStartAmPm = "PM"; } }

    [ObservableProperty] private string _stDraftEndHour   = "5";
    [ObservableProperty] private string _stDraftEndMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StDraftEndIsAm))]
    [NotifyPropertyChangedFor(nameof(StDraftEndIsPm))]
    private string _stDraftEndAmPm = "PM";

    public bool StDraftEndIsAm { get => StDraftEndAmPm == "AM"; set { if (value) StDraftEndAmPm = "AM"; } }
    public bool StDraftEndIsPm { get => StDraftEndAmPm == "PM"; set { if (value) StDraftEndAmPm = "PM"; } }

    [ObservableProperty] private bool _stIsDraftAppPopupOpen;

    public IEnumerable<SelectableApp> StFilteredDraftApps =>
        string.IsNullOrWhiteSpace(StDraftAppSearch)
            ? AvailableApps
            : AvailableApps.Where(a => a.DisplayName.Contains(StDraftAppSearch, StringComparison.OrdinalIgnoreCase));

    public bool StHasNoDraftApps => !IsLoadingApps && !StFilteredDraftApps.Any();

    // ── Constructor ───────────────────────────────────────────────────────────

    public SetupViewModel(ServiceClient client, NavigationService nav)
    {
        _client = client;
        _nav    = nav;

        ApplyDefaultDeadline();
        foreach (var (name, domains) in Core.Blocking.WebsiteCategories.All)
            WebsiteCategories.Add(new SelectableWebsiteCategory(name, domains, OnWebsiteCategoryToggled));

        BlockedApps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AppDropdownLabel));
        StDailyLimits.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasScreenTimeLimits));
        StAppLimits.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasScreenTimeLimits));
        _ = LoadAvailableAppsAsync();
        _ = LoadScreenTimeConfigAsync();
    }

    private void ApplyDefaultDeadline()
    {
        var deadline = DateTime.Now.AddHours(1);
        DeadlineDate = deadline.Date;
        int h24 = deadline.Hour;
        DeadlineAmPm = h24 < 12 ? "AM" : "PM";
        int h12 = h24 % 12;
        if (h12 == 0) h12 = 12;
        DeadlineHour   = h12.ToString();
        DeadlineMinute = deadline.Minute.ToString("00");
    }

    // ── Focus Lock app loading ────────────────────────────────────────────────

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
        OnPropertyChanged(nameof(StFilteredDraftApps));
        OnPropertyChanged(nameof(StHasNoDraftApps));
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

    // ── Screen Time loading ───────────────────────────────────────────────────

    private async Task LoadScreenTimeConfigAsync()
    {
        var resp = await _client.GetScreenTimeConfigAsync();
        if (resp is not null)
            LoadScreenTimeConfig(resp.Config);
    }

    private void LoadScreenTimeConfig(ScreenTimeConfig config)
    {
        ScreenTimeConfigNormalizer.Normalize(config);

        StDailyLimits.Clear();
        foreach (var rule in config.DailyLimits)
            StDailyLimits.Add(new DailyTimeLimitViewModel(rule, StRemoveDailyLimit));

        StAppLimits.Clear();
        foreach (var limit in config.AppLimits)
            StAppLimits.Add(new AppTimeLimitViewModel(limit, StRemoveAppLimit));
    }

    private void StRemoveDailyLimit(DailyTimeLimitViewModel vm) => StDailyLimits.Remove(vm);
    private void StRemoveAppLimit(AppTimeLimitViewModel vm) => StAppLimits.Remove(vm);

    // ── Focus Lock commands ───────────────────────────────────────────────────

    [RelayCommand]
    private void RemoveApp(BlockedApp app)
    {
        var selectable = AvailableApps.FirstOrDefault(a =>
            string.Equals(a.ExeName, app.ExeName, StringComparison.OrdinalIgnoreCase));
        if (selectable is not null)
            selectable.IsSelected = false;
        else
            BlockedApps.Remove(app);
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
    private void RemoveSite(BlockedSite site)
    {
        BlockedSites.Remove(site);
        SyncWebsiteCategorySelection();
    }

    private void OnWebsiteCategoryToggled(SelectableWebsiteCategory category, bool selected)
    {
        if (selected)
        {
            foreach (var domain in category.Domains)
            {
                if (!BlockedSites.Any(s => s.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                    BlockedSites.Add(new BlockedSite(domain));
            }
        }
        else
        {
            foreach (var domain in category.Domains.ToList())
            {
                var existing = BlockedSites.FirstOrDefault(s =>
                    s.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    BlockedSites.Remove(existing);
            }
        }
    }

    private void SyncWebsiteCategorySelection()
    {
        foreach (var category in WebsiteCategories)
        {
            bool allPresent = category.Domains.All(d =>
                BlockedSites.Any(s => s.Domain.Equals(d, StringComparison.OrdinalIgnoreCase)));
            if (category.IsSelected != allPresent)
                category.IsSelected = allPresent;
        }
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
        if (deadline > DateTime.Now.AddYears(1))
        {
            ErrorMessage = "Deadline cannot be more than one year in the future.";
            return;
        }
        if (BlockedApps.Count == 0 && BlockedSites.Count == 0 && !HasScreenTimeLimits)
        {
            ErrorMessage = "Add at least one blocked app or website, or enable a screen time limit.";
            return;
        }
        if (SelectedMode == SessionMode.Strict && !StrictConsentGiven)
        {
            ErrorMessage = "You must confirm consent to use Strict mode.";
            return;
        }
        if (StDailyLimits.Any(d => d.LimitMinutes < ScreenTimeConfig.MinDailyLimitMinutes))
        {
            ErrorMessage = $"Each daily screen time limit must be at least {ScreenTimeConfig.MinDailyLimitMinutes} minutes.";
            return;
        }

        IsBusy = true;
        try
        {
            await _client.SetScreenTimeConfigAsync(BuildScreenTimeConfig());

            var req = new StartSessionRequest(
                SelectedMode,
                deadline.ToUniversalTime(),
                BlockedApps.ToList(),
                BlockedSites.ToList(),
                StrictConsentGiven);

            var result = await _client.StartSessionAsync(req);
            if (result?.Success == true)
            {
                var dashboard = App.Services.GetRequiredService<DashboardViewModel>();
                dashboard.PrepareForActiveSession(SelectedMode, deadline);
                _nav.NavigateToDashboard();
                await dashboard.RefreshAsync();
            }
            else
                ErrorMessage = result?.ErrorMessage ?? "Failed to start session.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _nav.NavigateToDashboard();

    // ── Screen Time commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void StShowAddDailyLimit()
    {
        StDailyLimitAddError       = string.Empty;
        StIsAddingDailyLimit         = true;
        StDraftDailyLimitHours       = "2";
        StDraftDailyLimitMin         = "0";
        StDraftDailyUseCustomSchedule = true;
        _stDailyDraftDays            = (DayOfWeekFlags)127;
        StRefreshDayProps("StDailyDraft");
        StDailyDraftStartHour = "9"; StDailyDraftStartMinute = "00"; StDailyDraftStartAmPm = "AM";
        StDailyDraftEndHour   = "5"; StDailyDraftEndMinute   = "00"; StDailyDraftEndAmPm   = "PM";
    }

    [RelayCommand]
    private void StCancelAddDaily() => StIsAddingDailyLimit = false;

    [RelayCommand]
    private void StAddDailyLimit()
    {
        StDailyLimitAddError = string.Empty;

        int limitMinutes = StParseDailyLimitMinutes(StDraftDailyLimitHours, StDraftDailyLimitMin);
        if (limitMinutes < ScreenTimeConfig.MinDailyLimitMinutes)
        {
            StDailyLimitAddError = $"Daily limit must be at least {ScreenTimeConfig.MinDailyLimitMinutes} minutes.";
            return;
        }

        var schedule = BuildStDailyDraftSchedule();
        if (schedule.ActiveDays == DayOfWeekFlags.None)
        {
            StDailyLimitAddError = "Select at least one day for this daily limit.";
            return;
        }

        var existing = StDailyLimits.Select(d => d.ToModel()).ToList();
        if (ScreenTimeScheduleOverlap.TryFindDailyLimitOverlap(existing, schedule, out var overlapMsg))
        {
            StDailyLimitAddError = overlapMsg!;
            return;
        }

        var rule = new DailyTimeLimit
        {
            LimitMinutes = limitMinutes,
            Schedule     = schedule
        };
        StDailyLimits.Add(new DailyTimeLimitViewModel(rule, StRemoveDailyLimit));
        StIsAddingDailyLimit = false;
    }

    [RelayCommand]
    private void StShowAddAppLimit()
    {
        StAppLimitAddError    = string.Empty;
        StIsAddingAppLimit    = true;
        StDraftExeName        = string.Empty;
        StDraftDisplayName    = string.Empty;
        StDraftLimitType      = AppLimitType.DailyTotal;
        StDraftLimitHours     = "1";
        StDraftLimitMin       = "0";
        StDraftIntervalLimit  = "20";
        StDraftIntervalPeriod = "60";
        StDraftUseCustomSchedule = false;
        StDraftAppSearch      = string.Empty;
        _stDraftDays          = (DayOfWeekFlags)127;
        StRefreshDayProps("StDraft");
        StDraftStartHour = "9"; StDraftStartMinute = "00"; StDraftStartAmPm = "AM";
        StDraftEndHour   = "5"; StDraftEndMinute   = "00"; StDraftEndAmPm   = "PM";
    }

    [RelayCommand]
    private void StCancelAdd() => StIsAddingAppLimit = false;

    [RelayCommand]
    private void StSelectDraftApp(SelectableApp app)
    {
        StDraftExeName        = app.ExeName;
        StDraftDisplayName    = app.DisplayName;
        StDraftAppSearch      = string.Empty;
        StIsDraftAppPopupOpen = false;
    }

    [RelayCommand]
    private void StBrowseDraftApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe",
            Title  = "Select application to limit"
        };
        if (dlg.ShowDialog() != true) return;
        StDraftExeName     = Path.GetFileName(dlg.FileName);
        StDraftDisplayName = FileVersionInfo.GetVersionInfo(dlg.FileName).ProductName
            ?? Path.GetFileNameWithoutExtension(dlg.FileName);
        StIsDraftAppPopupOpen = false;
    }

    [RelayCommand]
    private void StAddAppLimit()
    {
        StAppLimitAddError = string.Empty;
        if (string.IsNullOrWhiteSpace(StDraftExeName)) return;

        var schedule = StDraftUseCustomSchedule ? BuildStDraftSchedule() : null;
        var candidateSchedule = schedule ?? ScreenTimeSchedule.Always;
        if (schedule is not null && candidateSchedule.ActiveDays == DayOfWeekFlags.None)
        {
            StAppLimitAddError = "Select at least one day for this app limit schedule.";
            return;
        }

        var existing = StAppLimits.Select(a => a.ToModel()).ToList();
        if (ScreenTimeScheduleOverlap.TryFindAppLimitOverlap(
                existing, StDraftExeName, candidateSchedule, out var overlapMsg))
        {
            StAppLimitAddError = overlapMsg!;
            return;
        }

        var limit = new AppTimeLimit
        {
            ExeName         = StDraftExeName,
            DisplayName     = string.IsNullOrWhiteSpace(StDraftDisplayName) ? StDraftExeName : StDraftDisplayName,
            LimitType       = StDraftLimitType,
            LimitMinutes    = StDraftLimitType == AppLimitType.DailyTotal
                                  ? StParseHM(StDraftLimitHours, StDraftLimitMin)
                                  : StParseInt(StDraftIntervalLimit, 20),
            IntervalMinutes = StParseInt(StDraftIntervalPeriod, 60),
            Schedule        = schedule
        };

        StAppLimits.Add(new AppTimeLimitViewModel(limit, StRemoveAppLimit));
        StIsAddingAppLimit = false;
    }

    // ── Builders ──────────────────────────────────────────────────────────────

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

    internal ScreenTimeConfig BuildScreenTimeConfig() => new()
    {
        DailyLimits = StDailyLimits.Select(d => d.ToModel()).ToList(),
        AppLimits   = StAppLimits.Select(a => a.ToModel()).ToList()
    };

    private ScreenTimeSchedule BuildStDailyDraftSchedule()
    {
        if (!StDraftDailyUseCustomSchedule)
        {
            return new ScreenTimeSchedule { ActiveDays = _stDailyDraftDays };
        }

        return new ScreenTimeSchedule
        {
            ActiveDays = _stDailyDraftDays,
            StartTime  = StParseTimeTo(StDailyDraftStartHour, StDailyDraftStartMinute, StDailyDraftStartAmPm),
            EndTime    = StParseTimeTo(StDailyDraftEndHour,   StDailyDraftEndMinute,   StDailyDraftEndAmPm)
        };
    }

    private ScreenTimeSchedule BuildStDraftSchedule() => new()
    {
        ActiveDays = _stDraftDays,
        StartTime  = StParseTimeTo(StDraftStartHour, StDraftStartMinute, StDraftStartAmPm),
        EndTime    = StParseTimeTo(StDraftEndHour,   StDraftEndMinute,   StDraftEndAmPm)
    };

    // ── Day flag helpers ──────────────────────────────────────────────────────

    private static bool GetDay(DayOfWeekFlags flags, DayOfWeekFlags flag)
        => (flags & flag) != 0;

    private void SetDay(ref DayOfWeekFlags flags, DayOfWeekFlags flag, bool value, string propName)
    {
        if (value) flags |= flag; else flags &= ~flag;
        OnPropertyChanged(propName);
    }

    private void StRefreshDayProps(string prefix)
    {
        foreach (var s in new[] { "Mon","Tue","Wed","Thu","Fri","Sat","Sun" })
            OnPropertyChanged(prefix + s);
    }

    // ── Time helpers ──────────────────────────────────────────────────────────

    private static TimeOnly? StParseTimeTo(string hour, string minute, string ampm)
    {
        if (!int.TryParse(hour, out int h) || !int.TryParse(minute, out int m)) return null;
        h = Math.Clamp(h, 1, 12);
        m = Math.Clamp(m, 0, 59);
        if (ampm == "PM" && h != 12) h += 12;
        else if (ampm == "AM" && h == 12) h = 0;
        return new TimeOnly(h, m);
    }

    private static (string hour, string minute, string ampm) StToTimeFields(TimeOnly? t)
    {
        if (t is null) return ("12", "00", "AM");
        int h   = t.Value.Hour;
        string ap = h < 12 ? "AM" : "PM";
        h = h % 12 == 0 ? 12 : h % 12;
        return (h.ToString(), t.Value.Minute.ToString("00"), ap);
    }

    private static int StParseHMRaw(string h, string m)
    {
        int.TryParse(h, out int hours);
        int.TryParse(m, out int mins);
        return Math.Max(0, hours * 60 + mins);
    }

    /// <summary>Device daily limit — minimum 5 minutes. Not used for per-app limits.</summary>
    private static int StParseDailyLimitMinutes(string h, string m)
        => Math.Max(ScreenTimeConfig.MinDailyLimitMinutes, StParseHMRaw(h, m));

    private static int StParseHM(string h, string m)
        => Math.Max(1, StParseHMRaw(h, m));

    private static int StParseInt(string s, int fallback)
        => int.TryParse(s, out int v) && v > 0 ? v : fallback;

    private static (string hours, string mins) StSplitMinutes(int total)
        => ((total / 60).ToString(), (total % 60).ToString());
}
