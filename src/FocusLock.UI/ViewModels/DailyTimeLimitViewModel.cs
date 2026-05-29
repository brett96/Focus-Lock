using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;

namespace FocusLock.UI.ViewModels;

public class DailyTimeLimitViewModel
{
    public string Id { get; }
    public int LimitMinutes { get; }
    public ScreenTimeSchedule Schedule { get; }

    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand EditCommand { get; }

    public DailyTimeLimitViewModel(
        DailyTimeLimit model,
        Action<DailyTimeLimitViewModel> remove,
        Action<DailyTimeLimitViewModel> edit)
    {
        Id            = model.Id;
        LimitMinutes  = model.LimitMinutes;
        Schedule      = model.Schedule;
        RemoveCommand = new RelayCommand(() => remove(this));
        EditCommand   = new RelayCommand(() => edit(this));
    }

    public string LimitSummary => FormatMinutes(LimitMinutes);

    public string ScheduleSummary => ScreenTimeScheduleHelper.Describe(Schedule);

    private static string FormatMinutes(int minutes)
    {
        int h = minutes / 60, m = minutes % 60;
        return h > 0 && m > 0 ? $"{h}h {m}m" : h > 0 ? $"{h}h" : $"{m}m";
    }

    public DailyTimeLimit ToModel() => new()
    {
        Id            = Id,
        LimitMinutes  = LimitMinutes,
        Schedule      = new ScreenTimeSchedule
        {
            ActiveDays = Schedule.ActiveDays,
            StartTime  = Schedule.StartTime,
            EndTime    = Schedule.EndTime
        }
    };
}
