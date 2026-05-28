using FocusLock.Core.Models;

namespace FocusLock.Core.ScreenTime;

public enum DailySchedulePhase
{
    Active,
    BeforeWindowToday,
    AfterWindowToday,
    InactiveDay
}

public static class ScreenTimeScheduleHelper
{
    public static bool HasTimedWindow(ScreenTimeSchedule? schedule)
        => schedule?.StartTime is not null && schedule.EndTime is not null;

    public static DailySchedulePhase GetPhase(ScreenTimeSchedule schedule, DateTime localNow)
    {
        var flag = localNow.DayOfWeek switch
        {
            DayOfWeek.Monday    => DayOfWeekFlags.Monday,
            DayOfWeek.Tuesday   => DayOfWeekFlags.Tuesday,
            DayOfWeek.Wednesday => DayOfWeekFlags.Wednesday,
            DayOfWeek.Thursday  => DayOfWeekFlags.Thursday,
            DayOfWeek.Friday    => DayOfWeekFlags.Friday,
            DayOfWeek.Saturday  => DayOfWeekFlags.Saturday,
            DayOfWeek.Sunday    => DayOfWeekFlags.Sunday,
            _                   => DayOfWeekFlags.None
        };
        if ((schedule.ActiveDays & flag) == 0)
            return DailySchedulePhase.InactiveDay;

        if (!HasTimedWindow(schedule))
            return DailySchedulePhase.Active;

        var tod = TimeOnly.FromDateTime(localNow);
        if (tod < schedule.StartTime!.Value)
            return DailySchedulePhase.BeforeWindowToday;
        if (tod >= schedule.EndTime!.Value)
            return DailySchedulePhase.AfterWindowToday;
        return DailySchedulePhase.Active;
    }

    public static bool IsEnforcementActive(ScreenTimeSchedule schedule, DateTime localNow)
        => GetPhase(schedule, localNow) == DailySchedulePhase.Active;

    public static string Describe(ScreenTimeSchedule schedule)
    {
        var days = DescribeDays(schedule.ActiveDays);
        if (!HasTimedWindow(schedule))
            return days == "Every day" ? "All day, every day" : days;

        return $"{days}, {FormatTime(schedule.StartTime!.Value)}–{FormatTime(schedule.EndTime!.Value)}";
    }

    /// <summary>Next local time the schedule becomes active (null if always active now).</summary>
    public static DateTime? GetNextActiveStart(ScreenTimeSchedule schedule, DateTime localNow)
    {
        if (schedule.IsActiveNow(localNow))
            return null;

        for (int d = 0; d <= 7; d++)
        {
            var day = localNow.Date.AddDays(d);
            if (!IsDayActive(schedule, day.DayOfWeek))
                continue;

            if (schedule.StartTime is { } start)
            {
                var candidate = day + start.ToTimeSpan();
                if (candidate > localNow)
                    return candidate;
                continue;
            }

            // All day on an upcoming active day.
            if (day > localNow.Date || localNow.TimeOfDay > TimeSpan.Zero)
                return day;
        }

        return null;
    }

    private static bool IsDayActive(ScreenTimeSchedule schedule, DayOfWeek dayOfWeek)
    {
        var flag = dayOfWeek switch
        {
            DayOfWeek.Monday    => DayOfWeekFlags.Monday,
            DayOfWeek.Tuesday   => DayOfWeekFlags.Tuesday,
            DayOfWeek.Wednesday => DayOfWeekFlags.Wednesday,
            DayOfWeek.Thursday  => DayOfWeekFlags.Thursday,
            DayOfWeek.Friday    => DayOfWeekFlags.Friday,
            DayOfWeek.Saturday  => DayOfWeekFlags.Saturday,
            DayOfWeek.Sunday    => DayOfWeekFlags.Sunday,
            _                   => DayOfWeekFlags.None
        };
        return (schedule.ActiveDays & flag) != 0;
    }

    private static string DescribeDays(DayOfWeekFlags f)
    {
        const DayOfWeekFlags weekdays = DayOfWeekFlags.Monday | DayOfWeekFlags.Tuesday |
            DayOfWeekFlags.Wednesday | DayOfWeekFlags.Thursday | DayOfWeekFlags.Friday;
        const DayOfWeekFlags weekend = DayOfWeekFlags.Saturday | DayOfWeekFlags.Sunday;
        const DayOfWeekFlags all = weekdays | weekend;

        if (f == all) return "Every day";
        if (f == weekdays) return "Weekdays";
        if (f == weekend) return "Weekends";

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
}
