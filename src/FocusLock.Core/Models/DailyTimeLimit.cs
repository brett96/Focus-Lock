namespace FocusLock.Core.Models;

public class DailyTimeLimit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int LimitMinutes { get; set; }

    public ScreenTimeSchedule Schedule { get; set; } = new();
}
