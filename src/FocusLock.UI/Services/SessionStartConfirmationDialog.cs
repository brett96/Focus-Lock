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
        IReadOnlyList<AppTimeLimitViewModel> appLimits,
        IReadOnlyList<BedtimeViewModel> bedtimes)
    {
        var message = BuildMessage(
            deadlineLocal, mode, blockedApps, blockedSites, dailyLimits, appLimits, bedtimes);

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
        IReadOnlyList<AppTimeLimitViewModel> appLimits,
        IReadOnlyList<BedtimeViewModel> bedtimes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Start this focus session?");
        sb.AppendLine();
        sb.AppendLine($"Ends: {deadlineLocal:ddd, MMM d 'at' h:mm tt}");
        sb.AppendLine(mode == SessionMode.Strict
            ? "Mode: Strict — cannot end early"
            : "Mode: Regular — can end early");

        if (blockedApps.Count > 0)
            sb.AppendLine($"Blocked applications: {blockedApps.Count}");
        if (blockedSites.Count > 0)
            sb.AppendLine($"Blocked websites: {blockedSites.Count}");

        AppendSection(sb, "Screen time limits — daily", dailyLimits.Count,
            dailyLimits.Select(d => $"{d.LimitSummary} — {d.ScheduleSummary}"));
        AppendSection(sb, "App time limits", appLimits.Count,
            appLimits.Select(a => $"{a.DisplayName} — {a.LimitSummary} — {a.ScheduleSummary}"));
        AppendSection(sb, "Bedtimes", bedtimes.Count,
            bedtimes.Select(b => b.ScheduleSummary));

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
}
