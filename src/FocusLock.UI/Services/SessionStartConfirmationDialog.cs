using System.Text;
using FocusLock.Core.Models;
using FocusLock.UI.ViewModels;

namespace FocusLock.UI.Services;

public static class SessionStartConfirmationDialog
{
    public static bool Show(
        DateTime deadlineLocal,
        SessionMode mode,
        IReadOnlyList<BlockedApp> blockedApps,
        IReadOnlyList<BlockedSite> blockedSites,
        IReadOnlyList<DailyTimeLimitViewModel> dailyLimits,
        IReadOnlyList<AppTimeLimitViewModel> appLimits)
    {
        var message = BuildMessage(
            deadlineLocal, mode, blockedApps, blockedSites, dailyLimits, appLimits);

        return System.Windows.MessageBox.Show(
            message,
            "Confirm Start Session",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
    }

    internal static string BuildMessage(
        DateTime deadlineLocal,
        SessionMode mode,
        IReadOnlyList<BlockedApp> blockedApps,
        IReadOnlyList<BlockedSite> blockedSites,
        IReadOnlyList<DailyTimeLimitViewModel> dailyLimits,
        IReadOnlyList<AppTimeLimitViewModel> appLimits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Start this focus session?");
        sb.AppendLine();
        sb.AppendLine($"Ends: {deadlineLocal:ddd, MMM d 'at' h:mm tt}");
        sb.AppendLine(mode == SessionMode.Strict
            ? "Mode: Strict — cannot end early"
            : "Mode: Regular — can end early");

        AppendSection(sb, "Blocked apps", blockedApps.Count,
            blockedApps.Select(a => FormatApp(a.DisplayName, a.ExeName)));
        AppendSection(sb, "Blocked websites", blockedSites.Count,
            blockedSites.Select(s => s.Domain));
        AppendSection(sb, "Screen time limits — daily", dailyLimits.Count,
            dailyLimits.Select(d => $"{d.LimitSummary} — {d.ScheduleSummary}"));
        AppendSection(sb, "App time limits", appLimits.Count,
            appLimits.Select(a => $"{a.DisplayName} — {a.LimitSummary} — {a.ScheduleSummary}"));

        if (mode == SessionMode.Strict)
        {
            sb.AppendLine();
            sb.AppendLine("Strict mode locks enforcement until the deadline. You will not be able to end the session early from the app.");
        }

        sb.AppendLine();
        sb.Append("Start the session now?");
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, int count, IEnumerable<string> lines)
    {
        if (count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine($"{title} ({count}):");
        foreach (var line in lines)
            sb.AppendLine($"• {line}");
    }

    private static string FormatApp(string displayName, string exeName)
        => string.Equals(displayName, exeName, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(displayName)
                ? exeName
                : $"{displayName} ({exeName})";
}
