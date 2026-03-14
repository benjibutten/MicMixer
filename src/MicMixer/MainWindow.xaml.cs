using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using MicMixer.Audio;
using MicMixer.Input;
using MicMixer.Settings;
using NAudio.CoreAudioApi;

namespace MicMixer;

public partial class MainWindow : Window
{
    private const int MaxReleaseDelayMilliseconds = 5_000;

    private readonly AudioRouter _router = new();
    private readonly GlobalHotkeyListener _hotkeyListener = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly DispatcherTimer _levelTimer;
    private readonly DispatcherTimer _releaseDelayTimer;
    private AppSettings _settings;
    private HotkeyBinding _hotkeyBinding = HotkeyBinding.Default;
    private int _releaseDelayMilliseconds;
    private bool _isCapturingHotkey;
    private bool _isReleaseDelayPending;
    private bool _isUpdatingUi;

    public MainWindow()
    {
        _settings = _settingsStore.Load();
        _releaseDelayMilliseconds = ClampReleaseDelay(_settings.ReleaseDelayMilliseconds);
        _releaseDelayTimer = new DispatcherTimer();
        _releaseDelayTimer.Tick += OnReleaseDelayTimerTick;
        InitializeComponent();

        _router.Error += OnRouterError;
        _hotkeyListener.Error += OnHotkeyError;
        _hotkeyListener.PressedStateChanged += OnHotkeyPressedStateChanged;

        _hotkeyBinding = HotkeyBinding.Parse(_settings.HotkeyId);
        _hotkeyListener.UpdateBinding(_hotkeyBinding);
        UpdateHotkeyUi();
        ReleaseDelayTextBox.Text = _releaseDelayMilliseconds.ToString(CultureInfo.InvariantCulture);

        RefreshDevices();

        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _levelTimer.Tick += OnLevelTimerTick;
        _levelTimer.Start();

        UpdateStatusText();
        Closing += OnClosing;
    }

    private void RefreshDevices()
    {
        var selectedDryInput = (DryInputCombo.SelectedItem as MMDevice)?.ID;
        var selectedModdedInput = (ModdedInputCombo.SelectedItem as MMDevice)?.ID;
        var selectedOutput = (OutputDeviceCombo.SelectedItem as MMDevice)?.ID;

        var inputs = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var outputs = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

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
                drySelection?.ID);

            if (moddedSelection == null && drySelection != null)
            {
                moddedSelection = inputs.FirstOrDefault(device => device.ID != drySelection.ID);
            }

            ModdedInputCombo.SelectedItem = moddedSelection ?? drySelection;

            if ((ModdedInputCombo.SelectedItem as MMDevice)?.ID == (DryInputCombo.SelectedItem as MMDevice)?.ID)
            {
                ModdedInputCombo.SelectedItem = inputs.FirstOrDefault(device => device.ID != (DryInputCombo.SelectedItem as MMDevice)?.ID)
                    ?? ModdedInputCombo.SelectedItem;
            }

