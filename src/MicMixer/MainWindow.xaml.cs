using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
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
using MicMixer.Settings;
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
    private readonly List<string> _musicQueue = new();
    private List<TrackItem> _allTracks = new();
    private string? _lastPlayedTrackPath;
    private string? _acknowledgedNonCableOutputId;
    private bool _musicWasAutoPaused;
    private bool _isSeekDragging;
    private bool _isUpdatingMusicUi;
    private bool _isDownloading;
    private AppSettings _settings;
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
    private TrayIconState _lastTrayState = TrayIconState.Stopped;

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
        InitializeComponent();
        App.StartupTrace("InitializeComponent done");

        using var windowIcon = RenderTrayIcon(TrayIconState.Normal);
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            windowIcon.Handle,
            System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

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

        ApplyModdedMicUiState(_settings.SkipModdedMic);

        _music.MusicVolume = Math.Clamp(_settings.MusicVolume, 0f, 1f);
        _music.MonitorVolume = Math.Clamp(_settings.MonitorVolume, 0f, 1f);
        _isUpdatingMusicUi = true;
        MusicVolumeSlider.Value = _music.MusicVolume;
        MonitorVolumeSlider.Value = _music.MonitorVolume;
        MonitorEnabledCheck.IsChecked = _settings.MonitorEnabled;
        _isUpdatingMusicUi = false;
        UpdateVolumePercentTexts();
        RefreshPlaylist(null);

        _musicTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _musicTimer.Tick += OnMusicTimerTick;
        _musicTimer.Start();

        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _levelTimer.Tick += OnLevelTimerTick;
        _levelTimer.Start();

        UpdateStatusText();
        UpdateTrayIcon();
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

        if (!skipModded && dryInput.Id == moddedInput!.Id)
        {
            StatusText.Text = "Vanlig mic och moddad mic måste vara två olika enheter.";
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

        try
        {
            await Task.Run(() => StartRoutingByDeviceId(dryInput.Id, skipModded ? null : moddedInput!.Id, output.Id));
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
            if (!skipModded)
            {
                SetHotkeyMonitoringEnabled(true);
            }

            ToggleBtnText.Text = "Stoppa";
            ToggleBtnIcon.Data = (Geometry)FindResource("StopIcon");
            DryInputCombo.IsEnabled = false;
            ModdedInputCombo.IsEnabled = false;
            OutputDeviceCombo.IsEnabled = false;
            SaveSettings();
            UpdateStatusText();
            ResumeMusicIfAutoPaused();
        }
        else
        {
            UpdateStatusText();
        }
    }

    private void StartRoutingByDeviceId(string dryInputId, string? moddedInputId, string outputId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dryInput = enumerator.GetDevice(dryInputId);
        var moddedInput = moddedInputId != null ? enumerator.GetDevice(moddedInputId) : null;
        var output = enumerator.GetDevice(outputId);

        _router.SetUseModdedInput(false);
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
        HotkeyDelayCard.IsEnabled = !skip;
        HotkeyDelayCard.Opacity = skip ? 0.55 : 1.0;
        ModdedMeterPanel.Visibility = skip ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(DryMeterPanel, skip ? 3 : 1);
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
        if (!_isReallyClosing && !App.StartupBenchmarkMode)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.Visible = true;
            return;
        }

        _levelTimer.Stop();
        _releaseDelayTimer.Stop();
        _musicTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _router.Dispose();
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
        _settings.MonitorEnabled = MonitorEnabledCheck.IsChecked == true;
        _settings.MusicVolume = (float)MusicVolumeSlider.Value;
        _settings.MonitorVolume = (float)MonitorVolumeSlider.Value;

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
            RoutingStateText.Text = "Routning aktiv";
            RoutingStateIcon.Data = (Geometry)FindResource("CheckCircleIcon");
            RoutingStateIcon.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F766E"));
            ActiveSourceText.Text = _router.UseModdedInput ? "Aktiv källa: Moddad mic" : "Aktiv källa: Vanlig mic";
            ActiveSourceText.Foreground = _router.UseModdedInput
                ? new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#C2410C"))
                : new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F766E"));
            if (!_isCapturingHotkey)
            {
                HotkeyStateText.Text = IsModdedMicSkipped
                    ? "Moddad mic används ej — musiken mixas in så länge den spelar"
                    : _hotkeyListener.IsPressed
                        ? $"{_hotkeyBinding.DisplayName} hålls nere — moddad mic"
                        : _isReleaseDelayPending
                            ? $"{_hotkeyBinding.DisplayName} släppt — återgår om {_releaseDelayMilliseconds} ms"
                            : $"{_hotkeyBinding.DisplayName} — vanlig mic";
            }

            StatusText.Text = $"Utgång: {((OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.FriendlyName ?? "—")}";

            UpdateTrayIcon();
            return;
        }

        RoutingStateText.Text = "Routning stoppad";
        RoutingStateIcon.Data = (Geometry)FindResource("CircleOffIcon");
        RoutingStateIcon.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#9CA3AF"));
        ActiveSourceText.Text = "Aktiv källa: Ingen";
        ActiveSourceText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F766E"));
        if (!_isCapturingHotkey)
        {
            HotkeyStateText.Text = IsModdedMicSkipped
                ? "Moddad mic används ej — endast vanlig mic + musik"
                : $"Håll {_hotkeyBinding.DisplayName} för moddad mic";
        }

        StatusText.Text = _devicesLoaded
            ? ""
            : _isDevicesLoading
                ? "Laddar ljudenheter..."
                : _deviceLoadError ?? "Laddar ljudenheter...";

        UpdateTrayIcon();
    }

    private void ApplyHotkeyBinding(HotkeyBinding binding)
    {
        _hotkeyBinding = binding;
        _isCapturingHotkey = false;
        CancelPendingReleaseDelay();
        _hotkeyListener.UpdateBinding(binding);
        _router.SetUseModdedInput(GetEffectiveModdedState());
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
        if (IsModdedMicSkipped)
        {
            CancelPendingReleaseDelay();
            _router.SetUseModdedInput(false);
            return;
        }

        if (!_router.IsRouting)
        {
            CancelPendingReleaseDelay();
            _router.SetUseModdedInput(false);
            return;
        }

        if (isPressed)
        {
            CancelPendingReleaseDelay();
            _router.SetUseModdedInput(true);
            UpdateStatusText();
            return;
        }

        if (_releaseDelayMilliseconds <= 0)
        {
            CancelPendingReleaseDelay();
            _router.SetUseModdedInput(false);
            UpdateStatusText();
            return;
        }

        _isReleaseDelayPending = true;
        _releaseDelayTimer.Stop();
        _releaseDelayTimer.Interval = TimeSpan.FromMilliseconds(_releaseDelayMilliseconds);
        _releaseDelayTimer.Start();
        _router.SetUseModdedInput(true);
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
        _router.SetUseModdedInput(false);
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
                _router.SetUseModdedInput(false);
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

    private bool GetEffectiveModdedState()
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

    private static string FormatTrackTime(TimeSpan time)
    {
        return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private void RefreshPlaylist(string? selectPath)
    {
        try
        {
            string? current = selectPath ?? (PlaylistListBox.SelectedItem as TrackItem)?.Path;
            _allTracks = _playlist.GetTracks()
                .Select(path => new TrackItem(path, Path.GetFileNameWithoutExtension(path)))
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

        var visible = filter.Length == 0
            ? _allTracks
            : _allTracks.Where(track => track.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

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
        if (_musicQueue.Count > 0)
        {
            QueueCountText.Text = $"Kö: {_musicQueue.Count}";
            QueueCountText.ToolTip = string.Join(
                "\n",
                _musicQueue.Select((path, index) => $"{index + 1}. {Path.GetFileNameWithoutExtension(path)}"));
            ClearQueueBtn.Visibility = Visibility.Visible;
        }
        else
        {
            QueueCountText.Text = "";
            QueueCountText.ToolTip = null;
            ClearQueueBtn.Visibility = Visibility.Collapsed;
        }

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

    private void OnMusicTrackEnded(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(PlayNextTrack);
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
        try
        {
            Directory.CreateDirectory(_playlist.MusicFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = _playlist.MusicFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open music folder.");
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
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
        PlayNextTrack();
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
        UpdateVolumePercentTexts();
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
        bool enabled = MonitorEnabledCheck.IsChecked == true;
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
            MusicStatusText.Text = $"Kunde inte starta medhörning: {ex.Message}";
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

            string? newFile = await _youTubeDownloader.DownloadAudioAsync(url, _playlist.MusicFolder, progress, CancellationToken.None);

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

    private sealed class TrackItem : INotifyPropertyChanged
    {
        private string _queueText = "";
        private Visibility _queueVisibility = Visibility.Collapsed;
        private Visibility _playingVisibility = Visibility.Collapsed;

        public TrackItem(string path, string name)
        {
            Path = path;
            Name = name;
        }

        public string Path { get; }

        public string Name { get; }

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

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            _trayIcon.Visible = true;
        }
    }

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "MicMixer",
            Icon = RenderTrayIcon(TrayIconState.Stopped),
            Visible = false
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
        WindowState = WindowState.Normal;
        Activate();
        _trayIcon.Visible = false;
    }

    private void UpdateTrayIcon()
    {
        var newState = _router.IsRouting
            ? (_router.UseModdedInput ? TrayIconState.Modded : TrayIconState.Normal)
            : TrayIconState.Stopped;

        if (newState == _lastTrayState)
            return;

        try
        {
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = RenderTrayIcon(newState);
            oldIcon?.Dispose();

            _trayIcon.Text = newState switch
            {
                TrayIconState.Normal => "MicMixer — Vanlig mic",
                TrayIconState.Modded => "MicMixer — Moddad mic",
                _ => "MicMixer — Stoppad"
            };

            _lastTrayState = newState;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update tray icon to state {TrayState}.", newState);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon RenderTrayIcon(TrayIconState state)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);

        System.Drawing.Color bgColor = state switch
        {
            TrayIconState.Normal => System.Drawing.Color.FromArgb(15, 118, 110),   // teal
            TrayIconState.Modded => System.Drawing.Color.FromArgb(194, 65, 12),    // orange
            _ => System.Drawing.Color.FromArgb(107, 114, 128)                       // gray
        };

        // Circle background
        using (var bgBrush = new SolidBrush(bgColor))
        {
            g.FillEllipse(bgBrush, 1, 1, size - 2, size - 2);
        }

        // Mic body (white) — simple rounded rectangle + circle cap
        using var micBrush = new SolidBrush(System.Drawing.Color.White);
        using var micPen = new System.Drawing.Pen(System.Drawing.Color.White, 1.8f);

        // Mic capsule
        var capsuleRect = new RectangleF(12, 6, 8, 12);
        using var capsulePath = new GraphicsPath();
        capsulePath.AddArc(capsuleRect.X, capsuleRect.Y, capsuleRect.Width, 8, 180, 180);
        capsulePath.AddLine(capsuleRect.Right, capsuleRect.Y + 4, capsuleRect.Right, capsuleRect.Bottom - 2);
        capsulePath.AddArc(capsuleRect.X, capsuleRect.Bottom - 6, capsuleRect.Width, 6, 0, 180);
        capsulePath.AddLine(capsuleRect.X, capsuleRect.Bottom - 3, capsuleRect.X, capsuleRect.Y + 4);
        g.FillPath(micBrush, capsulePath);

        // Arm arc
        g.DrawArc(micPen, 9, 10, 14, 12, 0, 180);

        // Stand line
        g.DrawLine(micPen, 16, 22, 16, 26);
        g.DrawLine(micPen, 12, 26, 20, 26);

        IntPtr hIcon = bmp.GetHicon();

        try
        {
            using Icon temporaryIcon = System.Drawing.Icon.FromHandle(hIcon);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    private sealed record AudioDeviceOption(string Id, string FriendlyName);

    private enum TrayIconState
    {
        Stopped,
        Normal,
        Modded
    }
}
