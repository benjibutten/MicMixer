using System.IO;

namespace MicMixer.Music;

/// <summary>How the single-track button behaves when the current track ends.</summary>
public enum SingleTrackPlayMode
{
    /// <summary>Playlist advances as usual.</summary>
    Off,
    /// <summary>Stop after the playing track, then fall back to <see cref="Off"/> automatically.</summary>
    Once,
    /// <summary>Stop after every track until the user turns it off (double-click).</summary>
    Always
}

/// <summary>What the window should do when the engine reports that a track ended.</summary>
public enum TrackEndAction
{
    /// <summary>The event is stale (external mode, a newer track is active, or an older track ended) — drop it.</summary>
    Ignore,
    /// <summary>Single-track mode: reflect the stop instead of advancing. The queue stays untouched.</summary>
    StopSingleTrack,
    /// <summary>Advance to the next queued or library track.</summary>
    PlayNext
}

public static class PlaybackBlockedReasons
{
    public const string ExternalModeActive = "external_mode_active";
    public const string NoPlaybackClock = "no_playback_clock";
    public const string EmptyLibrary = "empty_library";
}

public readonly record struct PlaybackGate(bool CanStart, string? BlockedReason)
{
    public static PlaybackGate Allowed { get; } = new(true, null);

    public static PlaybackGate Blocked(string reason) => new(false, reason);
}

public readonly record struct DelayedStartTick(bool IsReady, string? ArmedTrackPath)
{
    public static DelayedStartTick Waiting { get; } = new(false, null);
}

/// <summary>Owns the complete, atomic state of a delayed playback start.</summary>
public sealed class DelayedStartState
{
    public bool IsCountingDown { get; private set; }

    public int RemainingSeconds { get; private set; }

    public string? ArmedTrackPath { get; private set; }

    public void Arm(int seconds, string? trackPath)
    {
        if (seconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        IsCountingDown = true;
        RemainingSeconds = seconds;
        ArmedTrackPath = trackPath;
    }

    public DelayedStartTick Tick()
    {
        if (!IsCountingDown)
        {
            return DelayedStartTick.Waiting;
        }

        RemainingSeconds--;
        if (RemainingSeconds > 0)
        {
            return DelayedStartTick.Waiting;
        }

        string? armedTrackPath = ArmedTrackPath;
        Reset();
        return new DelayedStartTick(true, armedTrackPath);
    }

    public void Cancel() => Reset();

    private void Reset()
    {
        IsCountingDown = false;
        RemainingSeconds = 0;
        ArmedTrackPath = null;
    }
}

/// <summary>
/// Owns the music session state shared between the local UI and the
/// remote-control API: the manual queue, the library order used by
/// previous/next, the single-track mode and the last started track.
/// Decision logic only — no WPF and no audio engine — so both entry points
/// follow the same rules and the rules are testable without a window.
/// </summary>
public sealed class MusicSession
{
    private readonly List<string> _queue = new();
    private readonly Func<string, bool> _fileExists;
    private List<string> _library = new();

