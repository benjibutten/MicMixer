using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.IO;
using MicMixer.Audio;
using MicMixer.Input;
using MicMixer.Music;
using MicMixer.Overlay;
using MicMixer.Settings;
using MicMixer.UI;
using NAudio.CoreAudioApi;
using Serilog;

namespace MicMixer;

public partial class MainWindow : Window
{
    private const int MaxReleaseDelayMilliseconds = 5_000;

    private readonly AudioRouter _router;
    private readonly GlobalHotkeyListener _hotkeyListener;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherTimer _levelTimer;
    private readonly DispatcherTimer _releaseDelayTimer;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly MusicPlaybackEngine _music;
    private readonly PlaylistManager _playlist;
    private readonly ToolBootstrapper _toolBootstrapper;
    private readonly YouTubeDownloader _youTubeDownloader;
    private readonly DispatcherTimer _musicTimer;
    private readonly DispatcherTimer _delayedStartTimer;
    private int _delayedStartRemainingSeconds;
    private readonly List<string> _musicQueue = new();
    private List<TrackItem> _allTracks = new();
    private List<FolderChipItem> _folderChips = new();
    private Dictionary<string, FolderInfo> _folderInfoByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Active filter chips in the order they were turned on; the last one steers the download folder.</summary>
    private readonly List<string> _folderChipActivationOrder = new();
    /// <summary>The download folder the user picked themselves — filter chips override the combo but never this.</summary>
    private string? _userDownloadFolderPath;
    private System.Windows.Point _queueDragStart;
    private int _queueDragIndex = -1;
    private DateTime _queuePopupClosedAt = DateTime.MinValue;
    private string? _lastPlayedTrackPath;
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
    private AppSettings _settings;
    private OverlayIndicatorWindow? _overlayIndicator;
    private HotkeyBinding _hotkeyBinding = HotkeyBinding.Default;
    private int _releaseDelayMilliseconds;
    private bool _isCapturingHotkey;
    private bool _isReleaseDelayPending;
    private bool _isStartingRouting;
    private bool _isDevicesLoading;
    private bool _isUpdatingUi;
    private bool _isReallyClosing;
    private bool _devicesLoaded;
    private string? _deviceLoadError;
    private MicStatus _lastTrayStatus = MicStatus.Stopped;
    private WindowState _lastNonMinimizedWindowState = WindowState.Normal;

