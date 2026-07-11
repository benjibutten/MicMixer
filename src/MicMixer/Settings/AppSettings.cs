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

    /// <summary>Shows a small click-through status dot in the top-right screen corner while routing runs.</summary>
    public bool OverlayIndicatorEnabled { get; set; }

    public string? ExternalAppName { get; set; }
}