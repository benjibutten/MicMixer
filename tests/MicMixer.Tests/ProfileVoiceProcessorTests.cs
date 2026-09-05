using AwesomeAssertions;
using MicMixer.Dsp;
using Xunit;

namespace MicMixer.Tests;

public sealed class ProfileVoiceProcessorTests
{
    private static VoiceDspParameters TestParameters(bool longer) => new() { PitchSemitones = 3f, BlockMilliseconds = longer ? 50f : 45f, CompressorRatio = 4f };
    private const int SampleRate = 48_000;

    [Theory]
    [InlineData(false, 2_160)]
    [InlineData(true, 2_400)]
    public void BuiltInPreset_ShouldReportExpectedLatency(bool smoother, int expectedLatencySamples)
    {
        using var processor = new ProfileVoiceProcessor(SampleRate, channels: 1, TestParameters(smoother));

        processor.LatencySamples.Should().Be(expectedLatencySamples);
    }

    [Fact]
    public void InitialPreset_ShouldStayWithinTheAcceptableProcessorLatencyBudget()
    {
        using var processor = new ProfileVoiceProcessor(SampleRate, channels: 1);

        processor.LatencySamples.Should().BePositive();
        (processor.LatencySamples * 1_000d / SampleRate).Should().BeLessThanOrEqualTo(50d);
        processor.LatencySamples.Should().Be(processor.InputLatencySamples + processor.OutputLatencySamples);
    }

    [Fact]
    public void Constructor_ShouldRejectTonalityLimitAboveNyquist()
    {
        Action action = () => new ProfileVoiceProcessor(
            SampleRate,
            channels: 1,
            VoiceDspParameters.Initial with { TonalityLimitHz = SampleRate });

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Process_ShouldProduceFiniteAudioAfterReportedLatency()
    {
        using var processor = new ProfileVoiceProcessor(SampleRate, channels: 1);
        int signalFrames = SampleRate;
        var input = new float[signalFrames + processor.LatencySamples];
        var output = new float[input.Length];
        for (int i = 0; i < signalFrames; i++)
        {
            input[i] = 0.2f * MathF.Sin(2f * MathF.PI * 180f * i / SampleRate);
        }

        ProcessInTenMillisecondBlocks(processor, input, output);

        ReadOnlySpan<float> aligned = output.AsSpan(processor.LatencySamples, signalFrames);
        double squareSum = 0d;
        for (int i = 0; i < aligned.Length; i++)
        {
            float sample = aligned[i];
            float.IsFinite(sample).Should().BeTrue();
            squareSum += sample * sample;
        }

        Math.Sqrt(squareSum / aligned.Length).Should().BeGreaterThan(0.01d);
    }

    [Fact]
    public void Process_ShouldNotAllocateManagedMemoryAfterWarmup()
    {
        using var processor = new ProfileVoiceProcessor(SampleRate, channels: 1);
        var input = new float[480];
        var output = new float[input.Length];
        processor.Process(input, output);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            processor.Process(input, output);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().Be(0);
    }

    [Fact]
    public void ReportedLatency_ShouldAlignAnImpulseToItsOriginalFrame()
    {
        using var processor = new ProfileVoiceProcessor(
            SampleRate,
            channels: 1,
            VoiceDspParameters.Initial with { PitchSemitones = 0f, FormantSemitones = 0f });
        const int impulseFrame = 12_000;
        var input = new float[SampleRate + processor.LatencySamples];
        var output = new float[input.Length];
        input[impulseFrame] = 0.9f;

        ProcessInTenMillisecondBlocks(processor, input, output);

        ReadOnlySpan<float> aligned = output.AsSpan(processor.LatencySamples, SampleRate);
        int peakFrame = 0;
        float peak = 0f;
        for (int i = 0; i < aligned.Length; i++)
        {
            float magnitude = Math.Abs(aligned[i]);
            if (magnitude > peak)
            {
                peak = magnitude;
                peakFrame = i;
            }
        }

        peakFrame.Should().Be(impulseFrame);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuiltInPreset_ShouldLimitTransientPeaks(bool smoother)
    {
        using var processor = new ProfileVoiceProcessor(
            SampleRate,
            channels: 1,
            TestParameters(smoother) with { PitchSemitones = 0f, FormantSemitones = 0f });
        int signalFrames = SampleRate;
        var input = new float[signalFrames + processor.LatencySamples];
        var output = new float[input.Length];
        input[12_000] = 0.9f;

        ProcessInTenMillisecondBlocks(processor, input, output);

        output.Max(Math.Abs).Should().BeLessThanOrEqualTo(0.980_001f);
    }

    [Fact]
    public void Flush_ShouldRenderAnImpulseNearEndOfInput()
    {
        using var processor = new ProfileVoiceProcessor(
            SampleRate,
            channels: 1,
            VoiceDspParameters.Initial with { PitchSemitones = 0f, FormantSemitones = 0f });
        int signalFrames = SampleRate;
        int impulseFrame = signalFrames - Math.Max(1, processor.LatencySamples / 2);
        var input = new float[signalFrames];
        var output = new float[signalFrames + processor.LatencySamples];
        input[impulseFrame] = 0.9f;

        ProcessInTenMillisecondBlocks(processor, input, output);
        processor.Flush(output.AsSpan(signalFrames, processor.LatencySamples));

        ReadOnlySpan<float> aligned = output.AsSpan(processor.LatencySamples, signalFrames);
        int peakFrame = 0;
        float peak = 0f;
        for (int i = 0; i < aligned.Length; i++)
        {
            float magnitude = Math.Abs(aligned[i]);
            if (magnitude > peak)
            {
                peak = magnitude;
                peakFrame = i;
            }
        }

        peak.Should().BeGreaterThan(0.1f);
        peakFrame.Should().Be(impulseFrame);
    }

    [Fact]
    public void Process_ShouldRejectIncompleteInterleavedFrames()
    {
        using var processor = new ProfileVoiceProcessor(SampleRate, channels: 2);

        Action action = () => processor.Process(new float[3], new float[3]);

        action.Should().Throw<ArgumentException>();
    }

    private static void ProcessInTenMillisecondBlocks(
        ProfileVoiceProcessor processor,
        float[] input,
        float[] output)
    {
        const int blockSamples = SampleRate / 100;
        for (int offset = 0; offset < input.Length; offset += blockSamples)
        {
            int count = Math.Min(blockSamples, input.Length - offset);
            processor.Process(input.AsSpan(offset, count), output.AsSpan(offset, count));
        }
    }
}
