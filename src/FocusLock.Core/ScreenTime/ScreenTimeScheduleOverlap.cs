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
