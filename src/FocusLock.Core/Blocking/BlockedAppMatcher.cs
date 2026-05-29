using System.Diagnostics;
using FocusLock.Core.Models;

namespace FocusLock.Core.Blocking;

/// <summary>
/// Matches running or launching executables against session block entries by exe name and application display name.
/// </summary>
public static class BlockedAppMatcher
{
    public static bool IsBlocked(string exeName, BlockedApp app, string? exeFullPath = null)
    {
        exeName = SystemProcessList.NormalizeExeName(exeName);

        foreach (var blockedExe in app.GetExeNamesToBlock())
        {
            if (string.Equals(
                    SystemProcessList.NormalizeExeName(blockedExe),
                    exeName,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (ApplicationNameMatches(exeName, app.DisplayName))
            return true;

        if (!string.IsNullOrWhiteSpace(exeFullPath))
        {
            var product = TryGetProductName(exeFullPath);
            if (ApplicationNameMatches(product, app.DisplayName))
                return true;
        }

        return false;
    }

    public static bool IsBlocked(string exeName, IEnumerable<BlockedApp> apps, string? exeFullPath = null)
        => apps.Any(app => IsBlocked(exeName, app, exeFullPath));

    public static bool ApplicationNameMatches(string? candidate, string? blockedDisplayName)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(blockedDisplayName))
            return false;

        candidate = candidate.Trim();
        blockedDisplayName = blockedDisplayName.Trim();

        if (candidate.Equals(blockedDisplayName, StringComparison.OrdinalIgnoreCase))
            return true;

        var candidateBase = Path.GetFileNameWithoutExtension(candidate);
        if (candidateBase.Equals(blockedDisplayName, StringComparison.OrdinalIgnoreCase))
            return true;

        // "steam" matches display name "Steam Client"; "Notepad++" stays distinct from "notepad".
        if (MatchesFirstSignificantToken(candidateBase, blockedDisplayName))
            return true;

        return false;
    }

    private static bool MatchesFirstSignificantToken(string candidateBase, string blockedDisplayName)
    {
        var blockedToken = GetFirstSignificantToken(blockedDisplayName);
        if (blockedToken.Length < 3)
            return false;

        return candidateBase.Equals(blockedToken, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFirstSignificantToken(string name)
    {
        var token = name.Split([' ', '®', '™', '(', ')', '-'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(t => t.Length > 0);
        return token ?? string.Empty;
    }

    public static string? TryGetProductName(string exeFullPath)
    {
        try
        {
            if (!File.Exists(exeFullPath))
                return null;
            var info = FileVersionInfo.GetVersionInfo(exeFullPath);
            return string.IsNullOrWhiteSpace(info.ProductName) ? null : info.ProductName.Trim();
        }
        catch
        {
            return null;
        }
    }
}
