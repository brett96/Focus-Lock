using System.Text.Json;
using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;

namespace FocusLock.Core.Storage;

public static class ScreenTimeConfigRepository
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FocusLock");

    private static string ConfigFile => Path.Combine(DataDir, "screen-time-config.json");

    public static void Save(ScreenTimeConfig config)
    {
        ScreenTimeConfigNormalizer.Normalize(config);
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config));
    }

    public static ScreenTimeConfig Load()
    {
        if (!File.Exists(ConfigFile)) return new ScreenTimeConfig();
        try
        {
            var config = JsonSerializer.Deserialize<ScreenTimeConfig>(File.ReadAllText(ConfigFile))
                ?? new ScreenTimeConfig();
            ScreenTimeConfigNormalizer.Normalize(config);
            return config;
        }
        catch { return new ScreenTimeConfig(); }
    }
}
