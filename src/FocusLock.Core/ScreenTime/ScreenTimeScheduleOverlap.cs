using FocusLock.Core.Models;

namespace FocusLock.Core.ScreenTime;

public static class ScreenTimeScheduleOverlap
{
    /// <summary>
    /// True when both schedules share at least one active day and their time windows intersect on that day.
    /// All-day windows (no start/end) overlap any other window on a shared day.
    /// </summary>
    public static bool SchedulesOverlap(ScreenTimeSchedule a, ScreenTimeSchedule b)
    {
        if (a.ActiveDays == DayOfWeekFlags.None || b.ActiveDays == DayOfWeekFlags.None)
            return false;

        if ((a.ActiveDays & b.ActiveDays) == 0)
            return false;

        bool aAllDay = !ScreenTimeScheduleHelper.HasTimedWindow(a);
        bool bAllDay = !ScreenTimeScheduleHelper.HasTimedWindow(b);
        if (aAllDay || bAllDay)
            return true;

        return a.StartTime!.Value < b.EndTime!.Value
            && b.StartTime!.Value < a.EndTime!.Value;
    }

    public static bool TryFindBedtimeOverlap(
        IReadOnlyList<BedtimeRule> rules,
        ScreenTimeSchedule candidate,
        out string? message,
        string? excludeId = null)
    {
        foreach (var rule in rules)
        {
            if (excludeId is not null && rule.Id == excludeId)
                continue;

            if (!BedtimeScheduleHelper.SchedulesConflict(candidate, rule.Schedule))
                continue;

            message = $"This schedule overlaps with an existing bedtime ({DescribeBedtime(rule)}).";
            return true;
        }

        message = null;
        return false;
    }

    public static bool TryFindBedtimeLimitConflict(
        IReadOnlyList<BedtimeRule> bedtimes,
        ScreenTimeSchedule candidate,
        IReadOnlyList<DailyTimeLimit> dailyLimits,
        IReadOnlyList<AppTimeLimit> appLimits,
        out string? message,
        string? excludeBedtimeId = null)
    {
        foreach (var rule in dailyLimits)
        {
            if (!BedtimeScheduleHelper.SchedulesConflict(candidate, rule.Schedule))
                continue;

            message = $"This schedule overlaps with daily limit {DescribeRule(rule)}.";
            return true;
        }

        foreach (var rule in appLimits)
        {
            var existing = rule.Schedule ?? ScreenTimeSchedule.Always;
            if (!BedtimeScheduleHelper.SchedulesConflict(candidate, existing))
                continue;

            message = $"This schedule overlaps with app limit for {rule.DisplayName} ({DescribeAppRule(rule)}).";
            return true;
        }

        return TryFindBedtimeOverlap(bedtimes, candidate, out message, excludeBedtimeId);
    }

    public static bool TryFindDailyLimitBedtimeConflict(
        IReadOnlyList<BedtimeRule> bedtimes,
        ScreenTimeSchedule candidate,
        IReadOnlyList<DailyTimeLimit> dailyLimits,
        out string? message,
        string? excludeDailyId = null)
    {
        foreach (var bedtime in bedtimes)
        {
            if (!BedtimeScheduleHelper.SchedulesConflict(candidate, bedtime.Schedule))
                continue;

            message = $"This schedule overlaps with bedtime {DescribeBedtime(bedtime)}.";
            return true;
        }

        return TryFindDailyLimitOverlap(dailyLimits, candidate, out message, excludeDailyId);
    }

    public static bool TryFindAppLimitBedtimeConflict(
        IReadOnlyList<BedtimeRule> bedtimes,
        string exeName,
        ScreenTimeSchedule candidate,
        IReadOnlyList<AppTimeLimit> appLimits,
        out string? message,
        string? excludeAppId = null)
    {
        foreach (var bedtime in bedtimes)
        {
            if (!BedtimeScheduleHelper.SchedulesConflict(candidate, bedtime.Schedule))
                continue;

            message = $"This schedule overlaps with bedtime {DescribeBedtime(bedtime)}.";
            return true;
        }

        return TryFindAppLimitOverlap(appLimits, exeName, candidate, out message, excludeAppId);
    }

