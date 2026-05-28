using System.Text.Json;
using FocusLock.Core.Models;

namespace FocusLock.Core.Storage;

public static class SessionRepository
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FocusLock");

    private static string SessionFile => Path.Combine(DataDir, "session.json");

    public static void Save(FocusSession? session)
    {
        Directory.CreateDirectory(DataDir);
        if (session is null)
            File.Delete(SessionFile);
        else
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(session));
    }

    public static FocusSession? Load()
    {
        if (!File.Exists(SessionFile)) return null;
        try
        {
            return JsonSerializer.Deserialize<FocusSession>(File.ReadAllText(SessionFile));
        }
        catch
        {
            return null;
        }
    }
}
