using CommunityToolkit.Mvvm.ComponentModel;

namespace FocusLock.UI.ViewModels;

public partial class SelectableApp : ObservableObject
{
    public required string DisplayName { get; init; }
    /// <summary>Primary executable; <see cref="ExeNames"/> lists all related exe file names blocked for this app.</summary>
    public required string ExeName     { get; init; }
    public required string ExePath     { get; init; }
    public required IReadOnlyList<string> ExeNames { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}
