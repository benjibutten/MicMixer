using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace MicMixer.Audio;

/// <summary>
/// Plays MP3 files and exposes the audio in two directions: an optional local monitor
/// device (so the user hears the music themselves) and a mix tap that
/// <see cref="AudioRouter"/> blends into the microphone signal on the virtual cable.
///
/// Clocking: when the monitor output is running it is the master clock — it pulls the
/// decoder and tees every block into a buffered provider that the router drains at its
/// own pace (same drift-absorption pattern as the mic capture path). Without a monitor,
/// the router's pull drives the decoder directly, so playback then requires active routing.
/// </summary>
public sealed class MusicPlaybackEngine : IDisposable
{
    private const int InternalSampleRate = 48_000;
    private const int InternalChannels = 2;

    // Bounded-latency policy for the monitor→mix fanout buffer: clock drift between
    // the monitor device (producer) and the routing device (consumer) slowly walks
    // the fill level; past the high watermark the oldest audio is dropped down to
    // the trim target instead of letting BufferedWaveProvider discard the newest.
    private static readonly int MixBytesPerSecond = InternalSampleRate * InternalChannels * sizeof(float);
    private static readonly int MixHighWatermarkBytes = (int)(MixBytesPerSecond * 0.4);
    private static readonly int MixTrimTargetBytes = (int)(MixBytesPerSecond * 0.15);

    private readonly object _syncRoot = new();
    private readonly BufferedWaveProvider _mixBuffer;
    private readonly ISampleProvider _mixBufferReader;
    private byte[] _mixTrimBuffer = Array.Empty<byte>();

    private AudioFileReader? _reader;
    private ISampleProvider? _playbackChain;
    private string? _currentTrackPath;
    private bool _isPaused;
    private bool _isExternalSource;
    private bool _disposed;

    private WasapiOut? _monitorOut;
    private string? _monitorDeviceId;
    private VolumeSampleProvider? _monitorVolumeProvider;
    private VolumeSampleProvider? _mixVolumeProvider;
    private float _musicVolume = 0.5f;
    private float _monitorVolume = 0.5f;
    private bool _monitorPumpActive;
    private int _monitorConfigVersion;

    /// <summary>Raised when file playback reaches the end; carries the path of the finished track.</summary>
    public event EventHandler<string>? TrackEnded;
    public event EventHandler<string>? Error;

