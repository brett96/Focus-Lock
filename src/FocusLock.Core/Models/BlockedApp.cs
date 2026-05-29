namespace FocusLock.Core.Models;

public record BlockedApp(string DisplayName, string ExeName, IReadOnlyList<string>? ExeNames = null)
{
    /// <summary>All executable file names to redirect/kill for this installed application.</summary>
    public IReadOnlyList<string> GetExeNamesToBlock()
    {
        if (ExeNames is { Count: > 0 })
            return ExeNames;

        return [ExeName];
    }
}
