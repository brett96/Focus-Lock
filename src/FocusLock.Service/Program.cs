using FocusLock.Core.Services;
using FocusLock.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

if (args.Contains("--unlock-for-setup"))
{
    var (ok, message) = SetupUnlock.RunWithDiagnostics();
    Console.WriteLine(message);
    return ok ? 0 : 1;
}

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
builder.Services.AddHostedService<SessionWatchdogWorker>();

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

host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(static () =>
{
    SessionProtectionHelper.RestoreServiceProtection();
    ProcessProtection.TrySetCritical(false);
});

host.Run();
return 0;
