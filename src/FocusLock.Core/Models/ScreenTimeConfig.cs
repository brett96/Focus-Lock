namespace FocusLock.Core.Models;

public class ScreenTimeConfig
{
    /// <summary>Minimum device-wide daily screen time quota (does not apply to per-app limits).</summary>
    public const int MinDailyLimitMinutes = 5;

    public bool EnableDailyLimit { get; set; }
    public int DailyLimitMinutes { get; set; } = 120;

    // Legacy single-rule fields — migrated into DailyLimits on load.
    public ScreenTimeSchedule? DailySchedule { get; set; }

    public List<DailyTimeLimit> DailyLimits { get; set; } = new();

    public List<AppTimeLimit> AppLimits { get; set; } = new();
}
