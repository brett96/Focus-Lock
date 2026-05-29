using FocusLock.Core.Models;

namespace FocusLock.Core.ScreenTime;

public readonly record struct BedtimeOccurrence(
    BedtimeRule Rule,
    DateTime StartsAtLocal,
    DateTime EndsAtLocal);

public static class BedtimeScheduleHelper
{
    public static bool IsValidBedtimeSchedule(ScreenTimeSchedule schedule)
    {
        if (schedule.ActiveDays == DayOfWeekFlags.None)
            return false;
        if (schedule.StartTime is null || schedule.EndTime is null)
            return false;
        return schedule.StartTime.Value != schedule.EndTime.Value;
    }

    public static bool IsOvernight(ScreenTimeSchedule schedule)
        => schedule.StartTime is not null
           && schedule.EndTime is not null
           && schedule.EndTime.Value <= schedule.StartTime.Value;

    public static bool IsBedtimeActive(ScreenTimeSchedule schedule, DateTime localNow)
    {
        if (!IsValidBedtimeSchedule(schedule))
            return false;

        var tod = TimeOnly.FromDateTime(localNow);
        var start = schedule.StartTime!.Value;
        var end   = schedule.EndTime!.Value;

        if (IsDayActive(schedule, localNow.DayOfWeek))
        {
            if (IsOvernight(schedule))
            {
                if (tod >= start)
                    return true;
            }
            else if (tod >= start && tod < end)
            {
                return true;
            }
        }

        if (IsOvernight(schedule))
        {
            var previousDay = localNow.AddDays(-1).DayOfWeek;
            if (IsDayActive(schedule, previousDay) && tod < end)
                return true;
        }

        return false;
    }

    public static BedtimeRule? FindActiveBedtime(IReadOnlyList<BedtimeRule> rules, DateTime localNow)
    {
        foreach (var rule in rules)
        {
            if (IsBedtimeActive(rule.Schedule, localNow))
                return rule;
        }
        return null;
    }

    public static List<BedtimeOccurrence> GetOccurrencesInRange(
        IReadOnlyList<BedtimeRule> rules,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        var results = new List<BedtimeOccurrence>();
        foreach (var rule in rules)
        {
            foreach (var (start, end) in GetOccurrences(rule.Schedule, rangeStart, rangeEnd))
                results.Add(new BedtimeOccurrence(rule, start, end));
        }

        results.Sort((a, b) => a.StartsAtLocal.CompareTo(b.StartsAtLocal));
        return results;
    }

    public static IEnumerable<(DateTime Start, DateTime End)> GetOccurrences(
        ScreenTimeSchedule schedule,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        if (!IsValidBedtimeSchedule(schedule))
            yield break;

        for (var day = rangeStart.Date; day <= rangeEnd.Date; day = day.AddDays(1))
        {
            if (!IsDayActive(schedule, day.DayOfWeek))
                continue;

            var start = day + schedule.StartTime!.Value.ToTimeSpan();
            var end   = day + schedule.EndTime!.Value.ToTimeSpan();
            if (IsOvernight(schedule))
                end = end.AddDays(1);

            if (end <= rangeStart || start >= rangeEnd)
                continue;

            yield return (start, end);
        }
    }

    /// <summary>
    /// True when two schedules would both be active at some point within the next two weeks.
    /// </summary>
    public static bool SchedulesConflict(ScreenTimeSchedule a, ScreenTimeSchedule b)
    {
        var rangeStart = DateTime.Today;
        var rangeEnd   = rangeStart.AddDays(14);
        foreach (var occA in ExpandScheduleOccurrences(a, rangeStart, rangeEnd))
        foreach (var occB in ExpandScheduleOccurrences(b, rangeStart, rangeEnd))
        {
            if (occA.Start < occB.End && occB.Start < occA.End)
                return true;
        }
        return false;
    }

    public static string Describe(ScreenTimeSchedule schedule)
    {
        var days = DescribeDays(schedule.ActiveDays);
        if (schedule.StartTime is null || schedule.EndTime is null)
            return days;

        var window = $"{FormatTime(schedule.StartTime.Value)}–{FormatTime(schedule.EndTime.Value)}";
        if (IsOvernight(schedule))
            window += " (overnight)";
        return $"{days}, {window}";
    }

    private static IEnumerable<(DateTime Start, DateTime End)> ExpandScheduleOccurrences(
        ScreenTimeSchedule schedule,
        DateTime rangeStart,
        DateTime rangeEnd)
    {
        if (IsValidBedtimeSchedule(schedule))
        {
            foreach (var occ in GetOccurrences(schedule, rangeStart, rangeEnd))
                yield return occ;
            yield break;
        }

        if (schedule.ActiveDays == DayOfWeekFlags.None)
            yield break;

        if (!ScreenTimeScheduleHelper.HasTimedWindow(schedule))
        {
            for (var day = rangeStart.Date; day <= rangeEnd.Date; day = day.AddDays(1))
            {
                if (!IsDayActive(schedule, day.DayOfWeek))
                    continue;
                var start = day;
                var end   = day.AddDays(1);
                if (end > rangeStart && start < rangeEnd)
                    yield return (start, end);
            }
            yield break;
        }

        for (var day = rangeStart.Date; day <= rangeEnd.Date; day = day.AddDays(1))
        {
            if (!IsDayActive(schedule, day.DayOfWeek))
                continue;

            var start = day + schedule.StartTime!.Value.ToTimeSpan();
            var end   = day + schedule.EndTime!.Value.ToTimeSpan();
            if (end <= start)
                end = end.AddDays(1);

            if (end > rangeStart && start < rangeEnd)
                yield return (start, end);
        }
    }

    private static bool IsDayActive(ScreenTimeSchedule schedule, DayOfWeek dayOfWeek)
        => ScreenTimeScheduleHelper.IsDayActive(schedule, dayOfWeek);

    private static string DescribeDays(DayOfWeekFlags f)
    {
        const DayOfWeekFlags all = (DayOfWeekFlags)127;
        const DayOfWeekFlags weekdays = DayOfWeekFlags.Monday | DayOfWeekFlags.Tuesday |
            DayOfWeekFlags.Wednesday | DayOfWeekFlags.Thursday | DayOfWeekFlags.Friday;
        const DayOfWeekFlags weekend = DayOfWeekFlags.Saturday | DayOfWeekFlags.Sunday;

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
