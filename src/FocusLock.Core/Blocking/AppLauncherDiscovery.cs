using System.Diagnostics;

namespace FocusLock.Core.Blocking;

/// <summary>
/// Picks main launcher executables from uninstall-registry metadata (avoids uninstallers/setup tools).
/// </summary>
public static class AppLauncherDiscovery
{
    private static readonly string[] UtilityNameFragments =
    [
        "uninstall", "unins", "setup", "installer", "update", "updater", "patch",
        "repair", "redist", "crash", "error", "reporter", "helper", "service",
        "bootstrap", "dxsetup", "vcredist", "dotnet",
    ];

    public static bool IsUtilityExecutable(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (baseName.Length < 2)
            return true;

        foreach (var fragment in UtilityNameFragments)
        {
            if (baseName.Contains(fragment, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static int ScoreLauncherCandidate(string fullPath, string displayName, string? installLocation)
    {
        if (!File.Exists(fullPath))
            return int.MinValue;

        var fileName = Path.GetFileName(fullPath);
        if (IsUtilityExecutable(fileName))
            return int.MinValue + 1;

        int score = 0;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (BlockedAppMatcher.ApplicationNameMatches(baseName, displayName))
            score += 80;

        var product = BlockedAppMatcher.TryGetProductName(fullPath);
        if (BlockedAppMatcher.ApplicationNameMatches(product, displayName))
            score += 70;

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            try
            {
                var root = Path.GetFullPath(installLocation.TrimEnd(Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(Path.GetFullPath(fullPath));
                if (dir is not null && dir.Equals(root, StringComparison.OrdinalIgnoreCase))
                    score += 50;
            }
            catch { }
        }

        if (fileName.Equals(displayName + ".exe", StringComparison.OrdinalIgnoreCase))
            score += 40;

        var safe = SanitizeToken(displayName);
        if (!string.IsNullOrEmpty(safe)
            && fileName.Equals(safe + ".exe", StringComparison.OrdinalIgnoreCase))
            score += 35;

        return score;
    }

    public static string? PickBestLauncher(
        IEnumerable<string> candidates,
        string displayName,
        string? installLocation)
    {
        string? best = null;
        int bestScore = int.MinValue;

        foreach (var path in candidates)
        {
            int score = ScoreLauncherCandidate(path, displayName, installLocation);
            if (score > bestScore)
            {
                bestScore = score;
                best = path;
            }
        }

        return best;
    }

    public static IEnumerable<string> CollectRootInstallExes(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            yield break;

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(installLocation, "*.exe");
        }
        catch
        {
            yield break;
        }

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (IsUtilityExecutable(name))
                continue;
            if (SystemProcessList.IsProtectedFromBlocking(name, path))
                continue;
            yield return name;
        }
    }

    private static string SanitizeToken(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
