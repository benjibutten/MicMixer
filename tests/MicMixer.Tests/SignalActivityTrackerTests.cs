using AwesomeAssertions;
using MicMixer.Music;
using Xunit;

namespace MicMixer.Tests;

public sealed class SignalActivityTrackerTests
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(2);

    [Fact]
    public void Observe_ShouldHoldActivityAcrossShortSilentGaps()
    {
        var tracker = new SignalActivityTracker(0.005f, HoldDuration);

        tracker.Observe(0.2f, TimeSpan.Zero).Should().BeTrue();
        tracker.Observe(0f, TimeSpan.FromSeconds(1.9)).Should().BeFalse();

        tracker.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Observe_ShouldDeactivateAfterSustainedSilence()
    {
        var tracker = new SignalActivityTracker(0.005f, HoldDuration);
        tracker.Observe(0.2f, TimeSpan.Zero);

        tracker.Observe(0f, TimeSpan.FromSeconds(2.01)).Should().BeTrue();

        tracker.IsActive.Should().BeFalse();
    }

    [Fact]
    public void LaterSignal_ShouldRestartTheHoldWindow()
    {
        var tracker = new SignalActivityTracker(0.005f, HoldDuration);
        tracker.Observe(0.2f, TimeSpan.Zero);
        tracker.Observe(0.1f, TimeSpan.FromSeconds(1.5));

        tracker.Observe(0f, TimeSpan.FromSeconds(3)).Should().BeFalse();
        tracker.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Reset_ShouldClearActivityImmediately()
    {
        var tracker = new SignalActivityTracker(0.005f, HoldDuration);
        tracker.Observe(0.2f, TimeSpan.Zero);

        tracker.Reset();

        tracker.IsActive.Should().BeFalse();
    }
}
