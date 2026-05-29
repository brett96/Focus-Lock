using FocusLock.Core.Services;
using FocusLock.Watchdog;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : Directory.GetCurrentDirectory()
});

builder.Services.AddWindowsService(o => o.ServiceName = FocusLockServiceNames.WatchdogService);
builder.Services.AddHostedService<WatchdogWorker>();

if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddEventLog(cfg =>
    {
        cfg.SourceName = "FocusLockWatchdog";
        cfg.LogName = "Application";
    });
}

var host = builder.Build();
host.Run();
