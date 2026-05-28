using System.Windows.Controls;
using FocusLock.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI.Views;

public partial class ScreenTimePage : Page
{
    public ScreenTimePage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ScreenTimeViewModel>();
    }

    private void AppPopup_Closed(object sender, EventArgs e)
    {
        var vm = (ScreenTimeViewModel)DataContext;
        vm.IsDraftAppPopupOpen = false;
    }
}