    public MainWindow()
    {
        _router = new AudioRouter();
        App.StartupTrace("AudioRouter created");
        _hotkeyListener = new GlobalHotkeyListener();
        App.StartupTrace("GlobalHotkeyListener created");
        _settingsStore = new SettingsStore();
        App.StartupTrace("SettingsStore created");

        _music = new MusicPlaybackEngine();
        _playlist = new PlaylistManager();
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
        _releaseDelayTimer = new DispatcherTimer();
        _releaseDelayTimer.Tick += OnReleaseDelayTimerTick;
        _delayedStartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _delayedStartTimer.Tick += OnDelayedStartTick;
        _singleTrackAnnounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 50)
        };
        _singleTrackAnnounceTimer.Tick += OnSingleTrackAnnounceTick;
        InitializeComponent();
        App.StartupTrace("InitializeComponent done");
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

        _hotkeyBinding = HotkeyBinding.Parse(_settings.HotkeyId);
        _hotkeyListener.UpdateBinding(_hotkeyBinding);
        UpdateHotkeyUi();
        ReleaseDelayTextBox.Text = _releaseDelayMilliseconds.ToString(CultureInfo.InvariantCulture);

        _trayIcon = CreateTrayIcon();
        DryInputCombo.IsEnabled = false;
        ModdedInputCombo.IsEnabled = false;
        OutputDeviceCombo.IsEnabled = false;

        _music.MusicVolume = Math.Clamp(_settings.MusicVolume, 0f, 1f);
        _music.MonitorVolume = Math.Clamp(_settings.MonitorVolume, 0f, 1f);
        _isUpdatingMusicUi = true;
        MusicVolumeSlider.Value = _music.MusicVolume;
        MonitorVolumeSlider.Value = _music.MonitorVolume;
        MonitorEnabledCheck.IsChecked = _settings.MonitorEnabled;
        VolumeLinkToggle.IsChecked = _settings.LinkVolumes;
        PushToTalkCheck.IsChecked = _settings.PushToTalkMode;
        OverlayIndicatorCheck.IsChecked = _settings.OverlayIndicatorEnabled;
        OverlayVolumeMeterCheck.IsChecked = _settings.OverlayVolumeMeterEnabled;
        _isUpdatingMusicUi = false;
        UpdateDelayedPlayIdleUi();
        SetSingleTrackMode(_settings.SingleTrackMode ? SingleTrackPlayMode.Always : SingleTrackPlayMode.Off,
            save: false, announce: false);
        _volumeLinkOffset = MonitorVolumeSlider.Value - MusicVolumeSlider.Value;
        UpdateVolumePercentTexts();
        ApplyModdedMicUiState(_settings.SkipModdedMic);

        // Migrate the legacy single-folder setting into the folder list.
        var storedFolders = _settings.MusicFolderPaths is { Count: > 0 } paths
            ? paths
            : string.IsNullOrWhiteSpace(_settings.MusicFolderPath)
                ? new List<string>()
                : new List<string> { _settings.MusicFolderPath! };
        _playlist.SetFolders(storedFolders);
        _userDownloadFolderPath = _settings.DownloadFolderPath;
        RefreshMusicFolderUi();
        RefreshPlaylist(null);

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

        UpdateStatusText();
        UpdateTrayIcon();
        ApplyOverlayIndicatorSetting(_settings.OverlayIndicatorEnabled);
        Closing += OnClosing;

        App.StartupTrace("MainWindow ctor done");

        ContentRendered += (_, _) =>
        {
            App.StartupTrace("ContentRendered (startup complete)");
            App.StartupStopwatch.Stop();

            if (!App.StartupBenchmarkMode)
            {
                App.StartupTrace("Device refresh queued");
                _ = RefreshDevicesAsync();
            }

            if (App.StartupBenchmarkMode)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _isReallyClosing = true;
                    System.Windows.Application.Current.Shutdown();
                }, DispatcherPriority.Background);
            }
        };
    }

    private async Task RefreshDevicesAsync()
    {
        if (_isDevicesLoading)
        {
            return;
        }

        _isDevicesLoading = true;
        _devicesLoaded = false;
        _deviceLoadError = null;

        if (!_router.IsRouting)
        {
            DryInputCombo.IsEnabled = false;
            ModdedInputCombo.IsEnabled = false;
            OutputDeviceCombo.IsEnabled = false;
        }

        UpdateStatusText();

        var selectedDryInput = (DryInputCombo.SelectedItem as AudioDeviceOption)?.Id;
        var selectedModdedInput = (ModdedInputCombo.SelectedItem as AudioDeviceOption)?.Id;
        var selectedOutput = (OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;
        var selectedMonitor = (MonitorDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;

        try
        {
            App.StartupTrace("Device refresh started");
            var (inputs, outputs) = await Task.Run(EnumerateActiveDevices);

            _isUpdatingUi = true;

            try
            {
                var moddedItems = new List<AudioDeviceOption>(inputs.Count + 1) { NoModdedMicOption };
                moddedItems.AddRange(inputs);

                DryInputCombo.ItemsSource = inputs;
                ModdedInputCombo.ItemsSource = moddedItems;
                OutputDeviceCombo.ItemsSource = outputs;

                var drySelection = SelectInputDevice(
                    inputs,
                    selectedDryInput ?? _settings.NormalInputDeviceId,
                    device => !LooksLikeVoiceModDevice(device));

                DryInputCombo.SelectedItem = drySelection;

                string? preferredModdedId = selectedModdedInput
                    ?? (_settings.SkipModdedMic ? NoModdedMicOption.Id : _settings.ModdedInputDeviceId);

                AudioDeviceOption? moddedSelection;

                if (preferredModdedId == NoModdedMicOption.Id)
                {
                    moddedSelection = NoModdedMicOption;
                }
                else
                {
                    moddedSelection = SelectInputDevice(
                        inputs,
                        preferredModdedId,
                        LooksLikeVoiceModDevice,
                        drySelection?.Id);

                    // Never auto-pick the same device as the normal mic — fall back to
                    // "no modded mic" when there is no distinct second input.
                    if (moddedSelection == null || moddedSelection.Id == drySelection?.Id)
                    {
                        moddedSelection = inputs.FirstOrDefault(device => device.Id != drySelection?.Id)
                            ?? NoModdedMicOption;
                    }
                }

                ModdedInputCombo.SelectedItem = moddedSelection;

                OutputDeviceCombo.SelectedItem = SelectOutputDevice(outputs, selectedOutput ?? _settings.OutputDeviceId);

                MonitorDeviceCombo.ItemsSource = outputs;
                MonitorDeviceCombo.SelectedItem = SelectMonitorDevice(outputs, selectedMonitor ?? _settings.MusicMonitorDeviceId);
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
                OutputDeviceCombo.IsEnabled = true;
            }

            ApplyModdedMicUiState();
            UpdateOutputCableWarning();
            SaveSettings();
            await ApplyMonitorConfigAsync();
            App.StartupTrace("Devices refreshed");
        }
        catch (Exception ex)
        {
            _devicesLoaded = false;
            _deviceLoadError = $"Kunde inte läsa ljudenheter: {ex.Message}";
            Log.Error(ex, "Device refresh failed.");
            App.StartupTrace($"Device refresh failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isDevicesLoading = false;
            UpdateStatusText();
        }
    }

    private static (List<AudioDeviceOption> inputs, List<AudioDeviceOption> outputs) EnumerateActiveDevices()
    {
        App.StartupTrace("Creating MMDeviceEnumerator (lazy)");
        using var enumerator = new MMDeviceEnumerator();
        App.StartupTrace("MMDeviceEnumerator created (lazy)");

        var inputs = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new AudioDeviceOption(device.ID, device.FriendlyName))
            .ToList();

        var outputs = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new AudioDeviceOption(device.ID, device.FriendlyName))
            .ToList();

        return (inputs, outputs);
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_router.IsRouting)
        {
            StopRouting();
            return;
        }

        if (_isStartingRouting)
        {
            return;
        }

        if (_isDevicesLoading)
        {
            StatusText.Text = "Vänta, laddar ljudenheter...";
            return;
        }

        if (!_devicesLoaded)
        {
            StatusText.Text = _isDevicesLoading
                ? "Vänta, laddar ljudenheter..."
                : _deviceLoadError ?? "Kunde inte läsa ljudenheter. Klicka uppdatera och försök igen.";
            return;
        }

        bool skipModded = IsModdedMicSkipped;
        var moddedInput = ModdedInputCombo.SelectedItem as AudioDeviceOption;

        if (DryInputCombo.SelectedItem is not AudioDeviceOption dryInput ||
            OutputDeviceCombo.SelectedItem is not AudioDeviceOption output ||
            (!skipModded && moddedInput == null))
        {
            StatusText.Text = skipModded
                ? "Välj vanlig mic och virtuell kabel."
                : "Välj vanlig mic, moddad mic och virtuell kabel.";
            return;
        }

        // Same device for both mics works technically (two shared-mode captures),
        // the hotkey just switches between two identical signals. Warn instead of
        // hard-blocking — a silent refusal looks like a dead button.
        if (!skipModded && dryInput.Id == moddedInput!.Id && _acknowledgedSameMicId != dryInput.Id)
        {
            _acknowledgedSameMicId = dryInput.Id;
            StatusText.Text = "Vanlig och moddad mic är samma enhet — hotkeyn gör då ingen hörbar skillnad. Menade du 'Ingen moddad mic'? Klicka Aktivera igen för att starta ändå.";
            return;
        }

        // Warn instead of hard-blocking: unusual cable drivers (VAC, Voicemeeter Aux)
        // fail the name heuristic, and a silent refusal looks like a dead button.
        if (!LooksLikeVirtualCable(output) && _acknowledgedNonCableOutputId != output.Id)
        {
            _acknowledgedNonCableOutputId = output.Id;
            StatusText.Text = $"\"{output.FriendlyName}\" ser inte ut som en virtuell kabel — spelet hör mixen bara via t.ex. CABLE Input. Klicka Aktivera igen för att starta ändå.";
            return;
        }

        _isStartingRouting = true;
        ToggleBtn.IsEnabled = false;
        StatusText.Text = "Startar routning...";

        bool pushToTalk = IsPushToTalk;

        try
        {
            await Task.Run(() => StartRoutingByDeviceId(dryInput.Id, skipModded ? null : moddedInput!.Id, output.Id, pushToTalk));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Routing start failed.");
            StatusText.Text = $"Fel vid start: {ex.Message}";
            return;
        }
        finally
        {
            _isStartingRouting = false;
            ToggleBtn.IsEnabled = true;
        }

        if (_router.IsRouting)
        {
            if (!skipModded || pushToTalk)
            {
                SetHotkeyMonitoringEnabled(true);
            }

            ApplyEffectiveRoutingStates();
            ToggleBtnText.Text = "Stoppa routning";
            ToggleBtnIcon.Data = (Geometry)FindResource("StopIcon");
            DryInputCombo.IsEnabled = false;
            ModdedInputCombo.IsEnabled = false;
            OutputDeviceCombo.IsEnabled = false;
            SaveSettings();
            UpdateStatusText();
            ResumeMusicIfAutoPaused();
            UpdateExternalCaptureStatusText();
        }
        else
        {
            UpdateStatusText();
        }
    }

    private void StartRoutingByDeviceId(string dryInputId, string? moddedInputId, string outputId, bool startMuted)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dryInput = enumerator.GetDevice(dryInputId);
        var moddedInput = moddedInputId != null ? enumerator.GetDevice(moddedInputId) : null;
        var output = enumerator.GetDevice(outputId);

        _router.SetUseModdedInput(false);
        // Push-to-talk must start silent; the gate opens when the hotkey is pressed.
        _router.SetOutputGateOpen(!startMuted);
        _router.Start(dryInput, moddedInput, output);
    }

    private void StopRouting()
    {
        SetHotkeyMonitoringEnabled(false);
        CancelPendingReleaseDelay();
        _router.Stop();
        PauseMusicIfClockLost();
        ToggleBtnText.Text = "Aktivera";
        ToggleBtnIcon.Data = (Geometry)FindResource("PlayIcon");
        DryLevelMeter.Value = 0;
        ModdedLevelMeter.Value = 0;

        if (!_isDevicesLoading)
        {
            DryInputCombo.IsEnabled = true;
            ModdedInputCombo.IsEnabled = true;
            OutputDeviceCombo.IsEnabled = true;
        }

        UpdateStatusText();
        UpdateExternalCaptureStatusText();
    }

    private void OnRouterError(object? sender, string message)
    {
        Log.Error("Audio routing error: {ErrorMessage}", message);

        Dispatcher.BeginInvoke(() =>
        {
            StopRouting();
            StatusText.Text = $"Fel: {message}";
        });
    }

    private void OnHotkeyPressedStateChanged(object? sender, bool isPressed)
    {
        Dispatcher.BeginInvoke(() => ApplyHotkeyPressedState(isPressed));
    }

    private void OnLevelTimerTick(object? sender, EventArgs e)
    {
        // Feeds the overlay volume meter; SetOutputLevel no-ops while it is hidden,
        // and the read itself resets the levels so stale values never linger.
        if (_overlayIndicator is { } overlay)
        {
            var (outputPeak, outputRms) = _router.ReadAndResetOutputLevels();
            overlay.SetOutputLevel(outputPeak, outputRms);
        }

        if (_appCapture is { } capture)
        {
            // Peak-hold with decay so short transients stay visible.
            float peak = capture.ReadAndResetPeak();
            CaptureLevelMeter.Value = Math.Min(1d, Math.Max(peak, CaptureLevelMeter.Value * 0.82));
        }

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

        ApplyModdedMicUiState();
        UpdateOutputCableWarning();
        SaveSettings();
        UpdateStatusText();
    }

    /// <summary>List entry in the modded-mic combo that disables the modded route entirely.</summary>
    private static readonly AudioDeviceOption NoModdedMicOption = new("__no_modded_mic__", "Ingen moddad mic");

    private bool IsModdedMicSkipped => (ModdedInputCombo.SelectedItem as AudioDeviceOption)?.Id == NoModdedMicOption.Id;

    private void ApplyModdedMicUiState()
    {
        ApplyModdedMicUiState(IsModdedMicSkipped);
    }

    private void ApplyModdedMicUiState(bool skip)
    {
        // The hotkey still matters without a modded mic when push-to-talk gates the mix.
        bool hotkeyRelevant = !skip || IsPushToTalk;
        HotkeyConfigPanel.IsEnabled = hotkeyRelevant;
        HotkeyConfigPanel.Opacity = hotkeyRelevant ? 1.0 : 0.55;
        ModdedMeterPanel.Visibility = skip ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(DryMeterPanel, skip ? 3 : 1);
    }

    private bool IsPushToTalk => PushToTalkCheck.IsChecked == true;

    private void OnPushToTalkChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        ApplyModdedMicUiState();
        CancelPendingReleaseDelay();

        if (_router.IsRouting)
        {
            SetHotkeyMonitoringEnabled(!IsModdedMicSkipped || IsPushToTalk);
            ApplyEffectiveRoutingStates();
        }

        SaveSettings();
        UpdateStatusText();
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

    private void UpdateOutputCableWarning()
    {
        bool looksWrong = OutputDeviceCombo.SelectedItem is AudioDeviceOption output && !LooksLikeVirtualCable(output);
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
        Activate();
        Focus();
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        PersistWindowBounds();

        if (!_isReallyClosing && !App.StartupBenchmarkMode)
        {
            e.Cancel = true;
            Hide();

            if (!_trayBalloonShown)
            {
                _trayBalloonShown = true;
                _trayIcon.ShowBalloonTip(3000, "MicMixer körs kvar",
                    "Appen ligger i systemfältet. Dubbelklicka på ikonen för att öppna igen, eller högerklicka och välj Avsluta.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }

            return;
        }

        _levelTimer.Stop();
        _releaseDelayTimer.Stop();
        _musicTimer.Stop();
        _overlayIndicator?.Close();
        _overlayIndicator = null;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _router.Dispose();
        _appCapture?.Dispose();
        _music.Dispose();
        _hotkeyListener.Dispose();
    }

    private void SaveSettings()
    {
        _settings.NormalInputDeviceId = (DryInputCombo.SelectedItem as AudioDeviceOption)?.Id;
        _settings.SkipModdedMic = IsModdedMicSkipped;

        // Keep the last real device id so a stored selection survives toggling
        // "Ingen moddad mic" on and off between sessions.
        if (!IsModdedMicSkipped)
        {
            _settings.ModdedInputDeviceId = (ModdedInputCombo.SelectedItem as AudioDeviceOption)?.Id;
        }
        _settings.OutputDeviceId = (OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;
        _settings.HotkeyId = _hotkeyBinding.SerializedValue;
        _settings.ReleaseDelayMilliseconds = _releaseDelayMilliseconds;
        _settings.MusicMonitorDeviceId = (MonitorDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;

        // The checkbox is force-unchecked in external mode; keep the stored
        // preference so it comes back when the user returns to library mode.
        if (!_isExternalMode)
        {
            _settings.MonitorEnabled = MonitorEnabledCheck.IsChecked == true;
        }
        _settings.MusicVolume = (float)MusicVolumeSlider.Value;
        _settings.MonitorVolume = (float)MonitorVolumeSlider.Value;
        _settings.LinkVolumes = VolumeLinkToggle.IsChecked == true;
        _settings.PushToTalkMode = PushToTalkCheck.IsChecked == true;
        _settings.OverlayIndicatorEnabled = OverlayIndicatorCheck.IsChecked == true;
        _settings.OverlayVolumeMeterEnabled = OverlayVolumeMeterCheck.IsChecked == true;
        // Only the deliberate double-click mode survives a restart; the one-shot
        // state is transient by design.
        _settings.SingleTrackMode = _singleTrackMode == SingleTrackPlayMode.Always;
        _settings.MusicFolderPaths = _playlist.Folders.ToList();
        // Keep the legacy field pointing at the first custom folder so an older
        // app version reading these settings degrades gracefully.
        _settings.MusicFolderPath = _playlist.Folders.FirstOrDefault(folder => !PlaylistManager.IsDefaultFolder(folder));
        // Persist the user's own choice, not a temporary filter-chip override.
        _settings.DownloadFolderPath = _userDownloadFolderPath;
        _settings.ExternalCaptureMode = _isExternalMode;
        if ((ExternalAppCombo.SelectedItem as AudioAppOption)?.ProcessName is string externalAppName)
        {
            _settings.ExternalAppName = externalAppName;
        }

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings.");
            StatusText.Text = $"Kunde inte spara inställningar: {ex.Message}";
        }
    }

    private void UpdateStatusText()
    {
        if (_isCapturingHotkey)
        {
            HotkeyStateText.Text = "Tryck nu på den tangent eller musknapp som ska växla till moddad mic.";
        }

        if (_router.IsRouting)
        {
            bool pushToTalk = IsPushToTalk;
            var status = ComputeMicStatus();

            RoutingStateText.Text = "Routning aktiv";
            RoutingStateIcon.Data = (Geometry)FindResource("CheckCircleIcon");
            RoutingStateIcon.Fill = StatusTheme.BrushFor(status);

            ActiveSourceText.Text = status switch
            {
                MicStatus.Muted => "Aktiv källa: Tyst (push-to-talk)",
                MicStatus.Modded => "Aktiv källa: Moddad mic",
                _ => "Aktiv källa: Vanlig mic"
            };
            ActiveSourceText.Foreground = StatusTheme.InkFor(status);

            if (!_isCapturingHotkey)
            {
                if (pushToTalk)
                {
                    HotkeyStateText.Text = _hotkeyListener.IsPressed
                        ? IsModdedMicSkipped
                            ? $"{_hotkeyBinding.DisplayName} hålls nere — ljudet sänds"
                            : $"{_hotkeyBinding.DisplayName} hålls nere — moddad mic sänds"
                        : _isReleaseDelayPending
                            ? $"{_hotkeyBinding.DisplayName} släppt — mutar om {_releaseDelayMilliseconds} ms"
                            : $"Push-to-talk: håll {_hotkeyBinding.DisplayName} för att höras";
                }
                else
                {
                    HotkeyStateText.Text = IsModdedMicSkipped
                        ? "Moddad mic används ej — musiken mixas in så länge den spelar"
                        : _hotkeyListener.IsPressed
                            ? $"{_hotkeyBinding.DisplayName} hålls nere — moddad mic"
                            : _isReleaseDelayPending
                                ? $"{_hotkeyBinding.DisplayName} släppt — återgår om {_releaseDelayMilliseconds} ms"
                                : $"{_hotkeyBinding.DisplayName} — vanlig mic";
                }
            }

            StatusText.Text = $"Utgång: {((OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.FriendlyName ?? "—")}";

            UpdateCompactStatus();
            UpdateTrayIcon();
            return;
        }

        RoutingStateText.Text = "Routning stoppad";
        RoutingStateIcon.Data = (Geometry)FindResource("CircleOffIcon");
        RoutingStateIcon.Fill = StatusTheme.StoppedBrush;
        ActiveSourceText.Text = "Aktiv källa: Ingen";
        ActiveSourceText.Foreground = StatusTheme.StoppedInkBrush;
        if (!_isCapturingHotkey)
        {
            HotkeyStateText.Text = IsPushToTalk
                ? $"Push-to-talk aktivt — håll {_hotkeyBinding.DisplayName} för att höras när routningen är igång"
                : IsModdedMicSkipped
                    ? "Moddad mic används ej — endast vanlig mic + musik"
                    : $"Håll {_hotkeyBinding.DisplayName} för moddad mic";
        }

        StatusText.Text = _devicesLoaded
            ? ""
            : _isDevicesLoading
                ? "Laddar ljudenheter..."
                : _deviceLoadError ?? "Laddar ljudenheter...";

        UpdateCompactStatus();
        UpdateTrayIcon();
    }

    private void ApplyHotkeyBinding(HotkeyBinding binding)
    {
        _hotkeyBinding = binding;
        _isCapturingHotkey = false;
        CancelPendingReleaseDelay();
        _hotkeyListener.UpdateBinding(binding);
        ApplyEffectiveRoutingStates();
        UpdateHotkeyUi();
        SaveSettings();
        UpdateStatusText();
    }

    private void UpdateHotkeyUi()
    {
        HotkeyValueText.Text = _hotkeyBinding.DisplayName;
        CaptureHotkeyButton.Content = _isCapturingHotkey ? "Tryck nu..." : "Ändra";
        HotkeyCaptureHintText.Text = _isCapturingHotkey
            ? "Tryck valfri tangent eller musknapp nu."
            : "Klicka Ändra och tryck valfri tangent/musknapp.";
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

        SaveSettings();
        UpdateStatusText();
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

    private static AudioDeviceOption? SelectInputDevice(
        IReadOnlyList<AudioDeviceOption> devices,
        string? preferredId,
        Func<AudioDeviceOption, bool> heuristic,
        string? excludedId = null)
    {
        return devices.FirstOrDefault(device => device.Id == preferredId)
            ?? devices.FirstOrDefault(device => device.Id != excludedId && heuristic(device))
            ?? devices.FirstOrDefault(device => device.Id != excludedId)
            ?? devices.FirstOrDefault();
    }

    private static AudioDeviceOption? SelectOutputDevice(IReadOnlyList<AudioDeviceOption> devices, string? preferredId)
    {
        return devices.FirstOrDefault(device => device.Id == preferredId)
            ?? devices.FirstOrDefault(LooksLikeVirtualCable)
            ?? devices.FirstOrDefault();
    }

    private static bool LooksLikeVoiceModDevice(AudioDeviceOption device)
    {
        string name = device.FriendlyName;
        return name.Contains("voicemod", StringComparison.OrdinalIgnoreCase)
            || name.Contains("voice mod", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVirtualCable(AudioDeviceOption device)
    {
        string name = device.FriendlyName;
        return name.Contains("cable input", StringComparison.OrdinalIgnoreCase)
            || name.Contains("vb-audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("virtual cable", StringComparison.OrdinalIgnoreCase)
            || name.Contains("voicemeeter input", StringComparison.OrdinalIgnoreCase);
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
            MusicStatusText.Text = $"Pausad: {Path.GetFileNameWithoutExtension(pausedPath)} — spelar vidare när routningen är igång.";
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

        if (!_music.HasMonitorOutput && !_router.IsRouting)
        {
            return;
        }

        _musicWasAutoPaused = false;
        _music.Resume();

        if (_music.CurrentTrackPath is string resumedPath)
        {
            MusicStatusText.Text = $"Spelar: {Path.GetFileNameWithoutExtension(resumedPath)}";
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
        _overlayIndicator?.SetMusicActive(IsMusicRoutedOut());

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
        foreach (var track in _allTracks)
        {
            track.SetIsPlaying(playingPath != null
                && string.Equals(track.Path, playingPath, StringComparison.OrdinalIgnoreCase));
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
            MusicStatusText.Text = $"Kunde inte läsa musikmappen: {ex.Message}";
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
            MusicStatusText.Text = "Det där såg ut som en länk — startar nedladdning.";
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

    private void PlayTrack(string path)
    {
        CancelDelayedStart(null);

        if (!_music.HasMonitorOutput && !_router.IsRouting)
        {
            MusicStatusText.Text = "Starta routning eller aktivera medhörning för att spela musik.";
            return;
        }

        try
        {
            _music.Play(path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to play track {TrackPath}.", path);
            MusicStatusText.Text = $"Kunde inte spela låten: {ex.Message}";
            return;
        }

        _lastPlayedTrackPath = path;
        _musicWasAutoPaused = false;
        MusicStatusText.Text = $"Spelar: {Path.GetFileNameWithoutExtension(path)}";

        if (PlaylistListBox.ItemsSource is List<TrackItem> tracks)
        {
            PlaylistListBox.SelectedItem = tracks.FirstOrDefault(track =>
                string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        UpdateMusicUi();
    }

    private void PlayNextTrack()
    {
        while (_musicQueue.Count > 0)
        {
            string queued = _musicQueue[0];
            _musicQueue.RemoveAt(0);
            UpdateQueueUi();

            if (File.Exists(queued))
            {
                PlayTrack(queued);
                return;
            }
        }

        UpdateQueueUi();

        // Advance within the full playlist — the filter box is a search tool,
        // not a play scope.
        if (_allTracks.Count == 0)
        {
            _music.Stop();
            UpdateMusicUi();
            return;
        }

        string? current = _music.CurrentTrackPath ?? _lastPlayedTrackPath;
        int index = current == null
            ? -1
            : _allTracks.FindIndex(track => string.Equals(track.Path, current, StringComparison.OrdinalIgnoreCase));

        if (index >= 0 && index + 1 < _allTracks.Count)
        {
            PlayTrack(_allTracks[index + 1].Path);
        }
        else
        {
            _music.Stop();
            MusicStatusText.Text = "Spellistan är slut.";
            UpdateMusicUi();
        }
    }

    private void UpdateQueueUi()
    {
        if (_musicQueue.Count > 0 && !_isExternalMode)
        {
            QueueChipBtn.Content = $"Kö: {_musicQueue.Count}";
            QueueChipBtn.Visibility = Visibility.Visible;
            ClearQueueBtn.Visibility = Visibility.Visible;
        }
        else
        {
            QueueChipBtn.Visibility = Visibility.Collapsed;
            QueueChipBtn.IsChecked = false;
            ClearQueueBtn.Visibility = Visibility.Collapsed;
        }

        QueueListBox.ItemsSource = _musicQueue
            .Select((path, index) => new QueueEntry(index, path))
            .ToList();

        foreach (var track in _allTracks)
        {
            var positions = new List<int>();
            for (int i = 0; i < _musicQueue.Count; i++)
            {
                if (string.Equals(_musicQueue[i], track.Path, StringComparison.OrdinalIgnoreCase))
                {
                    positions.Add(i + 1);
                }
            }

            track.SetQueuePositions(positions);
        }
    }

    private void OnClearQueueClick(object sender, RoutedEventArgs e)
    {
        _musicQueue.Clear();
        UpdateQueueUi();
        MusicStatusText.Text = "Kön rensad.";
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
            fromIndex < 0 || fromIndex >= _musicQueue.Count)
        {
            return;
        }

        int insertIndex = ComputeQueueInsertIndex(e);
        if (insertIndex > fromIndex)
        {
            insertIndex--;
        }

        insertIndex = Math.Clamp(insertIndex, 0, _musicQueue.Count - 1);
        if (insertIndex == fromIndex)
        {
            return;
        }

        string moved = _musicQueue[fromIndex];
        _musicQueue.RemoveAt(fromIndex);
        _musicQueue.Insert(insertIndex, moved);
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

        _musicQueue.RemoveAt(entry.Index);
        UpdateQueueUi();

        if (File.Exists(entry.Path))
        {
            PlayTrack(entry.Path);
        }
        else
        {
            MusicStatusText.Text = "Filen finns inte längre.";
        }
    }

    private void OnQueueRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not QueueEntry entry || !IsQueueEntryCurrent(entry))
        {
            return;
        }

        _musicQueue.RemoveAt(entry.Index);
        UpdateQueueUi();
    }

    /// <summary>Entries are rebuilt on every queue change; reject any that got stale anyway.</summary>
    private bool IsQueueEntryCurrent(QueueEntry entry)
    {
        return entry.Index >= 0
            && entry.Index < _musicQueue.Count
            && string.Equals(_musicQueue[entry.Index], entry.Path, StringComparison.OrdinalIgnoreCase);
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
        SingleTrackPlayMode modeWhenTrackEnded = _singleTrackMode;

        Dispatcher.BeginInvoke(() =>
        {
            // The event is queued from an audio thread; by the time it runs the user
            // may have started another track, or this may be the end of an older
            // track than the last one we started. Acting on a stale event would
            // stop or advance from the wrong song — drop it instead.
            if (_isExternalMode
                || _music.HasTrack
                || !string.Equals(endedPath, _lastPlayedTrackPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Single-track mode: the engine already stopped by itself when the file
            // ran out — just reflect that instead of advancing. The queue stays
            // untouched; the next/play buttons resume it on explicit request.
            if (modeWhenTrackEnded != SingleTrackPlayMode.Off)
            {
                string finished = $"Klar: {Path.GetFileNameWithoutExtension(endedPath)}";

                if (modeWhenTrackEnded == SingleTrackPlayMode.Once)
                {
                    SetSingleTrackMode(SingleTrackPlayMode.Off, announce: false);
                    MusicStatusText.Text = $"{finished} — stannade. Enkellåtsläget stängdes av igen.";
                }
                else
                {
                    MusicStatusText.Text = $"{finished} — stannade (enkellåtsläge).";
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
            MusicStatusText.Text = "Markera en låt i listan att lägga i kön.";
            return;
        }

        _musicQueue.Add(track.Path);
        UpdateQueueUi();
        MusicStatusText.Text = $"Lade i kö: {track.Name}";
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
            MusicStatusText.Text = $"Kunde inte öppna mappen: {ex.Message}";
        }
    }

    private void OnMusicFolderOptionsClick(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = MusicFolderOptionsBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        // TextBlock as header so underscores in folder paths aren't eaten as access keys.
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = new TextBlock
            {
                Text = _playlist.Folders.Count == 1
                    ? "Musikmapp — lägg till fler för att blanda mappar"
                    : $"Musikmappar ({_playlist.Folders.Count}) — låtar från alla visas i listan"
            },
            IsEnabled = false
        });
        menu.Items.Add(new Separator());

        bool canRemove = _playlist.Folders.Count > 1;

        foreach (string folder in _playlist.Folders)
        {
            string captured = folder;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = new TextBlock { Text = GetFolderMenuLabel(folder) },
                Icon = CreateFolderBadge(_folderInfoByPath.GetValueOrDefault(folder)),
                IsCheckable = true,
                IsChecked = true,
                IsEnabled = canRemove,
                ToolTip = canRemove ? "Avmarkera för att ta bort mappen från listan" : "Minst en musikmapp måste finnas kvar"
            };
            item.Click += (_, _) => RemoveMusicFolder(captured);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var addItem = new System.Windows.Controls.MenuItem { Header = "Lägg till mapp…" };
        addItem.Click += (_, _) => AddMusicFolderViaDialog();
        menu.Items.Add(addItem);

        if (!_playlist.UsesOnlyDefaultFolder)
        {
            var resetItem = new System.Windows.Controls.MenuItem { Header = "Använd bara standardmappen" };
            resetItem.Click += (_, _) =>
            {
                _playlist.SetFolders(null);
                OnMusicFoldersChanged("Använder standardmusikmappen igen.");
            };
            menu.Items.Add(resetItem);
        }

        menu.IsOpen = true;
    }

    private void AddMusicFolderViaDialog()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Välj mapp med MP3-filer"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (_playlist.AddFolder(dialog.FolderName))
        {
            OnMusicFoldersChanged($"Lade till musikmapp: {dialog.FolderName}");
        }
        else
        {
            MusicStatusText.Text = "Mappen finns redan i listan.";
        }
    }

    private void RemoveMusicFolder(string folder)
    {
        if (_playlist.RemoveFolder(folder))
        {
            OnMusicFoldersChanged($"Tog bort musikmapp: {folder}");
        }
        else
        {
            MusicStatusText.Text = "Minst en musikmapp måste finnas kvar.";
        }
    }

    private void OnMusicFoldersChanged(string statusMessage)
    {
        RefreshMusicFolderUi();
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
        if (_userDownloadFolderPath == null || !_folderInfoByPath.ContainsKey(_userDownloadFolderPath))
        {
            _userDownloadFolderPath = infos[0].Path;
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
    /// override never touches <see cref="_userDownloadFolderPath"/>, so the
    /// stored preference survives the filter round trip.
    /// </summary>
    private void SyncDownloadFolderToFilter(bool announce)
    {
        if (DownloadFolderCombo.ItemsSource is not List<FolderInfo> infos || infos.Count == 0)
        {
            return;
        }

        string? filterPath = _folderChipActivationOrder.Count > 0 ? _folderChipActivationOrder[^1] : null;
        string targetPath = filterPath ?? _userDownloadFolderPath ?? infos[0].Path;

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
                ? $"Nya låtar laddas ner till {target.DisplayName} så länge mappfiltret är på."
                : $"Nya låtar laddas ner till {target.DisplayName} igen.";
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
            _userDownloadFolderPath = pickedPath;
        }

        SaveSettings();

        if (DownloadFolderCombo.SelectedItem is FolderInfo info)
        {
            MusicStatusText.Text = $"Nya låtar laddas ner till: {info.Path}";
        }
    }

    // --- External app capture (Spotify m.fl.) ---

    private static readonly System.Windows.Media.Brush CaptureIdleBrush = CreateFrozenBrush(0x9C, 0xA3, 0xAF);

    // The capture toggle deliberately uses the music panel's purple accent (never the
    // routing button's dark navy) so "sluta fånga appens ljud" can't be mistaken for
    // "stoppa routningen" — field testers mixed the two up when both were dark "Stoppa".
    private static readonly System.Windows.Media.Brush CaptureAccentBrush = CreateFrozenBrush(0x7C, 0x3A, 0xED);
    private static readonly System.Windows.Media.Brush CaptureAccentTintBrush = CreateFrozenBrush(0xED, 0xE9, 0xFE);
    private static readonly System.Windows.Media.Brush CaptureAccentTintBorderBrush = CreateFrozenBrush(0xDD, 0xD6, 0xFE);

    private const string CaptureIdleHint =
        "Välj appen som spelar musik (t.ex. Spotify) och klicka Fånga ljud. Du styr uppspelningen i appen som vanligt — ljudet mixas in i mic-kanalen.";
    private const string CaptureActiveHint =
        "Spela, pausa och byt låt i appen som vanligt. Musikvolymen nedan styr hur högt andra hör ljudet — du hör appen direkt, precis som annars.";

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
        CancelDelayedStart(null);

        if (external)
        {
            // Local playback cannot follow into app mode; stop it cleanly.
            _music.Stop();
            _musicWasAutoPaused = false;
            MusicStatusText.Text = "Välj appen som spelar musik och starta ljudinfångningen.";
            _ = RefreshAudioAppsAsync();
        }
        else
        {
            StopAppCapture();
            MusicStatusText.Text = "Dubbelklicka på en låt för att spela. Musiken mixas med micen när routning är aktiv.";
        }

        ApplyMusicModeUi();
        _ = ApplyMonitorConfigAsync();
        SaveSettings();
        UpdateMusicUi();
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
        // so reflect that fully: unchecked and grayed out, restored from settings on
        // the way back. SaveSettings skips MonitorEnabled while external, so the
        // user's real preference survives the round trip.
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
            MusicStatusText.Text = $"Kunde inte lista appar med ljud: {ex.Message}";
            return;
        }

        string? preferredName = (ExternalAppCombo.SelectedItem as AudioAppOption)?.ProcessName
            ?? _settings.ExternalAppName;

        ExternalAppCombo.ItemsSource = apps;
        ExternalAppCombo.SelectedItem =
            apps.FirstOrDefault(app => string.Equals(app.ProcessName, preferredName, StringComparison.OrdinalIgnoreCase))
            ?? apps.FirstOrDefault(app => app.ProcessName.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            ?? apps.FirstOrDefault(app => app.IsPlaying)
            ?? apps.FirstOrDefault();

        if (apps.Count == 0)
        {
            MusicStatusText.Text = "Inga appar med ljud hittades — starta t.ex. Spotify, spela något kort och uppdatera.";
        }
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
            MusicStatusText.Text = "Ljudinfångning stoppad.";
            return;
        }

        if (!ProcessLoopbackCapture.IsSupported)
        {
            MusicStatusText.Text = "Ljudinfångning per app kräver Windows 10 version 2004 eller senare.";
            return;
        }

        if (ExternalAppCombo.SelectedItem is not AudioAppOption app)
        {
            MusicStatusText.Text = "Välj först appen som spelar musik.";
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
            SaveSettings();
            UpdateExternalCaptureStatusText();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start process loopback capture for {App} (PID {ProcessId}).", app.DisplayName, app.ProcessId);
            capture.Error -= OnCaptureError;
            capture.Dispose();
            MusicStatusText.Text = $"Kunde inte fånga ljudet från {app.DisplayName}: {ex.Message}";
        }
        finally
        {
            _isCaptureStarting = false;
            UpdateCaptureUi();
        }
    }

    /// <summary>
    /// Keeps the capture status honest when routing starts or stops: "starta
    /// routningen så att andra hör" must not linger once routing is running.
    /// </summary>
    private void UpdateExternalCaptureStatusText()
    {
        if (!_isExternalMode || _appCapture == null || _captureTarget is not { } target)
        {
            return;
        }

        MusicStatusText.Text = _router.IsRouting
            ? $"Fångar ljud från {target.DisplayName}."
            : $"Fångar ljud från {target.DisplayName} — aktivera routningen så att andra hör.";
    }

    private void StopAppCapture()
    {
        var capture = _appCapture;
        _appCapture = null;
        _captureTarget = null;

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
            CaptureToggleText.Text = "Startar…";
            CaptureStateText.Text = "Startar ljudinfångning…";
            return;
        }

        if (capturing)
        {
            // Light purple "quiet" state while active: clearly not the dark routing button.
            CaptureToggleText.Text = "Sluta fånga";
            CaptureToggleIcon.Data = (Geometry)FindResource("StopIcon");
            CaptureToggleIcon.Fill = CaptureAccentBrush;
            CaptureToggleBtn.Background = CaptureAccentTintBrush;
            CaptureToggleBtn.BorderBrush = CaptureAccentTintBorderBrush;
            CaptureToggleBtn.Foreground = CaptureAccentBrush;
            CaptureStateIcon.Data = (Geometry)FindResource("CheckCircleIcon");
            CaptureStateIcon.Fill = StatusTheme.LiveInkBrush;
            CaptureStateText.Text = $"Fångar ljud från {_captureTarget?.DisplayName}";
            CaptureHintText.Text = CaptureActiveHint;
        }
        else
        {
            CaptureToggleText.Text = "Fånga ljud";
            CaptureToggleIcon.Data = (Geometry)FindResource("PlayIcon");
            CaptureToggleIcon.Fill = System.Windows.Media.Brushes.White;
            CaptureToggleBtn.Background = CaptureAccentBrush;
            CaptureToggleBtn.BorderBrush = CaptureAccentBrush;
            CaptureToggleBtn.Foreground = System.Windows.Media.Brushes.White;
            CaptureStateIcon.Data = (Geometry)FindResource("CircleOffIcon");
            CaptureStateIcon.Fill = CaptureIdleBrush;
            CaptureStateText.Text = "Ingen ljudinfångning";
            CaptureHintText.Text = CaptureIdleHint;
            CaptureLevelMeter.Value = 0;
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        CancelDelayedStart(null);

        if (_isExternalMode)
        {
            MediaKeySender.SendPlayPause();
            return;
        }

        if (_music.IsPlaying)
        {
            _music.Pause();
            _musicWasAutoPaused = false;
            if (_music.CurrentTrackPath is string playingPath)
            {
                MusicStatusText.Text = $"Pausad: {Path.GetFileNameWithoutExtension(playingPath)}";
            }

            UpdateMusicUi();
            return;
        }

        StartPlaybackFromCurrentState();
    }

    /// <summary>
    /// Starts playback exactly like the play button does when nothing is playing:
    /// resume a paused track, otherwise play the selected (or first) track.
    /// Shared by the play button and the delayed-start countdown.
    /// </summary>
    private void StartPlaybackFromCurrentState()
    {
        if (_music.IsPaused)
        {
            if (!_music.HasMonitorOutput && !_router.IsRouting)
            {
                MusicStatusText.Text = "Starta routning eller aktivera medhörning för att spela musik.";
                return;
            }

            _music.Resume();
            _musicWasAutoPaused = false;
            if (_music.CurrentTrackPath is string resumedPath)
            {
                MusicStatusText.Text = $"Spelar: {Path.GetFileNameWithoutExtension(resumedPath)}";
            }

            UpdateMusicUi();
            return;
        }

        if (PlaylistListBox.SelectedItem is TrackItem selected)
        {
            PlayTrack(selected.Path);
        }
        else if (_allTracks.Count > 0)
        {
            PlayTrack(_allTracks[0].Path);
        }
        else
        {
            MusicStatusText.Text = "Ingen musik ännu — klistra in en YouTube-länk ovan.";
        }
    }

    private void OnPrevTrackClick(object sender, RoutedEventArgs e)
    {
        CancelDelayedStart(null);

        if (_isExternalMode)
        {
            MediaKeySender.SendPreviousTrack();
            return;
        }

        if (!_music.HasTrack)
        {
            return;
        }

        if (_music.Position > TimeSpan.FromSeconds(3))
        {
            _music.Seek(TimeSpan.Zero);
            UpdateMusicUi();
            return;
        }

        string? current = _music.CurrentTrackPath ?? _lastPlayedTrackPath;
        if (current != null)
        {
            int index = _allTracks.FindIndex(track => string.Equals(track.Path, current, StringComparison.OrdinalIgnoreCase));
            if (index > 0)
            {
                PlayTrack(_allTracks[index - 1].Path);
                return;
            }
        }

        _music.Seek(TimeSpan.Zero);
        UpdateMusicUi();
    }

    private void OnNextTrackClick(object sender, RoutedEventArgs e)
    {
        CancelDelayedStart(null);

        if (_isExternalMode)
        {
            MediaKeySender.SendNextTrack();
            return;
        }

        PlayNextTrack();
    }

    // --- Delayed start & single-track mode ---

    private static readonly System.Windows.Media.Brush TransportIdleBrush = CreateFrozenBrush(0xE8, 0xEE, 0xF5);
    private static readonly System.Windows.Media.Brush TransportIdleBorderBrush = CreateFrozenBrush(0xD7, 0xDE, 0xE7);
    private static readonly System.Windows.Media.Brush TransportInkBrush = CreateFrozenBrush(0x10, 0x23, 0x3A);

    private bool IsDelayedStartCountingDown => _delayedStartTimer.IsEnabled;

    private static int ClampDelayedStartSeconds(int seconds) => Math.Clamp(seconds, 1, 60);

    private void OnDelayedPlayClick(object sender, RoutedEventArgs e)
    {
        if (IsDelayedStartCountingDown)
        {
            CancelDelayedStart("Fördröjd start avbruten.");
            return;
        }

        if (!_isExternalMode)
        {
            if (_music.IsPlaying)
            {
                MusicStatusText.Text = "Musiken spelar redan.";
                return;
            }

            if (!_music.HasMonitorOutput && !_router.IsRouting)
            {
                MusicStatusText.Text = "Starta routning eller aktivera medhörning för att spela musik.";
                return;
            }

            if (!_music.IsPaused && _allTracks.Count == 0)
            {
                MusicStatusText.Text = "Ingen musik ännu — klistra in en YouTube-länk ovan.";
                return;
            }
        }

        _delayedStartRemainingSeconds = ClampDelayedStartSeconds(_settings.DelayedStartSeconds);
        _delayedStartTimer.Start();
        UpdateDelayedPlayCountdownUi();
    }

    private void OnDelayedStartTick(object? sender, EventArgs e)
    {
        _delayedStartRemainingSeconds--;

        if (_delayedStartRemainingSeconds > 0)
        {
            UpdateDelayedPlayCountdownUi();
            return;
        }

        _delayedStartTimer.Stop();
        UpdateDelayedPlayIdleUi();

        if (_isExternalMode)
        {
            MediaKeySender.SendPlayPause();
            MusicStatusText.Text = "Skickade play till appen som spelar.";
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
        DelayedPlayText.Text = _delayedStartRemainingSeconds.ToString(CultureInfo.InvariantCulture);
        MusicStatusText.Text = $"Startar om {_delayedStartRemainingSeconds} s — klicka på knappen igen för att avbryta.";
    }

    private void UpdateDelayedPlayIdleUi()
    {
        DelayedPlayBtn.Background = TransportIdleBrush;
        DelayedPlayBtn.BorderBrush = TransportIdleBorderBrush;
        DelayedPlayIcon.Fill = TransportInkBrush;
        DelayedPlayText.Foreground = TransportInkBrush;
        DelayedPlayText.Text = $"{ClampDelayedStartSeconds(_settings.DelayedStartSeconds)} s";
    }

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
            _delayedStartTimer.Stop();
            _delayedStartRemainingSeconds = _settings.DelayedStartSeconds;
            _delayedStartTimer.Start();
            UpdateDelayedPlayCountdownUi();
            return;
        }

        UpdateDelayedPlayIdleUi();
        MusicStatusText.Text = $"Fördröjd start: {_settings.DelayedStartSeconds} s.";
    }

    /// <summary>How the single-track button behaves when the current track ends.</summary>
    private enum SingleTrackPlayMode
    {
        /// <summary>Playlist advances as usual.</summary>
        Off,
        /// <summary>Stop after the playing track, then fall back to <see cref="Off"/> automatically.</summary>
        Once,
        /// <summary>Stop after every track until the user turns it off (double-click).</summary>
        Always
    }

    private SingleTrackPlayMode _singleTrackMode;
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
            SetSingleTrackMode(_singleTrackMode == SingleTrackPlayMode.Off
                ? SingleTrackPlayMode.Once
                : SingleTrackPlayMode.Off);
        }
    }

    private void SetSingleTrackMode(SingleTrackPlayMode mode, bool save = true, bool announce = true)
    {
        _singleTrackAnnounceTimer.Stop();
        _singleTrackMode = mode;
        SingleTrackBtn.Tag = mode.ToString();

        if (save)
        {
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
                    "Enkellåtsläge: stannar när låten är klar, sedan stängs läget av igen. Dubbelklicka för att låta det ligga kvar.",
                SingleTrackPlayMode.Always =>
                    "Enkellåtsläge på tills du stänger av det — musiken stannar efter varje låt.",
                _ => "Enkellåtsläge av — spellistan fortsätter som vanligt."
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

        // Linked mode keeps a fixed offset between the sliders. The follower is
        // clamped at its edge, but the offset itself is preserved so the gap
        // comes back when the user drags in the other direction.
        if (VolumeLinkToggle.IsChecked == true && !_isSyncingLinkedVolume)
        {
            _isSyncingLinkedVolume = true;
            MonitorVolumeSlider.Value = Math.Clamp(e.NewValue + _volumeLinkOffset, 0d, 1d);
            _isSyncingLinkedVolume = false;
        }

        UpdateVolumePercentTexts();
        SaveSettings();
    }

    private void OnMonitorVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_music == null || _isUpdatingMusicUi)
        {
            return;
        }

        _music.MonitorVolume = (float)e.NewValue;

        if (VolumeLinkToggle.IsChecked == true && !_isSyncingLinkedVolume)
        {
            _isSyncingLinkedVolume = true;
            MusicVolumeSlider.Value = Math.Clamp(e.NewValue - _volumeLinkOffset, 0d, 1d);
            _isSyncingLinkedVolume = false;
        }

        UpdateVolumePercentTexts();
        SaveSettings();
    }

    private void OnVolumeLinkChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi)
        {
            return;
        }

        // Lock in whatever gap the sliders have right now as the linked offset.
        if (VolumeLinkToggle.IsChecked == true)
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

        SaveSettings();
        _ = ApplyMonitorConfigAsync();
    }

    private void OnMonitorDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        SaveSettings();
        _ = ApplyMonitorConfigAsync();
    }

    private async Task ApplyMonitorConfigAsync()
    {
        // In external mode the user already hears the app directly; monitoring
        // would double the audio with an offset.
        bool enabled = MonitorEnabledCheck.IsChecked == true && !_isExternalMode;
        string? deviceId = enabled ? (MonitorDeviceCombo.SelectedItem as AudioDeviceOption)?.Id : null;

        try
        {
            await Task.Run(() => _music.ConfigureMonitor(deviceId));
            PauseMusicIfClockLost();
            ResumeMusicIfAutoPaused();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to configure music monitor.");
            PauseMusicIfClockLost();
            MusicStatusText.Text = $"Kunde inte starta medhörning: {ex.Message}";
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

    private async Task StartDownloadAsync()
    {
        if (_isDownloading)
        {
            return;
        }

        string url = YoutubeUrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MusicStatusText.Text = "Klistra in en giltig länk (https://...).";
            return;
        }

        _isDownloading = true;
        DownloadBtn.IsEnabled = false;
        DownloadStatusRow.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = "Förbereder...";

        try
        {
            var toolStatus = new Progress<string>(text => DownloadStatusText.Text = text);
            await _toolBootstrapper.EnsureToolsAsync(toolStatus, CancellationToken.None);

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

            string downloadFolder = (DownloadFolderCombo.SelectedItem as FolderInfo)?.Path ?? _playlist.Folders[0];
            string? newFile = await _youTubeDownloader.DownloadAudioAsync(url, downloadFolder, progress, CancellationToken.None);

            YoutubeUrlBox.Text = "";
            RefreshPlaylist(newFile);
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            DownloadStatusText.Text = newFile != null
                ? $"Klar: {Path.GetFileNameWithoutExtension(newFile)}"
                : "Klar.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "YouTube download failed.");
            DownloadProgressBar.IsIndeterminate = false;
            DownloadStatusText.Text = "Nedladdning misslyckades.";
            MusicStatusText.Text = $"Fel vid nedladdning: {ex.Message}";
        }
        finally
        {
            _isDownloading = false;
            DownloadBtn.IsEnabled = true;
        }
    }

    private static AudioDeviceOption? SelectMonitorDevice(IReadOnlyList<AudioDeviceOption> devices, string? preferredId)
    {
        return devices.FirstOrDefault(device => device.Id == preferredId)
            ?? devices.FirstOrDefault(device => !LooksLikeVirtualCable(device))
            ?? devices.FirstOrDefault();
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
            ? $"{Info.Path}\nVisar bara låtar från den här mappen — klicka för att visa alla."
            : $"{Info.Path}\nKlicka för att bara visa låtar från den här mappen.";

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
    // exiting. "Avsluta" in the tray menu quits for real.

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
        menu.Items.Add("Visa MicMixer", null, OnTrayShowClick);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Avsluta", null, OnTrayExitClick);
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

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _lastNonMinimizedWindowState;
        }
        Activate();
    }

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

        // The overlay mirrors the tray state; SetState no-ops when unchanged.
        _overlayIndicator?.SetState(ToOverlayIndicatorState(newStatus));
        _overlayIndicator?.SetMusicActive(IsMusicRoutedOut());

        if (newStatus == _lastTrayStatus)
            return;

        try
        {
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = StatusTheme.RenderStatusIcon(newStatus, TrayIconPixelSize);
            oldIcon?.Dispose();

            _trayIcon.Text = newStatus switch
            {
                MicStatus.Live => "MicMixer — Vanlig mic hörs",
                MicStatus.Modded => "MicMixer — Moddad mic hörs",
                MicStatus.Muted => "MicMixer — Tyst (push-to-talk)",
                _ => "MicMixer — Routning stoppad"
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

    /// <summary>True while music actually reaches the virtual cable: routing runs, the push-to-talk gate is open and a source plays.</summary>
    private bool IsMusicRoutedOut()
    {
        if (!_router.IsRouting || !_router.OutputGateOpen)
        {
            return false;
        }

        if (!_isExternalMode)
        {
            return _music.IsPlaying;
        }

        // The capture object exists as long as external mode runs, even while the
        // app is paused or silent — require recently measured audio. The capture
        // meter holds peaks with ~1 s decay, so this also bridges short gaps.
        return _appCapture != null && CaptureLevelMeter.Value > 0.02;
    }

    private void OnOverlayIndicatorChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        ApplyOverlayIndicatorSetting(OverlayIndicatorCheck.IsChecked == true);
        SaveSettings();
    }

    private void OnOverlayVolumeMeterChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingMusicUi || _isUpdatingUi)
        {
            return;
        }

        if (_overlayIndicator is { } overlay)
        {
            overlay.MeterEnabled = OverlayVolumeMeterCheck.IsChecked == true;
        }

        UpdateOutputMeteringEnabled();
        SaveSettings();
    }

    /// <summary>
    /// The audio-thread level computation only runs while something can display
    /// it; otherwise the tap is a pure pass-through.
    /// </summary>
    private void UpdateOutputMeteringEnabled()
    {
        _router.OutputMeteringEnabled = _overlayIndicator != null && OverlayVolumeMeterCheck.IsChecked == true;
    }

    private void ApplyOverlayIndicatorSetting(bool enabled)
    {
        if (enabled)
        {
            try
            {
                _overlayIndicator ??= new OverlayIndicatorWindow();
                _overlayIndicator.MeterEnabled = OverlayVolumeMeterCheck.IsChecked == true;
                _overlayIndicator.SetState(ToOverlayIndicatorState(ComputeMicStatus()));
                _overlayIndicator.SetMusicActive(IsMusicRoutedOut());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create overlay indicator window.");
                _overlayIndicator = null;
                StatusText.Text = $"Kunde inte visa overlay-indikatorn: {ex.Message}";
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

    private enum LayoutMode
    {
        /// <summary>Routing and music side by side, equal width (the original layout).</summary>
        Wide,

        /// <summary>Still two columns, but the music player gets the larger share.</summary>
        Medium,

        /// <summary>Music player fills the window; routing folds into a status bar below.</summary>
        Narrow
    }

    private const double NarrowBreakpoint = 830;
    private const double WideBreakpoint = 1010;

    private LayoutMode _layoutMode = LayoutMode.Wide;
    private bool _layoutModeApplied;
    private bool _deviceGridStacked;
    private bool _delayPanelWrapped;
    private bool _musicHeaderCompact;
    private bool _volumesStacked;
    private bool _playlistButtonsCompact;

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var mode = ActualWidth < NarrowBreakpoint
            ? LayoutMode.Narrow
            : ActualWidth < WideBreakpoint
                ? LayoutMode.Medium
                : LayoutMode.Wide;

        if (mode != _layoutMode || !_layoutModeApplied)
        {
            _layoutMode = mode;
            _layoutModeApplied = true;
            ApplyLayoutMode();
        }

        if (_layoutMode == LayoutMode.Narrow)
        {
            UpdateNarrowRoutingHeight();
        }
    }

    private void ApplyLayoutMode()
    {
        bool narrow = _layoutMode == LayoutMode.Narrow;

        if (narrow)
        {
            // Music on top, full width — the part used most when space is scarce.
            Grid.SetRow(MusicCard, 0);
            Grid.SetColumn(MusicCard, 0);
            Grid.SetColumnSpan(MusicCard, 3);

            Grid.SetRow(RoutingScroll, 2);
            Grid.SetColumn(RoutingScroll, 0);
            Grid.SetColumnSpan(RoutingScroll, 3);
            RoutingScroll.Margin = new Thickness(0, 8, 0, 0);

            CompactStatusToggle.Visibility = Visibility.Visible;
            UpdateNarrowRoutingVisibility();
            UpdateNarrowRoutingHeight();
        }
        else
        {
            // The music player keeps the larger share as space shrinks.
            RoutingColumn.Width = _layoutMode == LayoutMode.Medium
                ? new GridLength(2, GridUnitType.Star)
                : new GridLength(1, GridUnitType.Star);
            MusicColumn.Width = _layoutMode == LayoutMode.Medium
                ? new GridLength(3, GridUnitType.Star)
                : new GridLength(1, GridUnitType.Star);

            Grid.SetRow(MusicCard, 0);
            Grid.SetColumn(MusicCard, 2);
            Grid.SetColumnSpan(MusicCard, 1);

            Grid.SetRow(RoutingScroll, 0);
            Grid.SetColumn(RoutingScroll, 0);
            Grid.SetColumnSpan(RoutingScroll, 1);
            RoutingScroll.Margin = new Thickness(0);
            RoutingScroll.MaxHeight = double.PositiveInfinity;
            RoutingScroll.Visibility = Visibility.Visible;

            CompactStatusToggle.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCompactStatusToggled(object sender, RoutedEventArgs e)
    {
        UpdateNarrowRoutingVisibility();
    }

    private void UpdateNarrowRoutingVisibility()
    {
        if (_layoutMode != LayoutMode.Narrow)
        {
            return;
        }

        bool expanded = CompactStatusToggle.IsChecked == true;
        RoutingScroll.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CompactStatusHint.Text = expanded ? "Dölj inställningar" : "Visa inställningar";
        CompactChevronRotate.Angle = expanded ? 0 : 180;
    }

    /// <summary>Caps the folded-out routing panel so the music player always keeps
    /// enough height for all its controls in narrow mode; the routing panel
    /// scrolls internally instead.</summary>
    private void UpdateNarrowRoutingHeight()
    {
        RoutingScroll.MaxHeight = Math.Clamp(ActualHeight - 470, 150, 420);
    }

    private void UpdateCompactStatus()
    {
        CompactStatusDot.Fill = RoutingStateIcon.Fill;
        var source = ActiveSourceText.Text.Replace("Aktiv källa: ", string.Empty);
        CompactStatusText.Text = _router.IsRouting
            ? $"Routning aktiv · {source}"
            : "Routning stoppad";
    }

    /// <summary>Stacks the three device pickers vertically when the card is too
    /// narrow for three columns side by side.</summary>
    private void OnDeviceGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool stacked = e.NewSize.Width < 430;
        if (stacked == _deviceGridStacked)
        {
            return;
        }

        _deviceGridStacked = stacked;
        var panels = new[] { DryDevicePanel, ModdedDevicePanel, OutputDevicePanel };
        for (int i = 0; i < panels.Length; i++)
        {
            if (stacked)
            {
                Grid.SetRow(panels[i], i);
                Grid.SetColumn(panels[i], 0);
                Grid.SetColumnSpan(panels[i], 3);
                panels[i].Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0);
            }
            else
            {
                Grid.SetRow(panels[i], 0);
                Grid.SetColumn(panels[i], i);
                Grid.SetColumnSpan(panels[i], 1);
                panels[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, i == panels.Length - 1 ? 0 : 8, 0);
            }
        }
    }

    /// <summary>Moves the release-delay field below the hotkey picker when the card
    /// cannot fit them side by side.</summary>
    private void OnHotkeyConfigSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wrapped = e.NewSize.Width < 350;
        if (wrapped == _delayPanelWrapped)
        {
            return;
        }

        _delayPanelWrapped = wrapped;
        Grid.SetRow(DelayPanel, wrapped ? 1 : 0);
        Grid.SetColumn(DelayPanel, wrapped ? 0 : 1);
        DelayPanel.Margin = wrapped ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        DelayPanel.HorizontalAlignment = wrapped ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
    }

    /// <summary>Drops the monitor-device combo to its own full-width row when the
    /// music card header gets cramped.</summary>
    private void OnMusicHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 470;
        if (compact == _musicHeaderCompact)
        {
            return;
        }

        _musicHeaderCompact = compact;
        if (compact)
        {
            Grid.SetRow(MonitorDeviceCombo, 1);
            Grid.SetColumn(MonitorDeviceCombo, 0);
            Grid.SetColumnSpan(MonitorDeviceCombo, 3);
            MonitorDeviceCombo.Width = double.NaN;
            MonitorDeviceCombo.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            MonitorDeviceCombo.Margin = new Thickness(0, 6, 0, 0);
        }
        else
        {
            Grid.SetRow(MonitorDeviceCombo, 0);
            Grid.SetColumn(MonitorDeviceCombo, 2);
            Grid.SetColumnSpan(MonitorDeviceCombo, 1);
            MonitorDeviceCombo.Width = 170;
            MonitorDeviceCombo.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            MonitorDeviceCombo.Margin = new Thickness(10, 0, 0, 0);
        }
    }

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

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save window bounds.");
        }
    }

    #endregion

    private sealed record AudioDeviceOption(string Id, string FriendlyName);
}
