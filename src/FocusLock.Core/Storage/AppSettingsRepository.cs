using System.Text.Json;
using FocusLock.Core.Models;

namespace FocusLock.Core.Storage;

public static class AppSettingsRepository
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FocusLock");

    private static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings));
    }

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsFile)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }
}
