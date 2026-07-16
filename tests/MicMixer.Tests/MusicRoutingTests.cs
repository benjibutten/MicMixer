using MicMixer.Audio;
using Xunit;

namespace MicMixer.Tests;

public sealed class MusicRoutingTests
{
    /// <summary>
    /// Truth table for the music branch's cable gate: monitor-only always wins,
    /// otherwise music follows push-to-talk unless it is set to ignore it.
    /// </summary>
    [Theory]
    [InlineData(true, false, false, true)]   // gate open, defaults: music flows
    [InlineData(false, false, false, false)] // push-to-talk closed: music muted with the mic
    [InlineData(false, true, false, true)]   // ...unless music ignores push-to-talk
    [InlineData(true, true, false, true)]    // ignore-PTT never blocks an open gate
    [InlineData(true, false, true, false)]   // monitor-only cuts music from the cable
    [InlineData(false, true, true, false)]   // monitor-only overrides ignore-PTT
    public void MusicRouteOpen_ShouldCombineGateAndMusicModes(
        bool gateOpen, bool ignoresPushToTalk, bool monitorOnly, bool expected)
    {
        using var router = new AudioRouter();

        router.SetOutputGateOpen(gateOpen);
        router.SetMusicIgnoresPushToTalk(ignoresPushToTalk);
        router.SetMusicMonitorOnly(monitorOnly);

        Assert.Equal(expected, router.MusicRouteOpen);
    }

    [Fact]
    public void Stop_ShouldKeepMusicRoutingConfigurationButResetTheGate()
    {
        using var router = new AudioRouter();

        router.SetOutputGateOpen(false);
        router.SetMusicIgnoresPushToTalk(true);
        router.SetMusicMonitorOnly(true);

        router.Stop();

        Assert.True(router.OutputGateOpen);
        Assert.True(router.MusicIgnoresPushToTalk);
        Assert.True(router.MusicMonitorOnly);
    }
}
