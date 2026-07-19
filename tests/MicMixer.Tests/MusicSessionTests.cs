using MicMixer.Music;
using Xunit;

namespace MicMixer.Tests;

/// <summary>
/// The queue and previous/next/track-ended rules shared by the local UI and
/// the remote-control API. File existence is injected so no real files are
/// needed.
/// </summary>
public sealed class MusicSessionTests
{
    private static MusicSession CreateSession(params string[] missingFiles)
    {
        var missing = new HashSet<string>(missingFiles, StringComparer.OrdinalIgnoreCase);
        return new MusicSession(path => !missing.Contains(path));
    }

    public static IEnumerable<object?[]> PlaybackGateTruthTable()
    {
        foreach (bool externalMode in new[] { false, true })
        foreach (bool hasMonitorOutput in new[] { false, true })
        foreach (bool isRouting in new[] { false, true })
        foreach (bool isPaused in new[] { false, true })
        foreach (bool hasLibraryTrack in new[] { false, true })
        {
            string? reason = externalMode
                ? PlaybackBlockedReasons.ExternalModeActive
                : !hasMonitorOutput && !isRouting
                    ? PlaybackBlockedReasons.NoPlaybackClock
                    : !isPaused && !hasLibraryTrack
                        ? PlaybackBlockedReasons.EmptyLibrary
                        : null;
            yield return
            [
                hasMonitorOutput,
                isRouting,
                isPaused,
                externalMode,
                hasLibraryTrack,
                reason == null,
                reason
            ];
        }
    }

    [Theory]
    [MemberData(nameof(PlaybackGateTruthTable))]
    public void EvaluatePlaybackGate_ShouldCoverEveryStateCombination(
        bool hasMonitorOutput,
        bool isRouting,
        bool isPaused,
        bool externalMode,
        bool hasLibraryTrack,
        bool expectedCanStart,
        string? expectedReason)
    {
        var session = CreateSession();
        if (hasLibraryTrack)
        {
            session.SetLibrary(["track.mp3"]);
        }

        PlaybackGate result = session.EvaluatePlaybackGate(
            hasMonitorOutput, isRouting, isPaused, externalMode);

        Assert.Equal(expectedCanStart, result.CanStart);
        Assert.Equal(expectedReason, result.BlockedReason);
    }

