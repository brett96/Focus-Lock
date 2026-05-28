using CommunityToolkit.Mvvm.ComponentModel;

namespace FocusLock.UI.ViewModels;

public partial class SelectableApp : ObservableObject
{
    public required string DisplayName { get; init; }
    public required string ExeName     { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}
