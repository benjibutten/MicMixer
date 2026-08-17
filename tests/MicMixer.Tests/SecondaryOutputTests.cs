using AwesomeAssertions;
using MicMixer.Audio;
using NAudio.Wave;
using Xunit;

namespace MicMixer.Tests;

/// <summary>
/// Hardware-independent tests for the secondary-output branch: the drift-absorbing
/// resampler, the bounded buffer with its startup cushion and re-buffering, and the
/// secondary-only volume stage. Gating is applied upstream by
/// <see cref="MixFanoutSampleProvider"/> and is covered by
/// <see cref="MixFanoutSampleProviderTests"/>.
/// </summary>
public sealed class SecondaryOutputTests
{
    private const int TestSampleRate = 48_000;
    private const int TestChannels = 2;
    private const int BlockDurationDivisor = 20;
    private const int HalfBlockDivisor = 2;
    private const double HighWatermarkSeconds = 0.4;
    private const double RateTolerance = 0.0005d;
    private const int DriftFramesPerBlock = 5;
    private const int WarmUpRounds = 20;
    private const int MeasuredRounds = 3_000;
    private const int MaxStarvationReads = 10;
    private const int OverflowBlockCount = 40;
    private const int PrimeBlockCount = 4;
    private const int VolumeBlockCount = 2;
    private const int SingleBlockCount = 1;
    private const int PartialFrameSamples = 1;
    private const int AlternateSampleRate = 22_050;
    private const double NominalRateRatio = 1d;
    private const float TestVolume = 0.5f;
    private const float SilenceLevel = 0f;
    private const float SentinelLevel = 1f;
    private const float FullSignalLevel = 0.5f;
    private const float RebufferSignalLevel = 0.25f;
    private const float TrimSignalLevel = 0.1f;
    private const float MinimumExpectedLevel = 0.4f;
    private const float PreGapLeakLimit = 0.35f;
    private const float LeftChannelLevel = 0.25f;
    private const float RightChannelLevel = -0.75f;
    private const int LeftChannelIndex = 0;
    private const int RightChannelIndex = 1;
    private const float ExpectedScaledLevel = FullSignalLevel * TestVolume;

    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(TestSampleRate, TestChannels);

    /// <summary>Interleaved samples for ~50 ms at the test format.</summary>
    private const int BlockFrames = TestSampleRate / BlockDurationDivisor;
    private const int BlockSamples = BlockFrames * TestChannels;

    private static readonly int BytesPerSecond = Format.SampleRate * Format.Channels * sizeof(float);

    private static readonly int HighWatermarkBytes = (int)(BytesPerSecond * HighWatermarkSeconds);

    /// <summary>
    /// Slack around a silence-to-audio boundary, in samples. The drift resampler
    /// carries a three-frame group delay and, while it corrects drift, consumes
    /// marginally more or less than it produces — so the step lands a frame or two
    /// either side of nominal. Assertions stay clear of it rather than pinning down
    /// a boundary a third of a millisecond wide.
    /// </summary>
    private const int BoundarySlackSamples = 64;

    [Fact]
    public void Volume_ShouldScaleOnlyTheSecondaryBranch()
    {
        var branch = new SecondaryTapBranch(Format) { Volume = TestVolume };

        WriteBlocks(branch, VolumeBlockCount, FullSignalLevel);
        DrainStartupCushion(branch);

        float[] secondary = new float[BlockSamples];
        branch.Read(secondary, 0, secondary.Length);
        ShouldSettleAt(secondary, ExpectedScaledLevel);
    }

    [Fact]
    public void Read_ShouldDeliverSilence_OnUnderflow()
    {
        var branch = new SecondaryTapBranch(Format);

        DrainStartupCushion(branch);

        float[] output = Constant(BlockSamples, SentinelLevel);
        branch.Read(output, 0, output.Length).Should().Be(output.Length);
        output.Should().OnlyContain(sample => sample == SilenceLevel);
    }

