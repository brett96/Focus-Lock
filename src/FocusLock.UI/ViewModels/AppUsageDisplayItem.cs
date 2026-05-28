using CommunityToolkit.Mvvm.ComponentModel;
using FocusLock.Core.Ipc;
using FocusLock.Core.Models;

namespace FocusLock.UI.ViewModels;

public partial class AppUsageDisplayItem : ObservableObject
{
    public string ExeName { get; }
    public string DisplayName { get; }

    [ObservableProperty] private string _usageText = "0m";
    [ObservableProperty] private string _limitLabel = string.Empty;
    [ObservableProperty] private string _remainingText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIntervalReset))]
    private string _intervalResetText = string.Empty;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBlocked;

    private DateTime? _intervalResetsAtLocal;

    public bool ShowIntervalReset => !string.IsNullOrEmpty(IntervalResetText);

    public AppUsageDisplayItem(string exeName, string displayName)
    {
        ExeName     = exeName;
        DisplayName = displayName;
    }

    public void Update(AppUsageStatus status)
    {
        IsBlocked = status.IsBlocked;
        _intervalResetsAtLocal = status.IntervalResetsAtLocal;

        if (status.LimitType == AppLimitType.DailyTotal)
        {
            IntervalResetText = string.Empty;
            var used  = TimeSpan.FromSeconds(status.TotalSecondsToday);
            var limit = TimeSpan.FromMinutes(status.LimitMinutes);
            int limitSec = status.LimitMinutes * 60;
            UsageText  = FormatTime(used);
            LimitLabel = $"of {FormatTime(limit)} today";
            Progress   = limitSec > 0
                ? Math.Min(1.0, status.TotalSecondsToday / (double)limitSec)
                : 0;
            RemainingText = status.IsBlocked
                ? "Limit reached"
                : FormatRemaining(limitSec - status.TotalSecondsToday);
        }
        else
        {
            int usedSec = status.CurrentIntervalSecondsUsed ?? 0;
            int limitSec = status.LimitMinutes * 60;
            var used = TimeSpan.FromSeconds(usedSec);
            UsageText  = FormatTime(used);
            LimitLabel = $"of {status.LimitMinutes} min this interval";
            Progress   = limitSec > 0
                ? Math.Min(1.0, usedSec / (double)limitSec)
                : 0;
            RemainingText = status.IsBlocked
                ? "Limit reached this interval"
                : FormatRemaining(limitSec - usedSec);
            IntervalResetText = FormatIntervalResetText();
        }
    }

    private string FormatIntervalResetText()
    {
        if (_intervalResetsAtLocal is not { } resetAt)
            return string.Empty;

        var untilReset = resetAt - DateTime.Now;
        int resetSec = Math.Max(0, (int)Math.Ceiling(untilReset.TotalSeconds));
        string when = resetAt.ToString("h:mm tt");
        if (resetSec <= 0)
            return $"Interval resets now ({when})";

        string inText = FormatRemaining(resetSec).Replace(" left", "", StringComparison.Ordinal);
        return $"Interval resets in {inText} ({when})";
    }

    private static string FormatRemaining(int secondsRemaining)
    {
        if (secondsRemaining <= 0) return "0s left";
        var t = TimeSpan.FromSeconds(secondsRemaining);
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m left";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m {t.Seconds}s left";
        return $"{t.Seconds}s left";
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return t.Minutes > 0 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalHours}h";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m";
        return $"{t.Seconds}s";
    }
}
