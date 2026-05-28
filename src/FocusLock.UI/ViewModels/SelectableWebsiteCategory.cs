using CommunityToolkit.Mvvm.ComponentModel;

namespace FocusLock.UI.ViewModels;

public partial class SelectableWebsiteCategory : ObservableObject
{
    private readonly Action<SelectableWebsiteCategory, bool> _onToggle;

    [ObservableProperty] private bool _isSelected;

    public string Name { get; }
    public string[] Domains { get; }
    public string Label => $"{Name} ({Domains.Length} sites)";

    public SelectableWebsiteCategory(string name, string[] domains, Action<SelectableWebsiteCategory, bool> onToggle)
    {
        Name = name;
        Domains = domains;
        _onToggle = onToggle;
    }

    partial void OnIsSelectedChanged(bool value) => _onToggle(this, value);
}
