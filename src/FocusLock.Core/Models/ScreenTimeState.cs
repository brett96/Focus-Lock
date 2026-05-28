namespace FocusLock.Core.Models;

public class ScreenTimeState
{
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    // Legacy — kept for JSON compatibility; per-rule tracking uses DailyRuleUsage.
    public int TotalSecondsUsed { get; set; }
    public bool IsLockedOutForDay { get; set; }
    public bool DailyWarned5Min { get; set; }
    public bool DailyWarned1Min { get; set; }

    public Dictionary<string, DailyRuleUsageEntry> DailyRuleUsage { get; set; } = new();
    public Dictionary<string, AppUsageEntry> AppUsage { get; set; } = new();
}

public class DailyRuleUsageEntry
{
    public int TotalSecondsUsed { get; set; }
    public bool IsLockedOut { get; set; }
    public bool Warned5Min { get; set; }
    public bool Warned1Min { get; set; }
}

public class AppUsageEntry
{
    public string ExeName { get; set; } = string.Empty;

    // DailyTotal tracking
    public int TotalSecondsToday { get; set; }
    public bool IsBlockedToday { get; set; }

    // Interval tracking
    public long CurrentIntervalIndex { get; set; } = -1;
    public int CurrentIntervalSecondsUsed { get; set; }
    public bool IsBlockedInCurrentInterval { get; set; }
    public bool Warned5Min { get; set; }
    public bool Warned1Min { get; set; }
}
