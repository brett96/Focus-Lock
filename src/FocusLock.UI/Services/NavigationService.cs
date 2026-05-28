using System.Windows.Controls;

namespace FocusLock.UI.Services;

public class NavigationService
{
    private Frame? _frame;

    public void Initialize(Frame frame) => _frame = frame;

    public void NavigateTo(Page page)
    {
        _frame?.Navigate(page);
    }
}
