using System.Windows.Controls;
using FocusLock.UI.ViewModels;
using FocusLock.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI.Services;

public class NavigationService
{
    private Frame? _frame;

    public void Initialize(Frame frame) => _frame = frame;

    public void NavigateTo(Page page)
    {
        _frame?.Navigate(page);
    }

    /// <summary>
    /// Returns to the dashboard and refreshes session state from the service.
    /// Uses a singleton <see cref="DashboardViewModel"/> so an active session stays visible.
    /// </summary>
    public void NavigateToDashboard(bool refresh = true)
    {
        NavigateTo(new DashboardPage());
        if (refresh)
            _ = App.Services.GetRequiredService<DashboardViewModel>().RefreshAsync();
    }
}
