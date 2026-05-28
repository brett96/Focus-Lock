using System.Windows.Controls;
using FocusLock.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI.Views;

public partial class ActiveSessionPage : Page
{
    public ActiveSessionPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<ActiveSessionViewModel>();
    }
}
