using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace MicMixer.Audio;

/// <summary>
/// The secondary-output side of the fanout: a bounded buffer that the primary
/// audio thread writes into (never blocking on the secondary device), followed
/// by the secondary-only volume stage. Gating happens upstream — the router's
/// <see cref="MixFanoutSampleProvider"/> applies the secondary's own mic and
/// music gates before writing, so this branch receives a finished mix.
///
/// Clock-drift policy: the producer runs on the primary output device's clock and
/// the consumer on the secondary device's clock, so the fill level drifts even
/// when nothing is wrong. Drift is absorbed continuously by resampling — the
/// branch plays back a hair faster or slower than nominal (at most
/// <see cref="MaxRateAdjustment"/>, i.e. well under the ~0.01% drift of real
/// hardware clocks) to steer the fill level back to <see cref="PrimeBytes"/>.
/// Nothing is dropped or inserted, so the stream stays continuous and monotonic;
/// capture software downstream (OBS and friends) sees no timestamp discontinuity
/// to compensate for.
///
/// Two coarse safety nets remain for the disturbances resampling cannot absorb —
/// a stalled host, a device switch, a suspended process:
/// - Buffer past the high watermark: the oldest audio is dropped down to the trim
///   target, bounding the latency.
/// - True starvation: the branch re-buffers (holds silence) until the cushion is
///   rebuilt — one rare audible gap instead of a periodic stream of tiny ones.
/// In steady state neither should ever fire; both reset the drift tracking, since
/// they move the fill level by hand.
///
/// The write path runs on the primary audio thread and must stay cheap: scratch
/// buffers are preallocated for the steady state and trim logging is throttled.
/// </summary>
internal sealed class SecondaryTapBranch : ISampleProvider
{
    private const int TrimLogThrottleMilliseconds = 5_000;

    /// <summary>
    /// Largest playback-rate correction, as a fraction of the nominal rate. 0.5%
    /// is roughly fifty times the drift between two real audio clocks, so the
    /// loop never saturates in practice, and it stays under the threshold where
    /// a pitch shift becomes audible (≈9 cents).
    /// </summary>
    private const double MaxRateAdjustment = 0.005;

    /// <summary>
    /// Time constant for smoothing the measured fill level before it steers the
    /// rate. The producer delivers whole blocks, so the raw level steps by tens of
    /// milliseconds at a time; correcting against that directly would wobble the
    /// pitch at block rate. Real drift is orders of magnitude slower than this.
    /// </summary>
    private const double FillSmoothingSeconds = 2d;

    private readonly BufferedWaveProvider _buffer;
    private readonly VolumeSampleProvider _volumeProvider;
    private readonly int _frameBytes;
    private readonly int _highWatermarkBytes;
    private readonly int _trimTargetBytes;
    private readonly int _primeBytes;
    private readonly int _sampleRate;
    private byte[] _writeScratch;
    private byte[] _trimScratch;
    private long _nextTrimLogTicks;
    private double _smoothedFillBytes;
    private double _rateRatio = 1d;
    private volatile bool _rebuffering;
    private volatile bool _resetDriftTracking;

