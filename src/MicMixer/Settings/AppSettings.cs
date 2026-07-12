namespace MicMixer.Settings;

public sealed class AppSettings
{
    public string? NormalInputDeviceId { get; set; }

    public string? ModdedInputDeviceId { get; set; }

    public bool SkipModdedMic { get; set; }

    public string? OutputDeviceId { get; set; }

    public string HotkeyId { get; set; } = Input.HotkeyBinding.Default.SerializedValue;

    public int ReleaseDelayMilliseconds { get; set; }

    public bool PushToTalkMode { get; set; }

    public string? MusicMonitorDeviceId { get; set; }

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

    public string? ExternalAppName { get; set; }

    /// <summary>Last window size; 0 means never saved, so the XAML default is used.</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }
}