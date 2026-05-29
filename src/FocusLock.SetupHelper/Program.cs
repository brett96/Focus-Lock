using FocusLock.Core.Services;

// MSI uses installer mode (release DACLs only). --full tries to stop services (manual recovery).
var full = args.Contains("--full", StringComparer.OrdinalIgnoreCase);
var (ok, message) = full
    ? SetupUnlock.RunWithDiagnostics()
    : SetupUnlock.RunForInstaller();
Console.WriteLine(message);
return ok ? 0 : 1;