            OutputDeviceCombo.SelectedItem = SelectOutputDevice(outputs, selectedOutput ?? _settings.OutputDeviceId);
        }
        finally
        {
            _isUpdatingUi = false;
        }

        SaveSettings();
        UpdateStatusText();
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_router.IsRouting)
        {
            StopRouting();
            return;
        }

        if (DryInputCombo.SelectedItem is not MMDevice dryInput ||
            ModdedInputCombo.SelectedItem is not MMDevice moddedInput ||
            OutputDeviceCombo.SelectedItem is not MMDevice output)
        {
            StatusText.Text = "Välj vanlig mic, moddad mic och virtuell kabel.";
            return;
        }

        if (dryInput.ID == moddedInput.ID)
        {
            StatusText.Text = "Vanlig mic och moddad mic måste vara två olika enheter.";
            return;
        }

        if (!LooksLikeVirtualCable(output))
        {
            StatusText.Text = "Virtuell kabel ut ska normalt vara CABLE Input från VB-CABLE eller annan virtuell kabeldrivrutin.";
            return;
        }

        _router.SetUseModdedInput(GetEffectiveModdedState());
        _router.Start(dryInput, moddedInput, output);

        if (_router.IsRouting)
        {
            ToggleBtn.Content = "Stoppa routning";
            DryInputCombo.IsEnabled = false;
            ModdedInputCombo.IsEnabled = false;
            OutputDeviceCombo.IsEnabled = false;
            SaveSettings();
            UpdateStatusText();
        }
    }

    private void StopRouting()
    {
        CancelPendingReleaseDelay();
        _router.Stop();
        ToggleBtn.Content = "Aktivera routning";
        DryLevelMeter.Value = 0;
        ModdedLevelMeter.Value = 0;
        DryInputCombo.IsEnabled = true;
        ModdedInputCombo.IsEnabled = true;
        OutputDeviceCombo.IsEnabled = true;
        UpdateStatusText();
    }

    private void OnRouterError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StopRouting();
            StatusText.Text = $"Fel: {message}";
        });
    }

    private void OnHotkeyError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() => StatusText.Text = $"Hotkey-fel: {message}");
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
            if (!DryInputCombo.IsEnabled)
            {
                StopRouting();
            }
        }

        _hotkeyListener.VerifyPressedState();

        UpdateStatusText();
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

    private void OnPreviewHotkeyKeyDown(object sender, KeyEventArgs e)
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

    private void OnReleaseDelayTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter and not Key.Return)
        {
            return;
        }

        e.Handled = true;
        ApplyReleaseDelayFromTextBox();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_router.IsRouting)
            StopRouting();

        RefreshDevices();
    }

    private void OnGuideNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _levelTimer.Stop();
        _releaseDelayTimer.Stop();
        _router.Dispose();
        _hotkeyListener.Dispose();
        _enumerator.Dispose();
    }

    private void SaveSettings()
    {
        _settings.NormalInputDeviceId = (DryInputCombo.SelectedItem as MMDevice)?.ID;
        _settings.ModdedInputDeviceId = (ModdedInputCombo.SelectedItem as MMDevice)?.ID;
        _settings.OutputDeviceId = (OutputDeviceCombo.SelectedItem as MMDevice)?.ID;
        _settings.HotkeyId = _hotkeyBinding.SerializedValue;
        _settings.ReleaseDelayMilliseconds = _releaseDelayMilliseconds;

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
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
            ActiveSourceText.Text = _router.UseModdedInput ? "Aktiv källa: Moddad mic" : "Aktiv källa: Vanlig mic";
            if (!_isCapturingHotkey)
            {
                HotkeyStateText.Text = _hotkeyListener.IsPressed
                    ? $"{_hotkeyBinding.DisplayName} hålls nere och VoiceMod-spåret skickas ut."
                    : _isReleaseDelayPending
                        ? $"{_hotkeyBinding.DisplayName} är släppt, men återgår först om {_releaseDelayMilliseconds} ms."
                        : $"{_hotkeyBinding.DisplayName} är släppt och vanliga micen skickas ut.";
            }

            StatusText.Text = $"Utgång: {((OutputDeviceCombo.SelectedItem as MMDevice)?.FriendlyName ?? "Ingen vald")}. Välj samma kabels inspelningssida i dina appar.";
            return;
        }

        RoutingStateText.Text = "Routning stoppad";
        ActiveSourceText.Text = "Aktiv källa: Ingen";
        if (!_isCapturingHotkey)
        {
            HotkeyStateText.Text = _isReleaseDelayPending
                ? $"Återgång väntar {_releaseDelayMilliseconds} ms efter att {_hotkeyBinding.DisplayName} släpptes."
                : $"Håll nere {_hotkeyBinding.DisplayName} för att använda moddad mic när routningen är aktiv.";
        }

        StatusText.Text = $"Tips: välj VoiceMods virtuella mic som moddad mic, CABLE Input som virtuell kabel ut och använd {_releaseDelayMilliseconds} ms om du vill ha mjukare återgång efter släpp.";
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
        CaptureHotkeyButton.Content = _isCapturingHotkey ? "Tryck tangent eller musknapp nu" : "Tryck och välj hotkey";
        HotkeyCaptureHintText.Text = _isCapturingHotkey
            ? "Nästa tangent eller musknapp du trycker på blir global hotkey. Det fungerar även med mittenknapp och musens sidoknappar."
            : "Klicka här ovanför och tryck sedan valfri tangent eller musknapp, till exempel Alt, F8, musens mittenknapp eller XButton1.";
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

    private static MMDevice? SelectInputDevice(
        IReadOnlyList<MMDevice> devices,
        string? preferredId,
        Func<MMDevice, bool> heuristic,
        string? excludedId = null)
    {
        return devices.FirstOrDefault(device => device.ID == preferredId)
            ?? devices.FirstOrDefault(device => device.ID != excludedId && heuristic(device))
            ?? devices.FirstOrDefault(device => device.ID != excludedId)
            ?? devices.FirstOrDefault();
    }

    private static MMDevice? SelectOutputDevice(IReadOnlyList<MMDevice> devices, string? preferredId)
    {
        return devices.FirstOrDefault(device => device.ID == preferredId)
            ?? devices.FirstOrDefault(LooksLikeVirtualCable)
            ?? devices.FirstOrDefault();
    }

    private static bool LooksLikeVoiceModDevice(MMDevice device)
    {
        string name = device.FriendlyName;
        return name.Contains("voicemod", StringComparison.OrdinalIgnoreCase)
            || name.Contains("voice mod", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVirtualCable(MMDevice device)
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
}