using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace MicMixer.Audio;

/// <summary>
/// The secondary-output side of the pre-gate fanout: a bounded buffer that the
/// primary audio thread writes into (never blocking on the secondary device),
/// followed by the secondary-only gate and volume stages.
///
/// Clock-drift policy, both directions:
/// - Secondary slower than primary (buffer grows): past the high watermark the
///   oldest audio is dropped down to the trim target, bounding the latency —
///   the same policy as the music monitor fanout in <see cref="MusicPlaybackEngine"/>.
/// - Secondary faster than primary (buffer shrinks): the branch starts with a
///   silence cushion at the trim-target level, and on a true starvation it
///   re-buffers (holds silence) until the cushion is rebuilt — one rare audible
///   gap instead of a periodic stream of tiny ones.
///
/// The write path runs on the primary audio thread and must stay cheap: scratch
/// buffers are preallocated for the steady state and trim logging is throttled.
/// </summary>
internal sealed class SecondaryTapBranch : ISampleProvider
{
    private const int TrimLogThrottleMilliseconds = 5_000;

    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volumeProvider;
    private readonly int _highWatermarkBytes;
    private readonly int _trimTargetBytes;
    private readonly int _primeBytes;
    private byte[] _writeScratch;
    private byte[] _trimScratch;
    private long _nextTrimLogTicks;
    private volatile bool _rebuffering;

    public SecondaryTapBranch(WaveFormat sourceFormat, Func<bool> isGateOpen)
    {
        int bytesPerSecond = sourceFormat.SampleRate * sourceFormat.Channels * sizeof(float);
        _highWatermarkBytes = (int)(bytesPerSecond * 0.4);
        _trimTargetBytes = (int)(bytesPerSecond * 0.15);
        _primeBytes = _trimTargetBytes;

        // Preallocated so the steady-state write path never allocates on the
        // primary audio thread: a WasapiOut block is well under 250 ms, and a
        // trim discards at most highWatermark - trimTarget bytes.
        _writeScratch = new byte[bytesPerSecond / 4];
        _trimScratch = new byte[_highWatermarkBytes - _trimTargetBytes];

        _buffer = new BufferedWaveProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(sourceFormat.SampleRate, sourceFormat.Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };

        // Initial cushion: a slightly fast secondary clock eats into this instead
        // of producing underflow gaps right from the start. The freshly allocated
        // scratch is all zeros, i.e. silence.
        _buffer.AddSamples(_writeScratch, 0, _primeBytes);

        var head = new RebufferingHead(this, _buffer.ToSampleProvider());
        var gate = new GateSampleProvider(head, isGateOpen);
        _volumeProvider = new VolumeSampleProvider(gate);
    }

    public WaveFormat WaveFormat => _volumeProvider.WaveFormat;

    /// <summary>Gain applied to this branch only; the primary chain never passes through it.</summary>
    public float Volume
    {
        get => _volumeProvider.Volume;
        set => _volumeProvider.Volume = Math.Clamp(value, 0f, 1f);
    }

    internal int BufferedBytes => _buffer.BufferedBytes;

    /// <summary>Size of the startup/re-buffer silence cushion, in bytes.</summary>
    internal int PrimeBytes => _primeBytes;

    /// <summary>
    /// Copies one pre-gate block from the primary audio thread into the buffer.
    /// Trims the oldest audio first when the fill level has drifted past the
    /// high watermark, so the call never stalls and latency stays bounded.
    /// </summary>
    public void Write(float[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        int byteCount = count * sizeof(float);

        if (_buffer.BufferedBytes + byteCount > _highWatermarkBytes)
        {
            int discard = _buffer.BufferedBytes - _trimTargetBytes;
            if (discard > 0)
            {
                if (_trimScratch.Length < discard)
                {
                    _trimScratch = new byte[discard];
                }

                _buffer.Read(_trimScratch, 0, discard);

                long now = Environment.TickCount64;
                if (now >= _nextTrimLogTicks)
                {
                    _nextTrimLogTicks = now + TrimLogThrottleMilliseconds;
                    Log.Debug("Secondary output buffer trimmed {DiscardedBytes} bytes to bound latency.", discard);
                }
            }
        }

        if (_writeScratch.Length < byteCount)
        {
            _writeScratch = new byte[byteCount];
        }

        Buffer.BlockCopy(buffer, offset * sizeof(float), _writeScratch, 0, byteCount);
        _buffer.AddSamples(_writeScratch, 0, byteCount);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        return _volumeProvider.Read(buffer, offset, count);
    }

    /// <summary>
    /// Always fills the requested count so the secondary WasapiOut keeps running
    /// on underflow. After a starvation it holds silence until the producer has
    /// rebuilt the cushion, converting steady clock drift into rare re-buffers.
    /// </summary>
    private sealed class RebufferingHead : ISampleProvider
    {
        private readonly SecondaryTapBranch _branch;
        private readonly ISampleProvider _source;

        public RebufferingHead(SecondaryTapBranch branch, ISampleProvider source)
        {
            _branch = branch;
            _source = source;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_branch._rebuffering)
            {
                if (_branch._buffer.BufferedBytes < _branch._primeBytes)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                _branch._rebuffering = false;
            }

            int samplesRead = _source.Read(buffer, offset, count);

            if (samplesRead < count)
            {
                Array.Clear(buffer, offset + samplesRead, count - samplesRead);
                _branch._rebuffering = true;
            }

            return count;
        }
    }
}
