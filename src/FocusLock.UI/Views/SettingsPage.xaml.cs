using System.Windows.Controls;
using FocusLock.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }
}
