using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MicMixer.Audio;
using MicMixer.Input;
using MicMixer.Settings;
using Serilog;

namespace MicMixer.UI;

/// <summary>
/// First-run walkthrough: explains the app and the virtual cable, recognizes an
/// installed cable (or explains how to install one), and collects the essential
/// choices. Nothing is written until the caller applies them after Finish.
/// </summary>
internal partial class SetupGuideWindow : Window
{
    private readonly StackPanel[] _pages;
    private List<AudioDeviceOption> _inputs = [];
    private List<AudioDeviceOption> _outputs = [];
    private HotkeyBinding _hotkey;
    private bool _isCapturingHotkey;
    private int _step;

    public SetupGuideWindow(AppSettings current)
    {
        InitializeComponent();
        _pages = [WelcomePage, CablePage, MicPage, OutputPage, AppPage, HotkeyPage, DonePage];
        StepProgress.Maximum = _pages.Length - 1;

        _hotkey = HotkeyBinding.Parse(current.HotkeyId);
        PushToTalkCheck.IsChecked = current.PushToTalkMode;
        (current.ModifiedVoiceMode switch
        {
            ModifiedVoiceMode.ExternalMicrophone => VoiceExternalRadio,
            ModifiedVoiceMode.LocalProfile => VoiceLocalRadio,
            _ => VoiceNoneRadio
        }).IsChecked = true;

        LoadDevices(current.NormalInputDeviceId, current.ModdedInputDeviceId, current.OutputDeviceId, current.MusicMonitorDeviceId);
        UpdateHotkeyTexts();
        ShowStep(0);
    }

    public bool StartRoutingWhenDone => StartRoutingCheck.IsChecked == true;

    /// <summary>
    /// Closed with "Skip for now". Closing the window otherwise (for example to restart
    /// Windows after installing a cable) is not a skip, so the guide comes back.
    /// </summary>
    public bool Skipped { get; private set; }

    /// <summary>Writes the choices into <paramref name="settings"/>; only call after Finish.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.NormalInputDeviceId = SelectedId(MicCombo);
        settings.ModifiedVoiceMode = SelectedVoiceMode;
        settings.SkipModdedMic = SelectedVoiceMode == ModifiedVoiceMode.None;
        if (SelectedVoiceMode == ModifiedVoiceMode.ExternalMicrophone)
        {
            settings.ModdedInputDeviceId = SelectedId(ExternalMicCombo);
        }

