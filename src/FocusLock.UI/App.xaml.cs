using System.Windows;
using Application = System.Windows.Application;
using FocusLock.UI.Services;
using FocusLock.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FocusLock.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private TrayManager? _tray;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ServiceClient>();
        services.AddSingleton<NavigationService>();
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<ActiveSessionViewModel>();
        services.AddTransient<SettingsViewModel>();
        Services = services.BuildServiceProvider();

        var mainWindow = new MainWindow();
        _tray = new TrayManager(mainWindow);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
