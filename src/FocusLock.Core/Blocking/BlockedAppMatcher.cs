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

    /// <summary>
    /// True when a running process should count toward an app time limit (same rules as session blocking).
    /// </summary>
    public static bool MatchesAppTimeLimit(string exeName, AppTimeLimit limit, string? exeFullPath = null)
        => IsBlocked(exeName, new BlockedApp(limit.DisplayName, limit.ExeName, limit.ExeNames), exeFullPath);

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

        // "geekbench6.exe" matches display name "Geekbench 6".
        if (MatchesNormalizedName(candidateBase, blockedDisplayName))
            return true;

        return false;
    }

    /// <summary>Lowercase letters and digits only — for fuzzy display vs exe matching.</summary>
    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        Span<char> buffer = stackalloc char[value.Length];
        int n = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[n++] = char.ToLowerInvariant(c);
        }

        return n == 0 ? string.Empty : new string(buffer[..n]);
    }

    private static bool MatchesNormalizedName(string candidateBase, string blockedDisplayName)
    {
        var normCandidate = NormalizeName(candidateBase);
        var normBlocked = NormalizeName(blockedDisplayName);
        if (normCandidate.Length < 3 || normBlocked.Length < 3)
            return false;

        if (normCandidate.Equals(normBlocked, StringComparison.Ordinal))
            return true;

        if (normBlocked.Length >= 5 && normCandidate.StartsWith(normBlocked, StringComparison.Ordinal))
            return true;

        if (normCandidate.Length >= 5 && normBlocked.StartsWith(normCandidate, StringComparison.Ordinal))
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
