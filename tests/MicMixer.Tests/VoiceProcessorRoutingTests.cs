using AwesomeAssertions;
using MicMixer.Audio;
using MicMixer.Dsp;
using NAudio.Wave;
using Xunit;

namespace MicMixer.Tests;

public sealed class VoiceProcessorRoutingTests
{
    private const int SampleRate = 48_000;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ProcessedVoiceVolume_ShouldScaleOnlyWetAfterDspAndUpdateItsMeter(int channels)
    {
        var source = new ConstantSampleProvider(0.4f, channels);
        using var processor = new DelayedPassthroughProcessor(channels);
        using var pair = new VoiceProcessorSamplePair(source, processor,
            new VoiceProcessorRuntimeDiagnostics(), () => 0.5f);
        var dry = new float[480 * channels];
        var wet = new float[dry.Length];
        pair.Read(dry, wet);
        pair.Read(dry, wet);
        dry.Should().OnlyContain(x => x == 0.4f);
        wet.Should().OnlyContain(x => x == 0.2f);
        pair.ProcessedPeak.Should().Be(0.2f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ProcessedVoiceVolume_ShouldRampAcrossCallbacksAndReachSilenceInTenMilliseconds(int channels)
    {
        float volume = 1f;
        using var processor = new DelayedPassthroughProcessor(channels);
        using var pair = new VoiceProcessorSamplePair(new ConstantSampleProvider(0.4f, channels),
            processor, new VoiceProcessorRuntimeDiagnostics(), () => volume);
        var dry = new float[240 * channels];
        var wet = new float[dry.Length];
        pair.Read(dry, wet);
        volume = 0f;
        pair.Read(dry, wet);
        wet[0].Should().BeInRange(0.399f, 0.4f);
        wet[^1].Should().BeApproximately(0.2f, 0.00001f);
        for (int i = 1; i < wet.Length; i++)
            Math.Abs(wet[i] - wet[i - 1]).Should().BeLessThan(0.001f);
        pair.Read(dry, wet);
        wet[^1].Should().Be(0f);
        pair.Read(dry, wet);
        wet.Should().OnlyContain(x => x == 0f);
        dry.Should().OnlyContain(x => x == 0.4f);
        pair.ProcessedPeak.Should().Be(0f);
        processor.ProcessCalls.Should().Be(4);
        volume = 0.5f;
        pair.Read(dry, wet);
        wet[^1].Should().BeApproximately(0.1f, 0.00001f);
        pair.Read(dry, wet);
        wet[^1].Should().Be(0.2f);
    }

    [Fact]
    public void ProcessedVoiceVolume_WithRealDsp_ShouldPreserveWaveformAndDryPath()
    {
        using var first = new VoiceProcessorSamplePair(new ConstantSampleProvider(0.1f),
            new ProfileVoiceProcessor(SampleRate, 1, new VoiceDspParameters { PitchSemitones = 3f }),
            new VoiceProcessorRuntimeDiagnostics());
        using var second = new VoiceProcessorSamplePair(new ConstantSampleProvider(0.1f),
            new ProfileVoiceProcessor(SampleRate, 1, new VoiceDspParameters { PitchSemitones = 3f }),
            new VoiceProcessorRuntimeDiagnostics(), () => 0.5f);
        var dry1 = new float[480];
        var dry2 = new float[480];
        var wet1 = new float[480];
        var wet2 = new float[480];
        float peak = 0f;
        for (int callback = 0; callback < 30; callback++)
        {
            first.Read(dry1, wet1);
            second.Read(dry2, wet2);
            dry1.Should().Equal(dry2);
            for (int i = 0; i < wet1.Length; i++)
            {
                wet2[i].Should().BeApproximately(wet1[i] * 0.5f, 0.000001f);
                peak = Math.Max(peak, Math.Abs(wet1[i]));
            }
        }
        peak.Should().BeGreaterThan(0.001f);
    }

    [Fact]
    public void BuiltInPair_ShouldReadPhysicalCaptureExactlyOncePerCallback()
    {
        var source = new SequenceSampleProvider();
        using var processor = new DelayedPassthroughProcessor(latencySamples: 16);
        using var pair = new VoiceProcessorSamplePair(source, processor, new VoiceProcessorRuntimeDiagnostics());
        var dry = new float[480];
        var wet = new float[480];

        pair.Read(dry, wet);
        pair.Read(dry, wet);

        source.ReadCalls.Should().Be(2);
        source.SamplesRead.Should().Be(960);
        processor.ProcessCalls.Should().Be(2);
    }

    [Fact]
    public void BuiltInPair_ShouldAlignDryAndWetUsingReportedLatency()
    {
        var source = new SequenceSampleProvider();
        using var processor = new DelayedPassthroughProcessor(latencySamples: 37);
        using var pair = new VoiceProcessorSamplePair(source, processor, new VoiceProcessorRuntimeDiagnostics());
        var dry = new float[257];
        var wet = new float[257];

        pair.Read(dry, wet);

        dry.Should().Equal(wet);
    }

    [Fact]
    public void HotkeyCrossfade_ShouldAdvanceBothTimelinesWithoutDroppingOrDuplicatingFrames()
    {
        var primary = new SequenceSampleProvider();
        var secondary = new SequenceSampleProvider();
        bool modified = false;
        using var sut = new SwitchingSampleProvider(primary, secondary, () => modified, crossfadeMilliseconds: 8);
        var output = new float[480];

        sut.Read(output).Should().Be(output.Length);
        modified = true;
        sut.Read(output).Should().Be(output.Length);
        AssertContinuousCrossfade(output, firstSample: 480);
        modified = false;
        sut.Read(output).Should().Be(output.Length);
        AssertContinuousCrossfade(output, firstSample: 960);

        primary.SamplesRead.Should().Be(1_440);
        secondary.SamplesRead.Should().Be(1_440);
        primary.ReadCalls.Should().Be(3);
        secondary.ReadCalls.Should().Be(3);
        output.Should().OnlyContain(sample => float.IsFinite(sample));
    }

    private static void AssertContinuousCrossfade(float[] samples, int firstSample)
    {
        const int fadeFrames = SampleRate * 8 / 1_000;
        for (int i = 0; i < samples.Length; i++)
        {
            double progress = i < fadeFrames ? i / (double)(fadeFrames - 1) : 1d;
            float gain = i < fadeFrames
                ? (float)(Math.Cos(progress * Math.PI / 2d) + Math.Sin(progress * Math.PI / 2d))
                : 1f;
            float expected = (firstSample + i) / 10_000f * gain;
            samples[i].Should().BeApproximately(expected, 0.000_001f);
        }
    }

    [Fact]
    public void BuiltInRoutingProcess_ShouldNotAllocateAfterWarmup()
    {
        var source = new SequenceSampleProvider();
        using var processor = new DelayedPassthroughProcessor(latencySamples: 16);
        float volume = 1f;
        using var pair = new VoiceProcessorSamplePair(source, processor, new VoiceProcessorRuntimeDiagnostics(), () => volume);
        using var sut = new SwitchingSampleProvider(pair, static () => true);
        var output = new float[480];
        sut.Read(output);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            volume = i % 2 == 0 ? 0.5f : 1f;
            sut.Read(output);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void HotkeyCrossfade_EarlyReversal_ShouldPreserveCurrentBlendWithoutGainOvershoot()
    {
        bool modified = false;
        using var sut = new SwitchingSampleProvider(
            new ConstantSampleProvider(1f),
            new ConstantSampleProvider(0f),
            () => modified,
            crossfadeMilliseconds: 8);
        var output = new float[96];

        sut.Read(output.AsSpan(0, 1));
        modified = true;
        sut.Read(output.AsSpan(0, 48));
        float beforeReversal = output[47];

        modified = false;
        sut.Read(output);

        output[0].Should().BeLessThanOrEqualTo(1f);
        Math.Abs(output[0] - beforeReversal).Should().BeLessThan(0.01f);
        output.Should().OnlyContain(sample => sample <= 1f);
    }

    [Fact]
    public void HotkeyCrossfade_LateReversal_ShouldNotJumpToThePreviousTarget()
    {
        bool modified = false;
        using var sut = new SwitchingSampleProvider(
            new ConstantSampleProvider(1f),
            new ConstantSampleProvider(0f),
            () => modified,
            crossfadeMilliseconds: 8);
        var output = new float[288];

        sut.Read(output.AsSpan(0, 1));
        modified = true;
        sut.Read(output);
        float beforeReversal = output[^1];

        modified = false;
        sut.Read(output.AsSpan(0, 1));

        output[0].Should().BeApproximately(beforeReversal, 0.01f);
    }

    [Fact]
    public void Dispose_ShouldResetAndDisposeProcessor()
    {
        var source = new SequenceSampleProvider();
        var processor = new DelayedPassthroughProcessor(latencySamples: 16);
        var pair = new VoiceProcessorSamplePair(source, processor, new VoiceProcessorRuntimeDiagnostics());
        var output = new float[64];
        pair.Read(output, new float[64]);

        pair.Dispose();

        processor.ResetCalls.Should().Be(1);
        processor.DisposeCalls.Should().Be(1);
    }

    private sealed class SequenceSampleProvider : ISampleProvider
    {
        private int _nextSample;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);
        public int ReadCalls { get; private set; }
        public int SamplesRead { get; private set; }

        public int Read(Span<float> buffer)
        {
            ReadCalls++;
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = _nextSample++ / 10_000f;
            }

            SamplesRead += buffer.Length;
            return buffer.Length;
        }

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    }

    private sealed class ConstantSampleProvider(float value, int channels = 1) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);

        public int Read(Span<float> buffer)
        {
            buffer.Fill(value);
            return buffer.Length;
        }

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    }

    private sealed class DelayedPassthroughProcessor : IVoiceProcessor
    {
        private readonly float[] _delay;
        private int _position;

        public DelayedPassthroughProcessor(int latencySamples)
        {
            LatencySamples = latencySamples;
            _delay = new float[latencySamples];
        }

        public int LatencySamples { get; }
        public int ProcessCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Process(ReadOnlySpan<float> input, Span<float> output)
        {
            ProcessCalls++;
            for (int i = 0; i < input.Length; i++)
            {
                output[i] = _delay[_position];
                _delay[_position] = input[i];
                _position++;
                if (_position == _delay.Length)
                {
                    _position = 0;
                }
            }
        }

        public void Reset()
        {
            ResetCalls++;
            Array.Clear(_delay);
            _position = 0;
        }

        public void Dispose() => DisposeCalls++;
    }
}
