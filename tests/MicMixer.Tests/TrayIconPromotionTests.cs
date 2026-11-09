using AwesomeAssertions;
using MicMixer.UI;
using Xunit;

namespace MicMixer.Tests;

public sealed class TrayIconPromotionTests
{
    private const string InstalledPath = @"C:\Program Files\MicMixer\MicMixer.exe";

    [Theory]
    [InlineData(@"C:\Program Files\MicMixer\MicMixer.exe")]
    [InlineData(@"c:\program files\micmixer\MICMIXER.EXE")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\MicMixer\MicMixer.exe")]
    public void IsSameExecutable_MatchesPathsAsWindowsRecordsThem(string recordedPath)
    {
        TrayIconPromotion.IsSameExecutable(recordedPath, InstalledPath).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Tools\MicMixer.exe")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Other\MicMixer.exe")]
    public void IsSameExecutable_RejectsOtherPaths(string? recordedPath)
    {
        TrayIconPromotion.IsSameExecutable(recordedPath, InstalledPath).Should().BeFalse();
    }
}
