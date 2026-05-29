using System.Diagnostics;
using System.IO;
using FocusLock.Core.Blocking;
using FocusLock.UI.ViewModels;
using Microsoft.Win32;

namespace FocusLock.UI.Services;

/// <summary>
/// Builds an installed-applications list from the Windows Uninstall registry (same source as Settings → Apps).
/// One entry per display name; related executables are grouped for blocking.
/// </summary>
public static class InstalledAppsLoader
{
    private static readonly (RegistryHive Hive, RegistryView View, string SubKey)[] UninstallSources =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Default,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    public static Task<List<SelectableApp>> LoadAsync() => Task.Run(Load);

    public static List<SelectableApp> Load()
    {
        var byDisplayName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, subKey) in UninstallSources)
            LoadFromUninstallHive(hive, view, subKey, byDisplayName);

        return byDisplayName.Values
            .Select(e => new SelectableApp
            {
                DisplayName = e.DisplayName,
                ExeName     = e.PrimaryExeName,
                ExePath     = e.PrimaryExePath,
                ExeNames    = e.ExeNames.ToList(),
            })
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void LoadFromUninstallHive(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        Dictionary<string, AppEntry> byDisplayName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(subKeyPath);
            if (uninstallKey is null) return;

            foreach (var keyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var sub = uninstallKey.OpenSubKey(keyName);
                    if (sub is null || ShouldSkipUninstallEntry(sub)) continue;

                    var displayName = (sub.GetValue("DisplayName") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    var installLocation = NormalizePath(sub.GetValue("InstallLocation") as string);
                    var primaryPath     = ResolvePrimaryExecutable(sub, displayName, installLocation);
                    if (primaryPath is null) continue;
                    if (SystemProcessList.IsProtectedFromBlocking(Path.GetFileName(primaryPath), primaryPath))
                        continue;

                    var exeNames = CollectRelatedExeNames(displayName, primaryPath, installLocation);

                    if (byDisplayName.TryGetValue(displayName, out var existing))
                        MergeEntry(existing, primaryPath, exeNames);
                    else
                        byDisplayName[displayName] = CreateEntry(displayName, primaryPath, exeNames);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool ShouldSkipUninstallEntry(RegistryKey key)
    {
        if (GetDword(key, "SystemComponent") == 1) return true;
        if (GetDword(key, "NoDisplay") == 1) return true;
        if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent))
            return true;

        var name = (key.GetValue("DisplayName") as string)?.Trim() ?? string.Empty;
        if (name.StartsWith("KB", StringComparison.OrdinalIgnoreCase) && name.Contains("Security Update"))
            return true;
        if (name.StartsWith("Update for", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static AppEntry CreateEntry(string displayName, string primaryPath, HashSet<string> exeNames)
    {
        var entry = new AppEntry
        {
            DisplayName    = displayName,
            PrimaryExePath = primaryPath,
            PrimaryExeName = Path.GetFileName(primaryPath),
        };
        foreach (var exe in exeNames)
            entry.ExeNames.Add(exe);
        return entry;
    }

    private static void MergeEntry(AppEntry existing, string primaryPath, HashSet<string> exeNames)
    {
        foreach (var exe in exeNames)
            existing.ExeNames.Add(exe);

        // Prefer an entry whose primary exe lives outside Windows and has a valid path.
        if (ShouldPreferPrimary(primaryPath, existing.PrimaryExePath))
        {
            existing.PrimaryExePath = primaryPath;
            existing.PrimaryExeName = Path.GetFileName(primaryPath);
        }
    }

    private static bool ShouldPreferPrimary(string candidate, string current)
    {
        if (!File.Exists(current) && File.Exists(candidate))
            return true;

        bool candidateProtected = SystemProcessList.IsProtectedFromBlocking(
            Path.GetFileName(candidate), candidate);
        bool currentProtected = SystemProcessList.IsProtectedFromBlocking(
            Path.GetFileName(current), current);

        if (currentProtected && !candidateProtected)
            return true;

        return false;
    }

    private static HashSet<string> CollectRelatedExeNames(
        string displayName,
        string primaryPath,
        string? installLocation)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(primaryPath),
        };

        var primaryProduct = BlockedAppMatcher.TryGetProductName(primaryPath) ?? displayName;

        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
            ScanInstallFolder(installLocation, displayName, primaryProduct, names, depth: 0);

        return names;
    }

    private static void ScanInstallFolder(
        string directory,
        string displayName,
        string primaryProduct,
        HashSet<string> names,
        int depth)
    {
        if (depth > 2) return;

        try
        {
            foreach (var exePath in Directory.EnumerateFiles(directory, "*.exe"))
            {
                if (SystemProcessList.IsProtectedFromBlocking(Path.GetFileName(exePath), exePath))
                    continue;

                var product = BlockedAppMatcher.TryGetProductName(exePath);
                if (BlockedAppMatcher.ApplicationNameMatches(product, displayName)
                    || BlockedAppMatcher.ApplicationNameMatches(product, primaryProduct)
                    || BlockedAppMatcher.ApplicationNameMatches(
                        Path.GetFileNameWithoutExtension(exePath), displayName))
                {
                    names.Add(Path.GetFileName(exePath));
                }
            }
        }
        catch { }

        if (depth == 2) return;

        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(directory))
                ScanInstallFolder(subDir, displayName, primaryProduct, names, depth + 1);
        }
        catch { }
    }

    private static string? ResolvePrimaryExecutable(
        RegistryKey key,
        string displayName,
        string? installLocation)
    {
        foreach (var candidate in EnumerateExecutableCandidates(key, displayName, installLocation))
        {
            if (!File.Exists(candidate)) continue;
            if (!candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (SystemProcessList.IsProtectedFromBlocking(Path.GetFileName(candidate), candidate))
                continue;
            return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateExecutableCandidates(
        RegistryKey key,
        string displayName,
        string? installLocation)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in new[]
                 {
                     ExtractExecutablePath(key.GetValue("DisplayIcon") as string),
                     ExtractExecutablePath(key.GetValue("QuietUninstallString") as string),
                     ExtractExecutablePath(key.GetValue("UninstallString") as string),
                 })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (seen.Add(path))
                yield return path;
        }

        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            yield break;

        var safeName = SanitizeForFileName(displayName);
        foreach (var guess in new[]
                 {
                     Path.Combine(installLocation, safeName + ".exe"),
                     Path.Combine(installLocation, displayName + ".exe"),
                 })
        {
            if (seen.Add(guess))
                yield return guess;
        }

        var installExes = new List<string>();
        try
        {
            installExes.AddRange(Directory.EnumerateFiles(installLocation, "*.exe"));
        }
        catch { }

        foreach (var exe in installExes)
        {
            if (seen.Add(exe))
                yield return exe;
        }
    }

    private static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        value = value.Trim().Trim('"');
        var comma = value.IndexOf(',');
        if (comma >= 0)
            value = value[..comma].Trim().Trim('"');

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return value;

        return null;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static string SanitizeForFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static int GetDword(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int i => i,
            byte b => b,
            _ => 0,
        };
    }

    private sealed class AppEntry
    {
        public required string DisplayName { get; init; }
        public required string PrimaryExeName { get; set; }
        public required string PrimaryExePath { get; set; }
        public HashSet<string> ExeNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
