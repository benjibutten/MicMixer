using AwesomeAssertions;
using MicMixer.Audio;
using NAudio.Wave;
using Xunit;

namespace MicMixer.Tests;

public sealed class SwitchingSampleProviderTests
{
    private const int SampleRate = 48_000;
    private const float PrimaryLevel = 0.2f;
    private const float SecondaryLevel = 0.5f;

    [Fact]
    public void PrimaryVolume_ShouldBeUnity_WhenNotProvided()
    {
        var switching = new SwitchingSampleProvider(
            new ConstantSampleProvider(PrimaryLevel), new ConstantSampleProvider(SecondaryLevel), () => false);

        float[] block = Read(switching, 480);

        block.Should().OnlyContain(sample => sample == PrimaryLevel);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.5f)]
    public void PrimaryVolume_ShouldScaleOnlyThePrimarySource(float volume)
    {
        bool useSecondary = false;
        var pair = new IndependentSamplePair(new ConstantSampleProvider(PrimaryLevel), new ConstantSampleProvider(SecondaryLevel));
        var switching = new SwitchingSampleProvider(pair, () => useSecondary, primaryVolume: () => volume);

        float[] primary = Read(switching, 480);
        primary.Should().OnlyContain(sample => Math.Abs(sample - PrimaryLevel * volume) < 1e-6f);

        useSecondary = true;
        Read(switching, 480);
        float[] secondary = Read(switching, 480);
        secondary.Should().OnlyContain(sample => sample == SecondaryLevel, "the modded source is never scaled by the normal mic volume");
    }

    [Fact]
    public void PrimaryVolume_ShouldRampToTheNewValueWithinTenMilliseconds()
    {
        float volume = 1f;
        var pair = new IndependentSamplePair(new ConstantSampleProvider(PrimaryLevel), secondary: null);
        var switching = new SwitchingSampleProvider(pair, () => false, primaryVolume: () => volume);
        Read(switching, 480);

        volume = 2f;
        float[] ramp = Read(switching, 480);

        ramp[0].Should().BeGreaterThan(PrimaryLevel).And.BeLessThan(PrimaryLevel * 1.01f);
        ramp[^1].Should().BeApproximately(PrimaryLevel * 2f, 1e-5f);
        for (int i = 1; i < ramp.Length; i++)
        {
            (ramp[i] - ramp[i - 1]).Should().BeInRange(0f, 0.001f);
        }
    }

    private static float[] Read(ISampleProvider provider, int count)
    {
        var buffer = new float[count];
        provider.Read(buffer.AsSpan()).Should().Be(count);
        return buffer;
    }

    private sealed class ConstantSampleProvider(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            buffer.Fill(value);
            return buffer.Length;
        }

    }
}