        settings.OutputDeviceId = SelectedId(OutputCombo);
        settings.MusicMonitorDeviceId = SelectedId(MonitorCombo);
        settings.HotkeyId = _hotkey.SerializedValue;
        settings.PushToTalkMode = PushToTalkCheck.IsChecked == true;
    }

    private ModifiedVoiceMode SelectedVoiceMode =>
        VoiceExternalRadio.IsChecked == true ? ModifiedVoiceMode.ExternalMicrophone
        : VoiceLocalRadio.IsChecked == true ? ModifiedVoiceMode.LocalProfile
        : ModifiedVoiceMode.None;

    private static string? SelectedId(System.Windows.Controls.ComboBox combo) => (combo.SelectedItem as AudioDeviceOption)?.Id;

    /// <summary>Reads the devices again, keeping what is selected where it still exists.</summary>
    private void LoadDevices(string? micId, string? externalId, string? outputId, string? monitorId)
    {
        try
        {
            (_inputs, _outputs) = AudioDevices.EnumerateActive();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Setup guide could not read audio devices.");
            _inputs = [];
            _outputs = [];
        }

        MicCombo.ItemsSource = _inputs;
        MicCombo.SelectedItem = AudioDevices.SelectInput(_inputs, micId, AudioDevices.LooksLikeNormalMic);
        ExternalMicCombo.ItemsSource = _inputs;
        ExternalMicCombo.SelectedItem = AudioDevices.SelectInput(_inputs, externalId, AudioDevices.LooksLikeVoiceModDevice, SelectedId(MicCombo));
        OutputCombo.ItemsSource = _outputs;
        OutputCombo.SelectedItem = AudioDevices.SelectCableOutput(_outputs, outputId);
        MonitorCombo.ItemsSource = _outputs;
        MonitorCombo.SelectedItem = AudioDevices.SelectMonitor(_outputs, monitorId);

        NoMicText.Visibility = _inputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCableTexts();
    }

    private void UpdateCableTexts()
    {
        AudioDeviceOption? playbackEnd = OutputCombo.SelectedItem is AudioDeviceOption chosen && AudioDevices.LooksLikeVirtualCable(chosen)
            ? chosen
            : _outputs.FirstOrDefault(AudioDevices.LooksLikeVirtualCable);
        AudioDeviceOption? recordingEnd = playbackEnd != null ? AudioDevices.FindRecordingEnd(playbackEnd, _inputs) : null;

        bool found = playbackEnd != null;
        CableFoundCard.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        CableMissingPanel.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
        if (found)
        {
            CableFoundText.Text = recordingEnd != null
                ? $"MicMixer found {playbackEnd!.FriendlyName} and {recordingEnd.FriendlyName}. There is nothing to install."
                : $"MicMixer found {playbackEnd!.FriendlyName}. There is nothing to install.";
        }

        PlaybackEndRun.Text = playbackEnd?.FriendlyName ?? "CABLE Input";
        RecordingEndRun.Text = recordingEnd?.FriendlyName ?? "CABLE Output";
        RecordingEndText.Text = recordingEnd?.FriendlyName ?? "CABLE Output (the recording end of your virtual cable)";

        if (OutputCombo.SelectedItem is not AudioDeviceOption output)
        {
            OutputCheckText.Text = "No playback device found.";
            OutputCheckText.Foreground = WarningBrush;
        }
        else if (AudioDevices.LooksLikeVirtualCable(output))
        {
            OutputCheckText.Text = "✓ This is a virtual cable.";
            OutputCheckText.Foreground = OkBrush;
        }
        else
        {
            OutputCheckText.Text = $"“{output.FriendlyName}” does not look like a virtual cable. A game or chat cannot pick up the mix from it; only choose it if it is a cable MicMixer does not recognize.";
            OutputCheckText.Foreground = WarningBrush;
        }
    }

    private static readonly System.Windows.Media.Brush OkBrush = Frozen(0x16, 0x65, 0x34);
    private static readonly System.Windows.Media.Brush WarningBrush = Frozen(0x9A, 0x34, 0x12);

    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void ShowStep(int step)
    {
        _step = step;
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        }

        StepProgress.Value = step;
        StepCounterText.Text = $"Step {step + 1} of {_pages.Length}";
        BackButton.Visibility = step == 0 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = step == 0 ? "Get started" : step == _pages.Length - 1 ? "Finish" : "Next";
        PageScroll.ScrollToTop();
        UpdateNextEnabled();
    }

    /// <summary>The pages that pick a device cannot be left without one.</summary>
    private void UpdateNextEnabled()
    {
        NextButton.IsEnabled = _pages[_step] switch
        {
            var page when page == MicPage => MicCombo.SelectedItem != null
                && (SelectedVoiceMode != ModifiedVoiceMode.ExternalMicrophone || ExternalMicCombo.SelectedItem != null),
            var page when page == OutputPage => OutputCombo.SelectedItem != null,
            _ => true
        };
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step < _pages.Length - 1)
        {
            ShowStep(_step + 1);
            return;
        }

        DialogResult = true;
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            ShowStep(_step - 1);
        }
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        Skipped = true;
        DialogResult = false;
    }

    private void OnCheckAgainClick(object sender, RoutedEventArgs e) =>
        LoadDevices(SelectedId(MicCombo), SelectedId(ExternalMicCombo), SelectedId(OutputCombo), SelectedId(MonitorCombo));

    private void OnOpenVbCableClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open the VB-CABLE website.");
        }
    }

    private void OnVoiceModeChanged(object sender, RoutedEventArgs e)
    {
        ExternalMicCombo.IsEnabled = SelectedVoiceMode == ModifiedVoiceMode.ExternalMicrophone;
        UpdateHotkeyTexts();
        UpdateNextEnabled();
    }

    private void OnOutputChanged(object sender, SelectionChangedEventArgs e) => UpdateCableTexts();

    private void OnPushToTalkChanged(object sender, RoutedEventArgs e) => UpdateHotkeyTexts();

    private void OnChangeHotkeyClick(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        UpdateHotkeyTexts();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!_isCapturingHotkey || key == Key.None)
        {
            return;
        }

        e.Handled = true;
        SetHotkey(HotkeyBinding.FromKeyboardKey(key));
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        e.Handled = true;
        SetHotkey(HotkeyBinding.FromMouseButton(e.ChangedButton));
    }

    private void SetHotkey(HotkeyBinding binding)
    {
        _hotkey = binding;
        _isCapturingHotkey = false;
        UpdateHotkeyTexts();
    }

    private void UpdateHotkeyTexts()
    {
        string key = _hotkey.DisplayName;
        HotkeyText.Text = key;
        ChangeHotkeyButton.Content = _isCapturingHotkey ? "Press now…" : "Change";
        HotkeyHintText.Text = _isCapturingHotkey
            ? "Press the keyboard key or mouse button you want to use."
            : "Click Change, then press any keyboard key or mouse button. A mouse side button works well.";

        bool pushToTalk = PushToTalkCheck.IsChecked == true;
        HotkeyEffectText.Text = (pushToTalk, SelectedVoiceMode != ModifiedVoiceMode.None) switch
        {
            (true, true) => $"Nobody hears you until you hold {key}. While you hold it, they hear your modified voice.",
            (true, false) => $"Nobody hears you until you hold {key}. Music can keep playing: turn on Music ignores push-to-talk in the music card.",
            (false, true) => $"Your normal mic is always on. While you hold {key}, others hear your modified voice instead.",
            _ => $"Your mic is always on. {key} does nothing yet; it matters once you turn on push-to-talk or a modified voice."
        };
    }
}
