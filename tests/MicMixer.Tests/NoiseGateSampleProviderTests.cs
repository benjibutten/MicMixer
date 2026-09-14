using AwesomeAssertions;
using MicMixer.Audio;
using NAudio.Wave;
using Xunit;

namespace MicMixer.Tests;

/// <summary>
/// The gate's promise: exact digital zero once the mic has been quiet past the
/// hold time, unity while speaking, and click-free ramps in between.
/// </summary>
public sealed class NoiseGateSampleProviderTests
{
    private const int SampleRate = 48_000;
    private const int HoldFrames = SampleRate / 4;      // 250 ms
    private const int ReleaseFrames = SampleRate * 40 / 1_000;
    private const int AttackFrames = SampleRate * 2 / 1_000;
    private const float ThresholdDb = -40f;
    private const float Loud = 0.1f;      // -20 dBFS, well above the threshold
    private const float Quiet = 0.001f;   // -60 dBFS, well below it

    [Fact]
    public void Disabled_ShouldPassTheSourceThroughUntouched()
    {
        var source = new LevelSampleProvider(channels: 2) { Level = Quiet };
        var gate = new NoiseGateSampleProvider(source, () => false, () => ThresholdDb);

        float[] block = ReadMilliseconds(gate, 100, channels: 2);

        block.Should().OnlyContain(sample => sample == Quiet);
        gate.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Enabled_ShouldReachExactSilence_WhenTheMicStaysBelowTheThreshold()
    {
        var source = new LevelSampleProvider(channels: 1) { Level = Quiet };
        var gate = new NoiseGateSampleProvider(source, () => true, () => ThresholdDb);

        // Hold (250 ms) plus release (40 ms) must have elapsed.
        ReadMilliseconds(gate, 300, channels: 1);
        float[] block = ReadMilliseconds(gate, 20, channels: 1);

        block.Should().OnlyContain(sample => sample == 0f);
        gate.IsOpen.Should().BeFalse();
        gate.ReadAndResetInputPeak().Should().Be(Quiet, "the readout shows the level before gating");
        gate.ReadAndResetInputPeak().Should().Be(0f);
    }

    [Fact]
    public void Enabled_ShouldOpen_WhenOnlyOneChannelExceedsTheThreshold()
    {
        var source = new LevelSampleProvider(channels: 2);
        source.SetLevels(Quiet, Loud);
        var gate = new NoiseGateSampleProvider(source, () => true, () => ThresholdDb);

        ReadMilliseconds(gate, 5, channels: 2);
        float[] block = ReadMilliseconds(gate, 10, channels: 2);

        block.Where((_, i) => i % 2 == 0).Should().OnlyContain(sample => sample == Quiet);
        block.Where((_, i) => i % 2 == 1).Should().OnlyContain(sample => sample == Loud);
        gate.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void InputPeak_ShouldAccumulateAcrossBlocksUntilRead()
    {
        var source = new LevelSampleProvider(channels: 1) { Level = Loud };
        var gate = new NoiseGateSampleProvider(source, () => true, () => ThresholdDb);
        ReadMilliseconds(gate, 10, channels: 1);
        source.Level = Quiet;
        ReadMilliseconds(gate, 10, channels: 1);

        gate.ReadAndResetInputPeak().Should().Be(Loud, "a transient in an earlier block must still reach the meter");
    }

    [Fact]
    public void Enabled_ShouldPassSpeechAtUnity_AfterTheAttack()
    {
        var source = new LevelSampleProvider(channels: 2) { Level = Quiet };
        var gate = new NoiseGateSampleProvider(source, () => true, () => ThresholdDb);
        ReadMilliseconds(gate, 300, channels: 2);

        source.Level = Loud;
        ReadMilliseconds(gate, 5, channels: 2);
        float[] block = ReadMilliseconds(gate, 20, channels: 2);

        block.Should().OnlyContain(sample => sample == Loud);
        gate.IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Enabled_ShouldHoldOpenThroughAShortPause_ThenCloseAfterTheHoldTime()
    {
        var source = new LevelSampleProvider(channels: 1) { Level = Loud };
        var gate = new NoiseGateSampleProvider(source, () => true, () => ThresholdDb);
        ReadMilliseconds(gate, 50, channels: 1);

        source.Level = Quiet;
        float[] afterSpeech = ReadMilliseconds(gate, 300, channels: 1);

        int firstAttenuated = Array.FindIndex(afterSpeech, sample => sample < Quiet);
        int firstSilent = Array.FindIndex(afterSpeech, sample => sample == 0f);
        firstAttenuated.Should().Be(HoldFrames, "the gate holds for exactly 250 ms after the level drops");
        firstSilent.Should().BeInRange(HoldFrames + ReleaseFrames, HoldFrames + ReleaseFrames + 2, "then releases over 40 ms");
        afterSpeech[firstSilent..].Should().OnlyContain(sample => sample == 0f);
    }

    [Fact]
    public void Transitions_ShouldRampWithoutSteps()
    {
        var source = new LevelSampleProvider(channels: 1) { Level = Loud };
        var gate = new NoiseGateSampleProvider(source, () => true, () => ThresholdDb);
        ReadMilliseconds(gate, 50, channels: 1);

        source.Level = Quiet;
        float[] closing = ReadMilliseconds(gate, 300, channels: 1);
        source.Level = Loud;
        float[] opening = ReadMilliseconds(gate, 5, channels: 1);

        AssertSmooth(closing, Quiet / ReleaseFrames * 1.01f);
        AssertSmooth(opening, Loud / AttackFrames * 1.01f);
        opening[^1].Should().Be(Loud);
    }

    [Fact]
    public void DisablingWhileClosed_ShouldRampBackToUnity()
    {
        bool enabled = true;
        var source = new LevelSampleProvider(channels: 1) { Level = Quiet };
        var gate = new NoiseGateSampleProvider(source, () => enabled, () => ThresholdDb);
        ReadMilliseconds(gate, 300, channels: 1);

        enabled = false;
        float[] reopening = ReadMilliseconds(gate, 5, channels: 1);

        reopening[0].Should().BeLessThan(Quiet);
        reopening[^1].Should().Be(Quiet);
        AssertSmooth(reopening, Quiet / AttackFrames * 1.01f);
    }

    private static void AssertSmooth(float[] samples, float maxStep)
    {
        for (int i = 1; i < samples.Length; i++)
        {
            Math.Abs(samples[i] - samples[i - 1]).Should().BeLessThanOrEqualTo(maxStep, $"sample {i} must not jump");
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
        private readonly float[] _levels = new float[channels];

        public float Level
        {
            set => Array.Fill(_levels, value);
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);

        public void SetLevels(params float[] levels) => levels.CopyTo(_levels, 0);

        public int Read(Span<float> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = _levels[i % _levels.Length];
            }

            return buffer.Length;
        }
    }
}
