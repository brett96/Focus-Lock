using System.Diagnostics;

namespace FocusLock.Core.Services;

/// <summary>
/// Resets a service security descriptor to Windows defaults via sc.exe sdset.
/// Used when deny ACEs block SCM stop and the running service keeps re-applying them.
/// </summary>
public static class ServiceDaclReset
{
    /// <summary>
    /// Grants LocalSystem and Administrators Generic All on the service (valid for sc.exe sdset).
    /// </summary>
    public const string DefaultServiceSddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)";

    public static bool TryResetToDefault(string serviceName, out string? error)
    {
        error = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"sdset \"{serviceName}\" \"{DefaultServiceSddl}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "Could not start sc.exe.";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode == 0)
                return true;

            error = process.StandardError.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(error))
                error = $"sc.exe sdset exited with code {process.ExitCode}.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
