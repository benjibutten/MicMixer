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
using MicMixer.Audio;
using MicMixer.Input;
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

        try
        {
            App.StartupTrace("Device refresh started");
            var (inputs, outputs) = await Task.Run(EnumerateActiveDevices);

            _isUpdatingUi = true;

            try
            {
                DryInputCombo.ItemsSource = inputs;
                ModdedInputCombo.ItemsSource = inputs;
                OutputDeviceCombo.ItemsSource = outputs;

                var drySelection = SelectInputDevice(
                    inputs,
                    selectedDryInput ?? _settings.NormalInputDeviceId,
                    device => !LooksLikeVoiceModDevice(device));

                DryInputCombo.SelectedItem = drySelection;

                var moddedSelection = SelectInputDevice(
                    inputs,
                    selectedModdedInput ?? _settings.ModdedInputDeviceId,
                    LooksLikeVoiceModDevice,
                    drySelection?.Id);

                if (moddedSelection == null && drySelection != null)
                {
                    moddedSelection = inputs.FirstOrDefault(device => device.Id != drySelection.Id);
                }

                ModdedInputCombo.SelectedItem = moddedSelection ?? drySelection;

                if ((ModdedInputCombo.SelectedItem as AudioDeviceOption)?.Id == (DryInputCombo.SelectedItem as AudioDeviceOption)?.Id)
                {
                    ModdedInputCombo.SelectedItem = inputs.FirstOrDefault(device => device.Id != (DryInputCombo.SelectedItem as AudioDeviceOption)?.Id)
                        ?? ModdedInputCombo.SelectedItem;
                }

                OutputDeviceCombo.SelectedItem = SelectOutputDevice(outputs, selectedOutput ?? _settings.OutputDeviceId);
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

            SaveSettings();
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

        if (DryInputCombo.SelectedItem is not AudioDeviceOption dryInput ||
            ModdedInputCombo.SelectedItem is not AudioDeviceOption moddedInput ||
            OutputDeviceCombo.SelectedItem is not AudioDeviceOption output)
        {
            StatusText.Text = "Välj vanlig mic, moddad mic och virtuell kabel.";
            return;
        }

        if (dryInput.Id == moddedInput.Id)
        {
            StatusText.Text = "Vanlig mic och moddad mic måste vara två olika enheter.";
            return;
        }

        if (!LooksLikeVirtualCable(output))
        {
            StatusText.Text = "Virtuell kabel ut ska normalt vara CABLE Input från VB-CABLE eller annan virtuell kabeldrivrutin.";
            return;
        }

        _isStartingRouting = true;
        ToggleBtn.IsEnabled = false;
        StatusText.Text = "Startar routning...";

        try
        {
            await Task.Run(() => StartRoutingByDeviceId(dryInput.Id, moddedInput.Id, output.Id));
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
            ToggleBtnText.Text = "Stoppa";
            ToggleBtnIcon.Data = (Geometry)FindResource("StopIcon");
            DryInputCombo.IsEnabled = false;
            ModdedInputCombo.IsEnabled = false;
            OutputDeviceCombo.IsEnabled = false;
            SaveSettings();
            UpdateStatusText();
        }
        else
        {
            UpdateStatusText();
        }
    }

    private void StartRoutingByDeviceId(string dryInputId, string moddedInputId, string outputId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var dryInput = enumerator.GetDevice(dryInputId);
        var moddedInput = enumerator.GetDevice(moddedInputId);
        var output = enumerator.GetDevice(outputId);

        _router.SetUseModdedInput(GetEffectiveModdedState());
        _router.Start(dryInput, moddedInput, output);
    }

    private void StopRouting()
    {
        CancelPendingReleaseDelay();
        _router.Stop();
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

        SaveSettings();
        UpdateStatusText();
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
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _router.Dispose();
        _hotkeyListener.Dispose();
    }

    private void SaveSettings()
    {
        _settings.NormalInputDeviceId = (DryInputCombo.SelectedItem as AudioDeviceOption)?.Id;
        _settings.ModdedInputDeviceId = (ModdedInputCombo.SelectedItem as AudioDeviceOption)?.Id;
        _settings.OutputDeviceId = (OutputDeviceCombo.SelectedItem as AudioDeviceOption)?.Id;
        _settings.HotkeyId = _hotkeyBinding.SerializedValue;
        _settings.ReleaseDelayMilliseconds = _releaseDelayMilliseconds;

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
                HotkeyStateText.Text = _hotkeyListener.IsPressed
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
            HotkeyStateText.Text = $"Håll {_hotkeyBinding.DisplayName} för moddad mic";
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