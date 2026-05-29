using FocusLock.Core.Blocking;
using FocusLock.Core.Models;

namespace FocusLock.Core.Tests;

public class BlockedAppMatcherTests
{
    [Fact]
    public void IsBlocked_MatchesAnyExeNameInGroup()
    {
        var app = new BlockedApp("Google Chrome", "chrome.exe", ["chrome.exe", "chrome_proxy.exe"]);

        Assert.True(BlockedAppMatcher.IsBlocked("chrome.exe", app));
        Assert.True(BlockedAppMatcher.IsBlocked("chrome_proxy.exe", app));
        Assert.False(BlockedAppMatcher.IsBlocked("firefox.exe", app));
    }

    [Fact]
    public void IsBlocked_MatchesDisplayNameAgainstExeBaseName()
    {
        var app = new BlockedApp("Spotify", "Spotify.exe");

        Assert.True(BlockedAppMatcher.IsBlocked("Spotify.exe", app));
        Assert.True(BlockedAppMatcher.IsBlocked("spotify.exe", app));
    }

    [Fact]
    public void IsBlocked_LegacySingleExeNameStillWorks()
    {
        var app = new BlockedApp("Notepad", "notepad.exe");

        Assert.True(BlockedAppMatcher.IsBlocked("notepad.exe", app));
        Assert.True(BlockedAppMatcher.IsBlocked("Notepad.exe", app));
    }

    [Fact]
    public void IsProtectedFromBlocking_RejectsWindowsSystem32()
    {
        var system32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "cmd.exe");

        Assert.True(SystemProcessList.IsProtectedFromBlocking("cmd.exe", system32));
    }

    [Fact]
    public void IsProtectedFromBlocking_RejectsCriticalShellProcesses()
    {
        Assert.True(SystemProcessList.IsProtectedFromBlocking("explorer.exe"));
        Assert.True(SystemProcessList.IsProtectedFromBlocking("lsass.exe"));
    }
}
