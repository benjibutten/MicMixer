using AwesomeAssertions;
using MicMixer.Audio;
using NAudio.Wave;
using Xunit;

namespace MicMixer.Tests;

public sealed class ChannelAdapterSampleProviderTests
{
    [Fact]
    public void Read_ShouldPutStereoOnTheFrontPairOnly_WhenTheDeviceHasMoreChannels()
    {
        var stereo = new StaticSource(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2), [0.25f, 0.5f, 0.25f, 0.5f]);
        var adapter = new ChannelAdapterSampleProvider(stereo, targetChannels: 8);
        float[] buffer = new float[16];

        int read = adapter.Read(buffer, 0, buffer.Length);

        read.Should().Be(16);
        buffer.Should().Equal(
            0.25f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0f,
            0.25f, 0.5f, 0f, 0f, 0f, 0f, 0f, 0f);
    }

    [Fact]
    public void Read_ShouldCopyMonoToEveryChannel()
    {
        var mono = new StaticSource(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1), [0.3f]);
        var adapter = new ChannelAdapterSampleProvider(mono, targetChannels: 2);
        float[] buffer = new float[2];

        adapter.Read(buffer, 0, buffer.Length);

        buffer.Should().Equal(0.3f, 0.3f);
    }

    private sealed class StaticSource(WaveFormat format, float[] samples) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = format;

        public int Read(Span<float> buffer)
        {
            int n = Math.Min(buffer.Length, samples.Length);
            samples.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    }
}
