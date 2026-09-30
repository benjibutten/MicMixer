using AwesomeAssertions;
using MicMixer.Audio;
using NAudio.Wave;
using Xunit;

namespace MicMixer.Tests;

/// <summary>
/// Ducking's promise: the music is untouched at full volume, settles exactly at the
/// ducked level while the mic is heard, and glides between the two without steps.
/// </summary>
public sealed class MusicDuckingSampleProviderTests
{
    private const int SampleRate = 48_000;
    private const float Level = 0.5f;
    private const float Ducked = 0.25f;

    [Fact]
    public void FullVolume_ShouldPassTheMusicThroughUntouched()
    {
        var ducker = new MusicDuckingSampleProvider(new LevelSampleProvider(channels: 2), () => 1f);

        float[] block = ReadMilliseconds(ducker, 100, channels: 2);

        block.Should().OnlyContain(sample => sample == Level);
    }

    [Fact]
    public void Ducking_ShouldSettleExactlyAtTheDuckedLevel()
    {
        var ducker = new MusicDuckingSampleProvider(new LevelSampleProvider(channels: 2), () => Ducked);

        ReadMilliseconds(ducker, 300, channels: 2);
        float[] block = ReadMilliseconds(ducker, 20, channels: 2);

        block.Should().OnlyContain(sample => sample == Level * Ducked);
    }

    [Fact]
    public void Ducking_ShouldGoDownFastAndComeBackSlowly_WithoutSteps()
    {
        float target = Ducked;
        var ducker = new MusicDuckingSampleProvider(new LevelSampleProvider(channels: 1), () => target);

        float[] goingDown = ReadMilliseconds(ducker, 150, channels: 1);
        target = 1f;
        float[] comingUp = ReadMilliseconds(ducker, 100, channels: 1);

        goingDown[^1].Should().BeApproximately(Level * Ducked, 0.001f);
        comingUp[^1].Should().BeLessThan(Level * 0.9f);
        AssertSmooth([.. goingDown, .. comingUp], maxStep: 0.001f);
    }

    private static void AssertSmooth(float[] samples, float maxStep)
    {
        for (int i = 1; i < samples.Length; i++)
        {
            Math.Abs(samples[i] - samples[i - 1]).Should().BeLessThanOrEqualTo(maxStep);
        }
    }

    private static float[] ReadMilliseconds(ISampleProvider provider, int milliseconds, int channels)
    {
        var buffer = new float[SampleRate * milliseconds / 1_000 * channels];
        provider.Read(buffer.AsSpan()).Should().Be(buffer.Length);
        return buffer;
    }

    private sealed class LevelSampleProvider(int channels) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);

        public int Read(Span<float> buffer)
        {
            buffer.Fill(Level);
            return buffer.Length;
        }

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    }
}