    public static bool HasInternalBedtimeOverlap(IReadOnlyList<BedtimeRule> rules, out string? message)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            for (int j = i + 1; j < rules.Count; j++)
            {
                if (!BedtimeScheduleHelper.SchedulesConflict(rules[i].Schedule, rules[j].Schedule))
                    continue;

                message = $"Bedtimes overlap: {DescribeBedtime(rules[i])} and {DescribeBedtime(rules[j])}.";
                return true;
            }
        }

        message = null;
        return false;
    }

    public static bool HasBedtimeLimitConflicts(
        IReadOnlyList<BedtimeRule> bedtimes,
        IReadOnlyList<DailyTimeLimit> dailyLimits,
        IReadOnlyList<AppTimeLimit> appLimits,
        out string? message)
    {
        if (HasInternalBedtimeOverlap(bedtimes, out message))
            return true;

        foreach (var bedtime in bedtimes)
        {
            foreach (var daily in dailyLimits)
            {
                if (!BedtimeScheduleHelper.SchedulesConflict(bedtime.Schedule, daily.Schedule))
                    continue;

                message = $"Bedtime {DescribeBedtime(bedtime)} overlaps with daily limit {DescribeRule(daily)}.";
                return true;
            }

            foreach (var app in appLimits)
            {
                var schedule = app.Schedule ?? ScreenTimeSchedule.Always;
                if (!BedtimeScheduleHelper.SchedulesConflict(bedtime.Schedule, schedule))
                    continue;

                message = $"Bedtime {DescribeBedtime(bedtime)} overlaps with app limit for {app.DisplayName}.";
                return true;
            }
        }

        message = null;
        return false;
    }

    public static bool TryFindDailyLimitOverlap(
        IReadOnlyList<DailyTimeLimit> rules,
        ScreenTimeSchedule candidate,
        out string? message,
        string? excludeId = null)
    {
        foreach (var rule in rules)
        {
            if (excludeId is not null && rule.Id == excludeId)
                continue;

            if (!SchedulesOverlap(candidate, rule.Schedule))
                continue;

            message = $"This schedule overlaps with an existing daily limit ({DescribeRule(rule)}).";
            return true;
        }

        message = null;
        return false;
    }

    public static bool TryFindAppLimitOverlap(
        IReadOnlyList<AppTimeLimit> rules,
        string exeName,
        ScreenTimeSchedule candidate,
        out string? message,
        string? excludeId = null)
    {
        foreach (var rule in rules)
        {
            if (excludeId is not null && rule.Id == excludeId)
                continue;
            if (!string.Equals(rule.ExeName, exeName, StringComparison.OrdinalIgnoreCase))
                continue;

            var existing = rule.Schedule ?? ScreenTimeSchedule.Always;
            if (!SchedulesOverlap(candidate, existing))
                continue;

            message = $"This schedule overlaps with an existing limit for {rule.DisplayName} ({DescribeAppRule(rule)}).";
            return true;
        }

        message = null;
        return false;
    }

    public static bool HasInternalDailyLimitOverlap(IReadOnlyList<DailyTimeLimit> rules, out string? message)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            for (int j = i + 1; j < rules.Count; j++)
            {
                if (!SchedulesOverlap(rules[i].Schedule, rules[j].Schedule))
                    continue;

                message = $"Daily limits overlap: {DescribeRule(rules[i])} and {DescribeRule(rules[j])}.";
                return true;
            }
        }

        message = null;
        return false;
    }

    public static bool HasInternalAppLimitOverlap(IReadOnlyList<AppTimeLimit> rules, out string? message)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            for (int j = i + 1; j < rules.Count; j++)
            {
                if (!string.Equals(rules[i].ExeName, rules[j].ExeName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var a = rules[i].Schedule ?? ScreenTimeSchedule.Always;
                var b = rules[j].Schedule ?? ScreenTimeSchedule.Always;
                if (!SchedulesOverlap(a, b))
                    continue;

                message = $"App limits overlap for {rules[i].DisplayName}: {DescribeAppRule(rules[i])} and {DescribeAppRule(rules[j])}.";
                return true;
            }
        }

        message = null;
        return false;
    }

    private static string DescribeBedtime(BedtimeRule rule)
        => BedtimeScheduleHelper.Describe(rule.Schedule);

    private static string DescribeRule(DailyTimeLimit rule)
        => $"{FormatMinutes(rule.LimitMinutes)}, {ScreenTimeScheduleHelper.Describe(rule.Schedule)}";

    private static string DescribeAppRule(AppTimeLimit rule)
    {
        var sched = rule.Schedule is null
            ? "always"
            : ScreenTimeScheduleHelper.Describe(rule.Schedule);
        return $"{rule.LimitType}: {FormatMinutes(rule.LimitMinutes)} · {sched}";
    }

    private static string FormatMinutes(int minutes)
    {
        int h = minutes / 60, m = minutes % 60;
        return h > 0 && m > 0 ? $"{h}h {m}m" : h > 0 ? $"{h}h" : $"{m}m";
    }
}
