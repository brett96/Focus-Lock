using System.Text.Json;
using FocusLock.Core.Models;

namespace FocusLock.Core.Storage;

public static class ScreenTimeStateRepository
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FocusLock");

    private static string StateFile => Path.Combine(DataDir, "screen-time-state.json");

    public static void Save(ScreenTimeState state)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
    }

    // Returns today's persisted state, or a fresh state if the file is missing/stale.
    public static ScreenTimeState LoadOrReset()
    {
        if (File.Exists(StateFile))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<ScreenTimeState>(File.ReadAllText(StateFile));
                if (saved is not null && saved.Date == DateOnly.FromDateTime(DateTime.Now))
                    return saved;
            }
            catch { }
        }
        return new ScreenTimeState { Date = DateOnly.FromDateTime(DateTime.Now) };
    }
}
