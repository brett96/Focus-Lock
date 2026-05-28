using System.Windows.Controls;
using FocusLock.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI.Views;

public partial class SetupPage : Page
{
    public SetupPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SetupViewModel>();
    }

    // Syncs the toggle button back to unchecked when Popup closes via click-outside.
    private void AppPopup_Closed(object sender, EventArgs e)
        => AppDropdownBtn.IsChecked = false;

    private void StAppPopup_Closed(object sender, EventArgs e)
    {
        var vm = (SetupViewModel)DataContext;
        vm.StIsDraftAppPopupOpen = false;
    }
}
