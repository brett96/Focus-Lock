using System.ComponentModel;

namespace FocusLock.UI.ViewModels;

public sealed class SelectableApp : INotifyPropertyChanged
{
    public required string DisplayName { get; init; }
    public required string ExeName     { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
