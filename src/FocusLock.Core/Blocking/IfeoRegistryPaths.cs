namespace FocusLock.Core.Blocking;

/// <summary>
/// IFEO registry locations. 32-bit executables on 64-bit Windows use the WOW6432Node tree.
/// </summary>
public static class IfeoRegistryPaths
{
    public const string Native =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public const string Wow6432 =
        @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    public static IReadOnlyList<string> All =>
        Environment.Is64BitOperatingSystem
            ? [Native, Wow6432]
            : [Native];
}