    [Fact]
    public void Branch_ShouldStartWithSilenceCushion_BeforeDeliveringAudio()
    {
        var branch = new SecondaryTapBranch(Format);
        WriteBlocks(branch, PrimeBlockCount, FullSignalLevel);

        // The cushion absorbs a secondary clock that runs slightly faster than
        // the primary: the first ~150 ms out are silence, then the real audio.
        int cushionSamples = branch.PrimeBytes / sizeof(float);
        float[] cushion = new float[cushionSamples - BoundarySlackSamples];
        branch.Read(cushion, 0, cushion.Length).Should().Be(cushion.Length);
        cushion.Should().OnlyContain(sample => sample == SilenceLevel);

        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);
        ShouldSettleAt(output, FullSignalLevel);
    }

    [Fact]
    public void Branch_ShouldRebufferAfterStarvation_UntilCushionIsRestored()
    {
        var branch = new SecondaryTapBranch(Format);
        DrainStartupCushion(branch);

        // True starvation: the read comes up short and re-buffering starts.
        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(sample => sample == SilenceLevel);

        // One block is less than the cushion — the branch must keep holding
        // silence instead of dribbling out audio in tiny gaps.
        branch.Write(Constant(BlockSamples, FullSignalLevel), 0, BlockSamples);
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(sample => sample == SilenceLevel, "re-buffering must hold silence until the cushion is rebuilt");

        // Refill up to the cushion level; audio then resumes.
        int cushionSamples = branch.PrimeBytes / sizeof(float);
        while (branch.BufferedBytes < branch.PrimeBytes)
        {
            branch.Write(Constant(BlockSamples, FullSignalLevel), 0, BlockSamples);
        }

        branch.Read(output, 0, output.Length);
        ShouldSettleAt(output, FullSignalLevel);
        cushionSamples.Should().BeGreaterThan(BlockSamples, "the test relies on one block being smaller than the cushion");
    }

    [Fact]
    public void Write_ShouldDropOldestAudio_WhenBufferGrowsPastHighWatermark()
    {
        var branch = new SecondaryTapBranch(Format);
        int totalBytesWritten = 0;

        // Two seconds of audio without any consumer: the buffer must stay bounded
        // by trimming the oldest audio instead of growing (or rejecting new audio).
        float[] block = Constant(BlockSamples, TrimSignalLevel);
        for (int i = 0; i < OverflowBlockCount; i++)
        {
            branch.Write(block, 0, BlockSamples);
            totalBytesWritten += BlockSamples * sizeof(float);
        }

        totalBytesWritten.Should().BeGreaterThan(HighWatermarkBytes, "the test must actually overflow the buffer");
        branch.BufferedBytes.Should().BeGreaterThan(0);
        branch.BufferedBytes.Should().BeLessThanOrEqualTo(HighWatermarkBytes);

        // The freshest audio survives the trim (the startup cushion and the oldest
        // blocks were dropped first).
        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);
        ShouldSettleAt(output, TrimSignalLevel);
    }

    [Fact]
    public void Branch_ShouldNotCorrectTheRate_WhileTheBufferSitsAtTarget()
    {
        var branch = new SecondaryTapBranch(Format);

        // Freshly primed, the fill level is exactly the target: the loop asks for
        // no correction and the interpolator stays at phase zero, where it
        // reproduces its input sample for sample. A branch that is not drifting
        // pays nothing for the drift compensation.
        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);

        branch.RateRatio.Should().Be(NominalRateRatio);
        output.Should().OnlyContain(sample => sample == SilenceLevel);
    }

    [Fact]
    public void Branch_ShouldStretchPlayback_WhenTheSecondaryClockRunsFast()
    {
        // 0.2% faster than the producer — an order of magnitude more drift than
        // two real audio clocks show. The branch must absorb it by rate alone:
        // no starvation gap, no trim, no discontinuity for a capture downstream.
        var drift = SimulateDrift(consumedFramesPerBlock: BlockFrames + DriftFramesPerBlock);

        drift.Quietest.Should().BeGreaterThan(MinimumExpectedLevel, "stretching must not leave silence gaps in the output");
        drift.MinimumFill.Should().BeGreaterThan(0, "the buffer must never run dry");
        drift.MaximumFill.Should().BeLessThan(HighWatermarkBytes, "the trim safety net must stay unused");

        // Settles on exactly the correction the mismatch demands — 2400/2405 —
        // well inside the ±0.5% the loop is allowed to spend.
        drift.FinalRateRatio.Should().BeApproximately(
            BlockFrames / (double)(BlockFrames + DriftFramesPerBlock),
            RateTolerance);
    }

    [Fact]
    public void Branch_ShouldCompressPlayback_WhenTheSecondaryClockRunsSlow()
    {
        var drift = SimulateDrift(consumedFramesPerBlock: BlockFrames - DriftFramesPerBlock);

        drift.Quietest.Should().BeGreaterThan(MinimumExpectedLevel, "compressing must not leave silence gaps in the output");
        drift.MinimumFill.Should().BeGreaterThan(0, "the buffer must never run dry");
        drift.MaximumFill.Should().BeLessThan(HighWatermarkBytes, "the trim safety net must stay unused");
        drift.FinalRateRatio.Should().BeApproximately(
            BlockFrames / (double)(BlockFrames - DriftFramesPerBlock),
            RateTolerance);
    }

    [Fact]
    public void Branch_ShouldResumeFromSilence_AfterRebuffering()
    {
        var branch = new SecondaryTapBranch(Format);
        WriteBlocks(branch, SingleBlockCount, FullSignalLevel);
        DrainStartupCushion(branch);

        // Play the one written block out until the branch starves, leaving the
        // interpolator's window holding audio from before the gap.
        float[] output = new float[BlockSamples];
        int reads = 0;

        do
        {
            branch.Read(output, 0, output.Length);
            reads++;
        }
        while (output.Any(sample => sample != SilenceLevel) && reads < MaxStarvationReads);

        output.Should().OnlyContain(sample => sample == SilenceLevel, "the branch must starve and hold silence");

        // Rebuild the cushion at a different level and let playback resume.
        while (branch.BufferedBytes < branch.PrimeBytes)
        {
            branch.Write(Constant(BlockSamples, RebufferSignalLevel), 0, BlockSamples);
        }

        // Nothing may come back near the pre-gap level. The threshold sits above
        // the interpolator's overshoot on the silence-to-0.25 step (~0.27) and
        // well below the 0.5 that played before the gap.
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(
            sample => sample < PreGapLeakLimit,
            "audio from before the gap must not survive in the interpolator's window");
        ShouldSettleAt(output, RebufferSignalLevel);
    }

    [Fact]
    public void Branch_ShouldKeepChannelsAligned_AtRatesWhereTheThresholdsSplitAFrame()
    {
        // 0.15 s at 22 050 Hz stereo is 3307.5 frames. A cushion measured in bytes
        // alone would put the stream half a frame out and swap left and right for
        // the rest of the session.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(AlternateSampleRate, TestChannels);
        int frameBytes = format.Channels * sizeof(float);
        var branch = new SecondaryTapBranch(format);

        (branch.PrimeBytes % frameBytes).Should().Be(0, "the cushion must be a whole number of frames");

        int blockFrames = format.SampleRate / BlockDurationDivisor;
        float[] block = new float[blockFrames * format.Channels];
        for (int frame = 0; frame < blockFrames; frame++)
        {
            int frameOffset = frame * format.Channels;
            block[frameOffset + LeftChannelIndex] = LeftChannelLevel;
            block[frameOffset + RightChannelIndex] = RightChannelLevel;
        }

        for (int i = 0; i < PrimeBlockCount; i++)
        {
            branch.Write(block, 0, block.Length);
        }

        DrainStartupCushion(branch);

        float[] output = new float[block.Length];
        branch.Read(output, 0, output.Length);

        // Second half only: the silence-to-audio step sits at the start.
        for (int sample = output.Length / HalfBlockDivisor; sample < output.Length; sample++)
        {
            float expected = sample % format.Channels == LeftChannelIndex ? LeftChannelLevel : RightChannelLevel;
            output[sample].Should().Be(expected, "sample {0} must stay on its own channel", sample);
        }
    }

    [Fact]
    public void Read_ShouldSilenceAPartialFrame_WithoutRestartingTheBranch()
    {
        var branch = new SecondaryTapBranch(Format);
        WriteBlocks(branch, PrimeBlockCount, FullSignalLevel);
        DrainStartupCushion(branch);

        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);

        // An odd sample count cannot be a whole number of stereo frames. The
        // remainder has to be silenced rather than read as a starving source —
        // the buffer is below the cushion level by now, so a spurious re-buffer
        // would mute the branch for as long as it took to refill.
        float[] partial = new float[BlockSamples + PartialFrameSamples];
        branch.Read(partial, 0, partial.Length).Should().Be(partial.Length);
        partial[^1].Should().Be(SilenceLevel, "the sample that cannot complete a frame is silenced");
        ShouldSettleAt(partial[..BlockSamples], FullSignalLevel);

        branch.BufferedBytes.Should().BeLessThan(branch.PrimeBytes, "the test relies on a re-buffer being audible");
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(sample => sample == FullSignalLevel);
    }

    /// <summary>
    /// Runs ~2.5 simulated minutes of a producer writing one block per round while
    /// the consumer reads a different number of frames, i.e. a steady clock
    /// mismatch. Measurements start once the startup cushion has played out.
    /// </summary>
    private static DriftResult SimulateDrift(int consumedFramesPerBlock)
    {
        var branch = new SecondaryTapBranch(Format);
        float[] block = Constant(BlockSamples, FullSignalLevel);
        float[] output = new float[consumedFramesPerBlock * Format.Channels];

        for (int round = 0; round < WarmUpRounds; round++)
        {
            branch.Write(block, 0, block.Length);
            branch.Read(output, 0, output.Length);
        }

        int minimumFill = int.MaxValue;
        int maximumFill = 0;
        float quietest = float.MaxValue;

        for (int round = 0; round < MeasuredRounds; round++)
        {
            branch.Write(block, 0, block.Length);
            branch.Read(output, 0, output.Length);

            minimumFill = Math.Min(minimumFill, branch.BufferedBytes);
            maximumFill = Math.Max(maximumFill, branch.BufferedBytes);

            foreach (float sample in output)
            {
                quietest = Math.Min(quietest, sample);
            }
        }

        return new DriftResult(minimumFill, maximumFill, quietest, branch.RateRatio);
    }

    private readonly record struct DriftResult(
        int MinimumFill,
        int MaximumFill,
        float Quietest,
        double FinalRateRatio);

    /// <summary>
    /// Asserts a block has settled on a constant by its second half, leaving the
    /// silence-to-audio step at the start out of it. Once the step has passed, the
    /// interpolator reproduces the samples exactly — hence the equality.
    /// </summary>
    private static void ShouldSettleAt(float[] output, float expected)
    {
        output.Skip(output.Length / HalfBlockDivisor).Should().OnlyContain(sample => sample == expected);
    }

    /// <summary>Consumes the branch's initial silence cushion so reads reach real audio.</summary>
    private static void DrainStartupCushion(SecondaryTapBranch branch)
    {
        int cushionSamples = branch.PrimeBytes / sizeof(float);
        float[] scratch = new float[cushionSamples];
        branch.Read(scratch, 0, cushionSamples);
    }

    private static void WriteBlocks(SecondaryTapBranch branch, int count, float value)
    {
        float[] block = Constant(BlockSamples, value);
        for (int i = 0; i < count; i++)
        {
            branch.Write(block, 0, BlockSamples);
        }
    }

    private static float[] Constant(int count, float value)
    {
        var samples = new float[count];
        Array.Fill(samples, value);
        return samples;
    }
}