    public MusicSession(Func<string, bool>? fileExists = null)
    {
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>The track most recently started, or null after an explicit stop.</summary>
    public string? LastPlayedTrackPath { get; set; }

    public SingleTrackPlayMode SingleTrackMode { get; set; }

    public DelayedStartState DelayedStart { get; } = new();

    public IReadOnlyList<string> Queue => _queue;

    public int LibraryCount => _library.Count;

    /// <summary>
    /// Applies the single playback-start rule used by local controls and the
    /// remote API. A paused engine already owns a track, so it does not require
    /// a non-empty library in order to resume.
    /// </summary>
    public PlaybackGate EvaluatePlaybackGate(
        bool hasMonitorOutput,
        bool isRouting,
        bool isPaused,
        bool externalMode,
        bool hasExplicitTrack = false)
    {
        if (externalMode)
        {
            return PlaybackGate.Blocked(PlaybackBlockedReasons.ExternalModeActive);
        }

        if (!hasMonitorOutput && !isRouting)
        {
            return PlaybackGate.Blocked(PlaybackBlockedReasons.NoPlaybackClock);
        }

        if (!isPaused && !hasExplicitTrack && _library.Count == 0)
        {
            return PlaybackGate.Blocked(PlaybackBlockedReasons.EmptyLibrary);
        }

        return PlaybackGate.Allowed;
    }

    /// <summary>Replaces the library order used by previous/next navigation.</summary>
    public void SetLibrary(IEnumerable<string> trackPaths)
    {
        _library = trackPaths.ToList();
    }

    public void Enqueue(string path) => _queue.Add(path);

    public bool RemoveQueueItemAt(int index)
    {
        if (index < 0 || index >= _queue.Count)
        {
            return false;
        }

        _queue.RemoveAt(index);
        return true;
    }

    public bool MoveQueueItem(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= _queue.Count
            || toIndex < 0 || toIndex >= _queue.Count)
        {
            return false;
        }

        if (fromIndex != toIndex)
        {
            string path = _queue[fromIndex];
            _queue.RemoveAt(fromIndex);
            _queue.Insert(toIndex, path);
        }

        return true;
    }

    public void ClearQueue() => _queue.Clear();

    /// <summary>1-based queue positions for a track; the same track may be queued several times.</summary>
    public IReadOnlyList<int> GetQueuePositions(string path)
    {
        var positions = new List<int>();
        for (int i = 0; i < _queue.Count; i++)
        {
            if (string.Equals(_queue[i], path, StringComparison.OrdinalIgnoreCase))
            {
                positions.Add(i + 1);
            }
        }

        return positions;
    }

    /// <summary>
    /// Dequeues the next queued track, dropping entries whose files have been
    /// deleted since they were queued. Null when the queue runs out.
    /// </summary>
    public string? ConsumeNextQueuedTrack()
    {
        while (_queue.Count > 0)
        {
            string queued = _queue[0];
            _queue.RemoveAt(0);

            if (_fileExists(queued))
            {
                return queued;
            }
        }

        return null;
    }

    /// <summary>
    /// The library track after the current one (falling back to the last
    /// started track), or null when the playlist has run out.
    /// </summary>
    public string? FindNextLibraryTrack(string? engineCurrentPath)
    {
        int index = IndexOfCurrent(engineCurrentPath);
        return index >= 0 && index + 1 < _library.Count ? _library[index + 1] : null;
    }

    /// <summary>
    /// The library track before the current one, or null when the current
    /// track should restart instead (start of playlist or unknown track).
    /// </summary>
    public string? FindPreviousLibraryTrack(string? engineCurrentPath)
    {
        int index = IndexOfCurrent(engineCurrentPath);
        return index > 0 ? _library[index - 1] : null;
    }

    /// <summary>
    /// Decides how to react to a track-ended event from the engine. The mode
    /// is passed in as captured when the event fired: the UI may toggle it
    /// again before the dispatcher gets around to processing the event.
    /// A stale event — the user already started another track, or an older
    /// track than the last one started ended — is ignored so we never stop or
    /// advance from the wrong song.
    /// </summary>
    public TrackEndAction OnTrackEnded(
        string endedPath,
        SingleTrackPlayMode modeWhenTrackEnded,
        bool engineHasTrack,
        bool externalMode)
    {
        if (externalMode
            || engineHasTrack
            || !string.Equals(endedPath, LastPlayedTrackPath, StringComparison.OrdinalIgnoreCase))
        {
            return TrackEndAction.Ignore;
        }

        return modeWhenTrackEnded != SingleTrackPlayMode.Off
            ? TrackEndAction.StopSingleTrack
            : TrackEndAction.PlayNext;
    }

    private int IndexOfCurrent(string? engineCurrentPath)
    {
        string? current = engineCurrentPath ?? LastPlayedTrackPath;
        return current == null
            ? -1
            : _library.FindIndex(track => string.Equals(track, current, StringComparison.OrdinalIgnoreCase));
    }
}