    public MusicPlaybackEngine()
    {
        _mixBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(InternalSampleRate, InternalChannels))
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        _mixBufferReader = _mixBuffer.ToSampleProvider();
    }

    public bool HasTrack
    {
        get { lock (_syncRoot) return _reader != null; }
    }

    public bool IsPlaying
    {
        get { lock (_syncRoot) return _reader != null && !_isPaused; }
    }

    public bool IsPaused
    {
        get { lock (_syncRoot) return _reader != null && _isPaused; }
    }

    public bool HasMonitorOutput
    {
        get { lock (_syncRoot) return _monitorOut != null; }
    }

    public bool IsExternalSourceActive
    {
        get { lock (_syncRoot) return _isExternalSource; }
    }

    public string? CurrentTrackPath
    {
        get { lock (_syncRoot) return _currentTrackPath; }
    }

    public TimeSpan Position
    {
        get { lock (_syncRoot) return _reader?.CurrentTime ?? TimeSpan.Zero; }
    }

    public TimeSpan Duration
    {
        get { lock (_syncRoot) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    public float MusicVolume
    {
        get { lock (_syncRoot) return _musicVolume; }
        set
        {
            lock (_syncRoot)
            {
                _musicVolume = Math.Clamp(value, 0f, 1f);
                if (_mixVolumeProvider != null)
                {
                    _mixVolumeProvider.Volume = _musicVolume;
                }
            }
        }
    }

    public float MonitorVolume
    {
        get { lock (_syncRoot) return _monitorVolume; }
        set
        {
            lock (_syncRoot)
            {
                _monitorVolume = Math.Clamp(value, 0f, 1f);
                if (_monitorVolumeProvider != null)
                {
                    _monitorVolumeProvider.Volume = _monitorVolume;
                }
            }
        }
    }

    public void Play(string path)
    {
        AudioFileReader newReader = new(path);
        AudioFileReader? oldReader;

        lock (_syncRoot)
        {
            oldReader = _reader;
            _reader = newReader;
            _playbackChain = FormatNormalizer.Normalize(newReader, InternalSampleRate, InternalChannels);
            _currentTrackPath = path;
            _isPaused = false;
            _isExternalSource = false;
            _mixBuffer.ClearBuffer();
        }

        oldReader?.Dispose();
    }

    /// <summary>
    /// Replaces file playback with a live external source (e.g. process loopback capture).
    /// The source may return short reads; the pipeline heads fill the remainder with
    /// silence, and it is never treated as "track ended".
    /// </summary>
    public void SetExternalSource(ISampleProvider source)
    {
        AudioFileReader? oldReader;

        lock (_syncRoot)
        {
            oldReader = _reader;
            _reader = null;
            _playbackChain = FormatNormalizer.Normalize(source, InternalSampleRate, InternalChannels);
            _currentTrackPath = null;
            _isPaused = false;
            _isExternalSource = true;
            _mixBuffer.ClearBuffer();
        }

        oldReader?.Dispose();
    }

    public void ClearExternalSource()
    {
        lock (_syncRoot)
        {
            if (!_isExternalSource)
            {
                return;
            }

            _isExternalSource = false;
            _playbackChain = null;
            _mixBuffer.ClearBuffer();
        }
    }

    public void Pause()
    {
        lock (_syncRoot)
        {
            if (_reader != null)
            {
                _isPaused = true;
            }
        }
    }

    public void Resume()
    {
        lock (_syncRoot)
        {
            if (_reader != null)
            {
                _isPaused = false;
            }
        }
    }

    public void Stop()
    {
        AudioFileReader? oldReader;

        lock (_syncRoot)
        {
            oldReader = _reader;
            _reader = null;
            _playbackChain = null;
            _currentTrackPath = null;
            _isPaused = false;
            _isExternalSource = false;
            _mixBuffer.ClearBuffer();
        }

        oldReader?.Dispose();
    }

    public void Seek(TimeSpan position)
    {
        lock (_syncRoot)
        {
            if (_reader == null)
            {
                return;
            }

            var clamped = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > _reader.TotalTime ? _reader.TotalTime : position;

            _reader.CurrentTime = clamped;
            _mixBuffer.ClearBuffer();
        }
    }

    /// <summary>
    /// Routes monitor playback to the given render device, or disables monitoring when null.
    /// Safe to call while a track is playing.
    /// </summary>
    public void ConfigureMonitor(string? deviceId)
    {
        int version;
        WasapiOut? oldOut;

        lock (_syncRoot)
        {
            bool unchanged = string.Equals(deviceId, _monitorDeviceId, StringComparison.Ordinal)
                && (deviceId == null) == (_monitorOut == null);

            if (unchanged)
            {
                return;
            }

            version = ++_monitorConfigVersion;
            oldOut = DetachMonitorLocked();
            _monitorDeviceId = deviceId;
        }

        DisposeMonitorOut(oldOut);

        if (deviceId == null)
        {
            return;
        }

        try
        {
            StartMonitor(deviceId, version);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start music monitor output on device {DeviceId}.", deviceId);
            lock (_syncRoot)
            {
                if (version == _monitorConfigVersion)
                {
                    _monitorDeviceId = null;
                }
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        WasapiOut? oldOut;
        lock (_syncRoot)
        {
            _monitorConfigVersion++;
            oldOut = DetachMonitorLocked();
            _monitorDeviceId = null;
        }

        DisposeMonitorOut(oldOut);
        Stop();
    }

    /// <summary>
    /// Creates the sample provider that <see cref="AudioRouter"/> mixes into the mic signal.
    /// Always fills the requested count (silence when nothing plays). Called on each routing start.
    /// </summary>
    public ISampleProvider CreateMixTap(WaveFormat targetFormat)
    {
        lock (_syncRoot)
        {
            _mixBuffer.ClearBuffer();
            var head = new MixHeadProvider(this);
            _mixVolumeProvider = new VolumeSampleProvider(head) { Volume = _musicVolume };
            return FormatNormalizer.Normalize(_mixVolumeProvider, targetFormat);
        }
    }

    private void StartMonitor(string deviceId, int version)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        var mixFormat = device.AudioClient.MixFormat;

        var head = new MonitorHeadProvider(this);
        var volumeProvider = new VolumeSampleProvider(head) { Volume = MonitorVolume };
        var normalized = FormatNormalizer.Normalize(volumeProvider, mixFormat);

        var monitorOut = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        monitorOut.PlaybackStopped += OnMonitorStopped;
        monitorOut.Init(new SampleToTargetWaveProvider(normalized, mixFormat));

        lock (_syncRoot)
        {
            if (version == _monitorConfigVersion)
            {
                _monitorOut = monitorOut;
                _monitorVolumeProvider = volumeProvider;
                Volatile.Write(ref _monitorPumpActive, true);

                // Play only signals the render thread; it does not block on it,
                // so it is safe to call while holding the lock — and doing so
                // guarantees no newer configuration can detach an unstarted out.
                monitorOut.Play();
                return;
            }
        }

        // A newer configuration superseded this start while it was being built.
        monitorOut.PlaybackStopped -= OnMonitorStopped;
        monitorOut.Dispose();
    }

    private WasapiOut? DetachMonitorLocked()
    {
        var oldOut = _monitorOut;
        _monitorOut = null;
        _monitorVolumeProvider = null;
        Volatile.Write(ref _monitorPumpActive, false);
        return oldOut;
    }

    private void DisposeMonitorOut(WasapiOut? monitorOut)
    {
        if (monitorOut == null)
        {
            return;
        }

        monitorOut.PlaybackStopped -= OnMonitorStopped;

        try
        {
            if (monitorOut.PlaybackState != PlaybackState.Stopped)
            {
                monitorOut.Stop();
            }
        }
        catch
        {
            // The device may already be unavailable.
        }

        monitorOut.Dispose();
    }

    private void OnMonitorStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null)
        {
            return;
        }

        Log.Error(e.Exception, "Music monitor playback stopped with exception.");

        WasapiOut? oldOut;
        lock (_syncRoot)
        {
            // Fall back to direct (router-driven) mode so mixing keeps working.
            oldOut = DetachMonitorLocked();
        }

        // Dispose on the thread pool: this event fires on the render thread itself.
        if (oldOut != null)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    oldOut.Dispose();
                }
                catch
                {
                    // Already torn down.
                }
            });
        }

        Error?.Invoke(this, $"Medhörning stoppades: {e.Exception.Message}");
    }

    /// <summary>
    /// Core decode step shared by both clock modes. Returns actual samples read;
    /// 0 when idle/paused. Detects end of track and raises <see cref="TrackEnded"/>.
    /// </summary>
    private int PumpRead(float[] buffer, int offset, int count)
    {
        AudioFileReader? endedReader = null;
        string? endedPath = null;
        int samplesRead;

        lock (_syncRoot)
        {
            if (_playbackChain == null || _isPaused)
            {
                return 0;
            }

            samplesRead = _playbackChain.Read(buffer, offset, count);

            // A short read from an external source just means "nothing captured yet";
            // only file playback interprets an empty read as end of track.
            if (samplesRead == 0 && _reader != null)
            {
                endedReader = _reader;
                endedPath = _currentTrackPath;
                _reader = null;
                _playbackChain = null;
                _currentTrackPath = null;
            }
        }

        if (endedReader != null)
        {
            // Capture the path with the ended reader so the event describes the
            // track that actually finished, even if another one starts meanwhile.
            string finishedPath = endedPath ?? string.Empty;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    endedReader.Dispose();
                }
                catch
                {
                    // Already torn down.
                }

                TrackEnded?.Invoke(this, finishedPath);
            });
        }

        return samplesRead;
    }

    private void PushToMixBuffer(byte[] scratch, float[] buffer, int offset, int count)
    {
        int byteCount = count * sizeof(float);

        if (_mixBuffer.BufferedBytes + byteCount > MixHighWatermarkBytes)
        {
            int discard = _mixBuffer.BufferedBytes - MixTrimTargetBytes;
            if (discard > 0)
            {
                if (_mixTrimBuffer.Length < discard)
                {
                    _mixTrimBuffer = new byte[discard];
                }

                _mixBuffer.Read(_mixTrimBuffer, 0, discard);
                Log.Debug("Mix buffer trimmed {DiscardedBytes} bytes to bound music latency.", discard);
            }
        }

        Buffer.BlockCopy(buffer, offset * sizeof(float), scratch, 0, byteCount);
        _mixBuffer.AddSamples(scratch, 0, byteCount);
    }

    private int ReadForMix(float[] buffer, int offset, int count)
    {
        if (Volatile.Read(ref _monitorPumpActive))
        {
            // Monitor output is the clock; drain what it teed into the buffer.
            return _mixBufferReader.Read(buffer, offset, count);
        }

        return PumpRead(buffer, offset, count);
    }

    /// <summary>
    /// Head of the monitor pipeline. Never returns less than requested so the
    /// monitor WasapiOut keeps running (silence between/after tracks).
    /// </summary>
    private sealed class MonitorHeadProvider : ISampleProvider
    {
        private readonly MusicPlaybackEngine _engine;
        private byte[] _scratch = Array.Empty<byte>();

        public MonitorHeadProvider(MusicPlaybackEngine engine)
        {
            _engine = engine;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(InternalSampleRate, InternalChannels);

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _engine.PumpRead(buffer, offset, count);

            if (samplesRead < count)
            {
                Array.Clear(buffer, offset + samplesRead, count - samplesRead);
            }

            if (_scratch.Length < count * sizeof(float))
            {
                _scratch = new byte[count * sizeof(float)];
            }

            // Tee the full block (including silence) so the mix side stays time-aligned.
            _engine.PushToMixBuffer(_scratch, buffer, offset, count);

            return count;
        }
    }

    /// <summary>
    /// Head of the mix pipeline handed to <see cref="AudioRouter"/>.
    /// Never returns less than requested (silence-filled).
    /// </summary>
    private sealed class MixHeadProvider : ISampleProvider
    {
        private readonly MusicPlaybackEngine _engine;

        public MixHeadProvider(MusicPlaybackEngine engine)
        {
            _engine = engine;
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(InternalSampleRate, InternalChannels);

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _engine.ReadForMix(buffer, offset, count);

            if (samplesRead < count)
            {
                Array.Clear(buffer, offset + samplesRead, count - samplesRead);
            }

            return count;
        }
    }
}
