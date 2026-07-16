using AwesomeAssertions;
using MicMixer.Audio;
using NAudio.Wave;
using Xunit;

namespace MicMixer.Tests;

/// <summary>
/// Hardware-independent tests for the routing chain's core: independent cable
/// gates for mic and music, and the secondary fanout with its own per-source
/// gates. Gate states are fixed before construction, so the gains start steady
/// and the outputs can be asserted exactly (no ramp in the way).
/// </summary>
public sealed class MixFanoutSampleProviderTests
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    private const int BlockSamples = 4_800;
    private const float MicLevel = 0.25f;
    private const float MusicLevel = 0.5f;

    [Fact]
    public void Cable_ShouldCarryMicAndMusic_WhenAllGatesAreOpen()
    {
        float[] cable = ReadCable(micGateOpen: true, musicGateOpen: true);

        cable.Should().OnlyContain(sample => sample == MicLevel + MusicLevel);
    }

    [Fact]
    public void Cable_ShouldCarryOnlyMusic_WhenPushToTalkClosesTheMicButMusicBypassesIt()
    {
        float[] cable = ReadCable(micGateOpen: false, musicGateOpen: true);

        cable.Should().OnlyContain(sample => sample == MusicLevel);
    }

    [Fact]
    public void Cable_ShouldCarryOnlyMic_WhenMusicIsMonitorOnly()
    {
        float[] cable = ReadCable(micGateOpen: true, musicGateOpen: false);

        cable.Should().OnlyContain(sample => sample == MicLevel);
    }

    /// <summary>
    /// Regression for the monitor-only promise: even when the secondary output
    /// follows push-to-talk (its mic gate closed), the music must still reach the
    /// secondary device — the stream hears what the streamer hears.
    /// </summary>
    [Fact]
    public void Secondary_ShouldStillCarryMonitorOnlyMusic_WhenItsMicFollowsClosedPushToTalk()
    {
        float[]? secondary = null;
        var provider = CreateProvider(
            micGateOpen: false,
            musicGateOpen: false,
            secondaryMicOpen: false,
            secondaryMusicOpen: true,
            secondaryWrite: (buffer, offset, count) => secondary = buffer[offset..(offset + count)]);

        float[] cable = new float[BlockSamples];
        provider.Read(cable, 0, cable.Length);

        cable.Should().OnlyContain(sample => sample == 0f, "nothing may reach the cable");
        secondary.Should().NotBeNull();
        secondary.Should().OnlyContain(sample => sample == MusicLevel, "the secondary keeps carrying the previewed music");
    }

    [Fact]
    public void Secondary_ShouldCarryFullMix_WhenIgnoringPushToTalk()
    {
        float[]? secondary = null;
        var provider = CreateProvider(
            micGateOpen: false,
            musicGateOpen: false,
            secondaryMicOpen: true,
            secondaryMusicOpen: true,
            secondaryWrite: (buffer, offset, count) => secondary = buffer[offset..(offset + count)]);

        float[] cable = new float[BlockSamples];
        provider.Read(cable, 0, cable.Length);

        cable.Should().OnlyContain(sample => sample == 0f);
        secondary.Should().OnlyContain(sample => sample == MicLevel + MusicLevel);
    }

    [Fact]
    public void Secondary_ShouldBeSilentButStillFed_WhenBothItsGatesAreClosed()
    {
        float[]? secondary = null;
        var provider = CreateProvider(
            micGateOpen: false,
            musicGateOpen: false,
            secondaryMicOpen: false,
            secondaryMusicOpen: false,
            secondaryWrite: (buffer, offset, count) => secondary = buffer[offset..(offset + count)]);

        float[] cable = new float[BlockSamples];
        provider.Read(cable, 0, cable.Length);

        secondary.Should().NotBeNull("silence must still be written so the secondary clock stays fed");
        secondary.Should().OnlyContain(sample => sample == 0f);
    }

    [Fact]
    public void GateTransition_ShouldRampInsteadOfClicking()
    {
        bool micGateOpen = true;
        var provider = new MixFanoutSampleProvider(
            new ConstantSampleProvider(Format, MicLevel),
            music: null,
            micGateOpen: () => micGateOpen,
            musicGateOpen: static () => false,
            secondaryMicOpen: static () => false,
            secondaryMusicOpen: static () => false,
            secondaryWrite: null,
            musicMeteringEnabled: static () => false,
            onMusicLevels: static (_, _) => { });

        float[] block = new float[BlockSamples];
        provider.Read(block, 0, block.Length);
        block.Should().OnlyContain(sample => sample == MicLevel);

        micGateOpen = false;
        provider.Read(block, 0, block.Length);

        // ~8 ms ramp: the block starts near full level, fades, and ends silent.
        block[0].Should().BeGreaterThan(0f).And.BeLessThan(MicLevel);
        block[^1].Should().Be(0f);
    }

    [Fact]
    public void MusicMetering_ShouldReportPreGateLevels_EvenWhileMusicIsCutFromTheCable()
    {
        float reportedPeak = 0f;
        var provider = CreateProvider(
            micGateOpen: true,
            musicGateOpen: false,
            secondaryMicOpen: false,
            secondaryMusicOpen: false,
            secondaryWrite: null,
            musicMeteringEnabled: true,
            onMusicLevels: (peak, _) => reportedPeak = peak);

        float[] cable = new float[BlockSamples];
        provider.Read(cable, 0, cable.Length);

        reportedPeak.Should().Be(MusicLevel, "the music ring shows the signal level regardless of its cable gate");
    }

    private static float[] ReadCable(bool micGateOpen, bool musicGateOpen)
    {
        var provider = CreateProvider(micGateOpen, musicGateOpen, secondaryMicOpen: false, secondaryMusicOpen: false, secondaryWrite: null);
        float[] cable = new float[BlockSamples];
        provider.Read(cable, 0, cable.Length).Should().Be(cable.Length);
        return cable;
    }

    private static MixFanoutSampleProvider CreateProvider(
        bool micGateOpen,
        bool musicGateOpen,
        bool secondaryMicOpen,
        bool secondaryMusicOpen,
        Action<float[], int, int>? secondaryWrite,
        bool musicMeteringEnabled = false,
        Action<float, float>? onMusicLevels = null)
    {
        return new MixFanoutSampleProvider(
            new ConstantSampleProvider(Format, MicLevel),
            new ConstantSampleProvider(Format, MusicLevel),
            micGateOpen: () => micGateOpen,
            musicGateOpen: () => musicGateOpen,
            secondaryMicOpen: () => secondaryMicOpen,
            secondaryMusicOpen: () => secondaryMusicOpen,
            secondaryWrite: secondaryWrite,
            musicMeteringEnabled: () => musicMeteringEnabled,
            onMusicLevels: onMusicLevels ?? ((_, _) => { }));
    }

    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _value;

        public ConstantSampleProvider(WaveFormat format, float value)
        {
            WaveFormat = format;
            _value = value;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, _value, offset, count);
            return count;
        }
    }
}
