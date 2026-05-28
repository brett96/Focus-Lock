using System.Diagnostics;
using System.IO;
using FocusLock.Core.Blocking;
using FocusLock.UI.ViewModels;
using Microsoft.Win32;

namespace FocusLock.UI.Services;

public static class InstalledAppsLoader
{
    public static Task<List<SelectableApp>> LoadAsync() => Task.Run(Load);

    public static List<SelectableApp> Load()
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SelectableApp>();

        var specs = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser,  RegistryView.Default),
        };

        foreach (var (hive, view) in specs)
        {
            try
            {
                using var baseKey  = RegistryKey.OpenBaseKey(hive, view);
                using var appPaths = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (appPaths is null) continue;

                foreach (var keyName in appPaths.GetSubKeyNames())
                {
                    try
                    {
                        using var sub     = appPaths.OpenSubKey(keyName);
                        var       exePath = (sub?.GetValue(string.Empty) as string)?.Trim().Trim('"');
                        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) continue;

                        var exeName = Path.GetFileName(exePath);
                        if (!seen.Add(exeName)) continue;
                        if (SystemProcessList.IsSystemExe(exeName)) continue;

                        var displayName = FileVersionInfo.GetVersionInfo(exePath).ProductName;
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = Path.GetFileNameWithoutExtension(exeName);

                        result.Add(new SelectableApp { DisplayName = displayName!, ExeName = exeName });
                    }
                    catch { }
                }
            }
            catch { }
        }

        return result.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
