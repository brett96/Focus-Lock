using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;

namespace FocusLock.Core.Tests;

public class ScreenTimeScheduleOverlapTests
{
    private static ScreenTimeSchedule WeekdayWindow(TimeOnly start, TimeOnly end) => new()
    {
        ActiveDays = DayOfWeekFlags.Monday | DayOfWeekFlags.Tuesday | DayOfWeekFlags.Wednesday
            | DayOfWeekFlags.Thursday | DayOfWeekFlags.Friday,
        StartTime = start,
        EndTime   = end
    };

    [Fact]
    public void NonOverlappingSameDayDifferentWindows_DoNotOverlap()
    {
        var a = WeekdayWindow(new TimeOnly(9, 0), new TimeOnly(12, 0));
        var b = WeekdayWindow(new TimeOnly(13, 0), new TimeOnly(17, 0));
        Assert.False(ScreenTimeScheduleOverlap.SchedulesOverlap(a, b));
    }

    [Fact]
    public void OverlappingSameDayWindows_Overlap()
    {
        var a = WeekdayWindow(new TimeOnly(9, 0), new TimeOnly(14, 0));
        var b = WeekdayWindow(new TimeOnly(13, 0), new TimeOnly(17, 0));
        Assert.True(ScreenTimeScheduleOverlap.SchedulesOverlap(a, b));
    }

    [Fact]
    public void DifferentDaysSameTime_DoNotOverlap()
    {
        var a = new ScreenTimeSchedule
        {
            ActiveDays = DayOfWeekFlags.Monday,
            StartTime  = new TimeOnly(9, 0),
            EndTime    = new TimeOnly(17, 0)
        };
        var b = new ScreenTimeSchedule
        {
            ActiveDays = DayOfWeekFlags.Tuesday,
            StartTime  = new TimeOnly(9, 0),
            EndTime    = new TimeOnly(17, 0)
        };
        Assert.False(ScreenTimeScheduleOverlap.SchedulesOverlap(a, b));
    }

    [Fact]
    public void ResolveDailyLimitDisplay_ShowsNextRuleBetweenWindows()
    {
        var rules = new List<DailyTimeLimit>
        {
            new()
            {
                LimitMinutes = 120,
                Schedule = new ScreenTimeSchedule
                {
                    ActiveDays = DayOfWeekFlags.Monday,
                    StartTime  = new TimeOnly(9, 0),
                    EndTime    = new TimeOnly(12, 0)
                }
            },
            new()
            {
                LimitMinutes = 60,
                Schedule = new ScreenTimeSchedule
                {
                    ActiveDays = DayOfWeekFlags.Monday,
                    StartTime  = new TimeOnly(13, 0),
                    EndTime    = new TimeOnly(17, 0)
                }
            }
        };

        var monday1230 = new DateTime(2026, 5, 25, 12, 30, 0); // Monday
        var ctx = ScreenTimeScheduleHelper.ResolveDailyLimitDisplay(rules, monday1230);

        Assert.False(ctx.IsActiveNow);
        Assert.True(ctx.IsNextRuleToday);
        Assert.Equal(60, ctx.FocusRule!.LimitMinutes);
        Assert.Equal(new DateTime(2026, 5, 25, 13, 0, 0), ctx.ReferenceTimeLocal);
    }

    [Fact]
    public void AllDayOnSharedDay_OverlapsTimedWindow()
    {
        var a = new ScreenTimeSchedule { ActiveDays = DayOfWeekFlags.Monday };
        var b = WeekdayWindow(new TimeOnly(9, 0), new TimeOnly(17, 0));
        Assert.True(ScreenTimeScheduleOverlap.SchedulesOverlap(a, b));
    }
}
