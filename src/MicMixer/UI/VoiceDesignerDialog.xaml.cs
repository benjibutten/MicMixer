using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using MicMixer.Audio;
using MicMixer.Dsp;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace MicMixer.UI;

internal partial class VoiceDesignerDialog : Window
{
    private const string NeutralName = "Neutral starting point";
    private static readonly Brush MutedBrush = Frozen(0x52, 0x61, 0x73);
    private static readonly Brush ErrorBrush = Frozen(0xB4, 0x23, 0x18);

    private readonly VoicePreview _audio = new();
    private readonly VoiceProfileStore _store = new();
    private readonly string? _inputId;
    private readonly Dictionary<string, ParameterRow> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    // A slider drag fires continuously; only the value it settles on is worth rendering.
    private readonly DispatcherTimer _liveRenderTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool? _playingProcessed;
    private CancellationTokenSource? _render, _liveRender;
    private readonly Stopwatch _recordingClock = new();
    private VoiceDspParameters _starting = VoiceDspParameters.Initial;
    private PitchEngine _engine = PitchEngine.Signalsmith;
    private VoiceProfile? _editing;
    private VoiceProfile? _neutral;
    private VoiceDspParameters? _baseline;
    private string _baselineName = "";
    private float[]? _raw;
    private bool _ready, _recording, _closed, _resetting, _parametersValid;
    public VoiceProfile? SavedProfile { get; private set; }
    private sealed record OutputOption(string Id, string Name);
    private sealed record EngineOption(PitchEngine Engine, string Name);
    private static readonly EngineOption[] EngineOptions =
    [
        new(PitchEngine.Signalsmith, "Signalsmith (default)"),
        new(PitchEngine.TimeDomain, "Time-domain")
    ];

