using CommunityToolkit.Mvvm.ComponentModel;

namespace FocusLock.UI.ViewModels;

public partial class SelectableApp : ObservableObject
{
    public required string DisplayName { get; init; }
    public required string ExeName     { get; init; }
    /// <summary>Full path to the executable (used for labels and search; blocking is still by <see cref="ExeName"/>).</summary>
    public required string ExePath     { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}
