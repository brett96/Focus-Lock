using System.Windows.Controls;
using FocusLock.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI.Views;

public partial class DashboardPage : Page
{
    private readonly DashboardViewModel _vm;

    public DashboardPage()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<DashboardViewModel>();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.RefreshAsync();
    }
}
