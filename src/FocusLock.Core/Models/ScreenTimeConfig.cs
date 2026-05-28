namespace FocusLock.Core.Models;

public class ScreenTimeConfig
{
    public bool EnableDailyLimit { get; set; }
    public int DailyLimitMinutes { get; set; } = 120;

    // null = no schedule restriction (all day, every day).
    public ScreenTimeSchedule? DailySchedule { get; set; }

    public List<AppTimeLimit> AppLimits { get; set; } = new();
}
