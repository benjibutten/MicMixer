using AwesomeAssertions;
using MicMixer.Audio;
using NAudio.Wave;
using Xunit;

namespace MicMixer.Tests;

/// <summary>
/// Hardware-independent tests for the secondary-output branch: the bounded buffer
/// with its startup cushion and re-buffering, and the secondary-only volume stage.
/// Gating is applied upstream by <see cref="MixFanoutSampleProvider"/> and is
/// covered by <see cref="MixFanoutSampleProviderTests"/>.
/// </summary>
public sealed class SecondaryOutputTests
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    /// <summary>Interleaved samples for ~50 ms at the test format.</summary>
    private const int BlockSamples = 4_800;

    [Fact]
    public void Volume_ShouldScaleOnlyTheSecondaryBranch()
    {
        var branch = new SecondaryTapBranch(Format) { Volume = 0.5f };

        branch.Write(Constant(BlockSamples, 0.5f), 0, BlockSamples);
        DrainStartupCushion(branch);

        float[] secondary = new float[BlockSamples];
        branch.Read(secondary, 0, secondary.Length);
        secondary.Should().OnlyContain(sample => sample == 0.25f);
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
        branch.Write(Constant(BlockSamples, 0.5f), 0, BlockSamples);

        // The cushion absorbs a secondary clock that runs slightly faster than
        // the primary: the first ~150 ms out are silence, then the real audio.
        int cushionSamples = branch.PrimeBytes / sizeof(float);
        float[] cushion = new float[cushionSamples];
        branch.Read(cushion, 0, cushion.Length).Should().Be(cushion.Length);
        cushion.Should().OnlyContain(sample => sample == 0f);

        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(sample => sample == 0.5f);
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
        output.Should().OnlyContain(sample => sample == 0.5f);
        cushionSamples.Should().BeGreaterThan(BlockSamples, "the test relies on one block being smaller than the cushion");
    }

    [Fact]
    public void Write_ShouldDropOldestAudio_WhenBufferGrowsPastHighWatermark()
    {
        var branch = new SecondaryTapBranch(Format);
        int bytesPerSecond = Format.SampleRate * Format.Channels * sizeof(float);
        int highWatermarkBytes = (int)(bytesPerSecond * 0.4);
        int totalBytesWritten = 0;

        // Two seconds of audio without any consumer: the buffer must stay bounded
        // by trimming the oldest audio instead of growing (or rejecting new audio).
        float[] block = Constant(BlockSamples, 0.1f);
        for (int i = 0; i < 40; i++)
        {
            branch.Write(block, 0, BlockSamples);
            totalBytesWritten += BlockSamples * sizeof(float);
        }

        totalBytesWritten.Should().BeGreaterThan(highWatermarkBytes, "the test must actually overflow the buffer");
        branch.BufferedBytes.Should().BeGreaterThan(0);
        branch.BufferedBytes.Should().BeLessThanOrEqualTo(highWatermarkBytes);

        // The freshest audio survives the trim (the startup cushion and the oldest
        // blocks were dropped first).
        float[] output = new float[BlockSamples];
        branch.Read(output, 0, output.Length);
        output.Should().OnlyContain(sample => sample == 0.1f);
    }

    /// <summary>Consumes the branch's initial silence cushion so reads reach real audio.</summary>
    private static void DrainStartupCushion(SecondaryTapBranch branch)
    {
        int cushionSamples = branch.PrimeBytes / sizeof(float);
        float[] scratch = new float[cushionSamples];
        branch.Read(scratch, 0, cushionSamples);
    }

    private static float[] Constant(int count, float value)
    {
        var samples = new float[count];
        Array.Fill(samples, value);
        return samples;
    }
}
