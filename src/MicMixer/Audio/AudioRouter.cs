using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace MicMixer.Audio;

public sealed class AudioRouter : IDisposable
{
    private readonly object _syncRoot = new();
    private WasapiOut? _player;
    private InputRoute? _normalRoute;
    private InputRoute? _moddedRoute;
    private bool _useModdedInput;
    private bool _outputGateOpen = true;
    private bool _disposed;

    public bool IsRouting => _player?.PlaybackState == PlaybackState.Playing;
    public bool UseModdedInput => Volatile.Read(ref _useModdedInput);
    public bool OutputGateOpen => Volatile.Read(ref _outputGateOpen);
    public float NormalPeak => _normalRoute?.CurrentPeak ?? 0f;
    public float ModdedPeak => _moddedRoute?.CurrentPeak ?? 0f;

    /// <summary>
    /// Optional factory that provides a music source in the routing target format.
    /// The returned provider must always fill requested sample counts (silence when idle).
    /// </summary>
    public Func<WaveFormat, ISampleProvider>? MusicSourceFactory { get; set; }

    public event EventHandler<string>? Error;

    /// <summary>
    /// Starts routing. When <paramref name="moddedInputDevice"/> is null only the normal
    /// mic is routed and hotkey switching has no effect.
    /// </summary>
    public void Start(MMDevice normalInputDevice, MMDevice? moddedInputDevice, MMDevice outputDevice)
    {
        Stop();

        InputRoute? normalRoute = null;
        InputRoute? moddedRoute = null;
        WasapiOut? player = null;

        try
        {
            var targetFormat = outputDevice.AudioClient.MixFormat;

            normalRoute = new InputRoute(normalInputDevice, targetFormat, RaiseError);
            moddedRoute = moddedInputDevice != null
                ? new InputRoute(moddedInputDevice, targetFormat, RaiseError)
                : null;

            var micSource = new SwitchingSampleProvider(normalRoute, moddedRoute, () => UseModdedInput);
            ISampleProvider source = micSource;

            if (MusicSourceFactory != null)
            {
                var mixer = new MixingSampleProvider(micSource.WaveFormat) { ReadFully = true };
                mixer.AddMixerInput(micSource);
                mixer.AddMixerInput(MusicSourceFactory(targetFormat));
                source = mixer;
            }

            // The gate sits last in the chain so push-to-talk silences the entire
            // outgoing mix (mic and music) while upstream sources keep advancing.
            source = new GateSampleProvider(source, () => OutputGateOpen);

            player = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 50);
            player.PlaybackStopped += OnPlaybackStopped;
            player.Init(new SampleToTargetWaveProvider(source, targetFormat));

            normalRoute.Start();
            moddedRoute?.Start();
            player.Play();

            lock (_syncRoot)
            {
                _normalRoute = normalRoute;
                _moddedRoute = moddedRoute;
                _player = player;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start audio routing.");

            if (player != null)
            {
                player.PlaybackStopped -= OnPlaybackStopped;
                player.Dispose();
            }

            normalRoute?.Dispose();
            moddedRoute?.Dispose();
            Stop();
            RaiseError(ex.Message);
        }
    }

    public void SetUseModdedInput(bool useModdedInput)
    {
        Volatile.Write(ref _useModdedInput, useModdedInput);
    }

    /// <summary>
    /// Opens or closes the outgoing mix (push-to-talk). Closed means the virtual
    /// cable receives silence; capture and music keep running underneath.
    /// </summary>
    public void SetOutputGateOpen(bool isOpen)
    {
        Volatile.Write(ref _outputGateOpen, isOpen);
    }

    public void Stop()
    {
        WasapiOut? player;
        InputRoute? normalRoute;
        InputRoute? moddedRoute;

        lock (_syncRoot)
        {
            player = _player;
            normalRoute = _normalRoute;
            moddedRoute = _moddedRoute;

            _player = null;
            _normalRoute = null;
            _moddedRoute = null;
        }

        if (player != null)
        {
            player.PlaybackStopped -= OnPlaybackStopped;

            try
            {
                if (player.PlaybackState != PlaybackState.Stopped)
                {
                    player.Stop();
                }
            }
            catch
            {
                // The device may already be unavailable.
            }

            player.Dispose();
        }

        normalRoute?.Dispose();
        moddedRoute?.Dispose();

        Volatile.Write(ref _useModdedInput, false);
        Volatile.Write(ref _outputGateOpen, true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Log.Error(e.Exception, "Audio playback stopped with exception.");
            RaiseError(e.Exception.Message);
        }
    }

    private void RaiseError(string message)
    {
        Error?.Invoke(this, message);
    }

