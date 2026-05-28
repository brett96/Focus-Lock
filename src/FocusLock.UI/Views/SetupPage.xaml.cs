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
}
