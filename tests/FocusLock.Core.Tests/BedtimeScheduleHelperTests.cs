using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;

namespace FocusLock.Core.Tests;

public class BedtimeScheduleHelperTests
{
    [Fact]
    public void IsBedtimeActive_SameDayWindow()
    {
        var schedule = new ScreenTimeSchedule
        {
            ActiveDays = DayOfWeekFlags.Thursday,
            StartTime  = new TimeOnly(22, 0),
            EndTime    = new TimeOnly(23, 0)
        };

        var thursdayAt10 = new DateTime(2026, 5, 28, 22, 30, 0);
        Assert.True(BedtimeScheduleHelper.IsBedtimeActive(schedule, thursdayAt10));
        Assert.False(BedtimeScheduleHelper.IsBedtimeActive(schedule, thursdayAt10.AddHours(-3)));
    }

    [Fact]
    public void IsBedtimeActive_OvernightMorningTail()
    {
        var schedule = new ScreenTimeSchedule
        {
            ActiveDays = DayOfWeekFlags.Monday,
            StartTime  = new TimeOnly(22, 0),
            EndTime    = new TimeOnly(7, 0)
        };

        var tuesdayMorning = new DateTime(2026, 5, 26, 6, 30, 0); // Tuesday after Monday start
        Assert.True(BedtimeScheduleHelper.IsBedtimeActive(schedule, tuesdayMorning));
    }

    [Fact]
    public void SchedulesConflict_DetectsOverlapWithDailyLimit()
    {
        var bedtime = new ScreenTimeSchedule
        {
            ActiveDays = DayOfWeekFlags.Monday | DayOfWeekFlags.Tuesday | DayOfWeekFlags.Wednesday |
                         DayOfWeekFlags.Thursday | DayOfWeekFlags.Friday,
            StartTime  = new TimeOnly(21, 0),
            EndTime    = new TimeOnly(23, 0)
        };
        var daily = new ScreenTimeSchedule
        {
            ActiveDays = DayOfWeekFlags.Monday,
            StartTime  = new TimeOnly(9, 0),
            EndTime    = new TimeOnly(22, 0)
        };

        Assert.True(BedtimeScheduleHelper.SchedulesConflict(bedtime, daily));
    }

    [Fact]
    public void GetOccurrencesInRange_IncludesSessionWindow()
    {
        var rule = new BedtimeRule
        {
            Schedule = new ScreenTimeSchedule
            {
                ActiveDays = DayOfWeekFlags.Thursday,
                StartTime  = new TimeOnly(22, 0),
                EndTime    = new TimeOnly(23, 0)
            }
        };

        var now = new DateTime(2026, 5, 28, 12, 0, 0);
        var deadline = new DateTime(2026, 5, 28, 23, 30, 0);
        var occ = BedtimeScheduleHelper.GetOccurrencesInRange(new[] { rule }, now, deadline);

        Assert.Single(occ);
        Assert.Equal(new DateTime(2026, 5, 28, 22, 0, 0), occ[0].StartsAtLocal);
    }
}