    public SecondaryTapBranch(WaveFormat sourceFormat)
    {
        // Every threshold is a whole number of frames. A level that splits a frame
        // would shift the interleaved stream by one sample from that point on —
        // i.e. swap the channels for the rest of the session — and the fractions
        // are real: 0.15 s at 22 050 Hz stereo is 3307.5 frames.
        _frameBytes = sourceFormat.Channels * sizeof(float);
        int bytesPerSecond = sourceFormat.SampleRate * _frameBytes;
        _highWatermarkBytes = AlignToFrame((int)(bytesPerSecond * 0.4));
        _trimTargetBytes = AlignToFrame((int)(bytesPerSecond * 0.15));
        _primeBytes = _trimTargetBytes;
        _sampleRate = sourceFormat.SampleRate;
        _smoothedFillBytes = _trimTargetBytes;

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

        var resampler = new DriftCompensatingResampler(this, _buffer.ToSampleProvider());
        var head = new RebufferingHead(this, resampler);
        _volumeProvider = new VolumeSampleProvider(head);
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
    /// Playback rate currently used to absorb clock drift, as a factor of the
    /// nominal rate: above 1 the branch is draining a buffer that has grown,
    /// below 1 it is stretching one that has shrunk.
    /// </summary>
    internal double RateRatio => Volatile.Read(ref _rateRatio);

    /// <summary>
    /// Copies one finished secondary-mix block from the primary audio thread into
    /// the buffer. Trims the oldest audio first when the fill level has drifted
    /// past the high watermark, so the call never stalls and latency stays bounded.
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
            // Aligned again here, so even a producer that hands over a partial
            // frame cannot make the trim cut one in half.
            int discard = AlignToFrame(_buffer.BufferedBytes - _trimTargetBytes);
            if (discard > 0)
            {
                if (_trimScratch.Length < discard)
                {
                    _trimScratch = new byte[discard];
                }

                _buffer.Read(_trimScratch, 0, discard);

                // The fill level just moved by hand; the smoothed view of it must
                // not chase the step and over-correct the rate for seconds after.
                _resetDriftTracking = true;

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

    /// <summary>Rounds a byte count down to a whole number of frames.</summary>
    private int AlignToFrame(int bytes) => bytes - (bytes % _frameBytes);

    /// <summary>
    /// Recomputes the playback rate from the buffer's fill level. Called once per
    /// device block from the render thread, which is the only writer of the
    /// smoothing state.
    /// </summary>
    private double NextRateRatio(int frames)
    {
        if (_resetDriftTracking)
        {
            _resetDriftTracking = false;
            _smoothedFillBytes = _trimTargetBytes;
        }

        // One-pole smoothing expressed in frames, so the response is the same
        // whether the device asks for 10 ms or 100 ms at a time.
        double alpha = Math.Clamp(frames / (FillSmoothingSeconds * _sampleRate), 0d, 1d);
        _smoothedFillBytes += (_buffer.BufferedBytes - _smoothedFillBytes) * alpha;

        // Proportional control: a full trim-target of excess buffer asks for the
        // maximum correction, which pulls the level back with a ~30 s time
        // constant. Slow is the point — the correction has to stay inaudible, and
        // the drift it cancels accumulates over minutes.
        double error = (_smoothedFillBytes - _trimTargetBytes) / _trimTargetBytes;
        double ratio = 1d + Math.Clamp(error * MaxRateAdjustment, -MaxRateAdjustment, MaxRateAdjustment);

        Volatile.Write(ref _rateRatio, ratio);
        return ratio;
    }

    /// <summary>
    /// Reads the buffer at a rate steered by <see cref="NextRateRatio"/>, using
    /// Catmull-Rom interpolation between the four most recent frames. The
    /// interpolation reproduces a constant exactly, so a settled buffer (ratio 1,
    /// phase 0) passes the mix through untouched; the cost is a fixed three-frame
    /// group delay, well under a tenth of a millisecond.
    /// </summary>
    private sealed class DriftCompensatingResampler : ISampleProvider
    {
        private const int WindowFrames = 4;

        private readonly SecondaryTapBranch _branch;
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private readonly float[] _window;
        private float[] _scratch;
        private int _scratchFill;
        private int _scratchPosition;
        private int _refillTargetSamples;
        private double _phase;

        public DriftCompensatingResampler(SecondaryTapBranch branch, ISampleProvider source)
        {
            _branch = branch;
            _source = source;
            _channels = source.WaveFormat.Channels;

            // Starts as silence, which is exactly the pre-history of a stream that
            // opens on the buffer's silence cushion.
            _window = new float[WindowFrames * _channels];

            // Sized up front, off the audio thread, for the most audio the branch
            // can ever hold: a device block larger than that would starve whatever
            // the scratch could stage, so the growth path below stays unused and
            // the render thread never allocates.
            _scratch = new float[branch._highWatermarkBytes / sizeof(float)];
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = _channels;

            // Whole frames only. The caller aligns the request, so a remainder
            // here means a partial frame that must not be fed through.
            int frames = count / channels;
            if (frames == 0)
            {
                return 0;
            }

            double ratio = _branch.NextRateRatio(frames);

            // Upper bound on the source frames this block can consume, so the
            // buffer is drained in one bulk read instead of a lock per frame.
            // The block size is fixed once the device is initialised, so the
            // scratch grows on the first read and is reused from then on.
            _refillTargetSamples = ((int)Math.Ceiling(_phase + (frames * ratio)) + 1) * channels;
            EnsureScratchCapacity(_refillTargetSamples);

            int written = 0;

            for (int frame = 0; frame < frames; frame++)
            {
                // Advance before interpolating: the phase must stay inside the
                // window, never extrapolate past it.
                while (_phase >= 1d)
                {
                    if (!TryAdvanceWindow())
                    {
                        // Source dry. The phase debt is kept, so playback resumes
                        // exactly where it stopped once audio returns.
                        return written;
                    }

                    _phase -= 1d;
                }

                int target = offset + written;
                float position = (float)_phase;

                for (int channel = 0; channel < channels; channel++)
                {
                    buffer[target + channel] = Interpolate(channel, position);
                }

                written += channels;
                _phase += ratio;
            }

            return written;
        }

        /// <summary>
        /// Shifts one source frame into the window, refilling the scratch from the
        /// buffer when it runs out.
        /// </summary>
        private bool TryAdvanceWindow()
        {
            int channels = _channels;

            if (_scratchFill - _scratchPosition < channels && !TryRefillScratch())
            {
                return false;
            }

            int tail = channels * (WindowFrames - 1);
            Array.Copy(_window, channels, _window, 0, tail);
            Array.Copy(_scratch, _scratchPosition, _window, tail, channels);
            _scratchPosition += channels;
            return true;
        }

        /// <summary>
        /// Pulls the rest of this block from the buffer in one read. A partial
        /// frame at the tail is carried over rather than consumed, so the channels
        /// can never slip out of order if the source ever hands one over.
        /// </summary>
        private bool TryRefillScratch()
        {
            int leftover = _scratchFill - _scratchPosition;

            if (leftover > 0 && _scratchPosition > 0)
            {
                Array.Copy(_scratch, _scratchPosition, _scratch, 0, leftover);
            }

            _scratchPosition = 0;
            _scratchFill = leftover;

            // Only what this block can consume: audio parked in the scratch has
            // already left the buffer, and the rate loop measures the buffer.
            int wanted = Math.Min(_refillTargetSamples, _scratch.Length) - _scratchFill;
            if (wanted > 0)
            {
                _scratchFill += _source.Read(_scratch, _scratchFill, wanted);
            }

            return _scratchFill >= _channels;
        }

        /// <summary>
        /// Drops the pre-gap audio still sitting in the window so playback resumes
        /// from silence rather than blending across the gap, leaving the resampler
        /// in the same state it started in.
        ///
        /// The scratch is deliberately left alone: anything parked there is the
        /// head of a frame whose tail has not arrived yet, and discarding it would
        /// leave every later read offset by part of a frame — a channel slip.
        /// </summary>
        internal void ResetToSilence()
        {
            Array.Clear(_window);
            _phase = 0d;
        }

        private void EnsureScratchCapacity(int samples)
        {
            if (_scratch.Length >= samples)
            {
                return;
            }

            var grown = new float[samples];
            int leftover = _scratchFill - _scratchPosition;
            Array.Copy(_scratch, _scratchPosition, grown, 0, leftover);
            _scratch = grown;
            _scratchPosition = 0;
            _scratchFill = leftover;
        }

        private float Interpolate(int channel, float position)
        {
            int channels = _channels;
            float y0 = _window[channel];
            float y1 = _window[channels + channel];
            float y2 = _window[(2 * channels) + channel];
            float y3 = _window[(3 * channels) + channel];

            float c1 = 0.5f * (y2 - y0);
            float c2 = y0 - (2.5f * y1) + (2f * y2) - (0.5f * y3);
            float c3 = (0.5f * (y3 - y0)) + (1.5f * (y1 - y2));

            return (((c3 * position) + c2) * position + c1) * position + y1;
        }
    }

    /// <summary>
    /// Always fills the requested count so the secondary WasapiOut keeps running
    /// on underflow. After a starvation it holds silence until the producer has
    /// rebuilt the cushion, converting a stall into one rare re-buffer. Steady
    /// clock drift no longer reaches this stage — the resampler absorbs it.
    /// </summary>
    private sealed class RebufferingHead : ISampleProvider
    {
        private readonly SecondaryTapBranch _branch;
        private readonly DriftCompensatingResampler _source;
        private readonly int _channels;

        public RebufferingHead(SecondaryTapBranch branch, DriftCompensatingResampler source)
        {
            _branch = branch;
            _source = source;
            _channels = source.WaveFormat.Channels;
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

                // The cushion was refilled in one step; start steering from the
                // target again instead of from the starved level.
                _branch._resetDriftTracking = true;
            }

            // The stage below deals in whole frames. Asking it for a partial one
            // would come back short and be read as starvation, so the remainder is
            // silenced here instead — every caller in the chain requests whole
            // frames, and one that did not must not restart the branch.
            int wholeFrames = count - (count % _channels);
            int samplesRead = _source.Read(buffer, offset, wholeFrames);

            if (samplesRead < wholeFrames)
            {
                _branch._rebuffering = true;

                // Nothing that played before the gap may leak into what plays
                // after it, however briefly.
                _source.ResetToSilence();
            }

            if (samplesRead < count)
            {
                Array.Clear(buffer, offset + samplesRead, count - samplesRead);
            }

            return count;
        }
    }
}
