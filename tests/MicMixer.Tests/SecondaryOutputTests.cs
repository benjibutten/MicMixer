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
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    /// <summary>Interleaved samples for ~50 ms at the test format.</summary>
    private const int BlockSamples = 4_800;

    private const int BlockFrames = BlockSamples / 2;

    private static readonly int BytesPerSecond = Format.SampleRate * Format.Channels * sizeof(float);

    private static readonly int HighWatermarkBytes = (int)(BytesPerSecond * 0.4);

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
        var branch = new SecondaryTapBranch(Format) { Volume = 0.5f };

        WriteBlocks(branch, 2, 0.5f);
        DrainStartupCushion(branch);

        float[] secondary = new float[BlockSamples];
        branch.Read(secondary, 0, secondary.Length);
        ShouldSettleAt(secondary, 0.25f);
    }

    [Fact]
    public void Read_ShouldDeliverSilence_OnUnderflow()
    {
        var branch = new SecondaryTapBranch(Format);

        DrainStartupCushion(branch);

        float[] output = Constant(BlockSamples, 1f);
        branch.Read(output, 0, output.Length).Should().Be(output.Length);
        output.Should().OnlyContain(sample => sample == 0f);
    }

    [Fact]
    public void Branch_ShouldStartWithSilenceCushion_BeforeDeliveringAudio()
    {
        var branch = new SecondaryTapBranch(Format);
        WriteBlocks(branch, 4, 0.5f);

        // The cushion absorbs a secondary clock that runs slightly faster than
        // the primary: the first ~150 ms out are silence, then the real audio.
        int cushionSamples = branch.PrimeBytes / sizeof(float);
        float[] cushion = new float[cushionSamples - BoundarySlackSamples];
        branch.Read(cushion, 0, cushion.Length).Should().Be(cushion.Length);
        cushion.Should().OnlyContain(sample => sample == 0f);

        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);
        ShouldSettleAt(output, 0.5f);
    }

    [Fact]
    public void Branch_ShouldRebufferAfterStarvation_UntilCushionIsRestored()
    {
        var branch = new SecondaryTapBranch(Format);
        DrainStartupCushion(branch);

        // True starvation: the read comes up short and re-buffering starts.
        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(sample => sample == 0f);

        // One block is less than the cushion — the branch must keep holding
        // silence instead of dribbling out audio in tiny gaps.
        branch.Write(Constant(BlockSamples, 0.5f), 0, BlockSamples);
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(sample => sample == 0f, "re-buffering must hold silence until the cushion is rebuilt");

        // Refill up to the cushion level; audio then resumes.
        int cushionSamples = branch.PrimeBytes / sizeof(float);
        while (branch.BufferedBytes < branch.PrimeBytes)
        {
            branch.Write(Constant(BlockSamples, 0.5f), 0, BlockSamples);
        }

        branch.Read(output, 0, output.Length);
        ShouldSettleAt(output, 0.5f);
        cushionSamples.Should().BeGreaterThan(BlockSamples, "the test relies on one block being smaller than the cushion");
    }

    [Fact]
    public void Write_ShouldDropOldestAudio_WhenBufferGrowsPastHighWatermark()
    {
        var branch = new SecondaryTapBranch(Format);
        int totalBytesWritten = 0;

        // Two seconds of audio without any consumer: the buffer must stay bounded
        // by trimming the oldest audio instead of growing (or rejecting new audio).
        float[] block = Constant(BlockSamples, 0.1f);
        for (int i = 0; i < 40; i++)
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
        ShouldSettleAt(output, 0.1f);
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

        branch.RateRatio.Should().Be(1d);
        output.Should().OnlyContain(sample => sample == 0f);
    }

    [Fact]
    public void Branch_ShouldStretchPlayback_WhenTheSecondaryClockRunsFast()
    {
        // 0.2% faster than the producer — an order of magnitude more drift than
        // two real audio clocks show. The branch must absorb it by rate alone:
        // no starvation gap, no trim, no discontinuity for a capture downstream.
        var drift = SimulateDrift(consumedFramesPerBlock: BlockFrames + 5);

        drift.Quietest.Should().BeGreaterThan(0.4f, "stretching must not leave silence gaps in the output");
        drift.MinimumFill.Should().BeGreaterThan(0, "the buffer must never run dry");
        drift.MaximumFill.Should().BeLessThan(HighWatermarkBytes, "the trim safety net must stay unused");

        // Settles on exactly the correction the mismatch demands — 2400/2405 —
        // well inside the ±0.5% the loop is allowed to spend.
        drift.FinalRateRatio.Should().BeApproximately(BlockFrames / (double)(BlockFrames + 5), 0.0005d);
    }

    [Fact]
    public void Branch_ShouldCompressPlayback_WhenTheSecondaryClockRunsSlow()
    {
        var drift = SimulateDrift(consumedFramesPerBlock: BlockFrames - 5);

        drift.Quietest.Should().BeGreaterThan(0.4f, "compressing must not leave silence gaps in the output");
        drift.MinimumFill.Should().BeGreaterThan(0, "the buffer must never run dry");
        drift.MaximumFill.Should().BeLessThan(HighWatermarkBytes, "the trim safety net must stay unused");
        drift.FinalRateRatio.Should().BeApproximately(BlockFrames / (double)(BlockFrames - 5), 0.0005d);
    }

    [Fact]
    public void Branch_ShouldResumeFromSilence_AfterRebuffering()
    {
        var branch = new SecondaryTapBranch(Format);
        WriteBlocks(branch, 1, 0.5f);
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
        while (output.Any(sample => sample != 0f) && reads < 10);

        output.Should().OnlyContain(sample => sample == 0f, "the branch must starve and hold silence");

        // Rebuild the cushion at a different level and let playback resume.
        while (branch.BufferedBytes < branch.PrimeBytes)
        {
            branch.Write(Constant(BlockSamples, 0.25f), 0, BlockSamples);
        }

        // Nothing may come back near the pre-gap level. The threshold sits above
        // the interpolator's overshoot on the silence-to-0.25 step (~0.27) and
        // well below the 0.5 that played before the gap.
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(
            sample => sample < 0.35f,
            "audio from before the gap must not survive in the interpolator's window");
        ShouldSettleAt(output, 0.25f);
    }

    [Fact]
    public void Branch_ShouldKeepChannelsAligned_AtRatesWhereTheThresholdsSplitAFrame()
    {
        // 0.15 s at 22 050 Hz stereo is 3307.5 frames. A cushion measured in bytes
        // alone would put the stream half a frame out and swap left and right for
        // the rest of the session.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(22_050, 2);
        int frameBytes = format.Channels * sizeof(float);
        var branch = new SecondaryTapBranch(format);

        (branch.PrimeBytes % frameBytes).Should().Be(0, "the cushion must be a whole number of frames");

        int blockFrames = format.SampleRate / 20;
        float[] block = new float[blockFrames * format.Channels];
        for (int frame = 0; frame < blockFrames; frame++)
        {
            block[frame * 2] = 0.25f;
            block[(frame * 2) + 1] = -0.75f;
        }

        for (int i = 0; i < 4; i++)
        {
            branch.Write(block, 0, block.Length);
        }

        DrainStartupCushion(branch);

        float[] output = new float[block.Length];
        branch.Read(output, 0, output.Length);

        // Second half only: the silence-to-audio step sits at the start.
        for (int sample = output.Length / 2; sample < output.Length; sample++)
        {
            float expected = sample % 2 == 0 ? 0.25f : -0.75f;
            output[sample].Should().Be(expected, "sample {0} must stay on its own channel", sample);
        }
    }

    [Fact]
    public void Read_ShouldSilenceAPartialFrame_WithoutRestartingTheBranch()
    {
        var branch = new SecondaryTapBranch(Format);
        WriteBlocks(branch, 4, 0.5f);
        DrainStartupCushion(branch);

        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);

        // An odd sample count cannot be a whole number of stereo frames. The
        // remainder has to be silenced rather than read as a starving source —
        // the buffer is below the cushion level by now, so a spurious re-buffer
        // would mute the branch for as long as it took to refill.
        float[] partial = new float[BlockSamples + 1];
        branch.Read(partial, 0, partial.Length).Should().Be(partial.Length);
        partial[^1].Should().Be(0f, "the sample that cannot complete a frame is silenced");
        ShouldSettleAt(partial[..BlockSamples], 0.5f);

        branch.BufferedBytes.Should().BeLessThan(branch.PrimeBytes, "the test relies on a re-buffer being audible");
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(sample => sample == 0.5f);
    }

    /// <summary>
    /// Runs ~2.5 simulated minutes of a producer writing one block per round while
    /// the consumer reads a different number of frames, i.e. a steady clock
    /// mismatch. Measurements start once the startup cushion has played out.
    /// </summary>
    private static DriftResult SimulateDrift(int consumedFramesPerBlock)
    {
        const int WarmUpRounds = 20;
        const int MeasuredRounds = 3_000;

        var branch = new SecondaryTapBranch(Format);
        float[] block = Constant(BlockSamples, 0.5f);
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
        output.Skip(output.Length / 2).Should().OnlyContain(sample => sample == expected);
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