    public VoiceDesignerDialog(VoiceProfile? selected, string? inputId, string? inputName, string? outputId, string? cableId)
    {
        // The window is never shown when construction fails, so Closed cannot release _audio.
        try
        {
            InitializeComponent();
            _inputId = inputId;
            InputLabel.Text = inputName == null ? "Select a normal microphone in the main window to record." : $"Microphone: {inputName}";
            RecordButton.IsEnabled = inputId != null;
            _neutral = new VoiceProfile { FormatVersion = 1, Id = Guid.NewGuid().ToString(), DisplayName = NeutralName, Parameters = VoiceDspParameters.Initial };
            StartingPoint.ItemsSource = new[] { _neutral }.Concat(_store.List().Profiles).ToList();
            Add(MainParameters, nameof(VoiceDspParameters.PitchSemitones), "Pitch", "st", "Lower or higher pitch", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.FormantSemitones), "Resonance adjustment", "st", "Pitch also shifts resonance. This adds a darker (−) or brighter (+) adjustment on top.", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.LowMidGainDb), "Warmth", "dB", "Body in the low mids", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.PresenceGainDb), "Clarity", "dB", "Speech definition and bite", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.AirGainDb), "Air", "dB", "High-frequency brightness", -24, 24, .5);
            Add(MainParameters, nameof(VoiceDspParameters.SaturationDrive), "Texture", "", "0 is clean; higher values add grit and can increase loudness", 0, 8, .1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.HighPassHz), "Rumble filter", "Hz", "High-pass cutoff", 10, 22800, 5, logarithmic: true);
            Add(AdvancedParameters, nameof(VoiceDspParameters.LowMidHz), "Warmth frequency", "Hz", "Low-mid EQ center", 10, 22800, 10, logarithmic: true);
            Add(AdvancedParameters, nameof(VoiceDspParameters.PresenceHz), "Clarity frequency", "Hz", "Presence EQ center", 10, 22800, 50, logarithmic: true);
            Add(AdvancedParameters, nameof(VoiceDspParameters.AirHz), "Air frequency", "Hz", "High-shelf cutoff", 10, 22800, 50, logarithmic: true);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorThresholdDb), "Compression threshold", "dB", "Compress above this level", -60, 0, 1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorRatio), "Compression ratio", ":1", "1 disables compression; 2–4 gently evens speech", 1, 20, .5);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorAttackMilliseconds), "Attack", "ms", "How quickly compression reacts", .1, 500, .1, logarithmic: true);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorReleaseMilliseconds), "Release", "ms", "How quickly compression recovers", 1, 2000, 1, logarithmic: true);
            Add(AdvancedParameters, nameof(VoiceDspParameters.CompressorMakeupDb), "Makeup gain", "dB", "Level after compression; avoid excessive gain", -12, 24, .5);
            Add(AdvancedParameters, nameof(VoiceDspParameters.TonalityLimitHz), "Tonality limit", "Hz", "Upper frequency for tonal pitch processing", 0, 24000, 100);
            Add(AdvancedParameters, nameof(VoiceDspParameters.FormantBaseHz), "Formant base", "Hz", "Formant reference frequency; 0 uses the engine default", 0, 24000, 10);
            Add(AdvancedParameters, nameof(VoiceDspParameters.TimeDomainWindowMilliseconds), "Waveform window", "ms", "Time-domain waveform segment length; not total latency", 10, 50, 1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.TimeDomainSearchMilliseconds), "Waveform search", "ms", "Time-domain alignment search radius", 0, 20, 1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.BlockMilliseconds), "Analysis window", "ms", "Larger windows can sound smoother but add live delay", 10, 250, 1);
            Add(AdvancedParameters, nameof(VoiceDspParameters.IntervalMilliseconds), "Processing interval", "ms", "At most half the analysis window", 1, 125, 1);
            EngineSelector.ItemsSource = EngineOptions;
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
            StartingPoint.SelectedItem = ((IEnumerable<VoiceProfile>)StartingPoint.ItemsSource).FirstOrDefault(x => x.Id == selected?.Id) ?? _neutral;
            _timer.Tick += (_, _) =>
            {
                RecordLevel.Value = _audio.RecordingPeak;
                SampleStatus.Text = $"Recording… {_recordingClock.Elapsed.TotalSeconds:0.0} / 15 s";
                if (_recordingClock.Elapsed.TotalSeconds >= 15) OnStopRecording(this, new RoutedEventArgs());
            };
            _playTimer.Tick += (_, _) =>
            {
                if (_audio.PlaybackState == PlaybackState.Stopped)
                {
                    var error = _audio.PlaybackError;
                    StopPreview();
                    if (error == null) SetStatus("Sample finished. Play again to listen from the start.");
                    else SetStatus("Preview stopped: " + error.Message, error: true);
                }
                UpdateButtons();
            };
            _liveRenderTimer.Tick += OnLiveRenderTick;
            Closed += (_, _) =>
            {
                _closed = true;
                _timer.Stop(); _playTimer.Stop(); _liveRenderTimer.Stop();
                _render?.Cancel(); _liveRender?.Cancel();
                _audio.Dispose();
            };
        }
        catch { _audio.Dispose(); throw; }
    }

    private static Brush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// One labelled slider. Frequency and time rows use a log scale: on a linear
    /// 10–22800 Hz track every useful cutoff sits in the leftmost few pixels.
    /// </summary>
    private sealed class ParameterRow
    {
        public required Grid Container { get; init; }
        public required Slider Slider { get; init; }
        public required double Minimum { get; init; }
        public required double Maximum { get; init; }
        public required bool Logarithmic { get; init; }

        public double Value
        {
            get => Math.Round(Logarithmic ? Math.Pow(10, Slider.Value) : Slider.Value, 2);
            set
            {
                double clamped = Math.Clamp(value, Minimum, Maximum);
                Slider.Value = Logarithmic ? Math.Log10(clamped) : clamped;
            }
        }
    }

    private void Add(StackPanel panel, string property, string label, string unit, string hint,
        double minimum, double maximum, double step, bool logarithmic = false)
    {
        var container = new Grid { Margin = new Thickness(0, 6, 0, 6), ToolTip = unit.Length == 0 ? hint : $"{hint} ({unit})" };
        container.ColumnDefinitions.Add(new() { Width = new GridLength(150) });
        container.ColumnDefinitions.Add(new());
        container.ColumnDefinitions.Add(new() { Width = new GridLength(60) });
        container.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
        var slider = new Slider
        {
            Minimum = logarithmic ? Math.Log10(minimum) : minimum,
            Maximum = logarithmic ? Math.Log10(maximum) : maximum,
            SmallChange = logarithmic ? .005 : step,
            LargeChange = logarithmic ? .05 : step * 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 12, 0)
        };
        var row = new ParameterRow { Container = container, Slider = slider, Minimum = minimum, Maximum = maximum, Logarithmic = logarithmic };
        var value = new TextBox { Text = row.Value.ToString("0.##"), VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, unit.Length == 0 ? label : $"{label} in {unit}");
        AutomationProperties.SetName(value, label + " value");
        slider.ValueChanged += (_, _) => { value.Text = row.Value.ToString("0.##"); DraftChanged(); };
        value.LostKeyboardFocus += (_, _) =>
        {
            // Out-of-range entries snap to the nearest legal value rather than vanishing.
            if (double.TryParse(value.Text, out double number) && double.IsFinite(number)) row.Value = number;
            value.Text = row.Value.ToString("0.##");
        };
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var suffix = new TextBlock { Text = unit, Foreground = MutedBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(slider, 1); Grid.SetColumn(value, 2); Grid.SetColumn(suffix, 3);
        container.Children.Add(name); container.Children.Add(slider); container.Children.Add(value); container.Children.Add(suffix);
        panel.Children.Add(container);
        _rows.Add(property, row);
    }

    private VoiceDspParameters Snapshot()
    {
        var parameters = _starting with { PitchEngine = _engine };
        foreach (var (name, row) in _rows) typeof(VoiceDspParameters).GetProperty(name)!.SetValue(parameters, (float)row.Value);
        if (parameters.IntervalMilliseconds > parameters.BlockMilliseconds / 2)
            throw new ArgumentException("Processing interval must be at most half the analysis window. Adjust either value under Advanced.");
        parameters.Validate(48_000, 1);
        return parameters;
    }

    private void SetStatus(string text, bool error = false)
    {
        Status.Text = text;
        Status.Foreground = error ? ErrorBrush : MutedBrush;
    }

    /// <summary>Save-blocking problems belong next to the Save button, not in the playback status.</summary>
    private void SetSaveHint(string? problem)
    {
        SaveHint.Text = problem ?? (_editing == null
            ? "Saved as a new local profile. Your starting point stays unchanged."
            : $"Saves back to '{_editing.DisplayName}'. Use Save as copy to keep both.");
        SaveHint.Foreground = problem == null ? MutedBrush : ErrorBrush;
    }

    private void DraftChanged()
    {
        if (!_ready || _resetting) return;
        try
        {
            Snapshot();
            _parametersValid = true;
            SetSaveHint(null);
            if (_playingProcessed == true) QueueLiveRender();
            else SetStatus(_raw == null ? "Record a sample, then compare Original and Voice." : "Voice updated. Press Play voice to hear the change.");
        }
        catch (ArgumentException ex)
        {
            _parametersValid = false;
            bool wasPlaying = _playingProcessed != null;
            StopPreview();
            if (wasPlaying) SetStatus("Preview stopped: these settings cannot be processed yet.", error: true);
            SetSaveHint(ex.Message);
            // Both rules that can fail here are governed by Advanced sliders.
            AdvancedSection.IsExpanded = true;
        }
        UpdateSaveButtons();
        UpdateButtons();
    }

    private void QueueLiveRender()
    {
        _liveRenderTimer.Stop();
        _liveRenderTimer.Start();
    }

    private async void OnLiveRenderTick(object? sender, EventArgs e)
    {
        _liveRenderTimer.Stop();
        if (_raw is not { } raw || _playingProcessed != true || !_parametersValid) return;
        // The first render has not handed its samples to the player yet, so there is
        // nothing to swap into. Retry instead of dropping the change.
        if (_render != null) { QueueLiveRender(); return; }
        _liveRender?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _liveRender = cancellation;
        try
        {
            var parameters = Snapshot();
            var samples = await Task.Run(() => VoicePreview.Render(raw, parameters, cancellation.Token));
            if (_closed || cancellation.IsCancellationRequested) return;
            _audio.ReplaceSamples(samples);
            SetStatus("Playing your voice. Keep adjusting — the preview follows along.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) SetStatus("Could not update the preview: " + ex.Message, error: true); }
        finally { if (ReferenceEquals(_liveRender, cancellation)) _liveRender = null; }
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) UpdateSaveButtons();
    }

    private void OnStartingPointChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || StartingPoint.SelectedItem is not VoiceProfile profile) return;
        _starting = profile.Parameters;
        _editing = ReferenceEquals(profile, _neutral) || BuiltInVoiceProfiles.Find(profile.Id) != null ? null : profile;
        StartingHint.Text = BuiltInVoiceProfiles.Description(profile.Id) ?? (_editing != null
            ? "Your own voice. Save the changes back to it, or keep both with Save as copy. Recordings stay in memory only."
            : "Customize this starting point and save your own copy. Recordings stay in memory only.");
        VoiceName.Text = _editing != null ? profile.DisplayName
            : ReferenceEquals(profile, _neutral) ? "My voice"
            : (profile.DisplayName.Length > 93 ? profile.DisplayName[..93] : profile.DisplayName) + " (copy)";
        OnReset(this, new RoutedEventArgs());
        // The chosen starting point is the new baseline, not an edit to warn about.
        _baselineName = VoiceName.Text;
        _baseline = CurrentDraft();
    }

    private VoiceDspParameters? CurrentDraft()
    {
        try { return Snapshot(); } catch (ArgumentException) { return null; }
    }

    /// <summary>Compares against the baseline rather than tracking edit events,
    /// which arrive in an order the dialog does not control.</summary>
    private bool HasUnsavedChanges() => VoiceName.Text != _baselineName || CurrentDraft() != _baseline;

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _resetting = true;
        _engine = _starting.PitchEngine;
        EngineSelector.SelectedItem = EngineOptions.First(o => o.Engine == _engine);
        foreach (var (name, row) in _rows) row.Value = (float)typeof(VoiceDspParameters).GetProperty(name)!.GetValue(_starting)!;
        _resetting = false;
        UpdateRowAvailability();
        UpdateEngineHint();
        DraftChanged();
    }

    private void OnEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || EngineSelector.SelectedItem is not EngineOption option) return;
        _engine = option.Engine;
        // Independent resonance adjustment is not supported by the time-domain engine.
        if (_engine == PitchEngine.TimeDomain && _rows.TryGetValue(nameof(VoiceDspParameters.FormantSemitones), out var formant))
            formant.Value = 0;
        UpdateRowAvailability();
        UpdateEngineHint();
        DraftChanged();
    }

    private void UpdateRowAvailability()
    {
        bool timeDomain = _engine == PitchEngine.TimeDomain;
        foreach (var (name, row) in _rows)
        {
            bool domainOnly = name is nameof(VoiceDspParameters.TimeDomainWindowMilliseconds) or nameof(VoiceDspParameters.TimeDomainSearchMilliseconds);
            bool spectralOnly = name is nameof(VoiceDspParameters.FormantSemitones) or nameof(VoiceDspParameters.FormantBaseHz)
                or nameof(VoiceDspParameters.TonalityLimitHz) or nameof(VoiceDspParameters.BlockMilliseconds) or nameof(VoiceDspParameters.IntervalMilliseconds);
            row.Container.IsEnabled = domainOnly ? timeDomain : !spectralOnly || !timeDomain;
        }
    }

    private void UpdateEngineHint()
    {
        EngineHint.Text = _engine == PitchEngine.TimeDomain
            ? "Time-domain: lower latency. Pitch also moves resonance; independent resonance adjustment is unavailable."
            : "Signalsmith: the default engine, better suited to larger pitch shifts.";
    }

    private void UpdateButtons()
    {
        OriginalButton.IsEnabled = !_recording && _raw is { Length: > 0 } && OutputDevice.SelectedItem != null;
        PreviewButton.IsEnabled = OriginalButton.IsEnabled && _parametersValid;
        OriginalButton.Content = PlaybackLabel(false);
        PreviewButton.Content = PlaybackLabel(true);
        StopPreviewButton.IsEnabled = _playingProcessed != null;
    }

    private void UpdateSaveButtons()
    {
        bool savable = _parametersValid && !string.IsNullOrWhiteSpace(VoiceName.Text);
        SaveButton.IsEnabled = savable;
        SaveButton.Content = _editing == null ? "Save voice" : "Save changes";
        SaveCopyButton.IsEnabled = savable;
        SaveCopyButton.Visibility = _editing == null ? Visibility.Collapsed : Visibility.Visible;
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
            SetStatus("Press Play voice to hear the sample through your settings.");
            RecordButton.Content = "● Record again";
        }
        catch (Exception ex) { if (!_closed) SetStatus("Could not record: " + ex.Message, error: true); }
        finally
        {
            _timer.Stop(); _audio.ReleaseRecording(); _recording = false;
            if (!_closed) { RecordLevel.Value = 0; RecordButton.IsEnabled = true; StopRecordButton.IsEnabled = false; UpdateButtons(); }
        }
    }

    private void OnStopRecording(object sender, RoutedEventArgs e)
    {
        _timer.Stop(); StopRecordButton.IsEnabled = false;
        try { _audio.StopRecording(); } catch (Exception ex) { SetStatus(ex.Message, error: true); }
    }

    private void StopPreview()
    {
        _liveRenderTimer.Stop(); _liveRender?.Cancel(); _liveRender = null;
        _render?.Cancel(); _playTimer.Stop(); _audio.StopPlayback(); _playingProcessed = null;
        if (_ready && !_closed) UpdateButtons();
    }

    private void OnStop(object sender, RoutedEventArgs e) { StopPreview(); SetStatus("Preview stopped."); }
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
                    _audio.Pause(); UpdateButtons(); SetStatus("Paused. Click Resume to continue from the same place."); return;
                }
                if (_audio.PlaybackState == PlaybackState.Paused)
                {
                    _audio.Resume(); UpdateButtons(); SetStatus("Playing. Click Pause to pause, or Stop to return to the start."); return;
                }
            }
            catch (Exception ex) { StopPreview(); SetStatus("Could not resume preview: " + ex.Message, error: true); return; }
        }
        StopPreview();
        using var cancellation = new CancellationTokenSource();
        _render = cancellation;
        _playingProcessed = processed;
        UpdateButtons();
        try
        {
            SetStatus(processed ? "Preparing voice preview…" : "Playing original sample.");
            var raw = _raw;
            var parameters = processed ? Snapshot() : VoiceDspParameters.Initial;
            var samples = processed ? await Task.Run(() => VoicePreview.Render(raw, parameters, cancellation.Token)) : raw;
            if (_closed || cancellation.IsCancellationRequested) return;
            _audio.Play(samples, output.Id, (float)ListenVolume.Value, Loop.IsChecked == true);
            _playTimer.Start();
            SetStatus(processed
                ? "Playing your voice. Keep adjusting — the preview follows along."
                : "Playing the original. Click Pause original to pause.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && ReferenceEquals(_render, cancellation)) { StopPreview(); SetStatus("Could not preview: " + ex.Message, error: true); } }
        finally { if (ReferenceEquals(_render, cancellation)) { _render = null; if (!_closed) UpdateButtons(); } }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save(asCopy: false);
    private void OnSaveCopy(object sender, RoutedEventArgs e) => Save(asCopy: true);

    private void Save(bool asCopy)
    {
        var target = asCopy ? null : _editing;
        try
        {
            var profile = new VoiceProfile
            {
                FormatVersion = _engine == PitchEngine.TimeDomain ? 2 : 1,
                Id = target?.Id ?? Guid.NewGuid().ToString(),
                DisplayName = VoiceName.Text.Trim(),
                Parameters = Snapshot(),
                // A legacy alternate window belongs to the profile; editing it must not drop it.
                AlternateBlockMilliseconds = target?.AlternateBlockMilliseconds
            };
            if (target == null) _store.Import(profile); else _store.Replace(profile);
            SavedProfile = profile; DialogResult = true;
        }
        catch (Exception ex) { SetSaveHint("Could not save voice: " + ex.Message); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult != true && HasUnsavedChanges())
        {
            var answer = System.Windows.MessageBox.Show(this,
                "Discard this voice? Your changes have not been saved.",
                "Create a voice", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) { e.Cancel = true; return; }
        }
        base.OnClosing(e);
    }
}
