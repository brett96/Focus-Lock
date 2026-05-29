namespace FocusLock.Core.Models;

public class BedtimeRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ScreenTimeSchedule Schedule { get; set; } = new();
}
