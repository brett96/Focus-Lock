using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;
using FocusLock.UI.Services;
using FocusLock.UI.Views;

namespace FocusLock.UI.ViewModels;

public partial class ScreenTimeViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly NavigationService _nav;
    private readonly DispatcherTimer _statusTimer;

    // ── Daily limits list ─────────────────────────────────────────────────────

    public ObservableCollection<DailyTimeLimitViewModel> DailyLimits { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotAddingDaily))]
    private bool _isAddingDailyLimit;

    public bool IsNotAddingDaily => !IsAddingDailyLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDailyLimitAddError))]
    private string _dailyLimitAddError = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppLimitAddError))]
    private string _appLimitAddError = string.Empty;

    public bool HasDailyLimitAddError => !string.IsNullOrEmpty(DailyLimitAddError);
    public bool HasAppLimitAddError   => !string.IsNullOrEmpty(AppLimitAddError);

    [ObservableProperty] private string _draftDailyLimitHours = "2";
    [ObservableProperty] private string _draftDailyLimitMin   = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDailyDraftSchedulePicker))]
    private bool _draftDailyUseCustomSchedule;

    public bool ShowDailyDraftSchedulePicker => DraftDailyUseCustomSchedule;

    private DayOfWeekFlags _dailyDraftDays = (DayOfWeekFlags)127;

    public bool DailyDraftMon { get => GetDay(_dailyDraftDays, DayOfWeekFlags.Monday);    set => SetDay(ref _dailyDraftDays, DayOfWeekFlags.Monday,    value, nameof(DailyDraftMon)); }
    public bool DailyDraftTue { get => GetDay(_dailyDraftDays, DayOfWeekFlags.Tuesday);   set => SetDay(ref _dailyDraftDays, DayOfWeekFlags.Tuesday,   value, nameof(DailyDraftTue)); }
    public bool DailyDraftWed { get => GetDay(_dailyDraftDays, DayOfWeekFlags.Wednesday); set => SetDay(ref _dailyDraftDays, DayOfWeekFlags.Wednesday, value, nameof(DailyDraftWed)); }
    public bool DailyDraftThu { get => GetDay(_dailyDraftDays, DayOfWeekFlags.Thursday);  set => SetDay(ref _dailyDraftDays, DayOfWeekFlags.Thursday,  value, nameof(DailyDraftThu)); }
    public bool DailyDraftFri { get => GetDay(_dailyDraftDays, DayOfWeekFlags.Friday);    set => SetDay(ref _dailyDraftDays, DayOfWeekFlags.Friday,    value, nameof(DailyDraftFri)); }
    public bool DailyDraftSat { get => GetDay(_dailyDraftDays, DayOfWeekFlags.Saturday);  set => SetDay(ref _dailyDraftDays, DayOfWeekFlags.Saturday,  value, nameof(DailyDraftSat)); }
    public bool DailyDraftSun { get => GetDay(_dailyDraftDays, DayOfWeekFlags.Sunday);    set => SetDay(ref _dailyDraftDays, DayOfWeekFlags.Sunday,    value, nameof(DailyDraftSun)); }

    [ObservableProperty] private string _dailyDraftStartHour   = "9";
    [ObservableProperty] private string _dailyDraftStartMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DailyDraftStartIsAm))]
    [NotifyPropertyChangedFor(nameof(DailyDraftStartIsPm))]
    private string _dailyDraftStartAmPm = "AM";

    public bool DailyDraftStartIsAm { get => DailyDraftStartAmPm == "AM"; set { if (value) DailyDraftStartAmPm = "AM"; } }
    public bool DailyDraftStartIsPm { get => DailyDraftStartAmPm == "PM"; set { if (value) DailyDraftStartAmPm = "PM"; } }

    [ObservableProperty] private string _dailyDraftEndHour   = "5";
    [ObservableProperty] private string _dailyDraftEndMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DailyDraftEndIsAm))]
    [NotifyPropertyChangedFor(nameof(DailyDraftEndIsPm))]
    private string _dailyDraftEndAmPm = "PM";

    public bool DailyDraftEndIsAm { get => DailyDraftEndAmPm == "AM"; set { if (value) DailyDraftEndAmPm = "AM"; } }
    public bool DailyDraftEndIsPm { get => DailyDraftEndAmPm == "PM"; set { if (value) DailyDraftEndAmPm = "PM"; } }

    // ── App limits list ───────────────────────────────────────────────────────

    public ObservableCollection<AppTimeLimitViewModel> AppLimits { get; } = new();

    // ── Add app limit draft form ──────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotAdding))]
    private bool _isAddingAppLimit;

    public bool IsNotAdding => !IsAddingAppLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredDraftApps))]
    [NotifyPropertyChangedFor(nameof(HasNoDraftApps))]
    private string _draftAppSearch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDraftApps))]
    private bool _isDraftAppsLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraftExe))]
    private string _draftExeName = string.Empty;

    [ObservableProperty] private string _draftDisplayName = string.Empty;

    public bool HasDraftExe => !string.IsNullOrEmpty(DraftExeName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDailyTotalDraft))]
    [NotifyPropertyChangedFor(nameof(IsIntervalDraft))]
    private AppLimitType _draftLimitType = AppLimitType.DailyTotal;

    public bool IsDailyTotalDraft
    {
        get => DraftLimitType == AppLimitType.DailyTotal;
        set { if (value) DraftLimitType = AppLimitType.DailyTotal; }
    }
    public bool IsIntervalDraft
    {
        get => DraftLimitType == AppLimitType.Interval;
        set { if (value) DraftLimitType = AppLimitType.Interval; }
    }

    [ObservableProperty] private string _draftLimitHours     = "1";
    [ObservableProperty] private string _draftLimitMin       = "0";
    [ObservableProperty] private string _draftIntervalLimit  = "20";
    [ObservableProperty] private string _draftIntervalPeriod = "60";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDraftSchedulePicker))]
    private bool _draftUseCustomSchedule;

    public bool ShowDraftSchedulePicker => DraftUseCustomSchedule;

    // ── Draft custom schedule days ────────────────────────────────────────────

    private DayOfWeekFlags _draftDays = (DayOfWeekFlags)127;

    public bool DraftMon { get => GetDay(_draftDays, DayOfWeekFlags.Monday);    set => SetDay(ref _draftDays, DayOfWeekFlags.Monday,    value, nameof(DraftMon)); }
    public bool DraftTue { get => GetDay(_draftDays, DayOfWeekFlags.Tuesday);   set => SetDay(ref _draftDays, DayOfWeekFlags.Tuesday,   value, nameof(DraftTue)); }
    public bool DraftWed { get => GetDay(_draftDays, DayOfWeekFlags.Wednesday); set => SetDay(ref _draftDays, DayOfWeekFlags.Wednesday, value, nameof(DraftWed)); }
    public bool DraftThu { get => GetDay(_draftDays, DayOfWeekFlags.Thursday);  set => SetDay(ref _draftDays, DayOfWeekFlags.Thursday,  value, nameof(DraftThu)); }
    public bool DraftFri { get => GetDay(_draftDays, DayOfWeekFlags.Friday);    set => SetDay(ref _draftDays, DayOfWeekFlags.Friday,    value, nameof(DraftFri)); }
    public bool DraftSat { get => GetDay(_draftDays, DayOfWeekFlags.Saturday);  set => SetDay(ref _draftDays, DayOfWeekFlags.Saturday,  value, nameof(DraftSat)); }
    public bool DraftSun { get => GetDay(_draftDays, DayOfWeekFlags.Sunday);    set => SetDay(ref _draftDays, DayOfWeekFlags.Sunday,    value, nameof(DraftSun)); }

    [ObservableProperty] private string _draftStartHour   = "9";
    [ObservableProperty] private string _draftStartMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftStartIsAm))]
    [NotifyPropertyChangedFor(nameof(DraftStartIsPm))]
    private string _draftStartAmPm = "AM";

    public bool DraftStartIsAm { get => DraftStartAmPm == "AM"; set { if (value) DraftStartAmPm = "AM"; } }
    public bool DraftStartIsPm { get => DraftStartAmPm == "PM"; set { if (value) DraftStartAmPm = "PM"; } }

    [ObservableProperty] private string _draftEndHour   = "5";
    [ObservableProperty] private string _draftEndMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftEndIsAm))]
    [NotifyPropertyChangedFor(nameof(DraftEndIsPm))]
    private string _draftEndAmPm = "PM";

    public bool DraftEndIsAm { get => DraftEndAmPm == "AM"; set { if (value) DraftEndAmPm = "AM"; } }
    public bool DraftEndIsPm { get => DraftEndAmPm == "PM"; set { if (value) DraftEndAmPm = "PM"; } }

    public ObservableCollection<SelectableApp> DraftAvailableApps { get; } = new();

    public IEnumerable<SelectableApp> FilteredDraftApps =>
        string.IsNullOrWhiteSpace(DraftAppSearch)
            ? DraftAvailableApps
            : DraftAvailableApps.Where(a =>
                a.DisplayName.Contains(DraftAppSearch, StringComparison.OrdinalIgnoreCase));

    public bool HasNoDraftApps => !IsDraftAppsLoading && !FilteredDraftApps.Any();

    // ── Popup state ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isDraftAppPopupOpen;

    // ── Status / feedback ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusSummary))]
    private string _statusUsageSummary = string.Empty;

    public bool HasStatusSummary => !string.IsNullOrEmpty(StatusUsageSummary);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveMessage))]
    private string _saveMessage = string.Empty;

    public bool HasSaveMessage => !string.IsNullOrEmpty(SaveMessage);

    // ── Constructor ───────────────────────────────────────────────────────────

    public ScreenTimeViewModel(ServiceClient client, NavigationService nav)
    {
        _client = client;
        _nav    = nav;

        _ = InitAsync();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _statusTimer.Start();
    }

    private async Task InitAsync()
    {
        var configResp = await _client.GetScreenTimeConfigAsync();
        if (configResp is not null)
            LoadFromConfig(configResp.Config);

        await RefreshStatusAsync();

        var apps = await InstalledAppsLoader.LoadAsync();
        foreach (var app in apps)
            DraftAvailableApps.Add(app);
        IsDraftAppsLoading = false;
        OnPropertyChanged(nameof(FilteredDraftApps));
        OnPropertyChanged(nameof(HasNoDraftApps));
    }

    private void LoadFromConfig(ScreenTimeConfig config)
    {
        ScreenTimeConfigNormalizer.Normalize(config);

        DailyLimits.Clear();
        foreach (var rule in config.DailyLimits)
            DailyLimits.Add(new DailyTimeLimitViewModel(rule, RemoveDailyLimit));

        AppLimits.Clear();
        foreach (var limit in config.AppLimits)
            AppLimits.Add(new AppTimeLimitViewModel(limit, RemoveAppLimit));
    }

    private void RemoveDailyLimit(DailyTimeLimitViewModel vm) => DailyLimits.Remove(vm);

    private async Task RefreshStatusAsync()
    {
        var status = await _client.GetScreenTimeStatusAsync();
        if (status is null) return;
        StatusUsageSummary = BuildUsageSummary(status);
    }

    private static string BuildUsageSummary(ScreenTimeStatusResponse s)
    {
        if (!s.Enabled && s.AppStatuses.Count == 0) return string.Empty;
        var lines = new List<string>();
        if (s.Enabled)
        {
            var used  = TimeSpan.FromSeconds(s.TotalSecondsUsedToday);
            var limit = TimeSpan.FromMinutes(s.DailyLimitMinutes);
            string extra = s.IsLockedOutForDay ? " — LOCKED OUT" : string.Empty;
            lines.Add($"Total screen time today: {Fmt(used)} / {Fmt(limit)}{extra}");
        }
        foreach (var a in s.AppStatuses)
        {
            string blocked = a.IsBlocked ? " — BLOCKED" : string.Empty;
            if (a.LimitType == AppLimitType.DailyTotal)
            {
                var used  = TimeSpan.FromSeconds(a.TotalSecondsToday);
                var limit = TimeSpan.FromMinutes(a.LimitMinutes);
                lines.Add($"{a.DisplayName}: {Fmt(used)} / {Fmt(limit)} today{blocked}");
            }
            else
            {
                var iUsed = TimeSpan.FromSeconds(a.CurrentIntervalSecondsUsed ?? 0);
                lines.Add($"{a.DisplayName}: {Fmt(iUsed)} / {a.LimitMinutes}min this interval{blocked}");
            }
        }
        return string.Join("\n", lines);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        SaveMessage = string.Empty;
        if (DailyLimits.Any(d => d.LimitMinutes < ScreenTimeConfig.MinDailyLimitMinutes))
        {
            SaveMessage = $"Each daily screen time limit must be at least {ScreenTimeConfig.MinDailyLimitMinutes} minutes.";
            return;
        }
        var config = BuildConfig();
        var result = await _client.SetScreenTimeConfigAsync(config);
        SaveMessage = result?.Success == true ? "Settings saved." : (result?.ErrorMessage ?? "Failed to reach service. Changes will apply next time the service is running.");
    }

    [RelayCommand]
    private void Back()
    {
        _statusTimer.Stop();
        _nav.NavigateToDashboard();
    }

    [RelayCommand]
    private void ShowAddDailyLimit()
    {
        DailyLimitAddError         = string.Empty;
        IsAddingDailyLimit         = true;
        DraftDailyLimitHours       = "2";
        DraftDailyLimitMin         = "0";
        DraftDailyUseCustomSchedule = true;
        _dailyDraftDays            = (DayOfWeekFlags)127;
        RefreshDayProps("DailyDraft");
        DailyDraftStartHour = "9"; DailyDraftStartMinute = "00"; DailyDraftStartAmPm = "AM";
        DailyDraftEndHour   = "5"; DailyDraftEndMinute   = "00"; DailyDraftEndAmPm   = "PM";
    }

    [RelayCommand]
    private void CancelAddDaily() => IsAddingDailyLimit = false;

    [RelayCommand]
    private void AddDailyLimit()
    {
        DailyLimitAddError = string.Empty;

        int limitMinutes = ParseDailyLimitMinutes(DraftDailyLimitHours, DraftDailyLimitMin);
        if (limitMinutes < ScreenTimeConfig.MinDailyLimitMinutes)
        {
            DailyLimitAddError = $"Daily limit must be at least {ScreenTimeConfig.MinDailyLimitMinutes} minutes.";
            return;
        }

        var schedule = BuildDailyDraftSchedule();
        if (schedule.ActiveDays == DayOfWeekFlags.None)
        {
            DailyLimitAddError = "Select at least one day for this daily limit.";
            return;
        }

        var existing = DailyLimits.Select(d => d.ToModel()).ToList();
        if (ScreenTimeScheduleOverlap.TryFindDailyLimitOverlap(existing, schedule, out var overlapMsg))
        {
            DailyLimitAddError = overlapMsg!;
            return;
        }

        DailyLimits.Add(new DailyTimeLimitViewModel(new DailyTimeLimit
        {
            LimitMinutes = limitMinutes,
            Schedule     = schedule
        }, RemoveDailyLimit));
        IsAddingDailyLimit = false;
    }

    [RelayCommand]
    private void ShowAddAppLimit()
    {
        AppLimitAddError    = string.Empty;
        IsAddingAppLimit    = true;
        DraftExeName        = string.Empty;
        DraftDisplayName    = string.Empty;
        DraftLimitType      = AppLimitType.DailyTotal;
        DraftLimitHours     = "1";
        DraftLimitMin       = "0";
        DraftIntervalLimit  = "20";
        DraftIntervalPeriod = "60";
        DraftUseCustomSchedule = false;
        DraftAppSearch      = string.Empty;
        _draftDays          = (DayOfWeekFlags)127;
        RefreshDayProps("Draft");
        DraftStartHour = "9"; DraftStartMinute = "00"; DraftStartAmPm = "AM";
        DraftEndHour   = "5"; DraftEndMinute   = "00"; DraftEndAmPm   = "PM";
    }

    [RelayCommand]
    private void CancelAdd() => IsAddingAppLimit = false;

    [RelayCommand]
    private void SelectDraftApp(SelectableApp app)
    {
        DraftExeName     = app.ExeName;
        DraftDisplayName = app.DisplayName;
        DraftAppSearch   = string.Empty;
        IsDraftAppPopupOpen = false;
    }

    [RelayCommand]
    private void BrowseDraftApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe",
            Title  = "Select application to limit"
        };
        if (dlg.ShowDialog() != true) return;
        DraftExeName     = Path.GetFileName(dlg.FileName);
        DraftDisplayName = FileVersionInfo.GetVersionInfo(dlg.FileName).ProductName
            ?? Path.GetFileNameWithoutExtension(dlg.FileName);
        IsDraftAppPopupOpen = false;
    }

    [RelayCommand]
    private void AddAppLimit()
    {
        AppLimitAddError = string.Empty;
        if (string.IsNullOrWhiteSpace(DraftExeName)) return;

        var schedule = DraftUseCustomSchedule ? BuildDraftSchedule() : null;
        var candidateSchedule = schedule ?? ScreenTimeSchedule.Always;
        if (schedule is not null && candidateSchedule.ActiveDays == DayOfWeekFlags.None)
        {
            AppLimitAddError = "Select at least one day for this app limit schedule.";
            return;
        }

        var existing = AppLimits.Select(a => a.ToModel()).ToList();
        if (ScreenTimeScheduleOverlap.TryFindAppLimitOverlap(
                existing, DraftExeName, candidateSchedule, out var overlapMsg))
        {
            AppLimitAddError = overlapMsg!;
            return;
        }

        var limit = new AppTimeLimit
        {
            ExeName         = DraftExeName,
            DisplayName     = string.IsNullOrWhiteSpace(DraftDisplayName) ? DraftExeName : DraftDisplayName,
            LimitType       = DraftLimitType,
            LimitMinutes    = DraftLimitType == AppLimitType.DailyTotal
                                  ? ParseHM(DraftLimitHours, DraftLimitMin)
                                  : ParseInt(DraftIntervalLimit, 20),
            IntervalMinutes = ParseInt(DraftIntervalPeriod, 60),
            Schedule        = schedule
        };

        AppLimits.Add(new AppTimeLimitViewModel(limit, RemoveAppLimit));
        IsAddingAppLimit = false;
    }

    private void RemoveAppLimit(AppTimeLimitViewModel vm) => AppLimits.Remove(vm);

    // ── Builders ──────────────────────────────────────────────────────────────

    private ScreenTimeConfig BuildConfig() => new()
    {
        DailyLimits = DailyLimits.Select(d => d.ToModel()).ToList(),
        AppLimits   = AppLimits.Select(a => a.ToModel()).ToList()
    };

    private ScreenTimeSchedule BuildDailyDraftSchedule()
    {
        if (!DraftDailyUseCustomSchedule)
            return new ScreenTimeSchedule { ActiveDays = _dailyDraftDays };

        return new ScreenTimeSchedule
        {
            ActiveDays = _dailyDraftDays,
            StartTime  = ParseTimeTo(DailyDraftStartHour, DailyDraftStartMinute, DailyDraftStartAmPm),
            EndTime    = ParseTimeTo(DailyDraftEndHour,   DailyDraftEndMinute,   DailyDraftEndAmPm)
        };
    }

    private ScreenTimeSchedule BuildDraftSchedule() => new()
    {
        ActiveDays = _draftDays,
        StartTime  = ParseTimeTo(DraftStartHour, DraftStartMinute, DraftStartAmPm),
        EndTime    = ParseTimeTo(DraftEndHour,   DraftEndMinute,   DraftEndAmPm)
    };

    // ── Day flag helpers ──────────────────────────────────────────────────────

    private static bool GetDay(DayOfWeekFlags flags, DayOfWeekFlags flag)
        => (flags & flag) != 0;

    private void SetDay(ref DayOfWeekFlags flags, DayOfWeekFlags flag, bool value, string propName)
    {
        if (value) flags |= flag; else flags &= ~flag;
        OnPropertyChanged(propName);
    }

    private void RefreshDayProps(string prefix)
    {
        foreach (var s in new[] { "Mon","Tue","Wed","Thu","Fri","Sat","Sun" })
            OnPropertyChanged(prefix + s);
    }

    // ── Time helpers ──────────────────────────────────────────────────────────

    private static TimeOnly? ParseTimeTo(string hour, string minute, string ampm)
    {
        if (!int.TryParse(hour, out int h) || !int.TryParse(minute, out int m)) return null;
        h = Math.Clamp(h, 1, 12);
        m = Math.Clamp(m, 0, 59);
        if (ampm == "PM" && h != 12) h += 12;
        else if (ampm == "AM" && h == 12) h = 0;
        return new TimeOnly(h, m);
    }

    private static (string hour, string minute, string ampm) ToTimeFields(TimeOnly? t)
    {
        if (t is null) return ("12", "00", "AM");
        int h   = t.Value.Hour;
        string ap = h < 12 ? "AM" : "PM";
        h = h % 12 == 0 ? 12 : h % 12;
        return (h.ToString(), t.Value.Minute.ToString("00"), ap);
    }

    private static int ParseHMRaw(string h, string m)
    {
        int.TryParse(h, out int hours);
        int.TryParse(m, out int mins);
        return Math.Max(0, hours * 60 + mins);
    }

    private static int ParseDailyLimitMinutes(string h, string m)
        => Math.Max(ScreenTimeConfig.MinDailyLimitMinutes, ParseHMRaw(h, m));

    private static int ParseHM(string h, string m)
        => Math.Max(1, ParseHMRaw(h, m));

    private static int ParseInt(string s, int fallback)
        => int.TryParse(s, out int v) && v > 0 ? v : fallback;

    private static (string hours, string mins) SplitMinutes(int total)
        => ((total / 60).ToString(), (total % 60).ToString());

    private static string Fmt(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return t.Minutes > 0 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalHours}h";
        return $"{(int)t.TotalMinutes}m";
    }
}
