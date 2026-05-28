using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Models;

namespace FocusLock.UI.ViewModels;

public class AppTimeLimitViewModel
{
    public string ExeName     { get; }
    public string DisplayName { get; }
    public AppLimitType LimitType     { get; }
    public int LimitMinutes           { get; }
    public int IntervalMinutes        { get; }
    public ScreenTimeSchedule? Schedule { get; }

    public IRelayCommand RemoveCommand { get; }

    public AppTimeLimitViewModel(AppTimeLimit model, Action<AppTimeLimitViewModel> remove)
    {
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

    public string ScheduleSummary => Schedule is null ? "Always" : DescribeSchedule(Schedule);

    private static string Fmt(int minutes)
    {
        int h = minutes / 60, m = minutes % 60;
        return h > 0 && m > 0 ? $"{h}h {m}m" : h > 0 ? $"{h}h" : $"{m}m";
    }

    private static string DescribeSchedule(ScreenTimeSchedule s)
    {
        var days = DescribeDays(s.ActiveDays);
        if (s.StartTime is null || s.EndTime is null) return days;
        return $"{days}, {FormatTime(s.StartTime.Value)}–{FormatTime(s.EndTime.Value)}";
    }

    private static string DescribeDays(DayOfWeekFlags f)
    {
        const DayOfWeekFlags weekdays = DayOfWeekFlags.Monday | DayOfWeekFlags.Tuesday |
            DayOfWeekFlags.Wednesday | DayOfWeekFlags.Thursday | DayOfWeekFlags.Friday;
        const DayOfWeekFlags weekend = DayOfWeekFlags.Saturday | DayOfWeekFlags.Sunday;
        const DayOfWeekFlags all = weekdays | weekend;

        if (f == all)     return "Every day";
        if (f == weekdays) return "Weekdays";
        if (f == weekend)  return "Weekends";

        var names = new List<string>();
        if ((f & DayOfWeekFlags.Monday)    != 0) names.Add("Mon");
        if ((f & DayOfWeekFlags.Tuesday)   != 0) names.Add("Tue");
        if ((f & DayOfWeekFlags.Wednesday) != 0) names.Add("Wed");
        if ((f & DayOfWeekFlags.Thursday)  != 0) names.Add("Thu");
        if ((f & DayOfWeekFlags.Friday)    != 0) names.Add("Fri");
        if ((f & DayOfWeekFlags.Saturday)  != 0) names.Add("Sat");
        if ((f & DayOfWeekFlags.Sunday)    != 0) names.Add("Sun");
        return string.Join(", ", names);
    }

    private static string FormatTime(TimeOnly t)
    {
        int h = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        string ap = t.Hour < 12 ? "AM" : "PM";
        return t.Minute == 0 ? $"{h} {ap}" : $"{h}:{t.Minute:00} {ap}";
    }

    public AppTimeLimit ToModel() => new()
    {
        ExeName        = ExeName,
        DisplayName    = DisplayName,
        LimitType      = LimitType,
        LimitMinutes   = LimitMinutes,
        IntervalMinutes = IntervalMinutes,
        Schedule       = Schedule
    };
}
