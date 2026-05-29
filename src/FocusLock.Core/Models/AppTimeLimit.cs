namespace FocusLock.Core.Models;

public class AppTimeLimit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ExeName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Related executables from the installed-app picker (e.g. all msedge*.exe in the install folder).</summary>
    public List<string>? ExeNames { get; set; }
    public AppLimitType LimitType { get; set; }

    // DailyTotal: max minutes per day (within schedule window).
    // Interval:   max minutes per interval period.
    public int LimitMinutes { get; set; }

    // Interval only: length of each repeating period in minutes (e.g. 60).
    public int IntervalMinutes { get; set; } = 60;

    // null = always active (all day, every day). Does not inherit the device daily limit schedule.
    public ScreenTimeSchedule? Schedule { get; set; }
}
