using AwesomeAssertions;
using MicMixer.Audio;
using Xunit;

namespace MicMixer.Tests;

public sealed class VolumeCurveTests
{
    [Theory]
    [InlineData(0d, 0f)]
    [InlineData(1d, 1f)]
    [InlineData(0.5d, 0.125f)]
    [InlineData(0.01d, 0.000001f)]
    public void ToGain_ShouldFollowTheCubeOfTheSliderPosition(double position, float expectedGain)
    {
        VolumeCurve.ToGain(position).Should().BeApproximately(expectedGain, 1e-7f);
    }

    [Fact]
    public void ToPosition_ShouldKeepASavedGainAtTheSameLoudness()
    {
        // A gain saved by a build with a linear slider must come back unchanged
        // once the slider position is turned into gain again.
        foreach (float savedGain in new[] { 0f, 0.003f, 0.19f, 0.5f, 1f })
        {
            VolumeCurve.ToGain(VolumeCurve.ToPosition(savedGain)).Should().BeApproximately(savedGain, 1e-6f);
        }
    }

    [Fact]
    public void ToGain_ShouldClampOutOfRangePositions()
    {
        VolumeCurve.ToGain(-0.5d).Should().Be(0f);
        VolumeCurve.ToGain(2d).Should().Be(1f);
    }
}
