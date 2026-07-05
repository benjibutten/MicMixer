namespace MicMixer.Settings;

public sealed class AppSettings
{
    public string? NormalInputDeviceId { get; set; }

    public string? ModdedInputDeviceId { get; set; }

    public string? OutputDeviceId { get; set; }

    public string HotkeyId { get; set; } = Input.HotkeyBinding.Default.SerializedValue;

    public int ReleaseDelayMilliseconds { get; set; }

    public string? MusicMonitorDeviceId { get; set; }

    public bool MonitorEnabled { get; set; } = true;

    public float MusicVolume { get; set; } = 0.5f;

    public float MonitorVolume { get; set; } = 0.5f;
}