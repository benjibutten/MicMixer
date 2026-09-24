using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Interop;
using System.Windows.Threading;
using System.IO;
using MicMixer.Audio;
using MicMixer.Diagnostics;
using MicMixer.Dsp;
using MicMixer.Input;
using MicMixer.Music;
using MicMixer.Overlay;
using MicMixer.Remote;
using MicMixer.Settings;
using MicMixer.UI;
using NAudio.CoreAudioApi;
using Serilog;

namespace MicMixer;

public partial class MainWindow : Window, IMicMixerControlHost
{
    private const int MaxReleaseDelayMilliseconds = 5_000;
    private const float ExternalSignalActivationThreshold = 0.005f;
    private static readonly TimeSpan ExternalSignalHoldDuration = TimeSpan.FromSeconds(2);

    private readonly AudioRouter _router;
    private readonly SecondaryOutputEngine _secondaryOutput;
    private readonly GlobalHotkeyListener _hotkeyListener;
    private readonly GlobalHotkeyListener _markerKeyListener;
    private readonly SettingsStore _settingsStore;
    private readonly StartupRegistrySyncService _startupRegistrySyncService;
    private readonly DispatcherTimer _levelTimer;
    private readonly DispatcherTimer _releaseDelayTimer;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly MusicPlaybackEngine _music;
    private readonly MusicSession _session;
    private readonly PlaylistManager _playlist;
    private readonly ToolBootstrapper _toolBootstrapper;
    private readonly YouTubeDownloader _youTubeDownloader;
    private readonly DispatcherTimer _musicTimer;
    private readonly DispatcherTimer _delayedStartTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _deviceChangeTimer;
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDeviceNotificationClient? _deviceNotifications;
    private readonly SignalActivityTracker _externalSignalActivity = new(
        ExternalSignalActivationThreshold,
        ExternalSignalHoldDuration);
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private List<TrackItem> _allTracks = new();
    private readonly Dictionary<string, TrackItem> _trackByPath = new(StringComparer.OrdinalIgnoreCase);
    private TrackItem? _playingTrackItem;
    private List<FolderChipItem> _folderChips = new();
    private Dictionary<string, FolderInfo> _folderInfoByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Active filter chips in the order they were turned on; the last one steers the download folder.</summary>
    private readonly List<string> _folderChipActivationOrder = new();
    private System.Windows.Point _queueDragStart;
    private int _queueDragIndex = -1;
    private DateTime _queuePopupClosedAt = DateTime.MinValue;
    private string? _acknowledgedNonCableOutputId;
    private string? _acknowledgedSameMicId;
    private bool _musicWasAutoPaused;
    private ProcessLoopbackCapture? _appCapture;
    private AudioAppOption? _captureTarget;
    private bool _isExternalMode;
    private bool _isCaptureStarting;
    private bool _isSeekDragging;
    private bool _isUpdatingMusicUi;
    private bool _isSyncingLinkedVolume;
    private double _volumeLinkOffset;
    private bool _trayBalloonShown;
    private bool _isDownloading;
    private ExternalCaptureRouteState? _lastExternalCaptureRouteState;
    /// <summary>Live settings: what the app runs with right now.</summary>
    private AppSettings _settings;
    /// <summary>What the settings window last saved; problems are measured against it.</summary>
    private AppSettings _savedSettings;
    private bool _hasUnsavedConfiguration;
    private Window? _settingsWindow;
    private List<Problem> _problems = [];
    private string? _secondaryOutputError;
    private OverlayIndicatorWindow? _overlayIndicator;
    private HotkeyBinding _hotkeyBinding = HotkeyBinding.Default;
    private int _releaseDelayMilliseconds;
    private float _noiseGatePeakHold;
    private DateTime? _noiseGateOpenedAt;
    private DateTime? _noiseGateLastClosedAt;
    private float _noiseGateOpenPeak;
    private bool _noiseGateOpenedModded;
    private int _noiseGateOpenTicks;
    private int _noiseGateCableOpenTicks;
    private DateTime? _musicSendingSince;
    private OutputDeviceAppWatcher? _outputAppWatcher;
    private readonly DispatcherTimer _outputAppWatchTimer;
    private bool _isCapturingHotkey;
    private bool _isReleaseDelayPending;
    private bool _isStartingRouting;
    private bool _isDevicesLoading;
    private Task _deviceRefresh = Task.CompletedTask;
    private bool _isUpdatingUi;
    private bool _isReallyClosing;
    private bool _devicesLoaded;
    private bool _startupCompleted;
    private string? _deviceLoadError;
    private MicStatus _lastTrayStatus = MicStatus.Stopped;
    private WindowState _lastNonMinimizedWindowState = WindowState.Normal;

