using FocusLock.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService()
        ? AppContext.BaseDirectory
        : Directory.GetCurrentDirectory()
});

builder.Services.AddWindowsService(o => o.ServiceName = "FocusLockService");
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddHostedService<SessionWorker>();
builder.Services.AddHostedService<AppMonitorWorker>();
builder.Services.AddHostedService<DeadlineWatcherWorker>();

// Log to Windows Event Log when running as a service; fall back to console during development.
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddEventLog(cfg =>
    {
        cfg.SourceName = "FocusLock";
        cfg.LogName = "Application";
    });
}

var host = builder.Build();
host.Run();
