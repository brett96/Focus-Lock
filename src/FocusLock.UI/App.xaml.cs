using System.Windows;
using System.Windows.Threading;
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
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        var services = new ServiceCollection();
        services.AddSingleton<ServiceClient>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<DashboardViewModel>();
        services.AddTransient<SetupViewModel>();
        services.AddTransient<ActiveSessionViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScreenTimeViewModel>();
        Services = services.BuildServiceProvider();

        var mainWindow = new MainWindow();
        _tray = new TrayManager(mainWindow);
        mainWindow.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        System.Windows.MessageBox.Show(
            $"Fatal: {ex?.GetType().Name}: {ex?.Message}\n\n{ex?.StackTrace}",
            "Fatal Exception", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