    public MainWindow(
        MusicSession session,
        AudioRouter router,
        MusicPlaybackEngine music,
        SettingsStore settingsStore,
        PlaylistManager playlist)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _music = music ?? throw new ArgumentNullException(nameof(music));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));

        _secondaryOutput = new SecondaryOutputEngine();
        _secondaryOutput.Error += OnSecondaryOutputError;
        _router.SecondaryOutput = _secondaryOutput;
        App.StartupTrace("AudioRouter created");
        _hotkeyListener = new GlobalHotkeyListener();
        App.StartupTrace("GlobalHotkeyListener created");
        _startupRegistrySyncService = new StartupRegistrySyncService();
        App.StartupTrace("SettingsStore created");

        _toolBootstrapper = new ToolBootstrapper();
        _youTubeDownloader = new YouTubeDownloader(_toolBootstrapper);
        _router.MusicSourceFactory = format => _music.CreateMixTap(format);
        _music.TrackEnded += OnMusicTrackEnded;
        _music.Error += OnMusicEngineError;
        App.StartupTrace("Music engine created");

        App.StartupTrace("MainWindow ctor begin");
        _settings = _settingsStore.Load();
        App.StartupTrace("Settings loaded");
        _releaseDelayMilliseconds = ClampReleaseDelay(_settings.ReleaseDelayMilliseconds);
        _settings.ReleaseDelayMilliseconds = _releaseDelayMilliseconds;
        _settings.SecondaryOutputVolume = Math.Clamp(_settings.SecondaryOutputVolume, 0f, 1f);
        _settings.MusicVolume = Math.Clamp(_settings.MusicVolume, 0f, 1f);
        _settings.MonitorVolume = Math.Clamp(_settings.MonitorVolume, 0f, 1f);
        _settings.MeterSensitivityDb = Math.Clamp(_settings.MeterSensitivityDb, -12f, 12f);
        _settings.DelayedStartSeconds = ClampDelayedStartSeconds(_settings.DelayedStartSeconds);
        _settings.ObsOverlayPort = Overlay.ObsOverlayServer.ClampPort(_settings.ObsOverlayPort);
        _releaseDelayTimer = new DispatcherTimer();
        _releaseDelayTimer.Tick += OnReleaseDelayTimerTick;
        _delayedStartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _delayedStartTimer.Tick += OnDelayedStartTick;
        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveSettings();
        };
        // Plugging one headset in fires several endpoint callbacks; wait for the burst to settle.
        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _deviceChangeTimer.Tick += OnDeviceChangeSettled;
        _outputAppWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _outputAppWatchTimer.Tick += (_, _) => _outputAppWatcher?.Poll();
        _singleTrackAnnounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 50)
        };
        _singleTrackAnnounceTimer.Tick += OnSingleTrackAnnounceTick;
        InitializeComponent();
        App.StartupTrace("InitializeComponent done");
        VersionText.Text = AppVersion.DisplayText;
        RestoreWindowBounds();
        _lastNonMinimizedWindowState = WindowState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        StateChanged += OnWindowStateChanged;

        // The window/taskbar icon is the neutral brand badge; only the tray icon
        // and the overlay carry routing state.
        Icon = StatusTheme.RenderBrandBadge(48);

        _router.Error += OnRouterError;
        _hotkeyListener.PressedStateChanged += OnHotkeyPressedStateChanged;

        // Diagnostic marker: pressing Pause stamps the log, so a moment someone
        // else reported can be matched against the noise gate periods.
        _markerKeyListener = new GlobalHotkeyListener();
        _markerKeyListener.UpdateBinding(HotkeyBinding.FromKeyboardKey(Key.Pause));
        _markerKeyListener.SetMonitoringEnabled(true);
        _markerKeyListener.PressedStateChanged += OnMarkerKeyPressedStateChanged;

        _trayIcon = CreateTrayIcon();
        DryInputCombo.IsEnabled = false;
        ModdedInputCombo.IsEnabled = false;
        ExternalModdedInputCombo.IsEnabled = false;
        OutputDeviceCombo.IsEnabled = false;
        SecondaryOutputCombo.IsEnabled = false;

        _isUpdatingUi = true;
        LoadVoiceProfiles();
        _isUpdatingUi = false;
        bool alternateWindowAvailable = VoiceProfileCombo.SelectedItem is VoiceProfile { AlternateBlockMilliseconds: not null };
        _settings.LongerAnalysisWindow = _settings.LongerAnalysisWindow && alternateWindowAvailable;
        _savedSettings = _settings.Clone();
        ApplyConfiguration();
        ModdedInputCombo.ItemsSource = ModifiedVoiceOptions;
        RenderVoiceChoice();
        SyncStartWithWindows();

        // Migrate the legacy single-folder setting into the folder list.
        _playlist.SetFolders(_settings.MusicFolderPaths is { Count: > 0 } paths
            ? paths
            : string.IsNullOrWhiteSpace(_settings.MusicFolderPath) ? [] : [_settings.MusicFolderPath]);
        RefreshMusicFolderUi();
        RefreshPlaylist(null);

        _music.MusicVolume = _settings.MusicVolume;
        _music.MonitorVolume = _settings.MonitorVolume;
        _isUpdatingMusicUi = true;
        MusicVolumeSlider.Value = _music.MusicVolume;
        MonitorVolumeSlider.Value = _music.MonitorVolume;
        MonitorEnabledCheck.IsChecked = _settings.MonitorEnabled;
        VolumeLinkToggle.IsChecked = _settings.LinkVolumes;
        MusicIgnorePttCheck.IsChecked = _settings.MusicIgnoresPushToTalk;
        MusicMonitorOnlyCheck.IsChecked = _settings.MusicMonitorOnly;
        _isUpdatingMusicUi = false;
        ApplyMusicRoutingModes();
        UpdateDelayedPlayIdleUi();
        SetSingleTrackMode(_settings.SingleTrackMode ? SingleTrackPlayMode.Always : SingleTrackPlayMode.Off,
            save: false, announce: false);
        _volumeLinkOffset = MonitorVolumeSlider.Value - MusicVolumeSlider.Value;
        UpdateVolumePercentTexts();

        // Restore the music source mode without letting the radio handler run
        // (it would save settings before the device combos are populated).
        _isExternalMode = _settings.ExternalCaptureMode;
        _isUpdatingMusicUi = true;
        ExternalModeRadio.IsChecked = _isExternalMode;
        LibraryModeRadio.IsChecked = !_isExternalMode;
        _isUpdatingMusicUi = false;
        ApplyMusicModeUi();
        if (_isExternalMode)
        {
            _ = RefreshAudioAppsAsync();
        }

        _musicTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _musicTimer.Tick += OnMusicTimerTick;
        _musicTimer.Start();

        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _levelTimer.Tick += OnLevelTimerTick;
        _levelTimer.Start();

        OnConfigurationChanged();
        Closing += OnClosing;

        App.StartupTrace("MainWindow ctor done");

        ContentRendered += (_, _) => CompleteStartup();
    }

    public void StartHiddenInTray()
    {
        ShowInTaskbar = false;
        new WindowInteropHelper(this).EnsureHandle();
        Hide();
        Dispatcher.BeginInvoke(CompleteStartup, DispatcherPriority.Loaded);
    }

    private void CompleteStartup()
    {
        if (_startupCompleted)
        {
            return;
        }

        _startupCompleted = true;
        App.StartupTrace("Startup complete");
        App.StartupStopwatch.Stop();

        if (!App.StartupBenchmarkMode)
        {
            App.StartupTrace("Device refresh queued");
            _ = LoadDevicesAndOfferSetupGuideAsync();
            StartWatchingDevices();
        }

        if (App.StartupBenchmarkMode)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _isReallyClosing = true;
                System.Windows.Application.Current.Shutdown();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Follows devices coming and going, so the cards about a missing mic, cable or
    /// monitor clear themselves once the device is plugged back in.
    /// </summary>
    private void StartWatchingDevices()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            // Created on the UI thread, so the events arrive there too.
            _deviceNotifications = _deviceEnumerator.CreateNotificationClient();
            _deviceNotifications.DeviceAdded += (_, _) => RestartDeviceChangeTimer();
            _deviceNotifications.DeviceRemoved += (_, _) => RestartDeviceChangeTimer();
            _deviceNotifications.DeviceStateChanged += (_, _) => RestartDeviceChangeTimer();
        }
        catch (Exception ex)
        {
            // Refresh devices still works; only the automatic part is lost.
            Log.Warning(ex, "Could not watch audio devices for changes.");
        }
    }

    private void RestartDeviceChangeTimer()
    {
        _deviceChangeTimer.Stop();
        _deviceChangeTimer.Start();
    }

    private void OnDeviceChangeSettled(object? sender, EventArgs e)
    {
        _deviceChangeTimer.Stop();

        // Devices cannot change under a running route, and the cards describe what the
        // route is actually using, so wait for routing to stop before reading them.
        // A refresh already under way may have listed the devices before this change.
        if (_router.IsRouting || _isStartingRouting || _isDevicesLoading)
        {
            _deviceChangeTimer.Start();
            return;
        }

        Log.Information("Audio endpoints changed; reading the device list again.");
        _ = RefreshDevicesAsync();
    }

    /// <summary>
    /// A refresh asked for during another one runs after it instead of being dropped:
    /// callers rely on the combos matching _settings and the devices once this completes.
    /// </summary>
    private Task RefreshDevicesAsync()
    {
        return _deviceRefresh = RefreshDevicesAfterAsync(_deviceRefresh);
    }

    private async Task RefreshDevicesAfterAsync(Task previous)
    {
        // WhenAny does not rethrow, so one failed refresh cannot fail every later one.
        await Task.WhenAny(previous);

        // _devicesLoaded keeps its value: the old list stays valid, and the cards and the
        // status line would otherwise blink on every refresh.
        _isDevicesLoading = true;
        _deviceLoadError = null;

        if (!_router.IsRouting)
        {
            DryInputCombo.IsEnabled = false;
            ModdedInputCombo.IsEnabled = false;
            ExternalModdedInputCombo.IsEnabled = false;
            OutputDeviceCombo.IsEnabled = false;
            SecondaryOutputCombo.IsEnabled = false;
        }

        UpdateStatusText();

        try
        {
            App.StartupTrace("Device refresh started");
            var (inputs, outputs) = await Task.Run(AudioDevices.EnumerateActive);

            _isUpdatingUi = true;

            try
            {
                // Every combo change is written to _settings at once, so _settings (not the
                // combos) holds the choice. A missing device gets a stand-in in the combo while
                // _settings keeps the chosen id; UpdateProblems reports the difference.
                DryInputCombo.ItemsSource = inputs;
                ModdedInputCombo.ItemsSource = ModifiedVoiceOptions;
                ExternalModdedInputCombo.ItemsSource = inputs;
                OutputDeviceCombo.ItemsSource = outputs;

                var drySelection = AudioDevices.SelectInput(
                    inputs,
                    _settings.NormalInputDeviceId,
                    AudioDevices.LooksLikeNormalMic);

                DryInputCombo.SelectedItem = drySelection;

                ModdedInputCombo.SelectedItem = ModifiedVoiceOptions.First(option => option.Mode == _settings.ModifiedVoiceMode);

                AudioDeviceOption? moddedSelection = AudioDevices.SelectInput(
                    inputs,
                    _settings.ModdedInputDeviceId,
                    AudioDevices.LooksLikeVoiceModDevice,
                    drySelection?.Id);
                // Excluding the normal mic means a guess never aliases it; only a deliberate
                // choice of the same device (Enable warns about it) can.
                ExternalModdedInputCombo.SelectedItem = moddedSelection;

                OutputDeviceCombo.SelectedItem = AudioDevices.SelectCableOutput(outputs, _settings.OutputDeviceId);

                MonitorDeviceCombo.ItemsSource = outputs;
                MonitorDeviceCombo.SelectedItem = AudioDevices.SelectMonitor(outputs, _settings.MusicMonitorDeviceId);

                // Strict id match only — never auto-pick a replacement. With the
                // feature enabled, a silently substituted device would play the
                // microphone on open speakers (feedback/unexpected exposure).
                // A missing device leaves the combo empty and start is blocked
                // until the user makes an explicit new choice.
                SecondaryOutputCombo.ItemsSource = outputs;
                SecondaryOutputCombo.SelectedItem = outputs.FirstOrDefault(device => device.Id == _settings.SecondaryOutputDeviceId);

                // First run: the guesses become unsaved choices, so Save keeps them. Later
                // runs leave an unset id alone (it means "automatic"), or every user who
                // never picked, say, a monitor device would face a permanent unsaved card.
                if (_savedSettings.OutputDeviceId == null)
                {
                    _settings.NormalInputDeviceId ??= drySelection?.Id;
                    _settings.OutputDeviceId ??= (OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;
                    _settings.MusicMonitorDeviceId ??= (MonitorDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;
                    if (_settings.ModifiedVoiceMode == ModifiedVoiceMode.ExternalMicrophone)
                    {
                        _settings.ModdedInputDeviceId ??= moddedSelection?.Id;
                    }
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }

            _devicesLoaded = true;
            _deviceLoadError = null;

            if (!_router.IsRouting)
            {
                DryInputCombo.IsEnabled = true;
                ModdedInputCombo.IsEnabled = true;
                ExternalModdedInputCombo.IsEnabled = true;
                OutputDeviceCombo.IsEnabled = true;
                SecondaryOutputCombo.IsEnabled = true;
            }

            ApplyModdedMicUiState();
            UpdateOutputCableWarning();
            ApplySecondaryOutputConfig();
            UpdateSecondaryOutputWarning();
            await ApplyMonitorConfigAsync();
            App.StartupTrace("Devices refreshed");
        }
        catch (Exception ex)
        {
            _devicesLoaded = false;
            _deviceLoadError = $"Could not read audio devices: {ex.Message}";
            Log.Error(ex, "Device refresh failed.");
            App.StartupTrace($"Device refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isDevicesLoading = false;
            OnConfigurationChanged();
        }
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_router.IsRouting)
        {
            StopRouting();
            return;
        }

        await StartRoutingAsync();
    }

    private async Task StartRoutingAsync()
    {
        // A device refresh can start by itself, so wait for it rather than refuse the click.
        await Task.WhenAny(_deviceRefresh);

        if (_isStartingRouting)
        {
            return;
        }

        if (!_devicesLoaded)
        {
            StatusText.Text = _deviceLoadError ?? "Could not read audio devices. Click Refresh and try again.";
            return;
        }

        ModifiedVoiceMode modifiedVoiceMode = CurrentModifiedVoiceMode;
        bool skipModded = modifiedVoiceMode == ModifiedVoiceMode.None;
        var moddedInput = ExternalModdedInputCombo.SelectedItem as AudioDeviceOption;

        if (DryInputCombo.SelectedItem is not AudioDeviceOption dryInput ||
            OutputDeviceCombo.SelectedItem is not AudioDeviceOption output ||
            (modifiedVoiceMode == ModifiedVoiceMode.ExternalMicrophone && moddedInput == null))
        {
            StatusText.Text = modifiedVoiceMode == ModifiedVoiceMode.ExternalMicrophone
                ? "Select a normal mic, the voice changer app's microphone and a virtual cable in Settings › Devices."
                : "Select a normal mic and a virtual cable in Settings › Devices.";
            return;
        }

        // Same device for both mics works technically (two shared-mode captures),
        // the hotkey just switches between two identical signals. Warn instead of
        // hard-blocking — a silent refusal looks like a dead button.
        if (modifiedVoiceMode == ModifiedVoiceMode.ExternalMicrophone
            && dryInput.Id == moddedInput!.Id
            && _acknowledgedSameMicId != dryInput.Id)
        {
            _acknowledgedSameMicId = dryInput.Id;
            StatusText.Text = "The normal mic and the voice changer app's microphone are the same device — the hotkey will make no audible difference. Did you mean Off? Click Enable again to start anyway.";
            return;
        }

        // Warn instead of hard-blocking: unusual cable drivers (VAC, Voicemeeter Aux)
        // fail the name heuristic, and a silent refusal looks like a dead button.
        if (!AudioDevices.LooksLikeVirtualCable(output) && _acknowledgedNonCableOutputId != output.Id)
        {
            _acknowledgedNonCableOutputId = output.Id;
            StatusText.Text = $"\"{output.FriendlyName}\" does not appear to be a virtual cable — the game can hear the mix only through a device such as CABLE Input. Click Enable again to start anyway.";
            return;
        }

        if (_settings.SecondaryOutputEnabled)
        {
            if (SecondaryOutputCombo.SelectedItem is not AudioDeviceOption secondaryDevice)
            {
                StatusText.Text = "Secondary output is enabled but no device is selected — select a device or disable secondary output.";
                return;
            }

            // Hard-block: two shared-mode render streams on the same endpoint would
            // sum both copies of the mix on the cable.
            if (secondaryDevice.Id == output.Id)
            {
                StatusText.Text = "The secondary output and the virtual cable are the same device — the mix would play twice. Select a different device for the secondary output.";
                return;
            }
        }

        ApplySecondaryOutputConfig();

        _isStartingRouting = true;
        ApplyModdedMicUiState();
        ToggleBtn.IsEnabled = false;
        StatusText.Text = "Starting routing...";

        bool pushToTalk = IsPushToTalk;
        _secondaryOutputError = null;

        try
        {
            string? externalInputId = modifiedVoiceMode == ModifiedVoiceMode.ExternalMicrophone ? moddedInput!.Id : null;
            bool smootherProcessing = LongerAnalysisWindowCheck.IsChecked == true;
            await Task.Run(() => StartRoutingByDeviceId(
                dryInput.Id,
                externalInputId,
                output.Id,
                modifiedVoiceMode,
                smootherProcessing,
                pushToTalk));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Routing start failed.");
            StatusText.Text = $"Startup error: {ex.Message}";
            return;
        }
        finally
        {
            _isStartingRouting = false;
            ApplyModdedMicUiState();
            ToggleBtn.IsEnabled = true;
        }

        if (_router.IsRouting)
        {
            if (!skipModded || pushToTalk)
            {
                SetHotkeyMonitoringEnabled(true);
            }

            ApplyEffectiveRoutingStates();
            _outputAppWatcher?.Dispose();
            _outputAppWatcher = new OutputDeviceAppWatcher(output.Id, output.FriendlyName);
            _outputAppWatcher.Poll();
            _outputAppWatchTimer.Start();
            Log.Information("Windows default playback device: {Defaults}", OutputDeviceAppWatcher.DescribeWindowsDefaults());
            ToggleBtnText.Text = "Stop";
            ToggleBtnIcon.Data = (Geometry)FindResource("StopIcon");
            // Devices are opened for the whole route. The voice changer choice stays
            // switchable: ChangeVoiceAsync restarts the route instead.
            DryInputCombo.IsEnabled = false;
            ExternalModdedInputCombo.IsEnabled = false;
            LongerAnalysisWindowCheck.IsEnabled = false;
            OutputDeviceCombo.IsEnabled = false;
            SecondaryOutputEnabledCheck.IsEnabled = false;
            SecondaryOutputCombo.IsEnabled = false;
            UpdateSecondaryOutputStatus();
            // Starting never saves the devices it used: a stand-in for a missing
            // device must not replace the saved choice.
            StatusText.Text = string.Empty;
            UpdateStatusText();
            ResumeMusicIfAutoPaused();
            UpdateExternalCaptureStatusText();
        }
        else
        {
            UpdateStatusText();
        }
    }

    private void StartRoutingByDeviceId(
        string dryInputId,
        string? moddedInputId,
        string outputId,
        ModifiedVoiceMode modifiedVoiceMode,
        bool smootherProcessing,
        bool startMuted)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var dryInput = enumerator.GetDevice(dryInputId);
        using var moddedInput = moddedInputId != null ? enumerator.GetDevice(moddedInputId) : null;
        using var output = enumerator.GetDevice(outputId);

        _router.SetUseModdedInput(false);
        // Push-to-talk must start silent; the gate opens when the hotkey is pressed.
        _router.SetOutputGateOpen(!startMuted);
        // Resolve an immutable snapshot before routing. Restart factories never read files.
        VoiceDspParameters? parameters = modifiedVoiceMode == ModifiedVoiceMode.LocalProfile
            ? new VoiceProfileStore().Load(_settings.SelectedVoiceProfileId).Resolve(smootherProcessing)
            : null;
        Func<int, int, IVoiceProcessor>? processorFactory = modifiedVoiceMode == ModifiedVoiceMode.LocalProfile
            ? (sampleRate, channels) => CreateLocalProfileProcessor(sampleRate, channels, parameters!)
            : null;
        _router.Start(dryInput, moddedInput, output, processorFactory);
        Log.Information(
            "Routing started. NoiseGate={NoiseGate} ThresholdDb={ThresholdDb:0} NormalMicVolume={NormalMicVolume:P0} ProcessedVoiceVolume={ProcessedVoiceVolume:P0} VoiceChanger={VoiceChanger} Voice={Voice} Hotkey={Hotkey} ReleaseDelayMs={ReleaseDelayMs} PushToTalk={PushToTalk}",
            _settings.NoiseGateEnabled,
            _settings.NoiseGateThresholdDb,
            _settings.NormalMicVolume,
            _settings.ProcessedVoiceVolume,
            modifiedVoiceMode,
            DescribeModifiedVoice(),
            _hotkeyBinding.DisplayName,
            _settings.ReleaseDelayMilliseconds,
            _settings.PushToTalkMode);
        Log.Information("FiveM voice settings: {Summary}", FiveMVoiceSettings.Describe());
    }

    private static IVoiceProcessor CreateLocalProfileProcessor(int sampleRate, int channels, VoiceDspParameters parameters)
    {
        var processor = new ProfileVoiceProcessor(sampleRate, channels, parameters);
        Log.Information(
            "Local voice profile processor started/restarted. AnalysisModeMs={AnalysisModeMs} AlgorithmicLatencySamples={LatencySamples} AlgorithmicLatencyMs={LatencyMs:F2}",
            processor.Preset.BlockMilliseconds,
            processor.LatencySamples,
            processor.LatencySamples * 1_000d / sampleRate);
        return processor;
    }

    private void StopRouting()
    {
        SetHotkeyMonitoringEnabled(false);
        CancelPendingReleaseDelay();
        _router.Stop();
        _outputAppWatchTimer.Stop();
        _outputAppWatcher?.Dispose();
        _outputAppWatcher = null;
        PauseMusicIfClockLost();
        ToggleBtnText.Text = "Enable";
        ToggleBtnIcon.Data = (Geometry)FindResource("PlayIcon");
        DryLevelMeter.Value = 0;
        ModdedLevelMeter.Value = 0;

        if (!_isDevicesLoading)
        {
            DryInputCombo.IsEnabled = true;
            ModdedInputCombo.IsEnabled = true;
            ExternalModdedInputCombo.IsEnabled = true;
            OutputDeviceCombo.IsEnabled = true;
            SecondaryOutputCombo.IsEnabled = true;
        }

        ApplyModdedMicUiState();
        SecondaryOutputEnabledCheck.IsEnabled = true;
        _secondaryOutputError = null;
        UpdateSecondaryOutputStatus();
        UpdateStatusText();
        UpdateExternalCaptureStatusText();
    }

    private void OnRouterError(object? sender, string message)
    {
        Log.Error("Audio routing error: {ErrorMessage}", message);

        Dispatcher.BeginInvoke(() =>
        {
            StopRouting();
            StatusText.Text = $"Error: {message}";
        });
    }

    private void OnHotkeyPressedStateChanged(object? sender, bool isPressed)
    {
        Dispatcher.BeginInvoke(() => ApplyHotkeyPressedState(isPressed));
    }

    private void OnLevelTimerTick(object? sender, EventArgs e)
    {
        // Feeds the overlay volume meters; the Set*Level calls no-op while their
        // rings are hidden, and the reads reset the levels so stale values never
        // linger. The reads are shared between the desktop overlay and the stream
        // overlay pages, because reading also resets the accumulators.
        bool obsWantsLevels = _obsOverlayServer is { HasClients: true };
        if (_overlayIndicator != null || obsWantsLevels)
        {
            var (outputPeak, outputRms) = _router.ReadAndResetOutputLevels();
            var (musicPeak, musicRms) = _router.ReadAndResetMusicLevels();

            _overlayIndicator?.SetOutputLevel(outputPeak, outputRms);
            _overlayIndicator?.SetMusicLevel(musicPeak, musicRms);

            if (obsWantsLevels && _settings.OverlayVolumeMeterEnabled)
            {
                _obsOverlayServer!.PublishLevels(outputPeak, outputRms, musicPeak, musicRms);
            }
        }

        if (_appCapture is { } capture)
        {
            // Peak-hold with decay so short transients stay visible.
            float peak = capture.ReadAndResetPeak();
            CaptureLevelMeter.Value = Math.Min(1d, Math.Max(peak, CaptureLevelMeter.Value * 0.82));

            // The raw capture arrives in short packets and music naturally has
            // quiet gaps. Keep a time-based activity latch instead of deriving
            // overlay visibility from the decaying presentation meter: crossing
            // that meter's threshold used to repeatedly hide/reset the music
            // ring, making external mode flash and appear to have a dead gauge.
            if (_externalSignalActivity.Observe(peak, _uptime.Elapsed))
            {
                _overlayIndicator?.SetMusicState(ComputeOverlayMusicState());
                PublishObsOverlayState();
            }

            UpdateExternalCaptureStatusText();
        }

        UpdateNoiseGateStateText();
        LogMusicCableActivity();
        _outputAppWatcher?.Sample();
        if (_router.IsRouting)
        {
            DryLevelMeter.Value = _router.NormalPeak;
            ModdedLevelMeter.Value = _router.ModdedPeak;
        }
        else
        {
            DryLevelMeter.Value = 0;
            ModdedLevelMeter.Value = 0;

            // Detect unexpected routing stop (e.g., device gracefully removed without
            // an exception). The combo boxes are disabled while routing is active, so
            // if routing has stopped but they are still disabled, reset the UI.
            if (!DryInputCombo.IsEnabled && !_isDevicesLoading)
            {
                StopRouting();
            }
        }
    }

    private void OnSettingsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi)
        {
            return;
        }

        if (ReferenceEquals(sender, DryInputCombo)
            && DryInputCombo.SelectedItem is AudioDeviceOption dryInput)
        {
            _settings.NormalInputDeviceId = dryInput.Id;
        }
        else if (ReferenceEquals(sender, ModdedInputCombo)
            && ModdedInputCombo.SelectedItem is ModifiedVoiceOption modifiedVoice)
        {
            _ = ChangeVoiceAsync(modifiedVoice.Mode, _settings.SelectedVoiceProfileId);
            return;
        }
        else if (ReferenceEquals(sender, ExternalModdedInputCombo)
            && ExternalModdedInputCombo.SelectedItem is AudioDeviceOption moddedInput)
        {
            _settings.ModdedInputDeviceId = moddedInput.Id;
        }
        else if (ReferenceEquals(sender, OutputDeviceCombo)
            && OutputDeviceCombo.SelectedItem is AudioDeviceOption output)
        {
            _settings.OutputDeviceId = output.Id;
        }

        ApplyModdedMicUiState();
        UpdateOutputCableWarning();
        UpdateSecondaryOutputWarning();
        OnConfigurationChanged();
    }

    // --- Secondary output (pre-gate fanout, e.g. for recording or streaming) ---

    /// <summary>
    /// Pushes the current UI state into the engine. The device takes effect at the
    /// next routing start; volume and ignore-PTT apply immediately while running.
    /// </summary>
    private void ApplySecondaryOutputConfig()
    {
        _secondaryOutput.Enabled = _settings.SecondaryOutputEnabled;
        _secondaryOutput.DeviceId = _settings.SecondaryOutputDeviceId;
        _secondaryOutput.IgnorePushToTalk = _settings.SecondaryOutputIgnorePushToTalk;
        _secondaryOutput.Volume = _settings.SecondaryOutputVolume;
    }

    private void OnSecondaryOutputConfigChanged(object sender, RoutedEventArgs e)
    {
        // Fires while XAML is still being parsed: SecondaryIgnorePttCheck defaults
        // to checked, which raises Checked before the later controls exist.
        if (SecondaryVolumeSlider == null || _isUpdatingUi || _isUpdatingMusicUi)
        {
            return;
        }

        _settings.SecondaryOutputEnabled = SecondaryOutputEnabledCheck.IsChecked == true;
        _settings.SecondaryOutputIgnorePushToTalk = SecondaryIgnorePttCheck.IsChecked == true;
        ApplySecondaryOutputConfig();
        UpdateSecondaryOutputWarning();
        OnConfigurationChanged();
    }

    private void OnSecondaryOutputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SecondaryVolumeSlider == null || _isUpdatingUi)
        {
            return;
        }

        if (SecondaryOutputCombo.SelectedItem is AudioDeviceOption device)
        {
            _settings.SecondaryOutputDeviceId = device.Id;
        }
        ApplySecondaryOutputConfig();
        UpdateSecondaryOutputWarning();
        OnConfigurationChanged();
    }

    private void OnSecondaryVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires while XAML is still being parsed (initial Value assignment).
        if (SecondaryVolumePercentText == null)
        {
            return;
        }

        UpdateSecondaryVolumePercentText();

        if (_isUpdatingUi || _isUpdatingMusicUi)
        {
            return;
        }

        _secondaryOutput.Volume = (float)e.NewValue;
        _settings.SecondaryOutputVolume = (float)e.NewValue;
        OnConfigurationChanged();
    }

    private void UpdateSecondaryVolumePercentText()
    {
        SecondaryVolumePercentText.Text = $"{Math.Round(SecondaryVolumeSlider.Value * 100)} %";
    }

    private void UpdateSecondaryOutputWarning()
    {
        if (!_settings.SecondaryOutputEnabled)
        {
            SecondaryOutputWarningText.Visibility = Visibility.Collapsed;
            return;
        }

        if (SecondaryOutputCombo.SelectedItem is not AudioDeviceOption secondary)
        {
            // No selection on a fresh install is normal. Only call it a missing
            // saved device when an id was actually persisted earlier.
            bool savedDeviceMissing = !string.IsNullOrWhiteSpace(_settings.SecondaryOutputDeviceId);
            SecondaryOutputWarningText.Text =
                "⚠ The saved secondary device was not found — reconnect it or select a new device. No device will be selected automatically.";
            SecondaryOutputWarningText.Visibility = _devicesLoaded && savedDeviceMissing
                ? Visibility.Visible
                : Visibility.Collapsed;
            return;
        }

        if ((OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.Id == secondary.Id)
        {
            SecondaryOutputWarningText.Text =
                "⚠ Same device as the virtual cable — the mix would play twice. Select a different device.";
            SecondaryOutputWarningText.Visibility = Visibility.Visible;
            return;
        }

        SecondaryOutputWarningText.Visibility = Visibility.Collapsed;
    }

    private void UpdateSecondaryOutputStatus()
    {
        // The monitor-only hint mentions the secondary output while it runs.
        UpdateMusicRouteHint();

        if (_router.IsRouting && _secondaryOutput.IsRunning)
        {
            SecondaryOutputStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D));
            SecondaryOutputStatusText.Text =
                $"Active — the mix is also playing on {((SecondaryOutputCombo.SelectedItem as AudioDeviceOption)?.FriendlyName ?? "the selected device")}.";
            SecondaryOutputStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            SecondaryOutputStatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSecondaryOutputError(object? sender, string message)
    {
        Log.Error("Secondary output error: {ErrorMessage}", message);

        Dispatcher.BeginInvoke(() =>
        {
            // A delayed teardown notification from an older session must not
            // replace the healthy status of a secondary output already restarted.
            if (_secondaryOutput.IsRunning)
            {
                return;
            }

            _secondaryOutputError = message;
            SecondaryOutputStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C));
            SecondaryOutputStatusText.Text = $"Secondary output stopped: {message} — routing to the cable continues.";
            SecondaryOutputStatusText.Visibility = Visibility.Visible;
            UpdateMusicRouteHint();
            UpdateStatusText();
        });
    }

    private static readonly System.Windows.Media.Brush MutedTextBrush = CreateFrozenBrush(0x47, 0x55, 0x69);
    private static readonly System.Windows.Media.Brush ProblemTextBrush = CreateFrozenBrush(0xB9, 0x1C, 0x1C);

    private List<string> _voiceProfileProblems = [];

    private static readonly ModifiedVoiceOption[] ModifiedVoiceOptions =
    [
        new(ModifiedVoiceMode.None, "Off"),
        new(ModifiedVoiceMode.ExternalMicrophone, "Voice changer app (Voicemod or similar)"),
        new(ModifiedVoiceMode.LocalProfile, "MicMixer voices (built in, experimental)")
    ];

    private ModifiedVoiceMode CurrentModifiedVoiceMode =>
        (ModdedInputCombo.SelectedItem as ModifiedVoiceOption)?.Mode ?? _settings.ModifiedVoiceMode;

    private void OnVoiceSwitchChecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi)
        {
            return;
        }

        ModifiedVoiceMode mode = ReferenceEquals(sender, VoiceAppRadio) ? ModifiedVoiceMode.ExternalMicrophone
            : ReferenceEquals(sender, VoiceMicMixerRadio) ? ModifiedVoiceMode.LocalProfile
            : ModifiedVoiceMode.None;
        _ = ChangeVoiceAsync(mode, _settings.SelectedVoiceProfileId);
    }

    /// <summary>Both voice lists (main window and settings) end up here.</summary>
    private void OnVoiceProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingUi || _router == null)
        {
            return;
        }

        if (((System.Windows.Controls.ComboBox)sender).SelectedItem is VoiceProfile profile)
        {
            _ = ChangeVoiceAsync(CurrentModifiedVoiceMode, profile.Id);
        }
    }

    /// <summary>
    /// Switches the voice changer or its voice. Both are built into the route when it
    /// starts, so a running route restarts with the new choice (a short gap in the audio).
    /// Saved at once: this is switched during a session, not set up once.
    /// </summary>
    private async Task ChangeVoiceAsync(ModifiedVoiceMode mode, string? profileId)
    {
        if (_isStartingRouting)
        {
            // The start in flight already took the old choice; show that one again.
            RenderVoiceChoice();
            return;
        }

        if (mode == _settings.ModifiedVoiceMode && profileId == _settings.SelectedVoiceProfileId)
        {
            return;
        }

        bool restart = _router.IsRouting;
        if (restart)
        {
            StopRouting();
        }

        if (profileId != _settings.SelectedVoiceProfileId)
        {
            // The alternate analysis window belongs to one profile.
            _settings.LongerAnalysisWindow = false;
        }

        _settings.ModifiedVoiceMode = mode;
        _settings.SkipModdedMic = mode == ModifiedVoiceMode.None;
        _settings.SelectedVoiceProfileId = profileId;
        if (mode == ModifiedVoiceMode.ExternalMicrophone)
        {
            _settings.ModdedInputDeviceId ??= SelectedDeviceId(ExternalModdedInputCombo);
        }

        SaveSettings();
        RenderVoiceChoice();
        ApplyModdedMicUiState();
        OnConfigurationChanged();

        if (restart)
        {
            await StartRoutingAsync();
        }
    }

    /// <summary>Shows the voice changer choice in both windows' controls.</summary>
    private void RenderVoiceChoice()
    {
        bool wasUpdating = _isUpdatingUi;
        _isUpdatingUi = true;
        try
        {
            ModdedInputCombo.SelectedItem = ModifiedVoiceOptions.First(option => option.Mode == _settings.ModifiedVoiceMode);
            VoiceProfileCombo.SelectedValue = _settings.SelectedVoiceProfileId;
            QuickVoiceProfileCombo.SelectedValue = _settings.SelectedVoiceProfileId;
            LongerAnalysisWindowCheck.IsChecked = _settings.LongerAnalysisWindow;
            VoiceOffRadio.IsChecked = _settings.ModifiedVoiceMode == ModifiedVoiceMode.None;
            VoiceAppRadio.IsChecked = _settings.ModifiedVoiceMode == ModifiedVoiceMode.ExternalMicrophone;
            VoiceMicMixerRadio.IsChecked = _settings.ModifiedVoiceMode == ModifiedVoiceMode.LocalProfile;
        }
        finally
        {
            _isUpdatingUi = wasUpdating;
        }
    }

    private bool IsModdedMicSkipped => CurrentModifiedVoiceMode == ModifiedVoiceMode.None;

    private void ApplyModdedMicUiState()
    {
        ApplyModdedMicUiState(CurrentModifiedVoiceMode);
    }

    private void ApplyModdedMicUiState(ModifiedVoiceMode mode)
    {
        bool skip = mode == ModifiedVoiceMode.None;
        bool external = mode == ModifiedVoiceMode.ExternalMicrophone;
        bool builtIn = mode == ModifiedVoiceMode.LocalProfile;

        // "None" has nothing to configure, so the panel is absent rather than empty.
        ModifiedVoicePanel.Visibility = skip ? Visibility.Collapsed : Visibility.Visible;
        ModifiedVoiceTitle.Text = builtIn ? "MicMixer voices · experimental" : "The microphone device your voice changer app creates";

        var localControls = builtIn ? Visibility.Visible : Visibility.Collapsed;
        VoiceProfileCombo.Visibility = localControls;
        DeleteVoiceButton.Visibility = localControls;
        CreateVoiceButton.Visibility = localControls;
        ProcessedVoiceVolumeSlider.Visibility = localControls;
        ProcessedVoiceVolumePercentText.Visibility = localControls;
        ModdedLevelLabel.Text = builtIn ? "Volume" : "Level";
        ExternalModdedInputCombo.Visibility = external ? Visibility.Visible : Visibility.Collapsed;

        bool idle = !_router.IsRouting && !_isStartingRouting;
        VoiceProfileCombo.IsEnabled = builtIn && !_isStartingRouting;
        VoiceSwitchPanel.IsEnabled = !_isStartingRouting;
        QuickVoiceProfileCombo.Visibility = localControls;
        string? appDevice = (ExternalModdedInputCombo.SelectedItem as AudioDeviceOption)?.FriendlyName;
        VoiceSwitchHint.Text = !external ? string.Empty
            : appDevice != null ? $"from {appDevice}"
            : "choose the app's microphone in Settings › Devices";
        CreateVoiceButton.IsEnabled = idle;
        CreateVoiceButton.ToolTip = idle
            ? "Record a sample, shape a voice and save it as a local profile."
            : "Stop routing to open the voice designer.";
        ExternalModdedInputCombo.IsEnabled = external && !_router.IsRouting && !_isDevicesLoading;
        LongerAnalysisWindowCheck.IsEnabled = builtIn && !_router.IsRouting;
        UpdateVoiceWindowLabel();
        UpdateDeleteButton();
        ShowModifiedVoiceStatus();

        // The hotkey still matters without a modded mic when push-to-talk gates the mix.
        bool hotkeyRelevant = !skip || IsPushToTalk;
        HotkeyConfigPanel.IsEnabled = hotkeyRelevant;
        HotkeyConfigPanel.Opacity = hotkeyRelevant ? 1.0 : 0.55;
        ModdedMeterPanel.Visibility = skip ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadVoiceProfiles()
    {
        var result = new VoiceProfileStore().List();
        _voiceProfileProblems = result.Errors;
        VoiceProfileCombo.ItemsSource = result.Profiles;
        QuickVoiceProfileCombo.ItemsSource = result.Profiles;
        VoiceProfileCombo.SelectedValue = _settings.SelectedVoiceProfileId;
        QuickVoiceProfileCombo.SelectedValue = _settings.SelectedVoiceProfileId;
        // A first run has no stored choice. Land on a starter rather than an empty
        // combo that only reports its emptiness once Enable has already failed.
        if (VoiceProfileCombo.SelectedItem == null && _settings.SelectedVoiceProfileId == null)
        {
            VoiceProfileCombo.SelectedItem = result.Profiles.FirstOrDefault();
            _settings.SelectedVoiceProfileId = (VoiceProfileCombo.SelectedItem as VoiceProfile)?.Id;
            QuickVoiceProfileCombo.SelectedValue = _settings.SelectedVoiceProfileId;
        }
        ShowModifiedVoiceStatus();
        UpdateVoiceWindowLabel();
    }

    private void ShowModifiedVoiceStatus()
    {
        if (CurrentModifiedVoiceMode != ModifiedVoiceMode.LocalProfile)
        {
            SetModifiedVoiceMessage("Pick the device your voice changer outputs to. Hold the hotkey to send it instead of your normal mic.");
        }
        else if (_voiceProfileProblems.Count > 0)
        {
            SetModifiedVoiceMessage(string.Join("\n", _voiceProfileProblems), problem: true);
        }
        else if (VoiceProfileCombo.SelectedItem is not VoiceProfile selected)
        {
            SetModifiedVoiceMessage("The selected profile is missing or invalid. Choose another one.", problem: true);
        }
        else
        {
            SetModifiedVoiceMessage(BuiltInVoiceProfiles.Find(selected.Id) != null
                ? "A built-in starting point. Create a voice to make it your own."
                : "One of your own voices. Create a voice to edit or copy it.");
        }
    }

    private void SetModifiedVoiceMessage(string text, bool problem = false)
    {
        ModifiedVoiceMessage.Text = text;
        ModifiedVoiceMessage.Foreground = problem ? ProblemTextBrush : MutedTextBrush;
    }

    private void UpdateDeleteButton()
    {
        bool own = VoiceProfileCombo.SelectedItem is VoiceProfile profile && BuiltInVoiceProfiles.Find(profile.Id) == null;
        bool idle = !_router.IsRouting && !_isStartingRouting;
        DeleteVoiceButton.IsEnabled = own && idle;
        DeleteVoiceButton.ToolTip = !own ? "Built-in starting points cannot be deleted."
            : idle ? "Permanently deletes the selected voice from this computer."
            : "Stop routing to delete a voice.";
    }

    private void OnDeleteVoice(object sender, RoutedEventArgs e)
    {
        if (VoiceProfileCombo.SelectedItem is not VoiceProfile profile || _router.IsRouting || _isStartingRouting) return;
        var answer = System.Windows.MessageBox.Show(Window.GetWindow(DeleteVoiceButton), $"Delete '{profile.DisplayName}' permanently? This cannot be undone.",
            "Delete voice", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            new VoiceProfileStore().Delete(profile.Id);
            _settings.SelectedVoiceProfileId = null;
            LoadVoiceProfiles();
            SaveSettings();
            UpdateStatusText();
            SetModifiedVoiceMessage($"Deleted '{profile.DisplayName}'.");
        }
        catch (Exception ex) { SetModifiedVoiceMessage("Could not delete the voice: " + ex.Message, problem: true); }
        UpdateDeleteButton();
    }

    private void OnCreateVoice(object sender, RoutedEventArgs e)
    {
        if (_router.IsRouting || _isStartingRouting) return;
        try
        {
            var input = DryInputCombo.SelectedItem as AudioDeviceOption;
            var dialog = new VoiceDesignerDialog(VoiceProfileCombo.SelectedItem as VoiceProfile,
                input?.Id, input?.FriendlyName, (MonitorDeviceCombo.SelectedItem as AudioDeviceOption)?.Id,
                (OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.Id ?? _settings.OutputDeviceId) { Owner = Window.GetWindow(CreateVoiceButton) };
            if (dialog.ShowDialog() == true && dialog.SavedProfile is { } profile)
            {
                _settings.SelectedVoiceProfileId = profile.Id;
                _settings.LongerAnalysisWindow = false;
                LongerAnalysisWindowCheck.IsChecked = false;
                LoadVoiceProfiles();
                SaveSettings();
                UpdateStatusText();
                SetModifiedVoiceMessage($"Saved '{profile.DisplayName}' and selected it.");
            }
        }
        catch (Exception ex) { SetModifiedVoiceMessage("Could not open the voice designer: " + ex.Message, problem: true); }
    }

    private void UpdateVoiceWindowLabel()
    {
        bool hasAlternate = VoiceProfileCombo.SelectedItem is VoiceProfile { AlternateBlockMilliseconds: not null };
        LongerAnalysisWindowCheck.Visibility = CurrentModifiedVoiceMode == ModifiedVoiceMode.LocalProfile && hasAlternate
            ? Visibility.Visible : Visibility.Collapsed;
        if (VoiceProfileCombo.SelectedItem is VoiceProfile { AlternateBlockMilliseconds: float alternate } profile)
            AlternateWindowLabel.Text = alternate > profile.Parameters.BlockMilliseconds
                ? "Smoother processing (more delay)" : "Alternate processing quality";
    }

    private void OnProcessedVoiceVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || ProcessedVoiceVolumePercentText == null)
        {
            return;
        }

        _settings.ProcessedVoiceVolume = (float)e.NewValue;
        _router.ProcessedVoiceVolume = _settings.ProcessedVoiceVolume;
        ProcessedVoiceVolumePercentText.Text = $"{Math.Round(e.NewValue * 100)} %";
        OnConfigurationChanged();
    }

    private void OnNormalMicVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || NormalMicVolumePercentText == null)
        {
            return;
        }

        _settings.NormalMicVolume = (float)e.NewValue;
        _router.NormalMicVolume = _settings.NormalMicVolume;
        NormalMicVolumePercentText.Text = $"{Math.Round(e.NewValue * 100)} %";
        OnConfigurationChanged();
    }

    private void OnNoiseGateChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi || NoiseGateStateText == null)
        {
            return;
        }

        _settings.NoiseGateEnabled = NoiseGateCheck.IsChecked == true;
        _router.NoiseGateEnabled = _settings.NoiseGateEnabled;
        UpdateNoiseGateStateText();
        OnConfigurationChanged();
    }

    private void OnNoiseGateThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingUi || NoiseGateThresholdText == null)
        {
            return;
        }

        _settings.NoiseGateThresholdDb = (float)e.NewValue;
        _router.NoiseGateThresholdDb = _settings.NoiseGateThresholdDb;
        NoiseGateThresholdText.Text = $"{_settings.NoiseGateThresholdDb:0} dB";
        OnConfigurationChanged();
    }

    /// <summary>
    /// Live gate readout: the level bar under the threshold slider plus open/closed.
    /// Peak-hold with decay, like the capture meter, so the bar is readable instead
    /// of flickering with every block.
    /// </summary>
    private void UpdateNoiseGateStateText()
    {
        if (!_router.IsRouting || !_settings.NoiseGateEnabled)
        {
            _noiseGatePeakHold = 0f;
            NoiseGateLevelBar.Value = NoiseGateLevelBar.Minimum;
            NoiseGateStateText.Text = string.Empty;
            _overlayIndicator?.SetNoiseGateOpen(null);
            LogNoiseGateActivity(open: false, peak: 0f);
            return;
        }

        float peak = _router.ReadAndResetNoiseGateInputPeak();
        _noiseGatePeakHold = Math.Max(float.IsFinite(peak) ? peak : 0f, _noiseGatePeakHold * 0.85f);
        double decibels = _noiseGatePeakHold > 0f ? 20 * Math.Log10(_noiseGatePeakHold) : double.NegativeInfinity;
        NoiseGateLevelBar.Value = Math.Clamp(decibels, NoiseGateLevelBar.Minimum, NoiseGateLevelBar.Maximum);

        bool open = _router.NoiseGateOpen;
        _overlayIndicator?.SetNoiseGateOpen(open);
        LogNoiseGateActivity(open, peak);
        NoiseGateStateText.Text = open ? "Open" : "Closed";
        NoiseGateStateText.Foreground = open ? StatusTheme.LiveBrush : StatusTheme.StoppedInkBrush;
        NoiseGateLevelBar.Foreground = open ? StatusTheme.LiveBrush : StatusTheme.StoppedInkBrush;
    }

    /// <summary>
    /// One log line per open period, written when it closes, so a moment where
    /// others saw the character talk can be checked against what the cable
    /// actually carried. Polled at the 50 ms tick; the gate's minimum open time
    /// (hold plus release) is longer than that, so no period is missed.
    /// </summary>
    private void LogNoiseGateActivity(bool open, float peak)
    {
        if (_noiseGateOpenedAt is not { } openedAt)
        {
            if (open)
            {
                _noiseGateOpenedAt = DateTime.Now;
                _noiseGateOpenPeak = peak;
                _noiseGateOpenedModded = _router.UseModdedInput;
                _noiseGateOpenTicks = 0;
                _noiseGateCableOpenTicks = 0;
            }

            return;
        }

        _noiseGateOpenPeak = Math.Max(_noiseGateOpenPeak, peak);
        if (open)
        {
            // The push-to-talk gate sits after the noise gate, so an open noise
            // gate only reaches the cable while that gate is open too.
            _noiseGateOpenTicks++;
            if (_router.OutputGateOpen)
            {
                _noiseGateCableOpenTicks++;
            }

            return;
        }

        double peakDb = _noiseGateOpenPeak > 0f ? 20 * Math.Log10(_noiseGateOpenPeak) : double.NegativeInfinity;
        double cableOpenPercent = _noiseGateOpenTicks == 0 ? 100d : 100d * _noiseGateCableOpenTicks / _noiseGateOpenTicks;
        Log.Information(
            "Noise gate open {Start:HH:mm:ss.fff} for {Seconds:0.00} s, peak {PeakDb:0.0} dBFS, threshold {ThresholdDb:0} dB, modified voice {ModifiedVoice}, reached the cable {CablePercent:0}% of the time",
            openedAt,
            (DateTime.Now - openedAt).TotalSeconds,
            peakDb,
            _settings.NoiseGateThresholdDb,
            _noiseGateOpenedModded,
            cableOpenPercent);
        _noiseGateOpenedAt = null;
        _noiseGateLastClosedAt = DateTime.Now;
    }

    /// <summary>
    /// One log line per period in which music reached the cable, written when it
    /// ends. Music is the only source besides the mic that can reach the cable,
    /// and with "ignore push-to-talk" it does so while the mic is silent.
    /// </summary>
    private void LogMusicCableActivity()
    {
        bool sending = ComputeOverlayMusicState() == OverlayMusicState.Sending;
        if (sending)
        {
            _musicSendingSince ??= DateTime.Now;
            return;
        }

        if (_musicSendingSince is not { } since)
        {
            return;
        }

        Log.Information(
            "Music reached the cable {Start:HH:mm:ss.fff} for {Seconds:0.00} s, ignores push-to-talk {IgnoresPushToTalk}",
            since,
            (DateTime.Now - since).TotalSeconds,
            _router.MusicIgnoresPushToTalk);
        _musicSendingSince = null;
    }

    private void OnMarkerKeyPressedStateChanged(object? sender, bool isPressed)
    {
        if (isPressed)
        {
            Dispatcher.BeginInvoke(LogDiagnosticMarker);
        }
    }

    private void OnMarkerClick(object sender, RoutedEventArgs e) => LogDiagnosticMarker();

    /// <summary>Stamps the log with everything that decides what the cable carries right now.</summary>
    private void LogDiagnosticMarker()
    {
        MarkerText.Text = $"Marked {DateTime.Now:HH:mm:ss}";
        string gate = !_router.IsRouting ? "routing stopped"
            : !_settings.NoiseGateEnabled ? "off"
            : _router.NoiseGateOpen ? "open"
            : _noiseGateLastClosedAt is { } closedAt ? $"closed {(DateTime.Now - closedAt).TotalSeconds:0.0} s ago"
            : "closed";

        Log.Information(
            "MARKER pressed. Gate {Gate}, hotkey held {HotkeyHeld}, modified voice {ModifiedVoice}, push-to-talk gate open {CableOpen}, threshold {ThresholdDb:0} dB, normal mic {NormalMicVolume:P0}, processed voice {ProcessedVoiceVolume:P0}",
            gate,
            _hotkeyListener.IsPressed,
            _router.UseModdedInput,
            _router.OutputGateOpen,
            _settings.NoiseGateThresholdDb,
            _settings.NormalMicVolume,
            _settings.ProcessedVoiceVolume);
        Log.Information("FiveM voice settings: {Summary}", FiveMVoiceSettings.Describe());

        string music = ComputeOverlayMusicState() switch
        {
            OverlayMusicState.Sending => "reaching the cable",
            OverlayMusicState.Blocked => "blocked by push-to-talk",
            OverlayMusicState.MonitorOnly => "monitor only",
            _ => "not playing"
        };
        _outputAppWatcher?.Poll();
        Log.Information(
            "Marker sources: music {Music}, ignores push-to-talk {IgnoresPushToTalk}; other apps playing to the cable: {OtherApps}; Windows default playback device: {Defaults}",
            music,
            _router.MusicIgnoresPushToTalk,
            _outputAppWatcher?.DescribePlaying() ?? "routing stopped",
            OutputDeviceAppWatcher.DescribeWindowsDefaults());
    }

    private void OnLongerAnalysisWindowChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi)
        {
            return;
        }

        _settings.LongerAnalysisWindow = LongerAnalysisWindowCheck.IsChecked == true;
        SaveSettings();
    }

    private bool IsPushToTalk => _settings.PushToTalkMode;

    private void OnPushToTalkChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        _settings.PushToTalkMode = PushToTalkCheck.IsChecked == true;
        ApplyModdedMicUiState();
        CancelPendingReleaseDelay();

        if (_router.IsRouting)
        {
            SetHotkeyMonitoringEnabled(!IsModdedMicSkipped || IsPushToTalk);
            ApplyEffectiveRoutingStates();
        }

        OnConfigurationChanged();
    }

    /// <summary>
    /// Derives the router's modded-mic switch and push-to-talk gate from the
    /// current hotkey state. Single writer for both so they can never diverge.
    /// </summary>
    private void ApplyEffectiveRoutingStates()
    {
        bool engaged = IsHotkeyEngaged();
        _router.SetUseModdedInput(!IsModdedMicSkipped && engaged);
        _router.SetOutputGateOpen(!IsPushToTalk || engaged);
    }

    private void OnMusicRoutingModeChanged(object sender, RoutedEventArgs e)
    {
        // Fires while XAML is still being parsed when a checkbox default raises
        // Checked before the later controls exist.
        if (MusicRouteHintText == null || _isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        SetMusicRoutingModes(
            MusicIgnorePttCheck.IsChecked == true,
            MusicMonitorOnlyCheck.IsChecked == true,
            renderControls: false);
    }

    private void SetMusicRoutingModes(bool ignoresPushToTalk, bool monitorOnly, bool renderControls)
    {
        _settings.MusicIgnoresPushToTalk = ignoresPushToTalk;
        _settings.MusicMonitorOnly = monitorOnly;

        if (renderControls)
        {
            _isUpdatingMusicUi = true;
            try
            {
                MusicIgnorePttCheck.IsChecked = ignoresPushToTalk;
                MusicMonitorOnlyCheck.IsChecked = monitorOnly;
            }
            finally
            {
                _isUpdatingMusicUi = false;
            }
        }

        ApplyMusicRoutingModes();
        SaveSettings();
        UpdateStatusText();
        UpdateMusicUi();
    }

    /// <summary>
    /// Pushes the music routing toggles into the router (they apply immediately,
    /// also while routing runs) and refreshes the honest-state hint below them.
    /// </summary>
    private void ApplyMusicRoutingModes()
    {
        _router.SetMusicIgnoresPushToTalk(_settings.MusicIgnoresPushToTalk);
        _router.SetMusicMonitorOnly(_settings.MusicMonitorOnly);
        UpdateMusicRouteHint();
    }

    /// <summary>
    /// The amber hint under the music routing toggles: states exactly where the
    /// music goes while monitor-only preview is active, including the cases the
    /// user could otherwise be surprised by (secondary output still carrying it,
    /// or monitoring being off so nothing is audible locally).
    /// </summary>
    private void UpdateMusicRouteHint()
    {
        if (!_settings.MusicMonitorOnly)
        {
            MusicRouteHintText.Visibility = Visibility.Collapsed;
            return;
        }

        string text = "Monitor only: music is not transmitted to the mic channel.";
        if (_secondaryOutput.IsRunning)
        {
            text += " The secondary output still receives it.";
        }

        if (_isExternalMode)
        {
            text += " In app mode, you hear the app directly.";
        }
        else if (!_settings.MonitorEnabled)
        {
            text += " Note: monitoring is off — you cannot hear any music right now.";
        }

        if (MusicRouteHintText.Text != text)
        {
            MusicRouteHintText.Text = text;
        }

        MusicRouteHintText.Visibility = Visibility.Visible;
    }

    private void UpdateOutputCableWarning()
    {
        bool looksWrong = OutputDeviceCombo.SelectedItem is AudioDeviceOption output && !AudioDevices.LooksLikeVirtualCable(output);
        OutputCableWarningText.Visibility = looksWrong ? Visibility.Visible : Visibility.Collapsed;

        if (!looksWrong)
        {
            _acknowledgedNonCableOutputId = null;
        }
    }

    private void OnCaptureHotkeyClick(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        UpdateHotkeyUi();
        Window window = Window.GetWindow(CaptureHotkeyButton);
        window.Activate();
        window.Focus();
    }

    private void OnPreviewHotkeyKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None)
        {
            return;
        }

        e.Handled = true;
        ApplyHotkeyBinding(HotkeyBinding.FromKeyboardKey(key));
    }

    private void OnPreviewHotkeyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        e.Handled = true;
        ApplyHotkeyBinding(HotkeyBinding.FromMouseButton(e.ChangedButton));
    }

    private void OnReleaseDelayTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        ApplyReleaseDelayFromTextBox();
    }

    private void OnReleaseDelayTextBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not Key.Enter and not Key.Return)
        {
            return;
        }

        e.Handled = true;
        ApplyReleaseDelayFromTextBox();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_router.IsRouting)
            StopRouting();

        await RefreshDevicesAsync();
    }

    private void OnGuideNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog(
            "MicMixer",
            "https://github.com/benjibutten/MicMixer")
        {
            Owner = Window.GetWindow((DependencyObject)sender)
        };
        dialog.ShowDialog();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        PersistWindowBounds();
        // PersistWindowBounds saves the same settings object, so any pending
        // debounced slider values are already included and need no later write.
        _settingsSaveTimer.Stop();

        if (!_isReallyClosing && !App.StartupBenchmarkMode)
        {
            e.Cancel = true;
            Hide();
            _settingsWindow?.Close();

            if (!_trayBalloonShown)
            {
                _trayBalloonShown = true;
                _trayIcon.ShowBalloonTip(3000, "MicMixer is still running",
                    "The app is in the system tray. Double-click the icon to reopen it, or right-click and select Exit.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }

            return;
        }

        _levelTimer.Stop();
        _releaseDelayTimer.Stop();
        _musicTimer.Stop();
        _settingsSaveTimer.Stop();
        _overlayIndicator?.Close();
        _overlayIndicator = null;
        StopObsOverlayServer();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _secondaryOutput.Dispose();
        _appCapture?.Dispose();
        _hotkeyListener.Dispose();
        _markerKeyListener.Dispose();
        _deviceChangeTimer.Stop();
        _deviceNotifications?.Dispose();
        _deviceEnumerator?.Dispose();
    }

    /// <summary>
    /// Saves what changes outside the settings window (music card, window size).
    /// Unsaved settings-window changes stay out of the file until its Save button.
    /// </summary>
    private void SaveSettings()
    {
        AppSettings file = _settings.Clone();
        file.CopyConfigurationFrom(_savedSettings);
        try
        {
            _settingsStore.Save(file);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings.");
            StatusText.Text = $"Could not save settings: {ex.Message}";
        }
    }

    private void ScheduleSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    // --- Settings window: live changes, explicit save ---

    /// <summary>Call after any settings-window value in _settings changed.</summary>
    private void OnConfigurationChanged()
    {
        _hasUnsavedConfiguration = !_settings.ConfigurationEquals(_savedSettings);
        SettingsSaveStateText.Text = _hasUnsavedConfiguration
            ? "Unsaved changes. They are already in use; Save keeps them for the next time MicMixer starts."
            : "Everything is saved.";
        SettingsSaveStateText.Foreground = _hasUnsavedConfiguration ? ProblemInkBrush : MutedInkBrush;
        UpdateDependentSettingsControls();
        UpdateStatusText();
    }

    private static readonly System.Windows.Media.Brush ProblemInkBrush = CreateFrozenBrush(0x9A, 0x34, 0x12);
    private static readonly System.Windows.Media.Brush MutedInkBrush = CreateFrozenBrush(0x6B, 0x72, 0x80);

    private void OnSaveSettingsClick(object sender, RoutedEventArgs e) => SaveConfiguration();

    private void SaveConfiguration()
    {
        AppSettings saved = _settings.Clone();
        try
        {
            _settingsStore.Save(saved);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings.");
            SettingsSaveStateText.Text = $"Could not save: {ex.Message}";
            StatusText.Text = $"Could not save settings: {ex.Message}";
            return;
        }

        _savedSettings = saved;
        SyncStartWithWindows();
        OnConfigurationChanged();
    }

    private void OnRunSetupGuideClick(object sender, RoutedEventArgs e) => ShowSetupGuide();

    /// <summary>
    /// Runs the setup guide. Finish applies and saves its choices, then optionally
    /// starts routing so the user can test straight away.
    /// </summary>
    private async void ShowSetupGuide()
    {
        var guide = new SetupGuideWindow(_settings)
        {
            Owner = _settingsWindow is { IsVisible: true } settingsWindow ? settingsWindow : this
        };

        if (guide.ShowDialog() != true)
        {
            if (guide.Skipped && !_settings.SetupGuideDismissed)
            {
                _settings.SetupGuideDismissed = true;
                SaveSettings();
            }

            return;
        }

        // Devices cannot change under a running route.
        if (_router.IsRouting)
        {
            StopRouting();
        }

        guide.ApplyTo(_settings);
        ApplyConfiguration();
        SaveConfiguration();
        await RefreshDevicesAsync();

        if (guide.StartRoutingWhenDone && !_router.IsRouting)
        {
            OnToggleClick(ToggleBtn, new RoutedEventArgs());
        }
    }

    private async Task LoadDevicesAndOfferSetupGuideAsync()
    {
        await RefreshDevicesAsync();

        // Nothing saved yet and the guide never skipped: this is a first run.
        if (IsVisible && _devicesLoaded && _savedSettings.OutputDeviceId == null && !_settings.SetupGuideDismissed)
        {
            ShowSetupGuide();
        }
    }

    private void OnDiscardSettingsClick(object sender, RoutedEventArgs e)
    {
        // Like Refresh devices: the route was started with the devices being discarded.
        if (_router.IsRouting)
        {
            StopRouting();
        }

        _settings.CopyConfigurationFrom(_savedSettings);
        ApplyConfiguration();
        OnConfigurationChanged();
        _ = RefreshDevicesAsync();
    }

    /// <summary>
    /// Shows the settings-window values of _settings in its controls and applies
    /// them to the running app. Device combos follow in RefreshDevicesAsync.
    /// </summary>
    private void ApplyConfiguration()
    {
        _isUpdatingUi = true;
        try
        {
            StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
            VoiceProfileCombo.SelectedValue = _settings.SelectedVoiceProfileId;
            LongerAnalysisWindowCheck.IsChecked = _settings.LongerAnalysisWindow;
            ProcessedVoiceVolumeSlider.Value = _settings.ProcessedVoiceVolume;
            NormalMicVolumeSlider.Value = _settings.NormalMicVolume;
            NoiseGateCheck.IsChecked = _settings.NoiseGateEnabled;
            NoiseGateThresholdSlider.Value = _settings.NoiseGateThresholdDb;
            ReleaseDelayTextBox.Text = _settings.ReleaseDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
            PushToTalkCheck.IsChecked = _settings.PushToTalkMode;
            SecondaryOutputEnabledCheck.IsChecked = _settings.SecondaryOutputEnabled;
            SecondaryIgnorePttCheck.IsChecked = _settings.SecondaryOutputIgnorePushToTalk;
            SecondaryVolumeSlider.Value = _settings.SecondaryOutputVolume;
            OverlayIndicatorCheck.IsChecked = _settings.OverlayIndicatorEnabled;
            OverlayVolumeMeterCheck.IsChecked = _settings.OverlayVolumeMeterEnabled;
            MeterSensitivitySlider.Value = _settings.MeterSensitivityDb;
            ObsOverlayCheck.IsChecked = _settings.ObsOverlayEnabled;
            ObsOverlayPortBox.Text = _settings.ObsOverlayPort.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isUpdatingUi = false;
        }

        _router.ProcessedVoiceVolume = _settings.ProcessedVoiceVolume;
        _router.NormalMicVolume = _settings.NormalMicVolume;
        _router.NoiseGateEnabled = _settings.NoiseGateEnabled;
        _router.NoiseGateThresholdDb = _settings.NoiseGateThresholdDb;
        ProcessedVoiceVolumePercentText.Text = $"{Math.Round(_settings.ProcessedVoiceVolume * 100)} %";
        NormalMicVolumePercentText.Text = $"{Math.Round(_settings.NormalMicVolume * 100)} %";
        NoiseGateThresholdText.Text = $"{_settings.NoiseGateThresholdDb:0} dB";
        UpdateSecondaryVolumePercentText();
        ApplySecondaryOutputConfig();

        _releaseDelayMilliseconds = _settings.ReleaseDelayMilliseconds;
        CancelPendingReleaseDelay();
        _hotkeyBinding = HotkeyBinding.Parse(_settings.HotkeyId);
        _hotkeyListener.UpdateBinding(_hotkeyBinding);
        UpdateHotkeyUi();

        UpdateMeterSensitivityText();
        ApplyOverlayIndicatorSetting(_settings.OverlayIndicatorEnabled);
        ApplyObsOverlaySetting(_settings.ObsOverlayEnabled);

        ShowModifiedVoiceStatus();
        UpdateVoiceWindowLabel();
        UpdateDeleteButton();
        ApplyModdedMicUiState();
        if (_router.IsRouting)
        {
            SetHotkeyMonitoringEnabled(!IsModdedMicSkipped || IsPushToTalk);
            ApplyEffectiveRoutingStates();
        }
    }

    /// <summary>Enables the settings that only matter while the setting they refine is on.</summary>
    private void UpdateDependentSettingsControls()
    {
        SecondaryOutputConfigPanel.IsEnabled = _settings.SecondaryOutputEnabled;
        NoiseGateSettingsPanel.IsEnabled = _settings.NoiseGateEnabled;
        bool anyOverlay = _settings.OverlayIndicatorEnabled || _settings.ObsOverlayEnabled;
        OverlayVolumeMeterCheck.IsEnabled = anyOverlay;
        MeterSensitivityPanel.IsEnabled = anyOverlay && _settings.OverlayVolumeMeterEnabled;
        ObsOverlayDetailsPanel.Visibility = _settings.ObsOverlayEnabled ? Visibility.Visible : Visibility.Collapsed;
        // Without push-to-talk there is nothing for the music to ignore.
        MusicIgnorePttCheck.IsEnabled = _settings.PushToTalkMode;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings(null);

    private void OnStopRoutingClick(object sender, RoutedEventArgs e) => StopRouting();

    /// <summary>Opens the settings window, on <paramref name="page"/> or where it was left.</summary>
    private void ShowSettings(TabItem? page)
    {
        if (_settingsWindow == null)
        {
            // The controls live in MainWindow.xaml so this class keeps its handlers; the
            // window only borrows them, and hides instead of closing to keep them alive.
            SettingsHost.Child = null;
            _settingsWindow = new Window
            {
                Title = "MicMixer settings",
                Content = SettingsContent,
                Resources = Resources,
                Owner = this,
                Icon = Icon,
                Width = 800,
                Height = 660,
                MinWidth = 620,
                MinHeight = 440,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _settingsWindow.PreviewKeyDown += OnPreviewHotkeyKeyDown;
            _settingsWindow.PreviewMouseDown += OnPreviewHotkeyMouseDown;
            _settingsWindow.Closing += (window, closing) =>
            {
                if (!_isReallyClosing)
                {
                    closing.Cancel = true;
                    ((Window)window!).Hide();
                    // An armed capture would otherwise take the next key or click for the hotkey.
                    _isCapturingHotkey = false;
                    UpdateHotkeyUi();
                }
            };
        }

        if (page != null)
        {
            SettingsTabs.SelectedItem = page;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUi)
        {
            return;
        }

        // The registry follows on Save, together with the settings file.
        _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        OnConfigurationChanged();
    }

    private void SyncStartWithWindows()
    {
        try
        {
            if (!_startupRegistrySyncService.Sync(_settings.StartWithWindows, Environment.ProcessPath))
            {
                Log.Warning("Could not open the Windows startup registry key.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to synchronize the Start with Windows registry setting.");
        }
    }

    /// <summary>Refreshes the status strip, the problem cards and the tray icon.</summary>
    private void UpdateStatusText()
    {
        MicStatus status = ComputeMicStatus();
        // Muted with the music branch still open must say so: "muted" alone would be
        // a lie while music keeps playing into the cable past push-to-talk.
        bool musicStillTransmitting = status == MicStatus.Muted && _router.MusicRouteOpen && HasActiveMusicSignal();
        StatePill.Background = StatusTheme.TintFor(status);
        StatePillDot.Fill = StatusTheme.BrushFor(status);
        StatePillGlyph.Data = StatusTheme.GlyphFor(status);
        StatePillText.Foreground = StatusTheme.InkFor(status);
        StatePillText.Text = status switch
        {
            MicStatus.Live => "Live",
            MicStatus.Modded => "Modified voice",
            MicStatus.Muted when musicStillTransmitting => "Mic muted",
            MicStatus.Muted => "Muted",
            _ => "Stopped"
        };

        HotkeyStateText.Text = string.Join(" · ", new[]
        {
            DescribeHotkeyState(),
            musicStillTransmitting ? "music is still transmitting" : null,
            IsModdedMicSkipped ? null : $"{DescribeModifiedVoice()} while held",
            _settings.NoiseGateEnabled ? "noise gate on" : null
        }.OfType<string>());

        OverviewText.Text = DescribeRoute();
        RoutingLockBanner.Visibility = _router.IsRouting ? Visibility.Visible : Visibility.Collapsed;
        UpdateProblems();
        UpdateTrayIcon();
    }

    private string DescribeHotkeyState()
    {
        string key = _hotkeyBinding.DisplayName;
        if (!_router.IsRouting)
        {
            return IsPushToTalk
                ? $"Push-to-talk: hold {key} to be heard"
                : IsModdedMicSkipped ? "Normal mic and music" : $"Hold {key} for the modified voice";
        }

        if (IsPushToTalk)
        {
            return _hotkeyListener.IsPressed ? $"{key} held, you are heard"
                : _isReleaseDelayPending ? $"{key} released, muting in {_releaseDelayMilliseconds} ms"
                : $"Hold {key} to be heard";
        }

        return IsModdedMicSkipped ? "Your normal mic is live"
            : _hotkeyListener.IsPressed ? $"{key} held, modified voice is live"
            : _isReleaseDelayPending ? $"{key} released, switching back in {_releaseDelayMilliseconds} ms"
            : $"Normal mic is live, hold {key} for the modified voice";
    }

    private string DescribeModifiedVoice() => CurrentModifiedVoiceMode == ModifiedVoiceMode.LocalProfile
        ? (VoiceProfileCombo.SelectedItem as VoiceProfile)?.DisplayName ?? "MicMixer voice"
        : (ExternalModdedInputCombo.SelectedItem as AudioDeviceOption)?.FriendlyName ?? "Voice changer app";

    /// <summary>Where the mix goes and what else is on, as one line.</summary>
    private string DescribeRoute()
    {
        if (!_devicesLoaded)
        {
            return _isDevicesLoading ? "Loading audio devices…" : "Audio devices could not be read.";
        }

        var parts = new List<string>
        {
            $"To {(OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.FriendlyName ?? "no output"}"
        };

        if (_secondaryOutput.IsRunning)
        {
            parts.Add($"also on {(SecondaryOutputCombo.SelectedItem as AudioDeviceOption)?.FriendlyName ?? "the secondary output"}");
        }
        else if (_settings.SecondaryOutputEnabled)
        {
            parts.Add("secondary output on");
        }

        if (!_isExternalMode)
        {
            parts.Add(_settings.MonitorEnabled ? "monitoring on" : "monitoring off");
        }

        string? overlay = (_settings.OverlayIndicatorEnabled, _settings.ObsOverlayEnabled) switch
        {
            (true, true) => "overlay on screen and stream",
            (true, false) => "overlay on screen",
            (false, true) => "stream overlay on",
            _ => null
        };
        if (overlay != null)
        {
            parts.Add(overlay);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Something that differs from how MicMixer was set up, and the settings page (or the setup guide) that fixes it.</summary>
    private sealed record Problem(string Message, string ActionText, TabItem? Page, bool OpensSetupGuide = false);

    /// <summary>
    /// Compares what is running and connected with the settings. Shows nothing when
    /// they agree, so an empty list means everything is as set up.
    /// </summary>
    private void UpdateProblems()
    {
        var problems = new List<Problem>();

        if (_deviceLoadError != null)
        {
            problems.Add(new Problem(_deviceLoadError, "Devices…", DevicesPage));
        }

        if (_devicesLoaded)
        {
            if (_savedSettings.OutputDeviceId == null)
            {
                problems.Add(new Problem(
                    "MicMixer is not set up yet. The setup guide walks you through it in a few minutes.",
                    "Start setup…", null, OpensSetupGuide: true));
            }

            AddDeviceProblems(problems);
        }

        if (CurrentModifiedVoiceMode == ModifiedVoiceMode.LocalProfile && VoiceProfileCombo.SelectedItem is not VoiceProfile)
        {
            problems.Add(new Problem(
                "The selected MicMixer voice is missing or invalid, so routing cannot start with it.",
                "Devices…", DevicesPage));
        }

        if (_secondaryOutputError != null)
        {
            problems.Add(new Problem(
                $"Secondary output stopped: {_secondaryOutputError} Routing to the virtual cable continues.",
                "Secondary output…", SecondaryOutputPage));
        }

        if (_hasUnsavedConfiguration && _savedSettings.OutputDeviceId != null)
        {
            problems.Add(new Problem(
                "Some settings are changed but not saved. They are in use now, but MicMixer goes back to the saved ones the next time it starts.",
                "Review…", null));
        }

        if (!problems.SequenceEqual(_problems))
        {
            _problems = problems;
            ProblemsList.ItemsSource = problems;
        }
    }

    private void AddDeviceProblems(List<Problem> problems)
    {
        // A combo shows a stand-in when the chosen device is missing, so a combo
        // that disagrees with _settings means "not connected".
        if (_settings.NormalInputDeviceId != null && SelectedDeviceId(DryInputCombo) != _settings.NormalInputDeviceId)
        {
            problems.Add(new Problem(
                $"Your normal mic is not connected.{UsingInstead(DryInputCombo)}",
                "Devices…", DevicesPage));
        }

        if (CurrentModifiedVoiceMode == ModifiedVoiceMode.ExternalMicrophone
            && _settings.ModdedInputDeviceId != null
            && SelectedDeviceId(ExternalModdedInputCombo) != _settings.ModdedInputDeviceId)
        {
            problems.Add(new Problem(
                $"The voice changer app's microphone is not connected.{UsingInstead(ExternalModdedInputCombo)}",
                "Devices…", DevicesPage));
        }

        if (OutputDeviceCombo.SelectedItem is not AudioDeviceOption output)
        {
            problems.Add(new Problem(
                "There is no device to send the mix to. Install a virtual cable such as VB-CABLE, then refresh devices.",
                "Devices…", DevicesPage));
        }
        else if (_settings.OutputDeviceId != null && output.Id != _settings.OutputDeviceId)
        {
            problems.Add(new Problem(
                $"Your virtual cable is not connected. The mix would go to {output.FriendlyName} instead.",
                "Devices…", DevicesPage));
        }
        // No card for an output that merely doesn't look like a cable: unrecognized cable
        // drivers are a deliberate, saved choice, and the Devices page and Enable warn anyway.

        if (_settings.MonitorEnabled && !_isExternalMode
            && _settings.MusicMonitorDeviceId != null
            && SelectedDeviceId(MonitorDeviceCombo) != _settings.MusicMonitorDeviceId)
        {
            problems.Add(new Problem(
                $"Your monitoring device is not connected.{UsingInstead(MonitorDeviceCombo)}",
                "Devices…", DevicesPage));
        }

        if (_settings.SecondaryOutputEnabled)
        {
            if (SecondaryOutputCombo.SelectedItem is not AudioDeviceOption secondary)
            {
                problems.Add(new Problem(
                    "Secondary output is on, but no connected device is chosen for it. Routing will not start until you choose one or turn it off.",
                    "Secondary output…", SecondaryOutputPage));
            }
            else if (secondary.Id == SelectedDeviceId(OutputDeviceCombo))
            {
                problems.Add(new Problem(
                    "Secondary output uses the same device as the virtual cable, so the mix would play twice.",
                    "Secondary output…", SecondaryOutputPage));
            }
        }
    }

    private static string? SelectedDeviceId(System.Windows.Controls.ComboBox combo) => (combo.SelectedItem as AudioDeviceOption)?.Id;

    private static string UsingInstead(System.Windows.Controls.ComboBox combo) =>
        combo.SelectedItem is AudioDeviceOption standIn ? $" Using {standIn.FriendlyName} until it is back." : string.Empty;

    private void OnProblemActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Problem problem)
        {
            return;
        }

        if (problem.OpensSetupGuide)
        {
            ShowSetupGuide();
        }
        else
        {
            ShowSettings(problem.Page);
        }
    }

    private void ApplyHotkeyBinding(HotkeyBinding binding)
    {
        _hotkeyBinding = binding;
        _isCapturingHotkey = false;
        CancelPendingReleaseDelay();
        _hotkeyListener.UpdateBinding(binding);
        _settings.HotkeyId = binding.SerializedValue;
        ApplyEffectiveRoutingStates();
        UpdateHotkeyUi();
        OnConfigurationChanged();
    }

    private void UpdateHotkeyUi()
    {
        HotkeyValueText.Text = _hotkeyBinding.DisplayName;
        CaptureHotkeyButton.Content = _isCapturingHotkey ? "Press now..." : "Change";
        HotkeyCaptureHintText.Text = _isCapturingHotkey
            ? "Press any keyboard key or mouse button now."
            : "Click Change, then press any keyboard key or mouse button.";
    }

    private void ApplyHotkeyPressedState(bool isPressed)
    {
        bool hotkeyRelevant = !IsModdedMicSkipped || IsPushToTalk;

        if (!hotkeyRelevant || !_router.IsRouting)
        {
            CancelPendingReleaseDelay();
            ApplyEffectiveRoutingStates();
            return;
        }

        if (isPressed || _releaseDelayMilliseconds <= 0)
        {
            CancelPendingReleaseDelay();
        }
        else
        {
            // Released with a delay configured: keep the current state (modded mic
            // and/or open gate) until the timer runs out.
            _isReleaseDelayPending = true;
            _releaseDelayTimer.Stop();
            _releaseDelayTimer.Interval = TimeSpan.FromMilliseconds(_releaseDelayMilliseconds);
            _releaseDelayTimer.Start();
        }

        ApplyEffectiveRoutingStates();
        UpdateStatusText();
    }

    private void SetHotkeyMonitoringEnabled(bool isEnabled)
    {
        _hotkeyListener.SetMonitoringEnabled(isEnabled);

        if (!isEnabled)
        {
            return;
        }

        if (_hotkeyListener.IsPressed)
        {
            ApplyHotkeyPressedState(true);
        }
    }

    private void OnReleaseDelayTimerTick(object? sender, EventArgs e)
    {
        _releaseDelayTimer.Stop();
        _isReleaseDelayPending = false;
        ApplyEffectiveRoutingStates();
        UpdateStatusText();
    }

    private void CancelPendingReleaseDelay()
    {
        if (_releaseDelayTimer.IsEnabled)
        {
            _releaseDelayTimer.Stop();
        }

        _isReleaseDelayPending = false;
    }

    private void ApplyReleaseDelayFromTextBox()
    {
        if (!TryParseReleaseDelay(ReleaseDelayTextBox.Text, out int releaseDelayMilliseconds))
        {
            ReleaseDelayTextBox.Text = _releaseDelayMilliseconds.ToString(CultureInfo.InvariantCulture);
            UpdateStatusText();
            return;
        }

        _releaseDelayMilliseconds = ClampReleaseDelay(releaseDelayMilliseconds);
        _settings.ReleaseDelayMilliseconds = _releaseDelayMilliseconds;
        ReleaseDelayTextBox.Text = _releaseDelayMilliseconds.ToString(CultureInfo.InvariantCulture);

        if (_isReleaseDelayPending)
        {
            if (_releaseDelayMilliseconds <= 0)
            {
                _releaseDelayTimer.Stop();
                _isReleaseDelayPending = false;
                ApplyEffectiveRoutingStates();
            }
            else
            {
                _releaseDelayTimer.Stop();
                _releaseDelayTimer.Interval = TimeSpan.FromMilliseconds(_releaseDelayMilliseconds);
                _releaseDelayTimer.Start();
            }
        }

        OnConfigurationChanged();
    }

    /// <summary>True while the hotkey is held or its release delay is still running.</summary>
    private bool IsHotkeyEngaged()
    {
        return _hotkeyListener.IsPressed || _isReleaseDelayPending;
    }

    private static bool TryParseReleaseDelay(string? rawValue, out int releaseDelayMilliseconds)
    {
        return int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out releaseDelayMilliseconds)
            && releaseDelayMilliseconds >= 0;
    }

    private static int ClampReleaseDelay(int releaseDelayMilliseconds)
    {
        return Math.Clamp(releaseDelayMilliseconds, 0, MaxReleaseDelayMilliseconds);
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    // --- Music player ---

    /// <summary>
    /// Without monitor output the routing pull is the only clock; pause instead of
    /// leaving the engine in a frozen "playing" state that the UI reports as playing.
    /// </summary>
    private void PauseMusicIfClockLost()
    {
        if (!_music.IsPlaying || _music.HasMonitorOutput || _router.IsRouting)
        {
            return;
        }

        _music.Pause();
        _musicWasAutoPaused = true;

        if (_music.CurrentTrackPath is string pausedPath)
        {
            MusicStatusText.Text = $"Paused: {Path.GetFileNameWithoutExtension(pausedPath)} — playback resumes when routing is active.";
        }

        UpdateMusicUi();
    }

    /// <summary>Resumes a track that was auto-paused by <see cref="PauseMusicIfClockLost"/> once a clock is back.</summary>
    private void ResumeMusicIfAutoPaused()
    {
        if (!_musicWasAutoPaused || !_music.IsPaused)
        {
            return;
        }

        if (!EvaluatePlaybackGate().CanStart)
        {
            return;
        }

        _musicWasAutoPaused = false;
        _music.Resume();

        if (_music.CurrentTrackPath is string resumedPath)
        {
            MusicStatusText.Text = $"Playing: {Path.GetFileNameWithoutExtension(resumedPath)}";
        }

        UpdateMusicUi();
    }

    private void OnMusicTimerTick(object? sender, EventArgs e)
    {
        UpdateMusicUi();
    }

    private void UpdateMusicUi()
    {
        UpdateMusicRoutingWarning();
        _overlayIndicator?.SetMusicState(ComputeOverlayMusicState());
        PublishObsOverlayState();

        if (_isExternalMode)
        {
            // Transport steers the external app via media keys; there is no local
            // play state or position to reflect.
            PlayPauseIcon.Data = (Geometry)FindResource("PlayPauseGlyph");
            return;
        }

        var duration = _music.Duration;
        var position = _music.Position;

        _isUpdatingMusicUi = true;
        try
        {
            if (!_isSeekDragging)
            {
                SeekSlider.Maximum = Math.Max(duration.TotalSeconds, 1d);
                SeekSlider.Value = Math.Min(position.TotalSeconds, SeekSlider.Maximum);
            }
        }
        finally
        {
            _isUpdatingMusicUi = false;
        }

        TrackTimeText.Text = $"{FormatTrackTime(position)} / {FormatTrackTime(duration)}";
        PlayPauseIcon.Data = (Geometry)FindResource(_music.IsPlaying ? "PauseIcon" : "PlayIcon");

        string? playingPath = _music.CurrentTrackPath;
        TrackItem? playingTrack = playingPath != null
            ? _trackByPath.GetValueOrDefault(playingPath)
            : null;

        if (!ReferenceEquals(playingTrack, _playingTrackItem))
        {
            _playingTrackItem?.SetIsPlaying(false);
            playingTrack?.SetIsPlaying(true);
            _playingTrackItem = playingTrack;
        }
    }

    /// <summary>
    /// Local monitoring and external apps can remain audible while routing is
    /// stopped. Keep that distinction visible next to the transport controls.
    /// </summary>
    private void UpdateMusicRoutingWarning()
    {
        bool hasActiveMusic = _isExternalMode
            ? _appCapture != null
            : _music.IsPlaying;

        MusicRoutingWarning.Visibility = hasActiveMusic && !_router.IsRouting
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatTrackTime(TimeSpan time)
    {
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private void RefreshPlaylist(string? selectPath)
    {
        try
        {
            string? current = selectPath ?? (PlaylistListBox.SelectedItem as TrackItem)?.Path;
            bool showFolderBadges = _playlist.Folders.Count > 1;
            _allTracks = _playlist.GetTracks()
                .Select(file => new TrackItem(
                    file.Path,
                    Path.GetFileNameWithoutExtension(file.Path),
                    _folderInfoByPath.GetValueOrDefault(file.Folder),
                    showFolderBadges))
                .ToList();
            _session.SetLibrary(_allTracks.Select(track => track.Path));
            _libraryVersion = RemoteId.VersionForPaths(_allTracks.Select(track => track.Path));

            _playingTrackItem = null;
            _trackByPath.Clear();
            foreach (var track in _allTracks)
            {
                _trackByPath.TryAdd(track.Path, track);
            }

            ApplyPlaylistFilter();
            UpdateQueueUi();

            if (current != null && PlaylistListBox.ItemsSource is List<TrackItem> visible)
            {
                PlaylistListBox.SelectedItem = visible.FirstOrDefault(track =>
                    string.Equals(track.Path, current, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load playlist.");
            MusicStatusText.Text = $"Could not read the music folder: {ex.Message}";
        }
    }

    private void ApplyPlaylistFilter()
    {
        string filter = PlaylistFilterBox.Text.Trim();
        string? selected = (PlaylistListBox.SelectedItem as TrackItem)?.Path;

        var activeFolders = _folderChips
            .Where(chip => chip.IsActive)
            .Select(chip => chip.Info.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<TrackItem> filtered = _allTracks;

        if (activeFolders.Count > 0)
        {
            filtered = filtered.Where(track => track.FolderPath != null && activeFolders.Contains(track.FolderPath));
        }

        if (filter.Length > 0)
        {
            filtered = filtered.Where(track => track.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var visible = ReferenceEquals(filtered, _allTracks) ? _allTracks : filtered.ToList();

        PlaylistListBox.ItemsSource = visible;

        if (selected != null)
        {
            PlaylistListBox.SelectedItem = visible.FirstOrDefault(track =>
                string.Equals(track.Path, selected, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void OnPlaylistFilterChanged(object sender, TextChangedEventArgs e)
    {
        PlaylistFilterHintText.Visibility = string.IsNullOrEmpty(PlaylistFilterBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        string text = PlaylistFilterBox.Text.Trim();
        if (LooksLikeWebUrl(text))
        {
            // A pasted link belongs in the download field — move it there and start.
            PlaylistFilterBox.Text = "";
            YoutubeUrlBox.Text = text;
            MusicStatusText.Text = "That looks like a link — starting the download.";
            _ = StartDownloadAsync();
            return;
        }

        ApplyPlaylistFilter();
    }

    private void OnYoutubeUrlChanged(object sender, TextChangedEventArgs e)
    {
        YoutubeUrlHintText.Visibility = string.IsNullOrEmpty(YoutubeUrlBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool LooksLikeWebUrl(string text)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private bool PlayTrack(string path)
    {
        CancelDelayedStart(null);

        PlaybackGate gate = EvaluatePlaybackGate(hasExplicitTrack: true);
        if (!gate.CanStart)
        {
            MusicStatusText.Text = PlaybackBlockedStatus(gate.BlockedReason);
            return false;
        }

        try
        {
            _music.Play(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to play track {TrackPath}.", path);
            MusicStatusText.Text = $"Could not play the track: {ex.Message}";
            return false;
        }

        _session.LastPlayedTrackPath = path;
        _musicWasAutoPaused = false;
        MusicStatusText.Text = $"Playing: {Path.GetFileNameWithoutExtension(path)}";

        if (PlaylistListBox.ItemsSource is List<TrackItem> tracks)
        {
            PlaylistListBox.SelectedItem = tracks.FirstOrDefault(track =>
                string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        UpdateMusicUi();
        return true;
    }

    private void PlayNextTrack()
    {
        string? queued = _session.ConsumeNextQueuedTrack();
        UpdateQueueUi();

        if (queued != null)
        {
            PlayTrack(queued);
            return;
        }

        // Advance within the full playlist — the filter box is a search tool,
        // not a play scope.
        if (_session.LibraryCount == 0)
        {
            _music.Stop();
            UpdateMusicUi();
            return;
        }

        if (_session.FindNextLibraryTrack(_music.CurrentTrackPath) is string next)
        {
            PlayTrack(next);
        }
        else
        {
            _music.Stop();
            MusicStatusText.Text = "The playlist has ended.";
            UpdateMusicUi();
        }
    }

    private void UpdateQueueUi()
    {
        if (_session.Queue.Count > 0 && !_isExternalMode)
        {
            QueueChipBtn.Content = $"Queue: {_session.Queue.Count}";
            QueueChipBtn.Visibility = Visibility.Visible;
            ClearQueueBtn.Visibility = Visibility.Visible;
        }
        else
        {
            QueueChipBtn.Visibility = Visibility.Collapsed;
            QueueChipBtn.IsChecked = false;
            ClearQueueBtn.Visibility = Visibility.Collapsed;
        }

        QueueListBox.ItemsSource = _session.Queue
            .Select((path, index) => new QueueEntry(index, path))
            .ToList();

        foreach (var track in _allTracks)
        {
            track.SetQueuePositions(_session.GetQueuePositions(track.Path));
        }
    }

    private void OnClearQueueClick(object sender, RoutedEventArgs e)
    {
        _session.ClearQueue();
        UpdateQueueUi();
        MusicStatusText.Text = "Queue cleared.";
    }

    // --- Queue popup: reorder via drag & drop ---

    private void OnQueueChipChecked(object sender, RoutedEventArgs e)
    {
        // Clicking the chip while the popup is open closes the popup on mouse-down
        // (StaysOpen=False) and the click would immediately re-open it — swallow that.
        if (DateTime.UtcNow - _queuePopupClosedAt < TimeSpan.FromMilliseconds(250))
        {
            QueueChipBtn.IsChecked = false;
        }
    }

    private void OnQueuePopupClosed(object? sender, EventArgs e)
    {
        _queuePopupClosedAt = DateTime.UtcNow;
        _queueDragIndex = -1;
        ClearQueueDropIndicators();
    }

    private void OnQueueListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _queueDragStart = e.GetPosition(QueueListBox);
        _queueDragIndex = -1;

        // A press on the remove button is a click, not the start of a drag.
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject, QueueListBox) != null)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject, QueueListBox);
        if (item?.DataContext is QueueEntry entry)
        {
            _queueDragIndex = entry.Index;
        }
    }

    private void OnQueueListMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_queueDragIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(QueueListBox);
        if (Math.Abs(position.X - _queueDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _queueDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        int dragIndex = _queueDragIndex;
        _queueDragIndex = -1;

        // StaysOpen=False closes the popup when it loses mouse capture, which the
        // drag loop takes — pin it open for the duration of the drag.
        QueuePopup.StaysOpen = true;
        try
        {
            DragDrop.DoDragDrop(QueueListBox, dragIndex, System.Windows.DragDropEffects.Move);
        }
        finally
        {
            QueuePopup.StaysOpen = false;
            ClearQueueDropIndicators();
        }
    }

    private void OnQueueListDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(int)))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
        ShowQueueDropIndicator(ComputeQueueInsertIndex(e));
    }

    private void OnQueueListDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ClearQueueDropIndicators();
    }

    private void OnQueueListDrop(object sender, System.Windows.DragEventArgs e)
    {
        ClearQueueDropIndicators();

        if (e.Data.GetData(typeof(int)) is not int fromIndex ||
            fromIndex < 0 || fromIndex >= _session.Queue.Count)
        {
            return;
        }

        int insertIndex = ComputeQueueInsertIndex(e);
        if (insertIndex > fromIndex)
        {
            insertIndex--;
        }

        insertIndex = Math.Clamp(insertIndex, 0, _session.Queue.Count - 1);
        if (insertIndex == fromIndex)
        {
            return;
        }

        _session.MoveQueueItem(fromIndex, insertIndex);
        UpdateQueueUi();
        e.Handled = true;
    }

    private void OnQueueListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject, QueueListBox) != null)
        {
            return;
        }

        if (QueueListBox.SelectedItem is not QueueEntry entry || !IsQueueEntryCurrent(entry))
        {
            return;
        }

        _session.RemoveQueueItemAt(entry.Index);
        UpdateQueueUi();

        if (File.Exists(entry.Path))
        {
            PlayTrack(entry.Path);
        }
        else
        {
            MusicStatusText.Text = "The file no longer exists.";
        }
    }

    private void OnQueueRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not QueueEntry entry || !IsQueueEntryCurrent(entry))
        {
            return;
        }

        _session.RemoveQueueItemAt(entry.Index);
        UpdateQueueUi();
    }

    /// <summary>Entries are rebuilt on every queue change; reject any that got stale anyway.</summary>
    private bool IsQueueEntryCurrent(QueueEntry entry)
    {
        return entry.Index >= 0
            && entry.Index < _session.Queue.Count
            && string.Equals(_session.Queue[entry.Index], entry.Path, StringComparison.OrdinalIgnoreCase);
    }

    private int ComputeQueueInsertIndex(System.Windows.DragEventArgs e)
    {
        for (int i = 0; i < QueueListBox.Items.Count; i++)
        {
            if (QueueListBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
            {
                continue;
            }

            var position = e.GetPosition(container);
            if (position.Y < 0)
            {
                return i;
            }

            if (position.Y <= container.ActualHeight)
            {
                return position.Y < container.ActualHeight / 2 ? i : i + 1;
            }
        }

        return QueueListBox.Items.Count;
    }

    private void ShowQueueDropIndicator(int insertIndex)
    {
        if (QueueListBox.ItemsSource is not List<QueueEntry> entries || entries.Count == 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            entry.SetDropIndicator(above: false, below: false);
        }

        if (insertIndex < entries.Count)
        {
            entries[insertIndex].SetDropIndicator(above: true, below: false);
        }
        else
        {
            entries[^1].SetDropIndicator(above: false, below: true);
        }
    }

    private void ClearQueueDropIndicators()
    {
        if (QueueListBox.ItemsSource is not List<QueueEntry> entries)
        {
            return;
        }

        foreach (var entry in entries)
        {
            entry.SetDropIndicator(above: false, below: false);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node, DependencyObject stopAt) where T : DependencyObject
    {
        while (node != null && node != stopAt)
        {
            if (node is T match)
            {
                return match;
            }

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnMusicTrackEnded(object? sender, string endedPath)
    {
        // Capture the mode together with the end notification. The UI may toggle
        // it again before the dispatcher gets around to processing this event.
        SingleTrackPlayMode modeWhenTrackEnded = _session.SingleTrackMode;

        Dispatcher.BeginInvoke(() =>
        {
            // The event is queued from an audio thread; the session drops it as
            // stale when the user started another track in the meantime, so we
            // never stop or advance from the wrong song.
            TrackEndAction action = _session.OnTrackEnded(
                endedPath, modeWhenTrackEnded, _music.HasTrack, _isExternalMode);

            if (action == TrackEndAction.Ignore)
            {
                return;
            }

            // Single-track mode: the engine already stopped by itself when the file
            // ran out — just reflect that instead of advancing. The queue stays
            // untouched; the next/play buttons resume it on explicit request.
            if (action == TrackEndAction.StopSingleTrack)
            {
                string finished = $"Finished: {Path.GetFileNameWithoutExtension(endedPath)}";

                if (modeWhenTrackEnded == SingleTrackPlayMode.Once)
                {
                    SetSingleTrackMode(SingleTrackPlayMode.Off, announce: false);
                    MusicStatusText.Text = $"{finished} — stopped. Single-track mode was turned off.";
                }
                else
                {
                    MusicStatusText.Text = $"{finished} — stopped (single-track mode).";
                }

                UpdateMusicUi();
                return;
            }

            PlayNextTrack();
        });
    }

    private void OnMusicEngineError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // The engine may have dropped its monitor output (e.g. device lost);
            // pause first so the error message below wins over the pause text.
            PauseMusicIfClockLost();
            MusicStatusText.Text = message;
            UpdateMusicUi();
        });
    }

    private void OnPlaylistDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistListBox.SelectedItem is TrackItem track)
        {
            PlayTrack(track.Path);
        }
    }

    private void OnQueueClick(object sender, RoutedEventArgs e)
    {
        if (PlaylistListBox.SelectedItem is not TrackItem track)
        {
            MusicStatusText.Text = "Select a track in the list to add to the queue.";
            return;
        }

        _session.Enqueue(track.Path);
        UpdateQueueUi();
        MusicStatusText.Text = $"Added to queue: {track.Name}";
    }

    private void OnPlaylistRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshPlaylist(null);
    }

    private void OnOpenMusicFolderClick(object sender, RoutedEventArgs e)
    {
        if (_playlist.Folders.Count == 1)
        {
            OpenMusicFolder(_playlist.Folders[0]);
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = OpenMusicFolderBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        foreach (string folder in _playlist.Folders)
        {
            string captured = folder;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = new TextBlock { Text = GetFolderMenuLabel(folder) },
                Icon = CreateFolderBadge(_folderInfoByPath.GetValueOrDefault(folder)),
                ToolTip = folder
            };
            item.Click += (_, _) => OpenMusicFolder(captured);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void OpenMusicFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open music folder {Folder}.", folder);
            MusicStatusText.Text = $"Could not open the folder: {ex.Message}";
        }
    }

    private void OnAddMusicFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder containing MP3 files"
        };

        if (dialog.ShowDialog(Window.GetWindow(MusicFoldersList)) != true)
        {
            return;
        }

        if (_playlist.AddFolder(dialog.FolderName))
        {
            OnMusicFoldersChanged($"Added music folder: {dialog.FolderName}");
        }
        else
        {
            MusicStatusText.Text = "The folder is already in the list.";
        }
    }

    private void OnRemoveMusicFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderInfo folder && _playlist.RemoveFolder(folder.Path))
        {
            OnMusicFoldersChanged($"Removed music folder: {folder.Path}");
        }
    }

    private void OnResetMusicFoldersClick(object sender, RoutedEventArgs e)
    {
        _playlist.SetFolders(null);
        OnMusicFoldersChanged("Using the default music folder again.");
    }

    private void OnMusicFoldersChanged(string statusMessage)
    {
        RefreshMusicFolderUi();
        _settings.MusicFolderPaths = _playlist.Folders.ToList();
        // Retain the legacy first-custom-folder field for downgrade compatibility.
        _settings.MusicFolderPath = _playlist.Folders.FirstOrDefault(
            folder => !PlaylistManager.IsDefaultFolder(folder));
        SaveSettings();
        RefreshPlaylist(null);
        MusicStatusText.Text = statusMessage;
    }

    private string GetFolderMenuLabel(string folder)
    {
        return PlaylistManager.IsDefaultFolder(folder) ? $"Standard: {folder}" : folder;
    }

    /// <summary>Small colored letter badge matching the playlist badges, for menu icons.</summary>
    private static UIElement? CreateFolderBadge(FolderInfo? info)
    {
        if (info == null)
        {
            return null;
        }

        return new Border
        {
            Background = info.Tint,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 1, 5, 1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = info.Letter,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = info.Accent
            }
        };
    }

    /// <summary>
    /// Rebuilds everything derived from the folder list: badge colors, the filter
    /// chips, and the download-target selector. Call before RefreshPlaylist.
    /// </summary>
    private void RefreshMusicFolderUi()
    {
        var folders = _playlist.Folders;
        var infos = new List<FolderInfo>(folders.Count);
        _folderInfoByPath = new Dictionary<string, FolderInfo>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < folders.Count; i++)
        {
            var (accent, tint) = FolderPalette[i % FolderPalette.Length];
            var info = new FolderInfo(folders[i], accent, tint);
            infos.Add(info);
            _folderInfoByPath[info.Path] = info;
        }

        bool multiple = infos.Count > 1;

        // Filter chips — keep an existing folder filter across list changes.
        var previouslyActive = _folderChips
            .Where(chip => chip.IsActive)
            .Select(chip => chip.Info.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _folderChips = infos
            .Select(info => new FolderChipItem(info) { IsActive = multiple && previouslyActive.Contains(info.Path) })
            .ToList();
        UpdateFolderChipVisuals();
        FolderChipsPanel.ItemsSource = _folderChips;
        FolderChipsPanel.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;

        // Keep the activation order aligned with the rebuilt chips (a removed
        // folder must not linger as the download target).
        _folderChipActivationOrder.RemoveAll(path =>
            !_folderChips.Any(chip => chip.IsActive && string.Equals(chip.Info.Path, path, StringComparison.OrdinalIgnoreCase)));

        // Download target — only a visible choice when there is more than one folder.
        if (_settings.DownloadFolderPath == null || !_folderInfoByPath.ContainsKey(_settings.DownloadFolderPath))
        {
            _settings.DownloadFolderPath = infos[0].Path;
        }

        _isUpdatingMusicUi = true;
        try
        {
            DownloadFolderCombo.ItemsSource = infos;
        }
        finally
        {
            _isUpdatingMusicUi = false;
        }

        SyncDownloadFolderToFilter(announce: false);
        DownloadFolderCombo.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;

        MusicFoldersList.ItemsSource = infos;
        // Read by the Remove buttons: the last folder cannot be removed.
        MusicFoldersList.Tag = multiple;
        ResetMusicFoldersButton.Visibility = _playlist.UsesOnlyDefaultFolder ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFolderChipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderChipItem chip)
        {
            _folderChipActivationOrder.RemoveAll(path =>
                string.Equals(path, chip.Info.Path, StringComparison.OrdinalIgnoreCase));

            if (chip.IsActive)
            {
                _folderChipActivationOrder.Add(chip.Info.Path);
            }
        }

        UpdateFolderChipVisuals();
        ApplyPlaylistFilter();
        SyncDownloadFolderToFilter(announce: true);
    }

    /// <summary>
    /// Points the download combo at the filtered folder: an active chip becomes
    /// the download target (the most recently activated one when several are on),
    /// and clearing the filter returns to the user's own combo choice. The
    /// override never touches <see cref="AppSettings.DownloadFolderPath"/>, so
    /// the stored preference survives the filter round trip.
    /// </summary>
    private void SyncDownloadFolderToFilter(bool announce)
    {
        if (DownloadFolderCombo.ItemsSource is not List<FolderInfo> infos || infos.Count == 0)
        {
            return;
        }

        string? filterPath = _folderChipActivationOrder.Count > 0 ? _folderChipActivationOrder[^1] : null;
        string targetPath = filterPath ?? _settings.DownloadFolderPath ?? infos[0].Path;

        var target = infos.FirstOrDefault(info => string.Equals(info.Path, targetPath, StringComparison.OrdinalIgnoreCase))
            ?? infos[0];

        if (ReferenceEquals(DownloadFolderCombo.SelectedItem, target))
        {
            return;
        }

        _isUpdatingMusicUi = true;
        try
        {
            DownloadFolderCombo.SelectedItem = target;
        }
        finally
        {
            _isUpdatingMusicUi = false;
        }

        if (announce)
        {
            MusicStatusText.Text = filterPath != null
                ? $"New tracks are downloaded to {target.DisplayName} while the folder filter is active."
                : $"New tracks are downloaded to {target.DisplayName} again.";
        }
    }

    private void UpdateFolderChipVisuals()
    {
        bool anyActive = _folderChips.Any(chip => chip.IsActive);
        foreach (var chip in _folderChips)
        {
            chip.SetDimmed(anyActive && !chip.IsActive);
        }
    }

    private void OnDownloadFolderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        // A manual pick is the user's real preference; filter-driven changes go
        // through SyncDownloadFolderToFilter and never reach this handler.
        if ((DownloadFolderCombo.SelectedItem as FolderInfo)?.Path is string pickedPath)
        {
            _settings.DownloadFolderPath = pickedPath;
        }

        SaveSettings();

        if (DownloadFolderCombo.SelectedItem is FolderInfo info)
        {
            MusicStatusText.Text = $"New tracks are downloaded to: {info.Path}";
        }
    }

    // --- External app capture ---

    private static readonly System.Windows.Media.Brush CaptureIdleBrush = CreateFrozenBrush(0x9C, 0xA3, 0xAF);

    // The capture toggle deliberately uses the music panel's purple accent (never the
    // routing button's dark navy) so "stop capturing app audio" can't be mistaken for
    // "stop routing" — field testers mixed the two up when both were dark "Stop".
    private static readonly System.Windows.Media.Brush CaptureAccentBrush = CreateFrozenBrush(0x7C, 0x3A, 0xED);
    private static readonly System.Windows.Media.Brush CaptureAccentTintBrush = CreateFrozenBrush(0xED, 0xE9, 0xFE);
    private static readonly System.Windows.Media.Brush CaptureAccentTintBorderBrush = CreateFrozenBrush(0xDD, 0xD6, 0xFE);

    private const string CaptureIdleHint =
        "Select the app playing music and click Capture audio. Control playback in the app as usual — its audio is mixed into the mic channel.";

    private static System.Windows.Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnMusicModeChanged(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent (before ExternalModeRadio exists) and
        // during programmatic restore; both must not re-enter the switch logic.
        if (_isUpdatingMusicUi || ExternalModeRadio == null || LibraryModeRadio == null)
        {
            return;
        }

        bool external = ExternalModeRadio.IsChecked == true;
        if (external == _isExternalMode)
        {
            return;
        }

        _isExternalMode = external;
        _settings.ExternalCaptureMode = external;
        CancelDelayedStart(null);

        if (external)
        {
            // Local playback cannot follow into app mode; stop it cleanly.
            _music.Stop();
            _musicWasAutoPaused = false;
            MusicStatusText.Text = "Select the app playing music and start audio capture.";
            _ = RefreshAudioAppsAsync();
        }
        else
        {
            StopAppCapture();
            MusicStatusText.Text = "Double-click a track to play it. Music is mixed with the mic while routing is active.";
        }

        ApplyMusicModeUi();
        _ = ApplyMonitorConfigAsync();
        SaveSettings();
        UpdateMusicUi();
        UpdateStatusText();
    }

    private void ApplyMusicModeUi()
    {
        bool external = _isExternalMode;
        var libraryVisibility = external ? Visibility.Collapsed : Visibility.Visible;

        YoutubeRow.Visibility = libraryVisibility;
        SearchRow.Visibility = libraryVisibility;
        PlaylistRow.Visibility = libraryVisibility;
        ExternalPanel.Visibility = external ? Visibility.Visible : Visibility.Collapsed;

        if (external)
        {
            DownloadStatusRow.Visibility = Visibility.Collapsed;
            QueuePopup.IsOpen = false;
        }
        else if (_isDownloading)
        {
            DownloadStatusRow.Visibility = Visibility.Visible;
        }

        SeekSlider.Visibility = libraryVisibility;
        TrackTimeText.Visibility = libraryVisibility;
        ExternalTransportHint.Visibility = external ? Visibility.Visible : Visibility.Collapsed;

        // Monitoring is pointless in external mode (the user hears the app directly),
        // so render it unchecked and disabled. The settings state itself is untouched,
        // and therefore restores the preference when returning to library mode.
        _isUpdatingMusicUi = true;
        MonitorEnabledCheck.IsChecked = !external && _settings.MonitorEnabled;
        _isUpdatingMusicUi = false;
        MonitorEnabledCheck.IsEnabled = !external;
        MonitorDeviceCombo.IsEnabled = !external;
        MonitorVolumeSlider.IsEnabled = !external;
        VolumeLinkToggle.IsEnabled = !external;

        // Single-track mode only applies to local playback — in app mode the
        // external app decides what plays next. Delayed start stays available
        // (it just sends play to the app after the countdown).
        SingleTrackBtn.IsEnabled = !external;

        UpdateMusicRouteHint();
        UpdateQueueUi();
        UpdateCaptureUi();
    }

    private void OnRefreshAudioAppsClick(object sender, RoutedEventArgs e)
    {
        if (_appCapture != null || _isCaptureStarting)
        {
            return;
        }

        _ = RefreshAudioAppsAsync();
    }

    private async Task RefreshAudioAppsAsync()
    {
        IReadOnlyList<AudioAppOption> apps;
        try
        {
            apps = await Task.Run(AudioAppEnumerator.GetAudioApps);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enumerate audio apps.");
            MusicStatusText.Text = $"Could not list apps with audio: {ex.Message}";
            return;
        }

        string? preferredName = (ExternalAppCombo.SelectedItem as AudioAppOption)?.ProcessName
            ?? _settings.ExternalAppName;

        _isUpdatingMusicUi = true;
        try
        {
            ExternalAppCombo.ItemsSource = apps;
            ExternalAppCombo.SelectedItem =
                apps.FirstOrDefault(app => string.Equals(app.ProcessName, preferredName, StringComparison.OrdinalIgnoreCase))
                ?? apps.FirstOrDefault(app => app.ProcessName.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                ?? apps.FirstOrDefault(app => app.IsPlaying)
                ?? apps.FirstOrDefault();
        }
        finally
        {
            _isUpdatingMusicUi = false;
        }

        if (apps.Count == 0)
        {
            MusicStatusText.Text = "No audio session was found. Play something in the app for a few seconds, then click Refresh.";
        }
    }

    private void OnExternalAppSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMusicUi
            || ExternalAppCombo.SelectedItem is not AudioAppOption app
            || string.Equals(_settings.ExternalAppName, app.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.ExternalAppName = app.ProcessName;
        SaveSettings();
    }

    private async void OnCaptureToggleClick(object sender, RoutedEventArgs e)
    {
        if (_isCaptureStarting)
        {
            return;
        }

        if (_appCapture != null)
        {
            StopAppCapture();
            MusicStatusText.Text = "Audio capture stopped.";
            return;
        }

        if (!ProcessLoopbackCapture.IsSupported)
        {
            MusicStatusText.Text = "Per-app audio capture requires Windows 10 version 2004 or later.";
            return;
        }

        if (ExternalAppCombo.SelectedItem is not AudioAppOption app)
        {
            MusicStatusText.Text = "First select the app playing music.";
            return;
        }

        _isCaptureStarting = true;
        UpdateCaptureUi();

        var capture = new ProcessLoopbackCapture(app.ProcessId);
        capture.Error += OnCaptureError;

        try
        {
            await Task.Run(capture.Start);

            // The user may have switched back to library mode while the capture
            // was starting; don't hijack the engine's playback chain in that case.
            if (!_isExternalMode)
            {
                capture.Error -= OnCaptureError;
                capture.Dispose();
                return;
            }

            _music.SetExternalSource(capture.SampleProvider!);
            _appCapture = capture;
            _captureTarget = app;
            _lastExternalCaptureRouteState = null;
            _settings.ExternalAppName = app.ProcessName;
            SaveSettings();
            UpdateExternalCaptureStatusText();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start process loopback capture for {App} (PID {ProcessId}).", app.DisplayName, app.ProcessId);
            capture.Error -= OnCaptureError;
            capture.Dispose();
            MusicStatusText.Text = $"Could not capture audio from {app.DisplayName}: {ex.Message}";
        }
        finally
        {
            _isCaptureStarting = false;
            UpdateCaptureUi();
        }
    }

    /// <summary>
    /// Keeps capture status honest about both incoming samples and the complete
    /// route to the virtual mic. Starting the Windows capture client alone does
    /// not prove that the selected app is currently producing audio.
    /// </summary>
    private void UpdateExternalCaptureStatusText()
    {
        if (!_isExternalMode || _appCapture == null || _captureTarget is not { } target)
        {
            return;
        }

        bool hasAudioSignal = _externalSignalActivity.IsActive;
        ExternalCaptureRouteState state = ExternalCaptureRoute.Evaluate(
            hasAudioSignal,
            _router.IsRouting,
            _router.MusicMonitorOnly,
            _router.MusicRouteOpen);

        CaptureStateText.Text = state switch
        {
            ExternalCaptureRouteState.WaitingForAudio => $"Waiting for audio from {target.DisplayName}",
            ExternalCaptureRouteState.RoutingStopped => "Audio detected — routing is off",
            ExternalCaptureRouteState.MonitorOnly => "Audio detected — monitor only",
            ExternalCaptureRouteState.BlockedByPushToTalk => "Audio detected — blocked by push-to-talk",
            _ => $"{target.DisplayName} is being sent to the virtual mic"
        };

        CaptureHintText.Text = state switch
        {
            ExternalCaptureRouteState.WaitingForAudio =>
                "Start playback in the app. If the meter does not move, play for a few seconds, refresh the app list, and select the app again.",
            ExternalCaptureRouteState.RoutingStopped =>
                "Enable routing with the button in the top right to send audio to the mic channel.",
            ExternalCaptureRouteState.MonitorOnly =>
                "Turn off Monitor only to send music to the mic channel.",
            ExternalCaptureRouteState.BlockedByPushToTalk =>
                $"Hold {_hotkeyBinding.DisplayName}, or enable Music ignores push-to-talk, to let others hear the music.",
            _ => "Audio is being received and routed to the virtual mic."
        };

        CaptureStateIcon.Data = (Geometry)FindResource(state == ExternalCaptureRouteState.Sending
            ? "CheckCircleIcon"
            : state == ExternalCaptureRouteState.WaitingForAudio
                ? "CircleOffIcon"
                : "InfoIcon");
        CaptureStateIcon.Fill = state == ExternalCaptureRouteState.Sending
            ? StatusTheme.LiveInkBrush
            : state == ExternalCaptureRouteState.WaitingForAudio
                ? CaptureIdleBrush
                : StatusTheme.MutedBrush;

        if (_lastExternalCaptureRouteState == state)
        {
            return;
        }

        _lastExternalCaptureRouteState = state;
        Log.Information(
            "External capture route state changed. App={App} ProcessId={ProcessId} State={State} Routing={Routing} MonitorOnly={MonitorOnly} MusicRouteOpen={MusicRouteOpen}",
            target.DisplayName,
            target.ProcessId,
            state,
            _router.IsRouting,
            _router.MusicMonitorOnly,
            _router.MusicRouteOpen);
        MusicStatusText.Text = state switch
        {
            ExternalCaptureRouteState.WaitingForAudio => $"Waiting for audio from {target.DisplayName}.",
            ExternalCaptureRouteState.RoutingStopped => "App audio is being received — enable routing so others can hear it.",
            ExternalCaptureRouteState.MonitorOnly => "App audio is being received, but Monitor only is active.",
            ExternalCaptureRouteState.BlockedByPushToTalk =>
                $"App audio is being received but blocked by push-to-talk — hold {_hotkeyBinding.DisplayName} or let music ignore push-to-talk.",
            _ => $"{target.DisplayName} is being sent to the virtual mic."
        };
    }

    private void StopAppCapture()
    {
        var capture = _appCapture;
        _appCapture = null;
        _captureTarget = null;
        _lastExternalCaptureRouteState = null;
        _externalSignalActivity.Reset();

        if (capture != null)
        {
            _music.ClearExternalSource();
            capture.Error -= OnCaptureError;
            capture.Dispose();
        }

        UpdateCaptureUi();
    }

    private void OnCaptureError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _appCapture))
            {
                return;
            }

            StopAppCapture();
            MusicStatusText.Text = message;
        });
    }

    private void UpdateCaptureUi()
    {
        bool capturing = _appCapture != null;

        CaptureToggleBtn.IsEnabled = !_isCaptureStarting;
        ExternalAppCombo.IsEnabled = !capturing && !_isCaptureStarting;

        if (_isCaptureStarting)
        {
            CaptureToggleText.Text = "Starting…";
            CaptureStateText.Text = "Starting audio capture…";
            return;
        }

        if (capturing)
        {
            // Light purple "quiet" state while active: clearly not the dark routing button.
            CaptureToggleText.Text = "Stop capture";
            CaptureToggleIcon.Data = (Geometry)FindResource("StopIcon");
            CaptureToggleIcon.Fill = CaptureAccentBrush;
            CaptureToggleBtn.Background = CaptureAccentTintBrush;
            CaptureToggleBtn.BorderBrush = CaptureAccentTintBorderBrush;
            CaptureToggleBtn.Foreground = CaptureAccentBrush;
            UpdateExternalCaptureStatusText();
        }
        else
        {
            CaptureToggleText.Text = "Capture audio";
            CaptureToggleIcon.Data = (Geometry)FindResource("PlayIcon");
            CaptureToggleIcon.Fill = System.Windows.Media.Brushes.White;
            CaptureToggleBtn.Background = CaptureAccentBrush;
            CaptureToggleBtn.BorderBrush = CaptureAccentBrush;
            CaptureToggleBtn.Foreground = System.Windows.Media.Brushes.White;
            CaptureStateIcon.Data = (Geometry)FindResource("CircleOffIcon");
            CaptureStateIcon.Fill = CaptureIdleBrush;
            CaptureStateText.Text = "No audio capture";
            CaptureHintText.Text = CaptureIdleHint;
            CaptureLevelMeter.Value = 0;
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_isExternalMode)
        {
            CancelDelayedStart(null);
            MediaKeySender.SendPlayPause();
            return;
        }

        _ = RemoteTogglePlayPause();
    }

    private void OnStopPlaybackClick(object sender, RoutedEventArgs e)
    {
        if (_isExternalMode)
        {
            CancelDelayedStart(null);
            MediaKeySender.SendStop();
            MusicStatusText.Text = "Sent Stop to the app that is playing.";
            return;
        }

        _ = RemoteStop();
    }

    /// <summary>
    /// Starts playback exactly like the play button does when nothing is playing:
    /// resume a paused track, otherwise play the selected (or first) track.
    /// Shared by the play button and the delayed-start countdown.
    /// </summary>
    private bool StartPlaybackFromCurrentState()
    {
        PlaybackGate gate = EvaluatePlaybackGate();
        if (!gate.CanStart)
        {
            MusicStatusText.Text = PlaybackBlockedStatus(gate.BlockedReason);
            return false;
        }

        if (_music.IsPaused)
        {
            _music.Resume();
            _musicWasAutoPaused = false;
            if (_music.CurrentTrackPath is string resumedPath)
            {
                MusicStatusText.Text = $"Playing: {Path.GetFileNameWithoutExtension(resumedPath)}";
            }

            UpdateMusicUi();
            return true;
        }

        if (PlaylistListBox.SelectedItem is TrackItem selected)
        {
            return PlayTrack(selected.Path);
        }
        else if (_allTracks.Count > 0)
        {
            return PlayTrack(_allTracks[0].Path);
        }
        return false;
    }

    private void OnPrevTrackClick(object sender, RoutedEventArgs e)
    {
        if (_isExternalMode)
        {
            CancelDelayedStart(null);
            MediaKeySender.SendPreviousTrack();
            return;
        }

        _ = RemotePrevious();
    }

    private void OnNextTrackClick(object sender, RoutedEventArgs e)
    {
        if (_isExternalMode)
        {
            CancelDelayedStart(null);
            MediaKeySender.SendNextTrack();
            return;
        }

        _ = RemoteNext();
    }

    // --- Delayed start & single-track mode ---

    private static readonly System.Windows.Media.Brush TransportIdleBrush = CreateFrozenBrush(0xE8, 0xEE, 0xF5);
    private static readonly System.Windows.Media.Brush TransportIdleBorderBrush = CreateFrozenBrush(0xD7, 0xDE, 0xE7);
    private static readonly System.Windows.Media.Brush TransportInkBrush = CreateFrozenBrush(0x10, 0x23, 0x3A);

    private bool IsDelayedStartCountingDown => _session.DelayedStart.IsCountingDown;

    private static int ClampDelayedStartSeconds(int seconds) => Math.Clamp(seconds, 1, 60);

    private void OnDelayedPlayClick(object sender, RoutedEventArgs e)
    {
        if (IsDelayedStartCountingDown)
        {
            CancelDelayedStart("Delayed start canceled.");
            return;
        }

        if (!_isExternalMode && _music.IsPlaying)
        {
            MusicStatusText.Text = "Music is already playing.";
            return;
        }

        PlaybackGate gate = EvaluatePlaybackGate();
        if (!gate.CanStart && gate.BlockedReason != PlaybackBlockedReasons.ExternalModeActive)
        {
            MusicStatusText.Text = PlaybackBlockedStatus(gate.BlockedReason);
            return;
        }

        _session.DelayedStart.Arm(ClampDelayedStartSeconds(_settings.DelayedStartSeconds), trackPath: null);
        _delayedStartTimer.Start();
        UpdateDelayedPlayCountdownUi();
    }

    private void OnDelayedStartTick(object? sender, EventArgs e)
    {
        DelayedStartTick tick = _session.DelayedStart.Tick();
        if (!tick.IsReady)
        {
            UpdateDelayedPlayCountdownUi();
            return;
        }

        _delayedStartTimer.Stop();
        UpdateDelayedPlayIdleUi();

        if (_isExternalMode)
        {
            MediaKeySender.SendPlayPause();
            MusicStatusText.Text = "Sent Play to the app that is playing.";
            return;
        }

        if (tick.ArmedTrackPath != null)
        {
            PlayTrack(tick.ArmedTrackPath);
            return;
        }

        StartPlaybackFromCurrentState();
    }

    private void CancelDelayedStart(string? statusMessage)
    {
        if (!IsDelayedStartCountingDown)
        {
            return;
        }

        _session.DelayedStart.Cancel();
        _delayedStartTimer.Stop();
        UpdateDelayedPlayIdleUi();

        if (statusMessage != null)
        {
            MusicStatusText.Text = statusMessage;
        }
    }

    private void UpdateDelayedPlayCountdownUi()
    {
        DelayedPlayBtn.Background = CaptureAccentBrush;
        DelayedPlayBtn.BorderBrush = CaptureAccentBrush;
        DelayedPlayIcon.Fill = System.Windows.Media.Brushes.White;
        DelayedPlayText.Foreground = System.Windows.Media.Brushes.White;
        DelayedPlayText.Text = _session.DelayedStart.RemainingSeconds.ToString(CultureInfo.InvariantCulture);
        MusicStatusText.Text = $"Starting in {_session.DelayedStart.RemainingSeconds} s — click the button again to cancel.";
    }

    private void UpdateDelayedPlayIdleUi()
    {
        DelayedPlayBtn.Background = TransportIdleBrush;
        DelayedPlayBtn.BorderBrush = TransportIdleBorderBrush;
        DelayedPlayIcon.Fill = TransportInkBrush;
        DelayedPlayText.Foreground = TransportInkBrush;
        DelayedPlayText.Text = $"{ClampDelayedStartSeconds(_settings.DelayedStartSeconds)} s";
    }

    private PlaybackGate EvaluatePlaybackGate(bool hasExplicitTrack = false) => _session.EvaluatePlaybackGate(
        _music.HasMonitorOutput,
        _router.IsRouting,
        _music.IsPaused,
        _isExternalMode,
        hasExplicitTrack);

    private static string PlaybackBlockedStatus(string? blockedReason) => blockedReason switch
    {
        PlaybackBlockedReasons.ExternalModeActive => "Switch to the music library to play MicMixer music.",
        PlaybackBlockedReasons.NoPlaybackClock => "Start routing or enable monitoring to play music.",
        PlaybackBlockedReasons.EmptyLibrary => "No music yet — paste a YouTube link above.",
        _ => "Music could not be started."
    };

    private void OnDelayedStartMenuOpened(object sender, RoutedEventArgs e)
    {
        int current = ClampDelayedStartSeconds(_settings.DelayedStartSeconds);
        foreach (var item in DelayedStartMenu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            item.IsChecked = item.Tag is string tag
                && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                && seconds == current;
        }
    }

    private void OnDelayedStartOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item || item.Tag is not string tag
            || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
        {
            return;
        }

        _settings.DelayedStartSeconds = ClampDelayedStartSeconds(seconds);
        SaveSettings();

        if (IsDelayedStartCountingDown)
        {
            // Picking a new delay mid-countdown restarts the countdown with the
            // new value; stop/start also resets the timer's one-second phase.
            string? armedTrackPath = _session.DelayedStart.ArmedTrackPath;
            _session.DelayedStart.Arm(_settings.DelayedStartSeconds, armedTrackPath);
            _delayedStartTimer.Stop();
            _delayedStartTimer.Start();
            UpdateDelayedPlayCountdownUi();
            return;
        }

        UpdateDelayedPlayIdleUi();
        MusicStatusText.Text = $"Delayed start: {_settings.DelayedStartSeconds} s.";
    }

    private readonly DispatcherTimer _singleTrackAnnounceTimer;
    private string _pendingSingleTrackStatus = "";

    private void OnSingleTrackMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Click handling lives in the preview event so a double-click can be told
        // apart from two toggles: click 1 arms/disarms, click 2 upgrades to Always.
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            SetSingleTrackMode(SingleTrackPlayMode.Always);
        }
        else
        {
            SetSingleTrackMode(_session.SingleTrackMode == SingleTrackPlayMode.Off
                ? SingleTrackPlayMode.Once
                : SingleTrackPlayMode.Off);
        }
    }

    private void SetSingleTrackMode(SingleTrackPlayMode mode, bool save = true, bool announce = true)
    {
        _singleTrackAnnounceTimer.Stop();
        _session.SingleTrackMode = mode;
        SingleTrackBtn.Tag = mode.ToString();

        if (save)
        {
            // Once is transient; only the deliberate persistent mode survives restart.
            _settings.SingleTrackMode = mode == SingleTrackPlayMode.Always;
            SaveSettings();
        }

        if (announce)
        {
            // The status text is deferred past the double-click window: a longer
            // message can rewrap the card and physically move this button between
            // the two clicks of a double-click. The button's own color feedback is
            // immediate — it never affects layout.
            _pendingSingleTrackStatus = mode switch
            {
                SingleTrackPlayMode.Once =>
                    "Single-track mode: stops when the track ends, then turns off. Double-click to keep it enabled.",
                SingleTrackPlayMode.Always =>
                    "Single-track mode remains on until you disable it — music stops after every track.",
                _ => "Single-track mode off — the playlist continues as usual."
            };
            _singleTrackAnnounceTimer.Start();
        }
    }

    private void OnSingleTrackAnnounceTick(object? sender, EventArgs e)
    {
        _singleTrackAnnounceTimer.Stop();
        MusicStatusText.Text = _pendingSingleTrackStatus;
    }

    private void OnSeekDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _isSeekDragging = true;
    }

    private void OnSeekDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _isSeekDragging = false;
        _music.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
        UpdateMusicUi();
    }

    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingMusicUi || _isSeekDragging)
        {
            return;
        }

        // Click-to-seek (IsMoveToPointEnabled) lands here without drag events.
        _music.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    private void OnMusicVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_music == null || _isUpdatingMusicUi)
        {
            return;
        }

        _music.MusicVolume = (float)e.NewValue;
        _settings.MusicVolume = (float)e.NewValue;

        // Linked mode keeps a fixed offset between the sliders. The follower is
        // clamped at its edge, but the offset itself is preserved so the gap
        // comes back when the user drags in the other direction.
        if (_settings.LinkVolumes && !_isSyncingLinkedVolume)
        {
            _isSyncingLinkedVolume = true;
            MonitorVolumeSlider.Value = Math.Clamp(e.NewValue + _volumeLinkOffset, 0d, 1d);
            _isSyncingLinkedVolume = false;
        }

        UpdateVolumePercentTexts();
        ScheduleSettingsSave();
    }

    private void OnMonitorVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_music == null || _isUpdatingMusicUi)
        {
            return;
        }

        _music.MonitorVolume = (float)e.NewValue;
        _settings.MonitorVolume = (float)e.NewValue;

        if (_settings.LinkVolumes && !_isSyncingLinkedVolume)
        {
            _isSyncingLinkedVolume = true;
            MusicVolumeSlider.Value = Math.Clamp(e.NewValue - _volumeLinkOffset, 0d, 1d);
            _isSyncingLinkedVolume = false;
        }

        UpdateVolumePercentTexts();
        ScheduleSettingsSave();
    }

    private void OnVolumeLinkChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi)
        {
            return;
        }

        SetVolumesLinked(VolumeLinkToggle.IsChecked == true, renderControl: false);
    }

    private void SetVolumesLinked(bool linked, bool renderControl)
    {
        _settings.LinkVolumes = linked;

        if (renderControl)
        {
            _isUpdatingMusicUi = true;
            try
            {
                VolumeLinkToggle.IsChecked = linked;
            }
            finally
            {
                _isUpdatingMusicUi = false;
            }
        }

        // Lock in whatever gap the sliders have right now as the linked offset.
        if (linked)
        {
            _volumeLinkOffset = MonitorVolumeSlider.Value - MusicVolumeSlider.Value;
        }

        SaveSettings();
    }

    private void UpdateVolumePercentTexts()
    {
        MusicVolumePercentText.Text = $"{Math.Round(MusicVolumeSlider.Value * 100)} %";
        MonitorVolumePercentText.Text = $"{Math.Round(MonitorVolumeSlider.Value * 100)} %";
    }

    private void OnMonitorConfigChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        // External-mode rendering force-unchecks this control under an update guard,
        // so only a deliberate library-mode event can change the preference.
        _settings.MonitorEnabled = MonitorEnabledCheck.IsChecked == true;
        SaveSettings();
        UpdateMusicRouteHint();
        UpdateStatusText();
        _ = ApplyMonitorConfigAsync();
    }

    private void OnMonitorDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        if (MonitorDeviceCombo.SelectedItem is AudioDeviceOption device)
        {
            _settings.MusicMonitorDeviceId = device.Id;
        }
        OnConfigurationChanged();
        _ = ApplyMonitorConfigAsync();
    }

    private async Task ApplyMonitorConfigAsync()
    {
        // In external mode the user already hears the app directly; monitoring
        // would double the audio with an offset.
        bool enabled = _settings.MonitorEnabled && !_isExternalMode;
        // The combo contributes only the currently available runtime endpoint.
        // It never writes the persisted preference during device discovery.
        string? deviceId = enabled
            ? (MonitorDeviceCombo.SelectedItem as AudioDeviceOption)?.Id
            : null;

        try
        {
            await Task.Run(() => _music.ConfigureMonitor(deviceId));
            PauseMusicIfClockLost();

            // Not onto a stand-in: unplugging the headset must not move the music to the
            // speakers by itself. It resumes when the chosen device is back, or on Play.
            bool isStandIn = deviceId != null
                && _settings.MusicMonitorDeviceId != null
                && deviceId != _settings.MusicMonitorDeviceId;
            if (!isStandIn)
            {
                ResumeMusicIfAutoPaused();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to configure music monitor.");
            PauseMusicIfClockLost();
            MusicStatusText.Text = $"Could not start monitoring: {ex.Message}";
            UpdateMusicUi();
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        await StartDownloadAsync();
    }

    private async void OnYoutubeUrlKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not Key.Enter and not Key.Return)
        {
            return;
        }

        e.Handled = true;
        await StartDownloadAsync();
    }

    private async Task StartDownloadAsync(string? downloadFolderOverride = null)
    {
        if (_isDownloading)
        {
            return;
        }

        DownloadUrlCheck check = DownloadUrlValidator.Check(YoutubeUrlBox.Text);
        if (!check.IsAllowed)
        {
            MusicStatusText.Text = check.Error!;
            return;
        }

        string url = check.Url!;

        _isDownloading = true;
        DownloadBtn.IsEnabled = false;
        DownloadStatusRow.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = "Preparing...";

        try
        {
            var toolStatus = new Progress<string>(text => DownloadStatusText.Text = text);
            await _toolBootstrapper.EnsureToolsAsync(
                toolStatus,
                CancellationToken.None,
                requireJavaScriptRuntime: DownloadUrlValidator.IsYouTubeUrl(url));

            var progress = new Progress<DownloadProgress>(update =>
            {
                DownloadStatusText.Text = update.Status;
                if (update.Percent is double percent)
                {
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = percent;
                }
                else
                {
                    DownloadProgressBar.IsIndeterminate = true;
                }
            });

            string downloadFolder = downloadFolderOverride
                ?? (DownloadFolderCombo.SelectedItem as FolderInfo)?.Path
                ?? _playlist.Folders[0];
            string? newFile = await _youTubeDownloader.DownloadAudioAsync(url, downloadFolder, progress, CancellationToken.None);

            YoutubeUrlBox.Text = "";
            RefreshPlaylist(newFile);
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            DownloadStatusText.Text = newFile != null
                ? $"Finished: {Path.GetFileNameWithoutExtension(newFile)}"
                : "Finished.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "YouTube download failed.");
            DownloadProgressBar.IsIndeterminate = false;
            DownloadStatusText.Text = "Download failed.";
            MusicStatusText.Text = $"Download error: {ex.Message}";
        }
        finally
        {
            _isDownloading = false;
            DownloadBtn.IsEnabled = true;
        }
    }

    private sealed class QueueEntry : INotifyPropertyChanged
    {
        private Visibility _insertAboveVisibility = Visibility.Collapsed;
        private Visibility _insertBelowVisibility = Visibility.Collapsed;

        public QueueEntry(int index, string path)
        {
            Index = index;
            Path = path;
            Name = System.IO.Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>0-based position in the queue at the time the list was built.</summary>
        public int Index { get; }

        public string Path { get; }

        public string Name { get; }

        public string NumberText => $"{Index + 1}.";

        public Visibility InsertAboveVisibility
        {
            get => _insertAboveVisibility;
            private set => SetField(ref _insertAboveVisibility, value, nameof(InsertAboveVisibility));
        }

        public Visibility InsertBelowVisibility
        {
            get => _insertBelowVisibility;
            private set => SetField(ref _insertBelowVisibility, value, nameof(InsertBelowVisibility));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetDropIndicator(bool above, bool below)
        {
            InsertAboveVisibility = above ? Visibility.Visible : Visibility.Collapsed;
            InsertBelowVisibility = below ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetField<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Per-folder presentation data: a display name, a one-letter badge, and a stable
    /// color pair (strong accent + light tint) assigned from <see cref="FolderPalette"/>.
    /// </summary>
    private sealed class FolderInfo
    {
        public FolderInfo(string path, System.Windows.Media.Brush accent, System.Windows.Media.Brush tint)
        {
            Path = path;
            DisplayName = PlaylistManager.IsDefaultFolder(path)
                ? "Standard"
                : System.IO.Path.GetFileName(path) is { Length: > 0 } leaf ? leaf : path;
            Letter = char.ToUpperInvariant(DisplayName[0]).ToString();
            Accent = accent;
            Tint = tint;
        }

        public string Path { get; }

        public string DisplayName { get; }

        public string Letter { get; }

        public System.Windows.Media.Brush Accent { get; }

        public System.Windows.Media.Brush Tint { get; }
    }

    /// <summary>Toggleable folder chip next to the search box; active chips narrow the playlist.</summary>
    private sealed class FolderChipItem : INotifyPropertyChanged
    {
        private bool _isActive;
        private bool _isDimmed;

        public FolderChipItem(FolderInfo info)
        {
            Info = info;
        }

        public FolderInfo Info { get; }

        public string Letter => Info.Letter;

        public string ToolTipText => IsActive
            ? $"{Info.Path}\nShowing tracks from this folder only — click to show all tracks."
            : $"{Info.Path}\nClick to show only tracks from this folder.";

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value)
                {
                    return;
                }

                _isActive = value;
                Raise(nameof(IsActive));
                Raise(nameof(ChipBackground));
                Raise(nameof(ChipForeground));
                Raise(nameof(ToolTipText));
            }
        }

        public System.Windows.Media.Brush ChipBackground => IsActive ? Info.Accent : Info.Tint;

        public System.Windows.Media.Brush ChipForeground => IsActive ? System.Windows.Media.Brushes.White : Info.Accent;

        public double ChipOpacity => _isDimmed ? 0.45 : 1.0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetDimmed(bool isDimmed)
        {
            if (_isDimmed == isDimmed)
            {
                return;
            }

            _isDimmed = isDimmed;
            Raise(nameof(ChipOpacity));
        }

        private void Raise(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>Accent/tint pairs assigned to folders by list position.</summary>
    private static readonly (System.Windows.Media.Brush Accent, System.Windows.Media.Brush Tint)[] FolderPalette =
    {
        (CreateFrozenBrush(0x0F, 0x76, 0x6E), CreateFrozenBrush(0xCC, 0xFB, 0xF1)), // teal
        (CreateFrozenBrush(0x43, 0x38, 0xCA), CreateFrozenBrush(0xE0, 0xE7, 0xFF)), // indigo
        (CreateFrozenBrush(0xC2, 0x41, 0x0C), CreateFrozenBrush(0xFF, 0xED, 0xD5)), // orange
        (CreateFrozenBrush(0xBE, 0x18, 0x5D), CreateFrozenBrush(0xFC, 0xE7, 0xF3)), // pink
        (CreateFrozenBrush(0x15, 0x80, 0x3D), CreateFrozenBrush(0xDC, 0xFC, 0xE7)), // green
        (CreateFrozenBrush(0x1D, 0x4E, 0xD8), CreateFrozenBrush(0xDB, 0xEA, 0xFE)), // blue
    };

    private sealed class TrackItem : INotifyPropertyChanged
    {
        private readonly FolderInfo? _folder;
        private readonly bool _showFolderBadge;
        private string _queueText = "";
        private Visibility _queueVisibility = Visibility.Collapsed;
        private Visibility _playingVisibility = Visibility.Collapsed;

        public TrackItem(string path, string name, FolderInfo? folder, bool showFolderBadge)
        {
            Path = path;
            Name = name;
            _folder = folder;
            _showFolderBadge = showFolderBadge;
        }

        public string Path { get; }

        public string Name { get; }

        public string? FolderPath => _folder?.Path;

        public string? FolderLetter => _folder?.Letter;

        public System.Windows.Media.Brush? FolderBadgeBackground => _folder?.Tint;

        public System.Windows.Media.Brush? FolderBadgeForeground => _folder?.Accent;

        public Visibility FolderBadgeVisibility =>
            _showFolderBadge && _folder != null ? Visibility.Visible : Visibility.Collapsed;

        public string QueueText
        {
            get => _queueText;
            private set => SetField(ref _queueText, value, nameof(QueueText));
        }

        public Visibility QueueVisibility
        {
            get => _queueVisibility;
            private set => SetField(ref _queueVisibility, value, nameof(QueueVisibility));
        }

        public Visibility PlayingVisibility
        {
            get => _playingVisibility;
            private set => SetField(ref _playingVisibility, value, nameof(PlayingVisibility));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetQueuePositions(IReadOnlyList<int> positions)
        {
            QueueText = positions.Count == 0 ? "" : string.Join(", ", positions);
            QueueVisibility = positions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetIsPlaying(bool isPlaying)
        {
            PlayingVisibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetField<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // --- System tray support ---
    // The tray icon is always visible: minimize keeps the app in the taskbar as
    // usual, while the close button (X) hides the window to the tray instead of
    // exiting. "Exit" in the tray menu quits for real.

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = StatusTheme.RenderStatusIcon(MicStatus.Stopped, TrayIconPixelSize),
            Text = "MicMixer",
            Visible = true
        };

        trayIcon.DoubleClick += OnTrayIconDoubleClick;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show MicMixer", null, OnTrayShowClick);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, OnTrayExitClick);
        trayIcon.ContextMenuStrip = menu;

        return trayIcon;
    }

    private void OnTrayIconDoubleClick(object? sender, EventArgs e)
    {
        RestoreFromTray();
    }

    private void OnTrayShowClick(object? sender, EventArgs e)
    {
        RestoreFromTray();
    }

    private void OnTrayExitClick(object? sender, EventArgs e)
    {
        _isReallyClosing = true;
        System.Windows.Application.Current.Shutdown();
    }

    internal void ShowAndActivate()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _lastNonMinimizedWindowState;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    internal void ExitForUpdate()
    {
        _isReallyClosing = true;
        System.Windows.Application.Current.Shutdown();
    }

    private void RestoreFromTray() => ShowAndActivate();

    private MicStatus ComputeMicStatus()
    {
        return !_router.IsRouting
            ? MicStatus.Stopped
            : !_router.OutputGateOpen
                ? MicStatus.Muted
                : _router.UseModdedInput ? MicStatus.Modded : MicStatus.Live;
    }

    /// <summary>Tray badge size matching the actual small-icon size at the current DPI.</summary>
    private static int TrayIconPixelSize => Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);

    private void UpdateTrayIcon()
    {
        var newStatus = ComputeMicStatus();

        // The overlay mirrors the tray state; SetState no-ops when unchanged,
        // and the stream overlay server dedupes identical snapshots the same way.
        _overlayIndicator?.SetState(ToOverlayIndicatorState(newStatus));
        _overlayIndicator?.SetMusicState(ComputeOverlayMusicState());
        PublishObsOverlayState();

        if (newStatus == _lastTrayStatus)
            return;

        try
        {
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = StatusTheme.RenderStatusIcon(newStatus, TrayIconPixelSize);
            oldIcon?.Dispose();

            _trayIcon.Text = newStatus switch
            {
                MicStatus.Live => "MicMixer — Normal mic is live",
                MicStatus.Modded => "MicMixer — Modified voice is live",
                MicStatus.Muted => "MicMixer — Muted (push-to-talk)",
                _ => "MicMixer — Routing stopped"
            };

            _lastTrayStatus = newStatus;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update tray icon to status {MicStatus}.", newStatus);
        }
    }

    // --- Overlay indicator ---

    private static OverlayIndicatorState ToOverlayIndicatorState(MicStatus status)
    {
        return status switch
        {
            MicStatus.Live => OverlayIndicatorState.Normal,
            MicStatus.Modded => OverlayIndicatorState.Modded,
            MicStatus.Muted => OverlayIndicatorState.Muted,
            _ => OverlayIndicatorState.Hidden
        };
    }

    /// <summary>
    /// State of the overlay's music circle: hidden without active music, otherwise
    /// exactly where the music currently goes — sent to the cable, previewed in
    /// monitor-only mode, or blocked by push-to-talk.
    /// </summary>
    private OverlayMusicState ComputeOverlayMusicState()
    {
        if (!_router.IsRouting || !HasActiveMusicSignal())
        {
            return OverlayMusicState.Hidden;
        }

        return _router.MusicMonitorOnly
            ? OverlayMusicState.MonitorOnly
            : _router.MusicRouteOpen ? OverlayMusicState.Sending : OverlayMusicState.Blocked;
    }

    private bool HasActiveMusicSignal()
    {
        if (!_isExternalMode)
        {
            return _music.IsPlaying;
        }

        // Process capture has no play/pause state. Use the stable activity latch
        // fed from raw capture packets so normal gaps between beats do not hide
        // and reset the music indicator.
        return _appCapture != null && _externalSignalActivity.IsActive;
    }

    private void OnOverlayIndicatorChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        _settings.OverlayIndicatorEnabled = OverlayIndicatorCheck.IsChecked == true;
        ApplyOverlayIndicatorSetting(_settings.OverlayIndicatorEnabled);
        OnConfigurationChanged();
    }

    private void OnOverlayVolumeMeterChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        _settings.OverlayVolumeMeterEnabled = OverlayVolumeMeterCheck.IsChecked == true;
        if (_overlayIndicator is { } overlay)
        {
            overlay.MeterEnabled = _settings.OverlayVolumeMeterEnabled;
        }

        UpdateOutputMeteringEnabled();
        PublishObsOverlayState();
        OnConfigurationChanged();
    }

    private void OnMeterSensitivityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        // Applies live so the meter can be calibrated against real sound while dragging.
        if (_overlayIndicator is { } overlay)
        {
            overlay.MeterSensitivityDb = (float)e.NewValue;
        }
        _settings.MeterSensitivityDb = (float)e.NewValue;
        PublishObsOverlayState();

        UpdateMeterSensitivityText();
        OnConfigurationChanged();
    }

    private void OnMeterSensitivityLabelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MeterSensitivitySlider.Value = 0;
        }
    }

    /// <summary>The ±0 default stays visually quiet; any deviation is emphasized.</summary>
    private void UpdateMeterSensitivityText()
    {
        int db = (int)Math.Round(_settings.MeterSensitivityDb);
        MeterSensitivityValueText.Text = db == 0 ? "±0 dB"
            : db > 0 ? $"+{db} dB"
            : $"−{-db} dB";
        MeterSensitivityValueText.Foreground = db == 0 ? MeterSensitivityIdleBrush : MeterSensitivityActiveBrush;
        MeterSensitivityValueText.FontWeight = db == 0 ? FontWeights.Normal : FontWeights.SemiBold;
    }

    private static readonly System.Windows.Media.Brush MeterSensitivityIdleBrush = CreateFrozenBrush(0x9C, 0xA3, 0xAF);
    private static readonly System.Windows.Media.Brush MeterSensitivityActiveBrush = CreateFrozenBrush(0x10, 0x23, 0x3A);

    /// <summary>
    /// The audio-thread level computation only runs while something can display
    /// it; otherwise the tap is a pure pass-through.
    /// </summary>
    private void UpdateOutputMeteringEnabled()
    {
        // The stream overlay only needs levels while a page is actually connected,
        // so an idle server keeps the audio thread as cheap as no overlay at all.
        bool metering = (_overlayIndicator != null || _obsOverlayServer is { HasClients: true })
            && _settings.OverlayVolumeMeterEnabled;
        _router.OutputMeteringEnabled = metering;
        _router.MusicMeteringEnabled = metering;
    }

    private void ApplyOverlayIndicatorSetting(bool enabled)
    {
        if (enabled)
        {
            try
            {
                _overlayIndicator ??= new OverlayIndicatorWindow();
                _overlayIndicator.MeterEnabled = _settings.OverlayVolumeMeterEnabled;
                _overlayIndicator.MeterSensitivityDb = _settings.MeterSensitivityDb;
                _overlayIndicator.SetState(ToOverlayIndicatorState(ComputeMicStatus()));
                _overlayIndicator.SetMusicState(ComputeOverlayMusicState());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create overlay indicator window.");
                _overlayIndicator = null;
                StatusText.Text = $"Could not display the overlay indicator: {ex.Message}";
            }
        }
        else if (_overlayIndicator is { } overlay)
        {
            _overlayIndicator = null;
            overlay.Close();
        }

        UpdateOutputMeteringEnabled();
    }

    #region Responsive layout

    private bool _volumesStacked;
    private bool _playlistButtonsCompact;

    /// <summary>Stacks the two volume sliders on top of each other when a single row
    /// would leave them too short to drag comfortably. The link toggle sits between
    /// them, spanning both rows.</summary>
    private void OnVolumesGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 460;
        if (stacked == _volumesStacked)
        {
            return;
        }

        _volumesStacked = stacked;
        if (stacked)
        {
            // Second star column must not eat width while its content lives in row 1.
            MonitorSliderColumn.Width = new GridLength(0);

            Grid.SetRow(MonitorVolumeLabel, 1);
            Grid.SetColumn(MonitorVolumeLabel, 0);
            MonitorVolumeLabel.Margin = new Thickness(0, 8, 8, 0);

            Grid.SetRow(MonitorVolumeSlider, 1);
            Grid.SetColumn(MonitorVolumeSlider, 1);
            MonitorVolumeSlider.Margin = new Thickness(0, 8, 0, 0);

            Grid.SetRow(MonitorVolumePercentText, 1);
            Grid.SetColumn(MonitorVolumePercentText, 2);
            MonitorVolumePercentText.Margin = new Thickness(0, 8, 0, 0);

            Grid.SetRowSpan(VolumeLinkToggle, 2);
        }
        else
        {
            MonitorSliderColumn.Width = new GridLength(1, GridUnitType.Star);

            Grid.SetRow(MonitorVolumeLabel, 0);
            Grid.SetColumn(MonitorVolumeLabel, 4);
            MonitorVolumeLabel.Margin = new Thickness(0, 0, 8, 0);

            Grid.SetRow(MonitorVolumeSlider, 0);
            Grid.SetColumn(MonitorVolumeSlider, 5);
            MonitorVolumeSlider.Margin = new Thickness(0);

            Grid.SetRow(MonitorVolumePercentText, 0);
            Grid.SetColumn(MonitorVolumePercentText, 6);
            MonitorVolumePercentText.Margin = new Thickness(0);

            Grid.SetRowSpan(VolumeLinkToggle, 1);
        }
    }

    /// <summary>Moves the playlist button column to a horizontal row under the list
    /// when the playlist area is too short to fit the stacked buttons.</summary>
    private void OnPlaylistRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Height < 150;
        if (compact == _playlistButtonsCompact)
        {
            return;
        }

        _playlistButtonsCompact = compact;
        PlaylistButtonsPanel.Orientation = compact
            ? System.Windows.Controls.Orientation.Horizontal
            : System.Windows.Controls.Orientation.Vertical;
        if (compact)
        {
            Grid.SetRow(PlaylistButtonsPanel, 1);
            Grid.SetColumn(PlaylistButtonsPanel, 0);
            Grid.SetColumnSpan(PlaylistButtonsPanel, 2);
            PlaylistButtonsPanel.Margin = new Thickness(0, 6, 0, 0);
        }
        else
        {
            Grid.SetRow(PlaylistButtonsPanel, 0);
            Grid.SetColumn(PlaylistButtonsPanel, 1);
            Grid.SetColumnSpan(PlaylistButtonsPanel, 1);
            PlaylistButtonsPanel.Margin = new Thickness(8, 0, 0, 0);
        }

        bool first = true;
        foreach (var button in PlaylistButtonsPanel.Children.OfType<System.Windows.Controls.Button>())
        {
            button.Margin = first
                ? new Thickness(0)
                : compact ? new Thickness(6, 0, 0, 0) : new Thickness(0, 6, 0, 0);
            first = false;
        }
    }

    private void RestoreWindowBounds()
    {
        if (_settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
        {
            Width = Math.Min(_settings.WindowWidth, SystemParameters.WorkArea.Width);
            Height = Math.Min(_settings.WindowHeight, SystemParameters.WorkArea.Height);
        }

        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _lastNonMinimizedWindowState = WindowState;
        }
    }

    private void PersistWindowBounds()
    {
        var size = WindowState == WindowState.Normal
            ? new System.Windows.Size(Width, Height)
            : RestoreBounds.Size;

        if (size.Width >= MinWidth && size.Height >= MinHeight)
        {
            _settings.WindowWidth = size.Width;
            _settings.WindowHeight = size.Height;
        }
        _settings.WindowMaximized = _lastNonMinimizedWindowState == WindowState.Maximized;
        SaveSettings();
    }

    #endregion

    private sealed record ModifiedVoiceOption(ModifiedVoiceMode Mode, string FriendlyName);
}
