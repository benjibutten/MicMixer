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
    private bool _musicIgnoresPushToTalk;
    private bool _musicMonitorOnly;
    private bool _outputMeteringEnabled;
    private bool _musicMeteringEnabled;
    private float _outputPeak;
    private float _outputRms;
    private float _musicPeak;
    private float _musicRms;
    private bool _disposed;

    public bool IsRouting => _player?.PlaybackState == PlaybackState.Playing;
    public bool UseModdedInput => Volatile.Read(ref _useModdedInput);
    public bool OutputGateOpen => Volatile.Read(ref _outputGateOpen);
    public bool MusicIgnoresPushToTalk => Volatile.Read(ref _musicIgnoresPushToTalk);
    public bool MusicMonitorOnly => Volatile.Read(ref _musicMonitorOnly);
    public float NormalPeak => _normalRoute?.CurrentPeak ?? 0f;
    public float ModdedPeak => _moddedRoute?.CurrentPeak ?? 0f;

    /// <summary>
    /// Whether the music branch currently reaches the virtual cable: monitor-only
    /// cuts it entirely, otherwise it follows the push-to-talk gate unless music
    /// is set to ignore it.
    /// </summary>
    public bool MusicRouteOpen =>
        !Volatile.Read(ref _musicMonitorOnly)
        && (Volatile.Read(ref _outputGateOpen) || Volatile.Read(ref _musicIgnoresPushToTalk));

    /// <summary>
    /// Optional factory that provides a music source in the routing target format.
    /// The returned provider must always fill requested sample counts (silence when idle).
    /// </summary>
    public Func<WaveFormat, ISampleProvider>? MusicSourceFactory { get; set; }

    /// <summary>
    /// Optional secondary output that receives the finished mix before the
    /// push-to-talk gate. Owned by the caller; the router only starts/stops it
    /// alongside the routing session and feeds it via a pre-gate tap.
    /// </summary>
    public SecondaryOutputEngine? SecondaryOutput { get; set; }

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
            ISampleProvider? musicSource = MusicSourceFactory?.Invoke(targetFormat);

            // A secondary start failure only skips that branch — the cable
            // routing continues untouched.
            var secondaryOutput = SecondaryOutput;
            bool secondaryStarted = secondaryOutput != null
                && secondaryOutput.TryStartForRouting(micSource.WaveFormat);

            // The secondary branch gates mic and music separately, like the
            // cable: the mic follows push-to-talk unless the secondary ignores
            // it, while the music also flows whenever it is audible to the
            // streamer — sent to the cable or previewed in monitor-only mode —
            // so the UI's promise "the secondary output still carries the
            // music" holds even when the secondary follows push-to-talk.
            Func<bool> secondaryMicOpen = secondaryStarted
                ? () => secondaryOutput!.IgnorePushToTalk || OutputGateOpen
                : static () => false;
            Func<bool> secondaryMusicOpen = secondaryStarted
                ? () => secondaryOutput!.IgnorePushToTalk || MusicMonitorOnly || MusicRouteOpen
                : static () => false;

            // Mic and music pass separate gates before being mixed for the cable:
            // push-to-talk always gates the mic, while the music branch can bypass
            // it (music ignores push-to-talk) or be cut from the cable entirely
            // (monitor-only preview). Upstream sources keep advancing regardless,
            // so music never rewinds while a gate is closed.
            ISampleProvider source = new MixFanoutSampleProvider(
                micSource,
                musicSource,
                micGateOpen: () => OutputGateOpen,
                musicGateOpen: () => MusicRouteOpen,
                secondaryMicOpen: secondaryMicOpen,
                secondaryMusicOpen: secondaryMusicOpen,
                secondaryWrite: secondaryStarted ? secondaryOutput!.Write : null,
                musicMeteringEnabled: () => MusicMeteringEnabled,
                onMusicLevels: MergeMusicLevels);

            // Tap after the gates: the peak reflects exactly what the cable receives,
            // so the overlay meter reads empty while nothing is sent.
            source = new OutputPeakTapProvider(source, this);

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

    /// <summary>
    /// Gates the per-sample output level computation. Off means the tap is a pure
    /// pass-through — no reason to scan the whole stream while no meter is shown.
    /// </summary>
    public bool OutputMeteringEnabled
    {
        get => Volatile.Read(ref _outputMeteringEnabled);
        set
        {
            Volatile.Write(ref _outputMeteringEnabled, value);
            if (!value)
            {
                Interlocked.Exchange(ref _outputPeak, 0f);
                Interlocked.Exchange(ref _outputRms, 0f);
            }
        }
    }

    /// <summary>
    /// Levels of the complete outgoing mix (mic + music, after the gates) since
    /// the last call, then resets. Peak is the max absolute sample (clipping
    /// detection); Rms is the highest block RMS (perceived loudness — what a
    /// volume indicator should judge, since limited music runs peaks near full
    /// scale while sounding far louder than speech at the same peak). Both are 0
    /// while routing is stopped, nothing passes the gates, or
    /// <see cref="OutputMeteringEnabled"/> is off.
    /// </summary>
    public (float Peak, float Rms) ReadAndResetOutputLevels()
    {
        // Exchange makes each read/reset atomic so an audio-thread write landing
        // between them is never wiped.
        return (Interlocked.Exchange(ref _outputPeak, 0f), Interlocked.Exchange(ref _outputRms, 0f));
    }

    /// <summary>
    /// Gates the per-sample music level computation, exactly like
    /// <see cref="OutputMeteringEnabled"/> gates the mix tap.
    /// </summary>
    public bool MusicMeteringEnabled
    {
        get => Volatile.Read(ref _musicMeteringEnabled);
        set
        {
            Volatile.Write(ref _musicMeteringEnabled, value);
            if (!value)
            {
                Interlocked.Exchange(ref _musicPeak, 0f);
                Interlocked.Exchange(ref _musicRms, 0f);
            }
        }
    }

    /// <summary>
    /// Levels of the music branch alone (after the music volume, before its cable
    /// gate) since the last call, then resets. Pre-gate on purpose: the music ring
    /// should show the level the music holds — also while previewing in
    /// monitor-only mode, so the volume can be set right before unleashing it.
    /// The overlay states, not this meter, tell where the music actually goes.
    /// </summary>
    public (float Peak, float Rms) ReadAndResetMusicLevels()
    {
        return (Interlocked.Exchange(ref _musicPeak, 0f), Interlocked.Exchange(ref _musicRms, 0f));
    }

    /// <summary>
    /// Max-merges one measured music block into the pending meter values. Runs on
    /// the audio thread; a lost race against the read/reset only costs one meter
    /// frame, so plain volatile reads/writes are enough.
    /// </summary>
    private void MergeMusicLevels(float peak, float rms)
    {
        if (peak > Volatile.Read(ref _musicPeak))
        {
            Volatile.Write(ref _musicPeak, peak);
        }

        if (rms > Volatile.Read(ref _musicRms))
        {
            Volatile.Write(ref _musicRms, rms);
        }
    }

    public void SetUseModdedInput(bool useModdedInput)
    {
        Volatile.Write(ref _useModdedInput, useModdedInput);
    }

    /// <summary>Lets the music branch keep reaching the cable while push-to-talk holds the mic silent.</summary>
    public void SetMusicIgnoresPushToTalk(bool ignoresPushToTalk)
    {
        Volatile.Write(ref _musicIgnoresPushToTalk, ignoresPushToTalk);
    }

    /// <summary>
    /// Preview mode: cuts the music branch from the cable entirely. Local
    /// monitoring and the secondary output still carry the music.
    /// </summary>
    public void SetMusicMonitorOnly(bool monitorOnly)
    {
        Volatile.Write(ref _musicMonitorOnly, monitorOnly);
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
        SecondaryOutput?.Stop();

        // The music routing flags survive Stop(): they are user configuration,
        // not transient session state like the gate.
        Volatile.Write(ref _useModdedInput, false);
        Volatile.Write(ref _outputGateOpen, true);
        Volatile.Write(ref _outputPeak, 0f);
        Volatile.Write(ref _outputRms, 0f);
        Volatile.Write(ref _musicPeak, 0f);
        Volatile.Write(ref _musicRms, 0f);
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
    /// Pass-through that tracks the block peak and block RMS of the outgoing mix
    /// for the UI volume indicator. A lost race between reader and reset only
    /// costs one meter frame, so plain volatile reads/writes are enough.
    /// </summary>
    private sealed class OutputPeakTapProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioRouter _router;

        public OutputPeakTapProvider(ISampleProvider source, AudioRouter router)
        {
            _source = source;
            _router = router;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _source.Read(buffer, offset, count);
            if (samplesRead == 0 || !_router.OutputMeteringEnabled)
            {
                return samplesRead;
            }

            float max = 0f;
            double squareSum = 0d;
            for (int i = 0; i < samplesRead; i++)
            {
                float sample = buffer[offset + i];
                float abs = Math.Abs(sample);
                if (abs > max)
                {
                    max = abs;
                }

                squareSum += (double)sample * sample;
            }

            if (max > Volatile.Read(ref _router._outputPeak))
            {
                Volatile.Write(ref _router._outputPeak, max);
            }

            float rms = (float)Math.Sqrt(squareSum / samplesRead);
            if (rms > Volatile.Read(ref _router._outputRms))
            {
                Volatile.Write(ref _router._outputRms, rms);
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
