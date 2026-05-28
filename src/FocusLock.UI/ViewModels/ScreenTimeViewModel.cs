using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;
using FocusLock.UI.Services;
using FocusLock.UI.Views;

namespace FocusLock.UI.ViewModels;

public partial class ScreenTimeViewModel : ObservableObject
{
    private readonly ServiceClient _client;
    private readonly NavigationService _nav;
    private readonly DispatcherTimer _statusTimer;

    // ── Daily limit ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool _enableDailyLimit;
    [ObservableProperty] private string _dailyLimitHours   = "2";
    [ObservableProperty] private string _dailyLimitMin     = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSchedulePicker))]
    private bool _dailyUseCustomSchedule;

    public bool ShowSchedulePicker => DailyUseCustomSchedule;

    // ── Global schedule days ──────────────────────────────────────────────────

    private DayOfWeekFlags _globalDays = (DayOfWeekFlags)127;

    public bool GlobalMon { get => GetDay(_globalDays, DayOfWeekFlags.Monday);    set => SetDay(ref _globalDays, DayOfWeekFlags.Monday,    value, nameof(GlobalMon)); }
    public bool GlobalTue { get => GetDay(_globalDays, DayOfWeekFlags.Tuesday);   set => SetDay(ref _globalDays, DayOfWeekFlags.Tuesday,   value, nameof(GlobalTue)); }
    public bool GlobalWed { get => GetDay(_globalDays, DayOfWeekFlags.Wednesday); set => SetDay(ref _globalDays, DayOfWeekFlags.Wednesday, value, nameof(GlobalWed)); }
    public bool GlobalThu { get => GetDay(_globalDays, DayOfWeekFlags.Thursday);  set => SetDay(ref _globalDays, DayOfWeekFlags.Thursday,  value, nameof(GlobalThu)); }
    public bool GlobalFri { get => GetDay(_globalDays, DayOfWeekFlags.Friday);    set => SetDay(ref _globalDays, DayOfWeekFlags.Friday,    value, nameof(GlobalFri)); }
    public bool GlobalSat { get => GetDay(_globalDays, DayOfWeekFlags.Saturday);  set => SetDay(ref _globalDays, DayOfWeekFlags.Saturday,  value, nameof(GlobalSat)); }
    public bool GlobalSun { get => GetDay(_globalDays, DayOfWeekFlags.Sunday);    set => SetDay(ref _globalDays, DayOfWeekFlags.Sunday,    value, nameof(GlobalSun)); }

    // ── Global schedule time range ────────────────────────────────────────────

    [ObservableProperty] private string _globalStartHour   = "9";
    [ObservableProperty] private string _globalStartMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalStartIsAm))]
    [NotifyPropertyChangedFor(nameof(GlobalStartIsPm))]
    private string _globalStartAmPm = "AM";

    public bool GlobalStartIsAm { get => GlobalStartAmPm == "AM"; set { if (value) GlobalStartAmPm = "AM"; } }
    public bool GlobalStartIsPm { get => GlobalStartAmPm == "PM"; set { if (value) GlobalStartAmPm = "PM"; } }

    [ObservableProperty] private string _globalEndHour   = "5";
    [ObservableProperty] private string _globalEndMinute = "00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalEndIsAm))]
    [NotifyPropertyChangedFor(nameof(GlobalEndIsPm))]
    private string _globalEndAmPm = "PM";

    public bool GlobalEndIsAm { get => GlobalEndAmPm == "AM"; set { if (value) GlobalEndAmPm = "AM"; } }
    public bool GlobalEndIsPm { get => GlobalEndAmPm == "PM"; set { if (value) GlobalEndAmPm = "PM"; } }

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
        EnableDailyLimit = config.EnableDailyLimit;
        (DailyLimitHours, DailyLimitMin) = SplitMinutes(config.DailyLimitMinutes);

        DailyUseCustomSchedule = config.DailySchedule is not null;
        if (config.DailySchedule is null)
        {
            _globalDays = (DayOfWeekFlags)127;
        }
        else
        {
            _globalDays = config.DailySchedule.ActiveDays;
            (GlobalStartHour, GlobalStartMinute, GlobalStartAmPm) =
                ToTimeFields(config.DailySchedule.StartTime);
            (GlobalEndHour, GlobalEndMinute, GlobalEndAmPm) =
                ToTimeFields(config.DailySchedule.EndTime);
            RefreshDayProps("Global");
        }

        AppLimits.Clear();
        foreach (var limit in config.AppLimits)
            AppLimits.Add(new AppTimeLimitViewModel(limit, RemoveAppLimit));
    }

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
        var config = BuildConfig();
        var result = await _client.SetScreenTimeConfigAsync(config);
        SaveMessage = result?.Success == true ? "Settings saved." : (result?.ErrorMessage ?? "Failed to reach service. Changes will apply next time the service is running.");
    }

    [RelayCommand]
    private void Back()
    {
        _statusTimer.Stop();
        _nav.NavigateTo(new DashboardPage());
    }

    [RelayCommand]
    private void ShowAddAppLimit()
    {
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
        if (string.IsNullOrWhiteSpace(DraftExeName)) return;

        var schedule = DraftUseCustomSchedule ? BuildDraftSchedule() : null;
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

        var existing = AppLimits.FirstOrDefault(a =>
            string.Equals(a.ExeName, DraftExeName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) AppLimits.Remove(existing);

        AppLimits.Add(new AppTimeLimitViewModel(limit, RemoveAppLimit));
        IsAddingAppLimit = false;
    }

    private void RemoveAppLimit(AppTimeLimitViewModel vm) => AppLimits.Remove(vm);

    // ── Builders ──────────────────────────────────────────────────────────────

    private ScreenTimeConfig BuildConfig() => new()
    {
        EnableDailyLimit  = EnableDailyLimit,
        DailyLimitMinutes = ParseHM(DailyLimitHours, DailyLimitMin),
        DailySchedule     = DailyUseCustomSchedule ? BuildGlobalSchedule() : null,
        AppLimits         = AppLimits.Select(a => a.ToModel()).ToList()
    };

    private ScreenTimeSchedule BuildGlobalSchedule() => new()
    {
        ActiveDays = _globalDays,
        StartTime  = ParseTimeTo(GlobalStartHour, GlobalStartMinute, GlobalStartAmPm),
        EndTime    = ParseTimeTo(GlobalEndHour,   GlobalEndMinute,   GlobalEndAmPm)
    };

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

    private static int ParseHM(string h, string m)
    {
        int.TryParse(h, out int hours);
        int.TryParse(m, out int mins);
        return Math.Max(1, hours * 60 + mins);
    }

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