    private sealed class InputRoute : IDisposable
    {
        private readonly Action<string> _errorHandler;
        private readonly WasapiCapture _capture;
        private readonly BufferedWaveProvider _buffer;
        private readonly MeteringSampleProvider _meter;
        private float _currentPeak;
        private bool _disposed;

        public InputRoute(MMDevice device, WaveFormat targetFormat, Action<string> errorHandler)
        {
            _errorHandler = errorHandler;
            _capture = new WasapiCapture(device);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(200),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            var source = FormatNormalizer.Normalize(_buffer.ToSampleProvider(), targetFormat);
            var samplesPerNotification = Math.Max(targetFormat.SampleRate * Math.Max(targetFormat.Channels, 1) / 20, targetFormat.Channels);
            _meter = new MeteringSampleProvider(source, samplesPerNotification);
            _meter.StreamVolume += OnStreamVolume;
        }

        public float CurrentPeak => Volatile.Read(ref _currentPeak);

        public WaveFormat OutputFormat => _meter.WaveFormat;

        public void Start()
        {
            _capture.StartRecording();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _meter.Read(buffer, offset, count);

            if (samplesRead == 0)
            {
                Volatile.Write(ref _currentPeak, 0f);
            }

            return samplesRead;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_capture.CaptureState == CaptureState.Capturing)
                {
                    _capture.StopRecording();
                }
            }
            catch
            {
                // The device may already be unavailable.
            }

            _meter.StreamVolume -= OnStreamVolume;
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();

            Volatile.Write(ref _currentPeak, 0f);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                Log.Error(e.Exception, "Audio capture stopped with exception.");
                _errorHandler(e.Exception.Message);
            }
        }

        private void OnStreamVolume(object? sender, StreamVolumeEventArgs e)
        {
            float peak = e.MaxSampleValues.Length == 0 ? 0f : e.MaxSampleValues.Max();
            Volatile.Write(ref _currentPeak, Math.Clamp(peak, 0f, 1f));
        }
    }

    /// <summary>
    /// Multiplies the stream by 0 or 1 depending on the gate state, with a short
    /// gain ramp on transitions so open/close never produces an audible click.
    /// </summary>
    private sealed class GateSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Func<bool> _isOpen;
        private readonly float _gainStepPerSample;
        private float _gain;

        public GateSampleProvider(ISampleProvider source, Func<bool> isOpen)
        {
            _source = source;
            _isOpen = isOpen;
            // ~8 ms full-range ramp at the stream's interleaved sample rate.
            _gainStepPerSample = 1f / Math.Max(0.008f * source.WaveFormat.SampleRate * source.WaveFormat.Channels, 1f);
            _gain = isOpen() ? 1f : 0f;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            float target = _isOpen() ? 1f : 0f;

            if (_gain == target)
            {
                if (target == 0f)
                {
                    Array.Clear(buffer, offset, samplesRead);
                }

                return samplesRead;
            }

            for (int i = 0; i < samplesRead; i++)
            {
                _gain = _gain < target
                    ? Math.Min(target, _gain + _gainStepPerSample)
                    : Math.Max(target, _gain - _gainStepPerSample);
                buffer[offset + i] *= _gain;
            }

            return samplesRead;
        }
    }

    private sealed class SwitchingSampleProvider : ISampleProvider
    {
        private readonly InputRoute _normalRoute;
        private readonly InputRoute? _moddedRoute;
        private readonly Func<bool> _useModdedInput;
        private float[] _normalBuffer = Array.Empty<float>();
        private float[] _moddedBuffer = Array.Empty<float>();

        public SwitchingSampleProvider(InputRoute normalRoute, InputRoute? moddedRoute, Func<bool> useModdedInput)
        {
            _normalRoute = normalRoute;
            _moddedRoute = moddedRoute;
            _useModdedInput = useModdedInput;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                normalRoute.OutputFormat.SampleRate,
                normalRoute.OutputFormat.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            EnsureCapacity(count);

            int normalRead = _normalRoute.Read(_normalBuffer, 0, count);
            int moddedRead = _moddedRoute?.Read(_moddedBuffer, 0, count) ?? 0;

            bool useModdedInput = _moddedRoute != null && _useModdedInput();
            float[] selectedBuffer = useModdedInput ? _moddedBuffer : _normalBuffer;
            int selectedSamples = useModdedInput ? moddedRead : normalRead;

            Array.Copy(selectedBuffer, 0, buffer, offset, selectedSamples);

            if (selectedSamples < count)
            {
                Array.Clear(buffer, offset + selectedSamples, count - selectedSamples);
            }

            return count;
        }

        private void EnsureCapacity(int count)
        {
            if (_normalBuffer.Length < count)
            {
                _normalBuffer = new float[count];
            }

            if (_moddedBuffer.Length < count)
            {
                _moddedBuffer = new float[count];
            }
        }
    }
}
