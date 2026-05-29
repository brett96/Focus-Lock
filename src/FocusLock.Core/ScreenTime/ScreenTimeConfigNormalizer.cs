using FocusLock.Core.Models;

namespace FocusLock.Core.ScreenTime;

public static class ScreenTimeConfigNormalizer
{
    public static void Normalize(ScreenTimeConfig config)
    {
        if (config.DailyLimits.Count == 0 && config.EnableDailyLimit)
        {
            config.DailyLimits.Add(new DailyTimeLimit
            {
                LimitMinutes = Math.Max(ScreenTimeConfig.MinDailyLimitMinutes, config.DailyLimitMinutes),
                Schedule = config.DailySchedule is null
                    ? new ScreenTimeSchedule()
                    : CloneSchedule(config.DailySchedule)
            });
        }

        foreach (var rule in config.DailyLimits)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
                rule.Id = Guid.NewGuid().ToString("N");
            rule.LimitMinutes = Math.Max(ScreenTimeConfig.MinDailyLimitMinutes, rule.LimitMinutes);
        }

        foreach (var app in config.AppLimits)
        {
            if (string.IsNullOrWhiteSpace(app.Id))
                app.Id = Guid.NewGuid().ToString("N");
        }

        foreach (var bedtime in config.Bedtimes)
        {
            if (string.IsNullOrWhiteSpace(bedtime.Id))
                bedtime.Id = Guid.NewGuid().ToString("N");
        }

        config.EnableDailyLimit = config.DailyLimits.Count > 0;

        if (config.DailyLimits.Count > 0)
        {
            var first = config.DailyLimits[0];
            config.DailyLimitMinutes = first.LimitMinutes;
            config.DailySchedule = IsAllDayEveryDay(first.Schedule) ? null : CloneSchedule(first.Schedule);
        }
        else
        {
            config.DailySchedule = null;
        }
    }

    private static bool IsAllDayEveryDay(ScreenTimeSchedule schedule)
        => !ScreenTimeScheduleHelper.HasTimedWindow(schedule)
           && schedule.ActiveDays == (DayOfWeekFlags)127;

    private static ScreenTimeSchedule CloneSchedule(ScreenTimeSchedule source) => new()
    {
        ActiveDays = source.ActiveDays,
        StartTime  = source.StartTime,
        EndTime    = source.EndTime
    };
}
