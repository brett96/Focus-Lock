using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;

namespace FocusLock.UI.ViewModels;

public class AppTimeLimitViewModel
{
    public string Id { get; }
    public string ExeName     { get; }
    public string DisplayName { get; }
    public AppLimitType LimitType     { get; }
    public int LimitMinutes           { get; }
    public int IntervalMinutes        { get; }
    public ScreenTimeSchedule? Schedule { get; }

    public IRelayCommand RemoveCommand { get; }

    public AppTimeLimitViewModel(AppTimeLimit model, Action<AppTimeLimitViewModel> remove)
    {
        Id             = model.Id;
        ExeName        = model.ExeName;
        DisplayName    = model.DisplayName;
        LimitType      = model.LimitType;
        LimitMinutes   = model.LimitMinutes;
        IntervalMinutes = model.IntervalMinutes;
        Schedule       = model.Schedule;
        RemoveCommand  = new RelayCommand(() => remove(this));
    }

    public string LimitSummary => LimitType == AppLimitType.DailyTotal
        ? $"Daily: {Fmt(LimitMinutes)}/day"
        : $"Interval: {LimitMinutes} min every {IntervalMinutes} min";

    public string ScheduleSummary => Schedule is null ? "Always" : ScreenTimeScheduleHelper.Describe(Schedule);

    private static string Fmt(int minutes)
    {
        int h = minutes / 60, m = minutes % 60;
        return h > 0 && m > 0 ? $"{h}h {m}m" : h > 0 ? $"{h}h" : $"{m}m";
    }

    public AppTimeLimit ToModel() => new()
    {
        Id              = Id,
        ExeName         = ExeName,
        DisplayName    = DisplayName,
        LimitType      = LimitType,
        LimitMinutes   = LimitMinutes,
        IntervalMinutes = IntervalMinutes,
        Schedule       = Schedule
    };
}
