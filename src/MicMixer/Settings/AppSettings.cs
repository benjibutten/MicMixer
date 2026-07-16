namespace MicMixer.Settings;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    public string? NormalInputDeviceId { get; set; }

    public string? ModdedInputDeviceId { get; set; }

    public bool SkipModdedMic { get; set; }

    public string? OutputDeviceId { get; set; }

    public string HotkeyId { get; set; } = Input.HotkeyBinding.Default.SerializedValue;

    public int ReleaseDelayMilliseconds { get; set; }

    public bool PushToTalkMode { get; set; }

    /// <summary>Music keeps flowing to the virtual cable while push-to-talk holds the mic silent.</summary>
    public bool MusicIgnoresPushToTalk { get; set; }

    /// <summary>Preview mode: music is never sent to the virtual cable — only local monitoring (and the secondary output) carry it.</summary>
    public bool MusicMonitorOnly { get; set; }

    public string? MusicMonitorDeviceId { get; set; }

    /// <summary>Plays the finished pre-gate mix (mic + music) on an extra render device, e.g. for OBS capture.</summary>
    public bool SecondaryOutputEnabled { get; set; }

    public string? SecondaryOutputDeviceId { get; set; }

    /// <summary>Gain applied to the secondary output branch only; never affects the cable.</summary>
    public float SecondaryOutputVolume { get; set; } = 1.0f;

    /// <summary>When true the secondary output keeps playing while push-to-talk holds the cable silent.</summary>
    public bool SecondaryOutputIgnorePushToTalk { get; set; } = true;

    public bool MonitorEnabled { get; set; } = true;

    public float MusicVolume { get; set; } = 0.5f;

    public float MonitorVolume { get; set; } = 0.5f;

    public bool LinkVolumes { get; set; }

    /// <summary>Legacy single-folder setting; migrated into <see cref="MusicFolderPaths"/> on load.</summary>
    public string? MusicFolderPath { get; set; }

    public List<string>? MusicFolderPaths { get; set; }

    public string? DownloadFolderPath { get; set; }

    public bool ExternalCaptureMode { get; set; }

    /// <summary>Seconds the delayed-start button counts down before playback begins.</summary>
    public int DelayedStartSeconds { get; set; } = 3;

    /// <summary>Stops playback when the current track ends instead of advancing through the playlist.</summary>
    public bool SingleTrackMode { get; set; }

    /// <summary>Shows a small click-through status dot in the top-right screen corner while routing runs.</summary>
    public bool OverlayIndicatorEnabled { get; set; }

    /// <summary>Shows a small level meter next to the overlay dot with the outgoing mix level (mic + music).</summary>
    public bool OverlayVolumeMeterEnabled { get; set; } = true;

    /// <summary>
    /// Calibration offset in dB for the overlay volume meter. Positive values make
    /// the meter read hotter (bar and color bands react earlier); 0 is the default window.
    /// </summary>
    public float MeterSensitivityDb { get; set; }

    /// <summary>Serves the overlay as a local web page for an OBS Browser source.</summary>
    public bool ObsOverlayEnabled { get; set; }

    /// <summary>Loopback port for the OBS overlay server.</summary>
    public int ObsOverlayPort { get; set; } = Overlay.ObsOverlayServer.DefaultPort;

    public string? ExternalAppName { get; set; }

    /// <summary>Last window size; 0 means never saved, so the XAML default is used.</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }
}
