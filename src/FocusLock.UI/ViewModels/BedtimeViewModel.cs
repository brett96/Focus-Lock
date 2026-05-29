using CommunityToolkit.Mvvm.Input;
using FocusLock.Core.Models;
using FocusLock.Core.ScreenTime;

namespace FocusLock.UI.ViewModels;

public class BedtimeViewModel
{
    public string Id { get; }
    public ScreenTimeSchedule Schedule { get; }

    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand EditCommand { get; }

    public BedtimeViewModel(
        BedtimeRule model,
        Action<BedtimeViewModel> remove,
        Action<BedtimeViewModel> edit)
    {
        Id            = model.Id;
        Schedule      = model.Schedule;
        RemoveCommand = new RelayCommand(() => remove(this));
        EditCommand   = new RelayCommand(() => edit(this));
    }

    public string ScheduleSummary => BedtimeScheduleHelper.Describe(Schedule);

    public BedtimeRule ToModel() => new()
    {
        Id       = Id,
        Schedule = new ScreenTimeSchedule
        {
            ActiveDays = Schedule.ActiveDays,
            StartTime  = Schedule.StartTime,
            EndTime    = Schedule.EndTime
        }
    };
}
