using System.ComponentModel;
using System.Windows;
using FocusLock.UI.Services;
using FocusLock.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var nav = App.Services.GetRequiredService<NavigationService>();
        nav.Initialize(MainFrame);
        nav.NavigateTo(new DashboardPage());
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    // Close button hides to tray instead of exiting.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
