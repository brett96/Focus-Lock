using System.Text.Json;
using FocusLock.Core.Models;

namespace FocusLock.Core.Storage;

public static class ScreenTimeConfigRepository
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FocusLock");

    private static string ConfigFile => Path.Combine(DataDir, "screen-time-config.json");

    public static void Save(ScreenTimeConfig config)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config));
    }

    public static ScreenTimeConfig Load()
    {
        if (!File.Exists(ConfigFile)) return new ScreenTimeConfig();
        try
        {
            return JsonSerializer.Deserialize<ScreenTimeConfig>(File.ReadAllText(ConfigFile))
                ?? new ScreenTimeConfig();
        }
        catch { return new ScreenTimeConfig(); }
    }
}
