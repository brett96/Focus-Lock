namespace FocusLock.Core.Models;

public class ScreenTimeState
{
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Now);
    public int TotalSecondsUsed { get; set; }
    public bool IsLockedOutForDay { get; set; }
    public Dictionary<string, AppUsageEntry> AppUsage { get; set; } = new();
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
}
