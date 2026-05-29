using FocusLock.Core.Blocking;

namespace FocusLock.Core.Tests;

public class AppLauncherDiscoveryTests
{
    [Theory]
    [InlineData("uninstall.exe", true)]
    [InlineData("steam.exe", false)]
    [InlineData("notepad++.exe", false)]
    public void IsUtilityExecutable_ClassifiesInstallUtilities(string fileName, bool expected)
    {
        Assert.Equal(expected, AppLauncherDiscovery.IsUtilityExecutable(fileName));
    }

    [Fact]
    public void PickBestLauncher_PrefersMainExeOverUninstaller()
    {
        var install = Path.Combine(Path.GetTempPath(), "fl-steam-test");
        Directory.CreateDirectory(install);
        try
        {
            var steam = Path.Combine(install, "steam.exe");
            var uninstall = Path.Combine(install, "uninstall.exe");
            File.WriteAllBytes(steam, [0, 0]);
            File.WriteAllBytes(uninstall, [0, 0]);

            var picked = AppLauncherDiscovery.PickBestLauncher(
                [uninstall, steam],
                "Steam Client",
                install);

            Assert.Equal(steam, picked);
        }
        finally
        {
            Directory.Delete(install, recursive: true);
        }
    }

    [Fact]
    public void ApplicationNameMatches_FirstToken_SteamClient()
    {
        Assert.True(BlockedAppMatcher.ApplicationNameMatches("steam", "Steam Client"));
        Assert.True(BlockedAppMatcher.ApplicationNameMatches("steam.exe", "Steam Client"));
    }
}
