using MicMixer.Updates;
using Xunit;

namespace MicMixer.Tests;

public sealed class InstallEnvironmentTests
{
    [Theory]
    [InlineData(@"C:\Users\alice\AppData\Local\Microsoft\WinGet\Packages\BenjiButten.MicMixer_Microsoft.Winget.Source_8wekyb3d8bbwe\MicMixer.exe")]
    [InlineData(@"C:\Users\alice\AppData\Local\Microsoft\WinGet\Links\micmixer.exe")]
    [InlineData(@"C:\Program Files\WinGet\Packages\BenjiButten.MicMixer\MicMixer.exe")]
    public void IsWingetPath_DetectsWingetInstallLocations(string executablePath)
    {
        Assert.True(InstallEnvironment.IsWingetPath(executablePath));
    }

    [Theory]
    [InlineData(@"C:\Users\alice\AppData\Local\Programs\MicMixer\MicMixer.exe")]
    [InlineData(@"C:\Program Files\MicMixer\MicMixer.exe")]
    [InlineData(@"D:\Games\MicMixer\MicMixer.exe")]
    public void IsWingetPath_IgnoresOtherInstallLocations(string executablePath)
    {
        Assert.False(InstallEnvironment.IsWingetPath(executablePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsWingetPath_HandlesMissingPath(string? executablePath)
    {
        Assert.False(InstallEnvironment.IsWingetPath(executablePath));
    }

    [Theory]
    // A custom portablePackageUserRoot / portablePackageMachineRoot moves the
    // install off the default WinGet roots; the recorded InstallLocation still
    // covers the running executable.
    [InlineData(@"D:\PortableApps\BenjiButten.MicMixer_Microsoft.Winget.Source_8wekyb3d8bbwe", @"D:\PortableApps\BenjiButten.MicMixer_Microsoft.Winget.Source_8wekyb3d8bbwe\MicMixer.exe")]
    [InlineData(@"D:\PortableApps\MicMixer\", @"D:\PortableApps\MicMixer\MicMixer.exe")]
    [InlineData(@"E:\apps\mm", @"E:\apps\mm\sub\MicMixer.exe")]
    public void IsExecutableWithin_DetectsExecutableInsideInstallLocation(string installLocation, string executablePath)
    {
        Assert.True(InstallEnvironment.IsExecutableWithin(executablePath, installLocation));
    }

    [Theory]
    // A sibling directory that merely shares a name prefix must not match.
    [InlineData(@"D:\PortableApps\MicMixer", @"D:\PortableApps\MicMixerBackup\MicMixer.exe")]
    [InlineData(@"D:\PortableApps\MicMixer", @"C:\Program Files\MicMixer\MicMixer.exe")]
    public void IsExecutableWithin_RejectsExecutableOutsideInstallLocation(string installLocation, string executablePath)
    {
        Assert.False(InstallEnvironment.IsExecutableWithin(executablePath, installLocation));
    }

    [Theory]
    [InlineData(null, @"D:\PortableApps\MicMixer\MicMixer.exe")]
    [InlineData("", @"D:\PortableApps\MicMixer\MicMixer.exe")]
    [InlineData(@"D:\PortableApps\MicMixer", null)]
    [InlineData(@"D:\PortableApps\MicMixer", "")]
    public void IsExecutableWithin_HandlesMissingInput(string? installLocation, string? executablePath)
    {
        Assert.False(InstallEnvironment.IsExecutableWithin(executablePath, installLocation));
    }
}
