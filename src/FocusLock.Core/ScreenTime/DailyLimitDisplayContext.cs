using FocusLock.Core.Models;

namespace FocusLock.Core.ScreenTime;

public readonly record struct DailyLimitDisplayContext(
    DailyTimeLimit? FocusRule,
    bool IsActiveNow,
    bool IsNextRuleToday,
    bool IsLastEndedRuleToday,
    DateTime? ReferenceTimeLocal);
