using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using MicMixer.Music;
using MicMixer.Remote;

namespace MicMixer;

public partial class MainWindow
{
    private string _libraryVersion = RemoteId.VersionForPaths([]);

    Task<ControlResult> IMicMixerControlHost.HandleControlRequestAsync(
        ControlRequest request,
        CancellationToken cancellationToken)
    {
        return Dispatcher.InvokeAsync(
            () => HandleControlRequest(request),
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }

    Task<MusicControlState> IMicMixerControlHost.GetControlStateAsync(CancellationToken cancellationToken)
    {
        return Dispatcher.InvokeAsync(
            BuildControlState,
            DispatcherPriority.Background,
            cancellationToken).Task;
    }

    private ControlResult HandleControlRequest(ControlRequest request)
    {
        string command = request.Command.Trim().ToLowerInvariant();

        if (command == "getstate")
        {
            return ControlResult.Ok(BuildControlState());
        }

        if (command == "gettracks")
        {
            return ControlResult.Ok(BuildRemoteTrackPage(request.Payload));
        }

        if (command == "getfolders")
        {
            return ControlResult.Ok(BuildRemoteFolders());
        }

        if (command == "refreshlibrary")
        {
            RefreshPlaylist(null);
            return ControlResult.Ok(BuildControlState());
        }

        if (command == "switchtolibrarymode")
        {
            if (_isExternalMode)
            {
                LibraryModeRadio.IsChecked = true;
            }

            return ControlResult.Ok(BuildControlState());
        }

        if (_isExternalMode && RequiresLibraryMode(command))
        {
            return ControlResult.Fail(
                PlaybackBlockedReasons.ExternalModeActive,
                "Direct control is available for MicMixer's built-in music library only.");
        }

        ControlResult result = command switch
        {
            "playtrack" => RemotePlayTrack(request.Payload),
            "toggleplaypause" => RemoteTogglePlayPause(),
            "stop" => RemoteStop(),
            "previous" => RemotePrevious(),
            "next" => RemoteNext(),
            "seek" => RemoteSeek(request.Payload),
            "setmusicvolume" => RemoteSetVolume(request.Payload, monitor: false),
            "setmonitorvolume" => RemoteSetVolume(request.Payload, monitor: true),
            "setvolumeslinked" => RemoteSetVolumesLinked(request.Payload),
            "setmusicignorespushtotalk" => RemoteSetMusicIgnoresPushToTalk(request.Payload),
            "setmusicmonitoronly" => RemoteSetMusicMonitorOnly(request.Payload),
            "enqueuetrack" => RemoteEnqueue(request.Payload),
            "removequeueitem" => RemoteRemoveQueueItem(request.Payload),
            "movequeueitem" => RemoteMoveQueueItem(request.Payload),
            "clearqueue" => RemoteClearQueue(),
            "startdelayedplay" => RemoteStartDelayedPlay(request.Payload),
            "canceldelayedplay" => RemoteCancelDelayedPlay(),
            "setdelayedstartseconds" => RemoteSetDelayedStartSeconds(request.Payload),
            "setsingletrackmode" => RemoteSetSingleTrackMode(request.Payload),
            "downloadfromurl" => RemoteDownloadFromUrl(request.Payload),
            "addmusicfolder" => RemoteAddMusicFolder(request.Payload),
            "removemusicfolder" => RemoteRemoveMusicFolder(request.Payload),
            "resetmusicfolders" => RemoteResetMusicFolders(),
            "setdownloadfolder" => RemoteSetDownloadFolder(request.Payload),
            _ => ControlResult.Fail("unknown_command", $"Unknown MicMixer command: {request.Command}")
        };

        return result.Success && result.Data == null
            ? ControlResult.Ok(BuildControlState())
            : result;
    }

    private static bool RequiresLibraryMode(string command) => command is
        "playtrack"
        or "toggleplaypause"
        or "stop"
        or "previous"
        or "next"
        or "seek"
        or "setmusicvolume"
        or "setmonitorvolume"
        or "setvolumeslinked"
        or "enqueuetrack"
        or "removequeueitem"
        or "movequeueitem"
        or "clearqueue"
        or "startdelayedplay"
        or "setsingletrackmode";

    private ControlResult RemotePlayTrack(JsonElement? payload)
    {
        if (!TryGetString(payload, "trackId", out string? trackId))
        {
            return MissingArgument("trackId");
        }

        TrackItem? track = FindTrack(trackId!);
        if (track == null)
        {
            return ControlResult.Fail("track_not_found", "The selected track is no longer in MicMixer's library.");
        }

        return PlayTrack(track.Path)
            ? ControlResult.Ok()
            : PlaybackStartFailed();
    }

    private ControlResult RemoteTogglePlayPause()
    {
        CancelDelayedStart(null);

        if (_music.IsPlaying)
        {
            _music.Pause();
            _musicWasAutoPaused = false;
            if (_music.CurrentTrackPath is string path)
            {
                MusicStatusText.Text = $"Pausad: {Path.GetFileNameWithoutExtension(path)}";
            }

            UpdateMusicUi();
            return ControlResult.Ok();
        }

        return StartPlaybackFromCurrentState()
            ? ControlResult.Ok()
            : PlaybackStartFailed();
    }

    private ControlResult RemoteStop()
    {
        CancelDelayedStart(null);
        _music.Stop();
        _musicWasAutoPaused = false;
        _session.LastPlayedTrackPath = null;
        PlaylistListBox.SelectedItem = null;
        MusicStatusText.Text = "Musiken stoppad — ingen låt är aktiv.";
        UpdateMusicUi();
        return ControlResult.Ok();
    }

    private ControlResult RemotePrevious()
    {
        CancelDelayedStart(null);
        if (!_music.HasTrack)
        {
            return ControlResult.Ok();
        }

        if (_music.Position > TimeSpan.FromSeconds(3))
        {
            _music.Seek(TimeSpan.Zero);
            UpdateMusicUi();
            return ControlResult.Ok();
        }

        if (_session.FindPreviousLibraryTrack(_music.CurrentTrackPath) is string previous)
        {
            PlayTrack(previous);
            return ControlResult.Ok();
        }

        _music.Seek(TimeSpan.Zero);
        UpdateMusicUi();
        return ControlResult.Ok();
    }

    private ControlResult RemoteNext()
    {
        CancelDelayedStart(null);
        PlayNextTrack();
        return ControlResult.Ok();
    }

    private ControlResult RemoteSeek(JsonElement? payload)
    {
        if (!TryGetDouble(payload, "positionSeconds", out double seconds) || !double.IsFinite(seconds))
        {
            return MissingArgument("positionSeconds");
        }

        _music.Seek(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        UpdateMusicUi();
        return ControlResult.Ok();
    }

    private ControlResult RemoteSetVolume(JsonElement? payload, bool monitor)
    {
        if (!TryGetDouble(payload, "volume", out double volume) || !double.IsFinite(volume))
        {
            return MissingArgument("volume");
        }

        if (monitor)
        {
            float value = (float)Math.Clamp(volume, 0, 1);
            _settings.MonitorVolume = value;
            _music.MonitorVolume = value;
            MonitorVolumeSlider.Value = value;
        }
        else
        {
            float value = (float)Math.Clamp(volume, 0, 1);
            _settings.MusicVolume = value;
            _music.MusicVolume = value;
            MusicVolumeSlider.Value = value;
        }

        ScheduleSettingsSave();

        return ControlResult.Ok();
    }

    private ControlResult RemoteEnqueue(JsonElement? payload)
    {
        if (!TryGetString(payload, "trackId", out string? trackId))
        {
            return MissingArgument("trackId");
        }

        TrackItem? track = FindTrack(trackId!);
        if (track == null)
        {
            return ControlResult.Fail("track_not_found", "The selected track is no longer in MicMixer's library.");
        }

        _session.Enqueue(track.Path);
        UpdateQueueUi();
        MusicStatusText.Text = $"Lade i kö: {track.Name}";
        return ControlResult.Ok();
    }

    private ControlResult RemoteRemoveQueueItem(JsonElement? payload)
    {
        if (!TryGetInt32(payload, "index", out int index))
        {
            return MissingArgument("index");
        }

        if (!_session.RemoveQueueItemAt(index))
        {
            return ControlResult.Fail("queue_index_out_of_range", "The queue item no longer exists.");
        }

        UpdateQueueUi();
        return ControlResult.Ok();
    }

    private ControlResult RemoteMoveQueueItem(JsonElement? payload)
    {
        if (!TryGetInt32(payload, "fromIndex", out int fromIndex)
            || !TryGetInt32(payload, "toIndex", out int toIndex))
        {
            return ControlResult.Fail("invalid_argument", "fromIndex and toIndex are required.");
        }

        if (!_session.MoveQueueItem(fromIndex, toIndex))
        {
            return ControlResult.Fail("queue_index_out_of_range", "The queue item no longer exists.");
        }

        UpdateQueueUi();
        return ControlResult.Ok();
    }

    private ControlResult RemoteClearQueue()
    {
        _session.ClearQueue();
        UpdateQueueUi();
        MusicStatusText.Text = "Kön rensad.";
        return ControlResult.Ok();
    }

    private ControlResult RemoteStartDelayedPlay(JsonElement? payload)
    {
        string? armedTrackPath = null;
        if (TryGetString(payload, "trackId", out string? trackId))
        {
            TrackItem? track = FindTrack(trackId!);
            if (track == null)
            {
                return ControlResult.Fail("track_not_found", "The selected track is no longer in MicMixer's library.");
            }

            armedTrackPath = track.Path;
        }

        if (IsDelayedStartCountingDown)
        {
            return ControlResult.Ok();
        }

        if (_music.IsPlaying)
        {
            return ControlResult.Fail("already_playing", "Music is already playing.");
        }

        PlaybackGate gate = EvaluatePlaybackGate();
        if (!gate.CanStart)
        {
            return PlaybackGateFailed(gate);
        }

        _session.DelayedStart.Arm(ClampDelayedStartSeconds(_settings.DelayedStartSeconds), armedTrackPath);
        _delayedStartTimer.Start();
        UpdateDelayedPlayCountdownUi();
        return ControlResult.Ok();
    }

    private ControlResult RemoteSetVolumesLinked(JsonElement? payload)
    {
        if (!TryGetBoolean(payload, "linked", out bool linked))
        {
            return MissingArgument("linked");
        }

        SetVolumesLinked(linked, renderControl: true);
        return ControlResult.Ok();
    }

    private ControlResult RemoteSetMusicIgnoresPushToTalk(JsonElement? payload)
    {
        if (!TryGetBoolean(payload, "enabled", out bool enabled))
        {
            return MissingArgument("enabled");
        }

        SetMusicRoutingModes(enabled, _settings.MusicMonitorOnly, renderControls: true);
        return ControlResult.Ok();
    }

    private ControlResult RemoteSetMusicMonitorOnly(JsonElement? payload)
    {
        if (!TryGetBoolean(payload, "enabled", out bool enabled))
        {
            return MissingArgument("enabled");
        }

        SetMusicRoutingModes(_settings.MusicIgnoresPushToTalk, enabled, renderControls: true);
        return ControlResult.Ok();
    }

    private ControlResult RemoteCancelDelayedPlay()
    {
        CancelDelayedStart("Fördröjd start avbruten.");
        return ControlResult.Ok();
    }

    private ControlResult RemoteSetDelayedStartSeconds(JsonElement? payload)
    {
        if (!TryGetInt32(payload, "seconds", out int seconds))
        {
            return MissingArgument("seconds");
        }

        _settings.DelayedStartSeconds = ClampDelayedStartSeconds(seconds);
        SaveSettings();

        if (IsDelayedStartCountingDown)
        {
            string? armedTrackPath = _session.DelayedStart.ArmedTrackPath;
            _session.DelayedStart.Arm(_settings.DelayedStartSeconds, armedTrackPath);
            _delayedStartTimer.Stop();
            _delayedStartTimer.Start();
            UpdateDelayedPlayCountdownUi();
        }
        else
        {
            UpdateDelayedPlayIdleUi();
        }

        return ControlResult.Ok();
    }

    private ControlResult RemoteSetSingleTrackMode(JsonElement? payload)
    {
        if (!TryGetString(payload, "mode", out string? modeText)
            || !Enum.TryParse(modeText, ignoreCase: true, out SingleTrackPlayMode mode)
            || !Enum.IsDefined(mode))
        {
            return ControlResult.Fail("invalid_argument", "mode must be Off, Once, or Always.");
        }

        SetSingleTrackMode(mode);
        return ControlResult.Ok();
    }

    private ControlResult RemoteDownloadFromUrl(JsonElement? payload)
    {
        if (!TryGetString(payload, "url", out string? url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ControlResult.Fail("invalid_argument", "url must be a valid http or https URL.");
        }

        if (_isDownloading)
        {
            return ControlResult.Fail("download_in_progress", "MicMixer is already downloading music.");
        }

        string? downloadFolder = null;
        if (TryGetString(payload, "folderId", out string? folderId))
        {
            downloadFolder = FindMusicFolder(folderId!);
            if (downloadFolder == null)
            {
                return ControlResult.Fail("folder_not_found", "The selected music folder is no longer configured.");
            }
        }

        YoutubeUrlBox.Text = url;
        _ = StartDownloadAsync(downloadFolder);
        return ControlResult.Ok();
    }

    private ControlResult RemoteAddMusicFolder(JsonElement? payload)
    {
        if (!TryGetString(payload, "path", out string? path))
        {
            return MissingArgument("path");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path!.Trim());
        }
        catch
        {
            return ControlResult.Fail("invalid_folder", "The music folder path is invalid.");
        }

        if (!Directory.Exists(fullPath))
        {
            return ControlResult.Fail("folder_not_found", "The music folder does not exist.");
        }

        if (!_playlist.AddFolder(fullPath))
        {
            return ControlResult.Fail("folder_already_configured", "The music folder is already configured.");
        }

        OnMusicFoldersChanged($"Lade till musikmapp: {fullPath}");
        return ControlResult.Ok();
    }

    private ControlResult RemoteRemoveMusicFolder(JsonElement? payload)
    {
        if (!TryGetString(payload, "folderId", out string? folderId))
        {
            return MissingArgument("folderId");
        }

        string? folder = FindMusicFolder(folderId!);
        if (folder == null)
        {
            return ControlResult.Fail("folder_not_found", "The selected music folder is no longer configured.");
        }

        if (!_playlist.RemoveFolder(folder))
        {
            return ControlResult.Fail("last_folder", "At least one music folder must remain configured.");
        }

        OnMusicFoldersChanged($"Tog bort musikmapp: {folder}");
        return ControlResult.Ok();
    }

    private ControlResult RemoteResetMusicFolders()
    {
        _playlist.SetFolders(null);
        OnMusicFoldersChanged("Använder standardmusikmappen igen.");
        return ControlResult.Ok();
    }

    private ControlResult RemoteSetDownloadFolder(JsonElement? payload)
    {
        if (!TryGetString(payload, "folderId", out string? folderId))
        {
            return MissingArgument("folderId");
        }

        string? folder = FindMusicFolder(folderId!);
        if (folder == null)
        {
            return ControlResult.Fail("folder_not_found", "The selected music folder is no longer configured.");
        }

        _settings.DownloadFolderPath = folder;
        SyncDownloadFolderToFilter(announce: false);
        SaveSettings();
        MusicStatusText.Text = $"Nya låtar laddas ner till: {folder}";
        return ControlResult.Ok();
    }

    private MusicControlState BuildControlState()
    {
        string? currentPath = _music.CurrentTrackPath;
        PlaybackGate gate = EvaluatePlaybackGate();

        string playbackState = _isExternalMode
            ? "External"
            : _music.IsPlaying
                ? "Playing"
                : _music.IsPaused
                    ? "Paused"
                    : "Stopped";

        var queue = _session.Queue
            .Select((path, index) => new RemoteQueueItem(
                index,
                RemoteId.FromPath(path),
                Path.GetFileNameWithoutExtension(path)))
            .ToList();

        double? downloadPercent = _isDownloading && !DownloadProgressBar.IsIndeterminate
            ? DownloadProgressBar.Value
            : null;

        return new MusicControlState(
            playbackState,
            _isExternalMode,
            currentPath == null ? null : RemoteId.FromPath(currentPath),
            currentPath == null ? null : Path.GetFileNameWithoutExtension(currentPath),
            _music.Position.TotalSeconds,
            _music.Duration.TotalSeconds,
            _settings.MusicVolume,
            _settings.MonitorVolume,
            _router.IsRouting,
            _music.HasMonitorOutput,
            gate.CanStart,
            gate.BlockedReason,
            IsDelayedStartCountingDown,
            ClampDelayedStartSeconds(_settings.DelayedStartSeconds),
            IsDelayedStartCountingDown ? _session.DelayedStart.RemainingSeconds : 0,
            _session.SingleTrackMode.ToString(),
            _libraryVersion,
            RemoteId.VersionForPaths(_session.Queue),
            BuildRemoteFolders(),
            queue,
            _isDownloading,
            downloadPercent,
            DownloadStatusText.Text ?? string.Empty,
            MusicStatusText.Text ?? string.Empty,
            _settings.LinkVolumes,
            _settings.MusicIgnoresPushToTalk,
            _settings.MusicMonitorOnly);
    }

    private RemoteTrackPage BuildRemoteTrackPage(JsonElement? payload)
    {
        string search = TryGetString(payload, "search", out string? parsedSearch)
            ? parsedSearch!.Trim()
            : string.Empty;
        string? folderId = TryGetString(payload, "folderId", out string? parsedFolderId)
            ? parsedFolderId
            : null;
        int offset = TryGetInt32(payload, "offset", out int parsedOffset) ? Math.Max(0, parsedOffset) : 0;
        int limit = TryGetInt32(payload, "limit", out int parsedLimit)
            ? Math.Clamp(parsedLimit, 1, 500)
            : 200;

        IEnumerable<TrackItem> query = _allTracks;
        if (search.Length > 0)
        {
            query = query.Where(track => track.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(folderId))
        {
            query = query.Where(track => track.FolderPath != null
                && string.Equals(RemoteId.FromPath(track.FolderPath), folderId, StringComparison.Ordinal));
        }

        List<TrackItem> matches = query.ToList();
        List<RemoteTrack> tracks = matches
            .Skip(offset)
            .Take(limit)
            .Select(track => new RemoteTrack(
                RemoteId.FromPath(track.Path),
                track.Name,
                RemoteId.FromPath(track.FolderPath ?? Path.GetDirectoryName(track.Path) ?? track.Path),
                GetRemoteFolderName(track),
                track.FolderPath ?? Path.GetDirectoryName(track.Path) ?? string.Empty,
                string.Equals(_music.CurrentTrackPath, track.Path, StringComparison.OrdinalIgnoreCase),
                _session.GetQueuePositions(track.Path)))
            .ToList();

        return new RemoteTrackPage(
            offset,
            limit,
            matches.Count,
            _libraryVersion,
            tracks);
    }

    private string GetRemoteFolderName(TrackItem track)
    {
        if (track.FolderPath == null)
        {
            return string.Empty;
        }

        return _folderInfoByPath.TryGetValue(track.FolderPath, out FolderInfo? info)
            ? info.DisplayName
            : Path.GetFileName(track.FolderPath);
    }

    private IReadOnlyList<RemoteMusicFolder> BuildRemoteFolders()
    {
        string? effectiveDownloadFolder = (DownloadFolderCombo.SelectedItem as FolderInfo)?.Path;
        return _playlist.Folders
            .Select(folder => new RemoteMusicFolder(
                RemoteId.FromPath(folder),
                _folderInfoByPath.TryGetValue(folder, out FolderInfo? info)
                    ? info.DisplayName
                    : Path.GetFileName(folder),
                folder,
                PlaylistManager.IsDefaultFolder(folder),
                string.Equals(folder, _settings.DownloadFolderPath, StringComparison.OrdinalIgnoreCase),
                string.Equals(folder, effectiveDownloadFolder, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private string? FindMusicFolder(string folderId)
    {
        return _playlist.Folders.FirstOrDefault(folder =>
            string.Equals(RemoteId.FromPath(folder), folderId, StringComparison.Ordinal));
    }

    private TrackItem? FindTrack(string trackId)
    {
        return _allTracks.FirstOrDefault(track =>
            string.Equals(RemoteId.FromPath(track.Path), trackId, StringComparison.Ordinal));
    }

    private static ControlResult MissingArgument(string name) =>
        ControlResult.Fail("invalid_argument", $"{name} is required.");

    private ControlResult PlaybackStartFailed()
    {
        PlaybackGate gate = EvaluatePlaybackGate();
        return gate.CanStart
            ? ControlResult.Fail("playback_failed", "MicMixer could not start the selected track.")
            : PlaybackGateFailed(gate);
    }

    private static ControlResult PlaybackGateFailed(PlaybackGate gate)
    {
        return gate.BlockedReason switch
        {
            PlaybackBlockedReasons.ExternalModeActive => ControlResult.Fail(
                PlaybackBlockedReasons.ExternalModeActive,
                "Direct control is available for MicMixer's built-in music library only."),
            PlaybackBlockedReasons.NoPlaybackClock => ControlResult.Fail(
                PlaybackBlockedReasons.NoPlaybackClock,
                "Start routing or enable monitoring in MicMixer before starting music."),
            PlaybackBlockedReasons.EmptyLibrary => ControlResult.Fail(
                PlaybackBlockedReasons.EmptyLibrary,
                "MicMixer's music library is empty."),
            _ => ControlResult.Fail("playback_failed", "MicMixer could not start playback.")
        };
    }

    private static bool TryGetString(JsonElement? payload, string name, out string? value)
    {
        value = null;
        if (payload is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetDouble(JsonElement? payload, string name, out double value)
    {
        value = default;
        return payload is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(name, out JsonElement property)
            && property.TryGetDouble(out value);
    }

    private static bool TryGetInt32(JsonElement? payload, string name, out int value)
    {
        value = default;
        return payload is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(name, out JsonElement property)
            && property.TryGetInt32(out value);
    }

    private static bool TryGetBoolean(JsonElement? payload, string name, out bool value)
    {
        value = default;
        if (payload is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }
}
