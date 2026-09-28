using System.Text.Json;

namespace MicMixer.Settings;

public sealed class AppSettings
{
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    /// <summary>
    /// Copies the settings edited in the settings window. They apply live but are
    /// written to disk only by its Save button, so the saved values stay a reliable
    /// reference for "is the app running the way I set it up?". Everything else is
    /// saved as it changes: what is switched during a session (music card, the voice
    /// changer and its voice) and what the remote-control API changes (music folders).
    /// </summary>
    public void CopyConfigurationFrom(AppSettings source)
    {
        StartWithWindows = source.StartWithWindows;
        RunAsAdministrator = source.RunAsAdministrator;
        NormalInputDeviceId = source.NormalInputDeviceId;
        ModdedInputDeviceId = source.ModdedInputDeviceId;
        NormalMicVolume = source.NormalMicVolume;
        NoiseGateEnabled = source.NoiseGateEnabled;
        NoiseGateThresholdDb = source.NoiseGateThresholdDb;
        ProcessedVoiceVolume = source.ProcessedVoiceVolume;
        OutputDeviceId = source.OutputDeviceId;
        HotkeyId = source.HotkeyId;
        ExtraHotkeyIds = [.. source.ExtraHotkeyIds];
        ReleaseDelayMilliseconds = source.ReleaseDelayMilliseconds;
        PushToTalkMode = source.PushToTalkMode;
        HoldKeyWhileSending = source.HoldKeyWhileSending;
        HeldKey = source.HeldKey;
        MusicMonitorDeviceId = source.MusicMonitorDeviceId;
        SecondaryOutputEnabled = source.SecondaryOutputEnabled;
        SecondaryOutputDeviceId = source.SecondaryOutputDeviceId;
        SecondaryOutputVolume = source.SecondaryOutputVolume;
        SecondaryOutputIgnorePushToTalk = source.SecondaryOutputIgnorePushToTalk;
        OverlayIndicatorEnabled = source.OverlayIndicatorEnabled;
        OverlayVolumeMeterEnabled = source.OverlayVolumeMeterEnabled;
        MeterSensitivityDb = source.MeterSensitivityDb;
        ObsOverlayEnabled = source.ObsOverlayEnabled;
        ObsOverlayPort = source.ObsOverlayPort;
    }

    /// <summary>True when <paramref name="other"/> has the same settings-window values.</summary>
    public bool ConfigurationEquals(AppSettings other)
    {
        // Copying our configuration onto a clone of other changes nothing exactly
        // when the two agree, which keeps the field list in one place.
        AppSettings probe = other.Clone();
        probe.CopyConfigurationFrom(this);
        return JsonSerializer.Serialize(probe) == JsonSerializer.Serialize(other);
    }

    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Runs MicMixer as administrator, so its hotkeys and held key also work while a
    /// program running as administrator has focus.
    /// </summary>
    public bool RunAsAdministrator { get; set; }

    /// <summary>The version whose "What's new" notes were last shown, or recorded on a first run.</summary>
    public string? WhatsNewShownForVersion { get; set; }

    public string? NormalInputDeviceId { get; set; }

    public string? ModdedInputDeviceId { get; set; }

    /// <summary>Gain for the normal mic only. 1 sends it exactly as captured; up to 2 boosts a quiet mic.</summary>
    public float NormalMicVolume { get; set; } = 1f;

    /// <summary>Mutes the mic between phrases so only signal above the threshold is sent.</summary>
    public bool NoiseGateEnabled { get; set; }

    public float NoiseGateThresholdDb { get; set; } = -45f;

    public ModifiedVoiceMode ModifiedVoiceMode { get; set; }

    public string? SelectedVoiceProfileId { get; set; }

    public bool LongerAnalysisWindow { get; set; }

    /// <summary>Post-effect gain for processed voice only. 1 is unity; up to 2 boosts, same scale as <see cref="NormalMicVolume"/>.</summary>
    public float ProcessedVoiceVolume { get; set; } = 1f;

    /// <summary>Legacy mirror retained so older MicMixer builds still understand a saved settings file.</summary>
    public bool SkipModdedMic { get; set; }

    public string? OutputDeviceId { get; set; }

    /// <summary>The first hotkey.</summary>
    // Older builds read only this one, so it stays a single value.
    public string HotkeyId { get; set; } = Input.HotkeyBinding.Default.SerializedValue;

    /// <summary>Further hotkeys that do exactly what <see cref="HotkeyId"/> does.</summary>
    public List<string> ExtraHotkeyIds { get; set; } = [];

    /// <summary>Every hotkey, <see cref="HotkeyId"/> first, without duplicates.</summary>
    public List<Input.HotkeyBinding> ReadHotkeys() =>
        [.. new[] { HotkeyId }.Concat(ExtraHotkeyIds).Select(Input.HotkeyBinding.Parse).DistinctBy(binding => binding.SerializedValue)];

    /// <summary>Stores <paramref name="hotkeys"/>, which must hold at least one, the first as <see cref="HotkeyId"/>.</summary>
    public void WriteHotkeys(IReadOnlyList<Input.HotkeyBinding> hotkeys)
    {
        HotkeyId = hotkeys[0].SerializedValue;
        ExtraHotkeyIds = [.. hotkeys.Skip(1).Select(binding => binding.SerializedValue)];
    }

    public int ReleaseDelayMilliseconds { get; set; }

    public bool PushToTalkMode { get; set; }

    /// <summary>Holds <see cref="HeldKey"/> down while MicMixer sends to the virtual cable.</summary>
    public bool HoldKeyWhileSending { get; set; }

    /// <summary>Name of the function key (F13–F24) that <see cref="HoldKeyWhileSending"/> holds.</summary>
    public string HeldKey { get; set; } = Input.FunctionKey.Default.Name;

    /// <summary>Music keeps flowing to the virtual cable while push-to-talk holds the mic silent.</summary>
    public bool MusicIgnoresPushToTalk { get; set; }

    /// <summary>Preview mode: music is never sent to the virtual cable — only local monitoring (and the secondary output) carry it.</summary>
    public bool MusicMonitorOnly { get; set; }

    public string? MusicMonitorDeviceId { get; set; }

    /// <summary>Plays the finished pre-gate mix (mic + music) on an extra render device, e.g. for recording or streaming.</summary>
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

    /// <summary>Serves the overlay as a local web page for a browser source.</summary>
    public bool ObsOverlayEnabled { get; set; }

    /// <summary>Loopback port for the stream overlay server.</summary>
    public int ObsOverlayPort { get; set; } = Overlay.ObsOverlayServer.DefaultPort;

    public string? ExternalAppName { get; set; }

    /// <summary>The first-run setup guide was skipped; don't open it on its own again.</summary>
    public bool SetupGuideDismissed { get; set; }

    /// <summary>Last window size; 0 means never saved, so the XAML default is used.</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }
}
