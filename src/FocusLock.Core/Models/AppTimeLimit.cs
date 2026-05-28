namespace FocusLock.Core.Models;

public class AppTimeLimit
{
    public string ExeName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public AppLimitType LimitType { get; set; }

    // DailyTotal: max minutes per day (within schedule window).
    // Interval:   max minutes per interval period.
    public int LimitMinutes { get; set; }

    // Interval only: length of each repeating period in minutes (e.g. 60).
    public int IntervalMinutes { get; set; } = 60;

    // null = inherit the global DailySchedule (or "always" if that is also null).
    public ScreenTimeSchedule? Schedule { get; set; }
}
