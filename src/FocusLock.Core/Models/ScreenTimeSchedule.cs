namespace FocusLock.Core.Models;

public class ScreenTimeSchedule
{
    public DayOfWeekFlags ActiveDays { get; set; } = (DayOfWeekFlags)127; // all 7 days
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }

    public bool IsActiveNow(DateTime localNow)
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
        if ((ActiveDays & flag) == 0) return false;
        if (StartTime is null || EndTime is null) return true;
        var tod = TimeOnly.FromDateTime(localNow);
        return tod >= StartTime.Value && tod < EndTime.Value;
    }

    public static ScreenTimeSchedule Always => new();
}
