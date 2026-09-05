using System.Windows;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using MicMixer.Audio;
using MicMixer.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace MicMixer.UI;

internal partial class VoiceDesignerDialog : Window
{
    private readonly VoicePreview _audio = new();
    private readonly VoiceProfileStore _store = new();
    private readonly string? _inputId;
    private readonly Dictionary<string, Slider> _sliders = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool? _playingProcessed;
    private CancellationTokenSource? _render;
    private readonly Stopwatch _recordingClock = new();
    private VoiceDspParameters _starting = VoiceDspParameters.Initial;
    private float[]? _raw;
    private bool _ready, _recording, _closed, _resetting, _parametersValid;
    public VoiceProfile? SavedProfile { get; private set; }
    private sealed record OutputOption(string Id, string Name);

    public VoiceDesignerDialog(VoiceProfile? selected, string? inputId, string? inputName, string? outputId, string? cableId)
    {
        // The window is never shown when construction fails, so Closed cannot release _audio.
        try
        {
            InitializeComponent();
            _inputId = inputId;
            InputLabel.Text = inputName == null ? "Select a normal microphone in the main window to record." : $"Microphone: {inputName}";
            RecordButton.IsEnabled = inputId != null;
            var neutral = new VoiceProfile { FormatVersion = 1, Id = Guid.NewGuid().ToString(), DisplayName = "Neutral starting point", Parameters = VoiceDspParameters.Initial };
            StartingPoint.ItemsSource = new[] { neutral }.Concat(_store.List().Profiles).ToList();
            Add(MainParameters, nameof(VoiceDspParameters.PitchSemitones), "Pitch", "Lower ↔ higher pitch, in semitones", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.FormantSemitones), "Resonance adjustment", "Pitch also shifts resonance. This adds a darker (−) or brighter (+) adjustment, in semitones.", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.LowMidGainDb), "Warmth", "Body in the low mids (dB)", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.PresenceGainDb), "Clarity", "Speech definition and bite (dB)", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.AirGainDb), "Air", "High-frequency brightness (dB)", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.SaturationDrive), "Texture", "0 is clean; higher values add grit and can increase loudness", 0, 8, .1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.HighPassHz), "Rumble filter", "High-pass cutoff (Hz)", 10, 22800, 5);
            Add(AdvancedParameters, nameof(VoiceDspParameters.LowMidHz), "Warmth frequency", "Low-mid EQ center (Hz)", 10, 22800, 10);
            Add(AdvancedParameters, nameof(VoiceDspParameters.PresenceHz), "Clarity frequency", "Presence EQ center (Hz)", 10, 22800, 50);
            Add(AdvancedParameters, nameof(VoiceDspParameters.AirHz), "Air frequency", "High-shelf cutoff (Hz)", 10, 22800, 50);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorThresholdDb), "Compression threshold", "Compress above this level (dB)", -60, 0, 1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorRatio), "Compression ratio", "1 disables compression; 2–4 gently evens speech", 1, 20, .5);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorAttackMilliseconds), "Attack", "How quickly compression reacts (ms)", .1, 500, .1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorReleaseMilliseconds), "Release", "How quickly compression recovers (ms)", 1, 2000, 1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorMakeupDb), "Makeup gain", "Level after compression (dB); avoid excessive gain", -12, 24, .5);
            Add(AdvancedParameters, nameof(VoiceDspParameters.TonalityLimitHz), "Tonality limit", "Upper frequency for tonal pitch processing (Hz)", 0, 24000, 100);
            Add(AdvancedParameters, nameof(VoiceDspParameters.FormantBaseHz), "Formant base", "Formant reference frequency; 0 uses the engine default (Hz)", 0, 24000, 10);
            Add(AdvancedParameters, nameof(VoiceDspParameters.BlockMilliseconds), "Analysis window", "Larger windows can sound smoother but add live delay (ms)", 10, 250, 1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.IntervalMilliseconds), "Processing interval", "At most half the analysis window (ms)", 1, 125, 1);
            using (var enumerator = new MMDeviceEnumerator())
            {
                var outputs = new List<OutputOption>();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (device)
                        if (device.ID != cableId) outputs.Add(new(device.ID, device.FriendlyName));
                }
                OutputDevice.ItemsSource = outputs;
                OutputDevice.SelectedItem = outputs.FirstOrDefault(x => x.Id == outputId);
            }
            _ready = true;
            StartingPoint.SelectedItem = ((IEnumerable<VoiceProfile>)StartingPoint.ItemsSource).FirstOrDefault(x => x.Id == selected?.Id) ?? neutral;
            _timer.Tick += (_, _) =>
            {
                RecordProgress.Value = _recordingClock.Elapsed.TotalSeconds;
                SampleStatus.Text = $"Recording… {_recordingClock.Elapsed.TotalSeconds:0.0} / 15 s";
                if (_recordingClock.Elapsed.TotalSeconds >= 15) OnStopRecording(this, new RoutedEventArgs());
            };
            _playTimer.Tick += (_, _) =>
            {
                if (_audio.PlaybackState == PlaybackState.Stopped)
                {
                    var error = _audio.PlaybackError;
                    StopPreview();
                    Status.Text = error == null ? "Sample finished. Play again to listen from the start." : "Preview stopped: " + error.Message;
                }
                UpdateButtons();
            };
            Closed += (_, _) => { _closed = true; _timer.Stop(); _playTimer.Stop(); _render?.Cancel(); _audio.Dispose(); };
        }
        catch { _audio.Dispose(); throw; }
    }

    private void Add(StackPanel panel, string property, string label, string hint, double minimum, double maximum, double step)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = new GridLength(66) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, ToolTip = hint });
        var slider = new Slider { Minimum = minimum, Maximum = maximum, SmallChange = step, LargeChange = step * 4, TickFrequency = step, ToolTip = hint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
        AutomationProperties.SetName(slider, label);
        var value = new TextBox { Text = slider.Value.ToString("0.##"), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = hint };
        AutomationProperties.SetName(value, label + " value");
        slider.ValueChanged += (_, _) => { value.Text = slider.Value.ToString("0.##"); DraftChanged(); };
        value.LostKeyboardFocus += (_, _) =>
        {
            if (double.TryParse(value.Text, out var number) && double.IsFinite(number) && number >= slider.Minimum && number <= slider.Maximum) slider.Value = number;
            value.Text = slider.Value.ToString("0.##");
        };
        Grid.SetColumn(slider, 1); Grid.SetColumn(value, 2);
        row.Children.Add(slider); row.Children.Add(value); panel.Children.Add(row);
        _sliders.Add(property, slider);
    }

    private VoiceDspParameters Snapshot()
    {
        var parameters = _starting with { };
        foreach (var (name, slider) in _sliders) typeof(VoiceDspParameters).GetProperty(name)!.SetValue(parameters, (float)slider.Value);
        if (parameters.IntervalMilliseconds > parameters.BlockMilliseconds / 2)
            throw new ArgumentException("Processing interval must be at most half the analysis window. Adjust either value under Advanced.");
        parameters.Validate(48_000, 1);
        return parameters;
    }

    private void DraftChanged()
    {
        if (!_ready || _resetting) return;
        StopPreview();
        try { Snapshot(); _parametersValid = true; Status.Text = _raw == null ? "Record a sample, then compare Original and Voice." : "Voice updated. Press Play voice to hear the changes."; SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(VoiceName.Text); }
        catch (ArgumentException ex) { _parametersValid = false; Status.Text = ex.Message; SaveButton.IsEnabled = false; }
        UpdateButtons();
    }
    private void OnNameChanged(object sender, TextChangedEventArgs e) { if (_ready) SaveButton.IsEnabled = _parametersValid && !string.IsNullOrWhiteSpace(VoiceName.Text); }
    private void OnStartingPointChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || StartingPoint.SelectedItem is not VoiceProfile profile) return;
        _starting = profile.Parameters;
        StartingHint.Text = BuiltInVoiceProfiles.Description(profile.Id) ?? "Customize this starting point and save your own copy. Recordings stay in memory only.";
        VoiceName.Text = profile.DisplayName == "Neutral starting point" ? "My voice" : (profile.DisplayName.Length > 93 ? profile.DisplayName[..93] : profile.DisplayName) + " (copy)";
        OnReset(this, new RoutedEventArgs());
    }
    private void OnReset(object sender, RoutedEventArgs e)
    {
        _resetting = true;
        foreach (var (name, slider) in _sliders) slider.Value = (float)typeof(VoiceDspParameters).GetProperty(name)!.GetValue(_starting)!;
        _resetting = false;
        DraftChanged();
    }
    private void UpdateButtons()
    {
        OriginalButton.IsEnabled = !_recording && _raw is { Length: > 0 } && OutputDevice.SelectedItem != null;
        PreviewButton.IsEnabled = OriginalButton.IsEnabled && _parametersValid;
        OriginalButton.Content = PlaybackLabel(false);
        PreviewButton.Content = PlaybackLabel(true);
        StopPreviewButton.IsEnabled = _playingProcessed != null;
    }

    private string PlaybackLabel(bool processed)
    {
        string name = processed ? "voice" : "original";
        if (_playingProcessed != processed) return "▶ Play " + name;
        if (_render != null) return "Cancel preparation";
        return _audio.PlaybackState switch
        {
            PlaybackState.Playing => "Ⅱ Pause " + name,
            PlaybackState.Paused => "▶ Resume " + name,
            _ => "▶ Play " + name
        };
    }
    private async void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_inputId == null || _recording) return;
        StopPreview();
        _recording = true; RecordButton.IsEnabled = false; StopRecordButton.IsEnabled = true; UpdateButtons();
        try
        {
            var task = _audio.RecordAsync(_inputId);
            _recordingClock.Restart();
            _timer.Start();
            var sample = await task;
            if (_closed) return;
            if (sample.Length < 48000) { SampleStatus.Text = "Sample too short. Record at least a second of speech. Any previous sample is kept."; return; }
            _raw = sample;
            var peak = sample.Max(x => Math.Abs(x));
            SampleStatus.Text = $"Sample ready · {sample.Length / 48000d:0.0} s. " + (peak < .001 ? "Very quiet — check your microphone and try again." : peak >= .99 ? "Clipping detected — lower microphone gain and re-record." : "Compare the original with your new voice.");
            RecordButton.Content = "● Record again";
        }
        catch (Exception ex) { if (!_closed) Status.Text = "Could not record: " + ex.Message; }
        finally
        {
            _timer.Stop(); _audio.ReleaseRecording(); _recording = false;
            if (!_closed) { RecordButton.IsEnabled = true; StopRecordButton.IsEnabled = false; UpdateButtons(); }
        }
    }
    private void OnStopRecording(object sender, RoutedEventArgs e)
    {
        _timer.Stop(); StopRecordButton.IsEnabled = false;
        try { _audio.StopRecording(); } catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void StopPreview()
    {
        _render?.Cancel(); _playTimer.Stop(); _audio.StopPlayback(); _playingProcessed = null;
        if (_ready && !_closed) UpdateButtons();
    }
    private void OnStop(object sender, RoutedEventArgs e) { StopPreview(); Status.Text = "Preview stopped."; }
    private void OnPreviewOptionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) { StopPreview(); UpdateButtons(); } }
    private void OnPreviewSettingChanged(object sender, RoutedEventArgs e) { if (_ready) _audio.SetLoop(Loop.IsChecked == true); }
    private void OnListenVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_ready) _audio.SetVolume((float)e.NewValue); }
    private void OnOriginal(object sender, RoutedEventArgs e) => Play(false);
    private void OnPreview(object sender, RoutedEventArgs e) => Play(true);
    private async void Play(bool processed)
    {
        if (_raw == null || OutputDevice.SelectedItem is not OutputOption output) return;
        if (_playingProcessed == processed)
        {
            if (_render != null) { OnStop(this, new RoutedEventArgs()); return; }
            try
            {
                if (_audio.PlaybackState == PlaybackState.Playing)
                {
                    _audio.Pause(); UpdateButtons(); Status.Text = "Paused. Click Resume to continue from the same place."; return;
                }
                if (_audio.PlaybackState == PlaybackState.Paused)
                {
                    _audio.Resume(); UpdateButtons(); Status.Text = "Playing. Click Pause to pause, or Stop to return to the start."; return;
                }
            }
            catch (Exception ex) { StopPreview(); Status.Text = "Could not resume preview: " + ex.Message; return; }
        }
        StopPreview();
        using var cancellation = new CancellationTokenSource();
        _render = cancellation;
        _playingProcessed = processed;
        UpdateButtons();
        try
        {
            Status.Text = processed ? "Preparing voice preview…" : "Playing original sample.";
            var raw = _raw;
            var parameters = processed ? Snapshot() : VoiceDspParameters.Initial;
            var samples = processed ? await Task.Run(() => VoicePreview.Render(raw, parameters, cancellation.Token)) : raw;
            if (_closed || cancellation.IsCancellationRequested) return;
            _audio.Play(samples, output.Id, (float)ListenVolume.Value, Loop.IsChecked == true);
            _playTimer.Start();
            Status.Text = processed ? "Playing your voice. Click Pause voice to pause." : "Playing the original. Click Pause original to pause.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && ReferenceEquals(_render, cancellation)) { StopPreview(); Status.Text = "Could not preview: " + ex.Message; } }
        finally { if (ReferenceEquals(_render, cancellation)) { _render = null; if (!_closed) UpdateButtons(); } }
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = new VoiceProfile { FormatVersion = 1, Id = Guid.NewGuid().ToString(), DisplayName = VoiceName.Text.Trim(), Parameters = Snapshot() };
            _store.Import(profile);
            SavedProfile = profile; DialogResult = true;
        }
        catch (Exception ex) { Status.Text = "Could not save voice: " + ex.Message; }
    }
}
