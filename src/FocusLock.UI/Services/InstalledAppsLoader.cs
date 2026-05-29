using System.Diagnostics;
using System.IO;
using FocusLock.Core.Blocking;
using FocusLock.UI.ViewModels;
using Microsoft.Win32;

namespace FocusLock.UI.Services;

public static class InstalledAppsLoader
{
    private static readonly string[] ProgramFilesRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsApps",
        "Common Files",
        "Microsoft SDKs",
        "dotnet",
        "Package Cache",
        "Reference Assemblies",
        "Windows NT",
        "ModifiableWindowsApps",
        "MSBuild",
        "Windows Kits",
        "Microsoft Visual Studio",
        "Microsoft SQL Server",
        "Internet Explorer",
        "WindowsPowerShell",
    };

    private const int ProgramFilesMaxDepth = 4;

    public static Task<List<SelectableApp>> LoadAsync() => Task.Run(Load);

    public static List<SelectableApp> Load()
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries   = new List<AppEntry>();

        LoadFromRegistry(seenPaths, entries);
        LoadFromProgramFiles(seenPaths, entries);

        ApplyDisplayNameDisambiguation(entries);

        return entries
            .Select(e => new SelectableApp
            {
                DisplayName = e.DisplayName,
                ExeName     = e.ExeName,
                ExePath     = e.ExePath,
            })
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void LoadFromRegistry(HashSet<string> seenPaths, List<AppEntry> entries)
    {
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
                        TryAddEntry(seenPaths, entries, exePath);
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static void LoadFromProgramFiles(HashSet<string> seenPaths, List<AppEntry> entries)
    {
        foreach (var root in ProgramFilesRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            try
            {
                ScanProgramFilesDirectory(root, depth: 0, seenPaths, entries);
            }
            catch { }
        }
    }

    private static void ScanProgramFilesDirectory(
        string directory,
        int depth,
        HashSet<string> seenPaths,
        List<AppEntry> entries)
    {
        if (depth > ProgramFilesMaxDepth)
            return;

        try
        {
            foreach (var exePath in Directory.EnumerateFiles(directory, "*.exe"))
                TryAddEntry(seenPaths, entries, exePath);
        }
        catch { }

        if (depth == ProgramFilesMaxDepth)
            return;

        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(subDir);
                if (string.IsNullOrEmpty(name) || SkipDirectoryNames.Contains(name))
                    continue;

                ScanProgramFilesDirectory(subDir, depth + 1, seenPaths, entries);
            }
        }
        catch { }
    }

    private static void TryAddEntry(HashSet<string> seenPaths, List<AppEntry> entries, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        try
        {
            exePath = Path.GetFullPath(exePath);
        }
        catch
        {
            return;
        }

        if (!File.Exists(exePath) || !seenPaths.Add(exePath))
            return;

        var exeName = Path.GetFileName(exePath);
        if (SystemProcessList.IsSystemExe(exeName))
            return;

        var baseName = FileVersionInfo.GetVersionInfo(exePath).ProductName;
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = Path.GetFileNameWithoutExtension(exeName);

        entries.Add(new AppEntry(baseName!, exeName, exePath));
    }

    internal static void ApplyDisplayNameDisambiguation(List<AppEntry> entries)
    {
        foreach (var group in entries.GroupBy(e => e.BaseDisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                group.First().DisplayName = group.First().BaseDisplayName;
                continue;
            }

            foreach (var entry in group)
                entry.DisplayName = $"{entry.BaseDisplayName} ({FormatLocationDescriptor(entry.ExePath)})";
        }
    }

    internal static string FormatLocationDescriptor(string exePath)
    {
        try
        {
            exePath = Path.GetFullPath(exePath);
        }
        catch
        {
            return Path.GetDirectoryName(exePath) ?? exePath;
        }

        foreach (var root in ProgramFilesRoots.OrderByDescending(r => r.Length))
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var rootFull = Path.GetFullPath(root);
            if (!exePath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = exePath[rootFull.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parts    = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length <= 1)
                return Path.GetFileName(rootFull);

            // Drop the exe filename; keep install folder trail (e.g. Google\Chrome\Application).
            var folderParts = parts.Length > 1 ? parts[..^1] : parts;
            if (folderParts.Length == 0)
                return Path.GetFileName(rootFull);

            if (folderParts.Length > 2)
                return string.Join(" › ", folderParts.Take(2));

            return string.Join(" › ", folderParts);
        }

        var parent = Path.GetDirectoryName(exePath);
        return string.IsNullOrEmpty(parent) ? exePath : parent;
    }

    internal sealed class AppEntry(string baseDisplayName, string exeName, string exePath)
    {
        public string BaseDisplayName { get; } = baseDisplayName;
        public string ExeName         { get; } = exeName;
        public string ExePath         { get; } = exePath;
        public string DisplayName     { get; set; } = baseDisplayName;
    }
}