    [Theory]
    [InlineData(false, false, false, false, false, PlaybackBlockedReasons.NoPlaybackClock)]
    [InlineData(false, false, true, false, false, PlaybackBlockedReasons.NoPlaybackClock)]
    [InlineData(true, false, false, false, false, PlaybackBlockedReasons.EmptyLibrary)]
    [InlineData(false, true, false, false, false, PlaybackBlockedReasons.EmptyLibrary)]
    [InlineData(true, false, true, false, true, null)]
    [InlineData(false, true, true, false, true, null)]
    [InlineData(true, true, true, true, false, PlaybackBlockedReasons.ExternalModeActive)]
    public void EvaluatePlaybackGate_ShouldFollowThePlaybackTruthTable(
        bool hasMonitorOutput,
        bool isRouting,
        bool isPaused,
        bool externalMode,
        bool expectedCanStart,
        string? expectedReason)
    {
        var session = CreateSession();

        PlaybackGate result = session.EvaluatePlaybackGate(
            hasMonitorOutput, isRouting, isPaused, externalMode);

        Assert.Equal(expectedCanStart, result.CanStart);
        Assert.Equal(expectedReason, result.BlockedReason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EvaluatePlaybackGate_ShouldAllowAClockedNonEmptyLibrary(
        bool hasMonitorOutput,
        bool isRouting)
    {
        var session = CreateSession();
        session.SetLibrary(["track.mp3"]);

        PlaybackGate result = session.EvaluatePlaybackGate(
            hasMonitorOutput, isRouting, isPaused: false, externalMode: false);

        Assert.True(result.CanStart);
        Assert.Null(result.BlockedReason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EvaluatePlaybackGate_ShouldAllowAnExplicitTrackWhenLibraryIsEmpty(
        bool hasMonitorOutput,
        bool isRouting)
    {
        var session = CreateSession();

        PlaybackGate result = session.EvaluatePlaybackGate(
            hasMonitorOutput,
            isRouting,
            isPaused: false,
            externalMode: false,
            hasExplicitTrack: true);

        Assert.True(result.CanStart);
        Assert.Null(result.BlockedReason);
    }

    [Fact]
    public void EvaluatePlaybackGate_ShouldStillRequireAClockForAnExplicitTrack()
    {
        var session = CreateSession();

        PlaybackGate result = session.EvaluatePlaybackGate(
            hasMonitorOutput: false,
            isRouting: false,
            isPaused: false,
            externalMode: false,
            hasExplicitTrack: true);

        Assert.False(result.CanStart);
        Assert.Equal(PlaybackBlockedReasons.NoPlaybackClock, result.BlockedReason);
    }

    [Fact]
    public void DelayedStart_ShouldCountDownAndAtomicallyReturnTheArmedTrack()
    {
        var delayed = new DelayedStartState();
        delayed.Arm(2, "armed.mp3");

        Assert.Equal(DelayedStartTick.Waiting, delayed.Tick());
        Assert.True(delayed.IsCountingDown);
        Assert.Equal(1, delayed.RemainingSeconds);

        DelayedStartTick completed = delayed.Tick();

        Assert.True(completed.IsReady);
        Assert.Equal("armed.mp3", completed.ArmedTrackPath);
        Assert.False(delayed.IsCountingDown);
        Assert.Equal(0, delayed.RemainingSeconds);
        Assert.Null(delayed.ArmedTrackPath);
    }

    [Fact]
    public void DelayedStart_CancelShouldClearTheWholeState()
    {
        var delayed = new DelayedStartState();
        delayed.Arm(10, "armed.mp3");

        delayed.Cancel();

        Assert.False(delayed.IsCountingDown);
        Assert.Equal(0, delayed.RemainingSeconds);
        Assert.Null(delayed.ArmedTrackPath);
        Assert.Equal(DelayedStartTick.Waiting, delayed.Tick());
    }

    [Fact]
    public void DelayedStart_RearmingShouldChangeTheDurationAndKeepTheChosenTarget()
    {
        var delayed = new DelayedStartState();
        delayed.Arm(10, "armed.mp3");
        delayed.Tick();

        delayed.Arm(3, delayed.ArmedTrackPath);

        Assert.True(delayed.IsCountingDown);
        Assert.Equal(3, delayed.RemainingSeconds);
        Assert.Equal("armed.mp3", delayed.ArmedTrackPath);
    }

    [Fact]
    public void DelayedStart_NullTrackShouldRepresentPlaySelectedAtCompletion()
    {
        var delayed = new DelayedStartState();
        delayed.Arm(1, trackPath: null);

        DelayedStartTick completed = delayed.Tick();

        Assert.True(completed.IsReady);
        Assert.Null(completed.ArmedTrackPath);
    }

    [Fact]
    public void DelayedStart_ShouldRejectNonPositiveDurations()
    {
        var delayed = new DelayedStartState();

        Assert.Throws<ArgumentOutOfRangeException>(() => delayed.Arm(0, null));
    }

    // --- Queue ---

    [Fact]
    public void ConsumeNextQueuedTrack_ShouldReturnTracksInQueueOrder()
    {
        var session = CreateSession();
        session.Enqueue(@"C:\music\a.mp3");
        session.Enqueue(@"C:\music\b.mp3");

        Assert.Equal(@"C:\music\a.mp3", session.ConsumeNextQueuedTrack());
        Assert.Equal(@"C:\music\b.mp3", session.ConsumeNextQueuedTrack());
        Assert.Null(session.ConsumeNextQueuedTrack());
        Assert.Empty(session.Queue);
    }

    [Fact]
    public void ConsumeNextQueuedTrack_ShouldSkipEntriesWhoseFilesWereDeleted()
    {
        var session = CreateSession(@"C:\music\deleted1.mp3", @"C:\music\deleted2.mp3");
        session.Enqueue(@"C:\music\deleted1.mp3");
        session.Enqueue(@"C:\music\deleted2.mp3");
        session.Enqueue(@"C:\music\still-here.mp3");

        Assert.Equal(@"C:\music\still-here.mp3", session.ConsumeNextQueuedTrack());
        Assert.Empty(session.Queue);
    }

    [Fact]
    public void ConsumeNextQueuedTrack_ShouldDrainAQueueOfOnlyDeletedFiles()
    {
        var session = CreateSession(@"C:\music\gone.mp3");
        session.Enqueue(@"C:\music\gone.mp3");
        session.Enqueue(@"C:\music\gone.mp3");

        Assert.Null(session.ConsumeNextQueuedTrack());
        Assert.Empty(session.Queue);
    }

    [Fact]
    public void MoveQueueItem_ShouldReorderTheQueue()
    {
        var session = CreateSession();
        session.Enqueue("a");
        session.Enqueue("b");
        session.Enqueue("c");

        Assert.True(session.MoveQueueItem(2, 0));

        Assert.Equal(new[] { "c", "a", "b" }, session.Queue);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    public void MoveQueueItem_ShouldRejectIndexesOutsideTheQueue(int fromIndex, int toIndex)
    {
        var session = CreateSession();
        session.Enqueue("a");
        session.Enqueue("b");

        Assert.False(session.MoveQueueItem(fromIndex, toIndex));
        Assert.Equal(new[] { "a", "b" }, session.Queue);
    }

    [Fact]
    public void MoveQueueItem_ShouldTreatSameIndexAsANoOp()
    {
        var session = CreateSession();
        session.Enqueue("a");
        session.Enqueue("b");

        Assert.True(session.MoveQueueItem(1, 1));
        Assert.Equal(new[] { "a", "b" }, session.Queue);
    }

    [Fact]
    public void RemoveQueueItemAt_ShouldRejectStaleIndexes()
    {
        var session = CreateSession();
        session.Enqueue("a");

        Assert.False(session.RemoveQueueItemAt(1));
        Assert.False(session.RemoveQueueItemAt(-1));
        Assert.True(session.RemoveQueueItemAt(0));
        Assert.Empty(session.Queue);
    }

    [Fact]
    public void GetQueuePositions_ShouldReportOneBasedPositionsForEveryDuplicate()
    {
        var session = CreateSession();
        session.Enqueue(@"C:\music\a.mp3");
        session.Enqueue(@"C:\music\b.mp3");
        session.Enqueue(@"C:\MUSIC\A.MP3");

        Assert.Equal(new[] { 1, 3 }, session.GetQueuePositions(@"C:\music\a.mp3"));
        Assert.Empty(session.GetQueuePositions(@"C:\music\missing.mp3"));
    }

    // --- Library navigation ---

    [Fact]
    public void FindNextLibraryTrack_ShouldAdvanceFromTheCurrentTrack()
    {
        var session = CreateSession();
        session.SetLibrary(new[] { "a", "b", "c" });

        Assert.Equal("b", session.FindNextLibraryTrack("a"));
        Assert.Equal("c", session.FindNextLibraryTrack("B"));
    }

    [Fact]
    public void FindNextLibraryTrack_ShouldReturnNullAtTheEndOfThePlaylist()
    {
        var session = CreateSession();
        session.SetLibrary(new[] { "a", "b" });

        Assert.Null(session.FindNextLibraryTrack("b"));
    }

    [Fact]
    public void FindNextLibraryTrack_ShouldReturnNullWhenNoTrackIsActive()
    {
        var session = CreateSession();
        session.SetLibrary(new[] { "a", "b" });

        Assert.Null(session.FindNextLibraryTrack(null));
        Assert.Null(session.FindNextLibraryTrack("not-in-library"));
    }

    [Fact]
    public void FindNextLibraryTrack_ShouldFallBackToTheLastStartedTrackWhenTheEngineIsStopped()
    {
        var session = CreateSession();
        session.SetLibrary(new[] { "a", "b", "c" });
        session.LastPlayedTrackPath = "b";

        Assert.Equal("c", session.FindNextLibraryTrack(null));
    }

    [Fact]
    public void FindPreviousLibraryTrack_ShouldStepBackWithinThePlaylist()
    {
        var session = CreateSession();
        session.SetLibrary(new[] { "a", "b", "c" });

        Assert.Equal("a", session.FindPreviousLibraryTrack("b"));
    }

    [Fact]
    public void FindPreviousLibraryTrack_ShouldSignalRestartAtTheStartOfThePlaylist()
    {
        var session = CreateSession();
        session.SetLibrary(new[] { "a", "b" });

        Assert.Null(session.FindPreviousLibraryTrack("a"));
        Assert.Null(session.FindPreviousLibraryTrack("not-in-library"));
        Assert.Null(session.FindPreviousLibraryTrack(null));
    }

    // --- Track-ended decisions ---

    [Fact]
    public void OnTrackEnded_ShouldAdvanceWhenTheLastStartedTrackEnded()
    {
        var session = CreateSession();
        session.LastPlayedTrackPath = @"C:\music\a.mp3";

        Assert.Equal(
            TrackEndAction.PlayNext,
            session.OnTrackEnded(@"C:\MUSIC\A.MP3", SingleTrackPlayMode.Off,
                engineHasTrack: false, externalMode: false));
    }

    [Fact]
    public void OnTrackEnded_ShouldIgnoreTheEventWhenANewerTrackWasAlreadyStarted()
    {
        var session = CreateSession();
        session.LastPlayedTrackPath = @"C:\music\newer.mp3";

        Assert.Equal(
            TrackEndAction.Ignore,
            session.OnTrackEnded(@"C:\music\older.mp3", SingleTrackPlayMode.Off,
                engineHasTrack: false, externalMode: false));
    }

    [Fact]
    public void OnTrackEnded_ShouldIgnoreTheEventWhileTheEngineStillHasATrack()
    {
        var session = CreateSession();
        session.LastPlayedTrackPath = @"C:\music\a.mp3";

        Assert.Equal(
            TrackEndAction.Ignore,
            session.OnTrackEnded(@"C:\music\a.mp3", SingleTrackPlayMode.Off,
                engineHasTrack: true, externalMode: false));
    }

    [Fact]
    public void OnTrackEnded_ShouldIgnoreTheEventInExternalMode()
    {
        var session = CreateSession();
        session.LastPlayedTrackPath = @"C:\music\a.mp3";

        Assert.Equal(
            TrackEndAction.Ignore,
            session.OnTrackEnded(@"C:\music\a.mp3", SingleTrackPlayMode.Off,
                engineHasTrack: false, externalMode: true));
    }

    [Fact]
    public void OnTrackEnded_ShouldIgnoreTheEventAfterAnExplicitStop()
    {
        var session = CreateSession();
        session.LastPlayedTrackPath = null;

        Assert.Equal(
            TrackEndAction.Ignore,
            session.OnTrackEnded(@"C:\music\a.mp3", SingleTrackPlayMode.Off,
                engineHasTrack: false, externalMode: false));
    }

    [Theory]
    [InlineData(SingleTrackPlayMode.Once)]
    [InlineData(SingleTrackPlayMode.Always)]
    public void OnTrackEnded_ShouldStopInsteadOfAdvancingInSingleTrackMode(SingleTrackPlayMode mode)
    {
        var session = CreateSession();
        session.LastPlayedTrackPath = @"C:\music\a.mp3";
        session.Enqueue(@"C:\music\queued.mp3");

        Assert.Equal(
            TrackEndAction.StopSingleTrack,
            session.OnTrackEnded(@"C:\music\a.mp3", mode,
                engineHasTrack: false, externalMode: false));

        // Single-track mode leaves the queue untouched for a later explicit play.
        Assert.Single(session.Queue);
    }
}
