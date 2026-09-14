using NAudio.Wave;

namespace MicMixer.Audio;

/// <summary>
/// Mutes the mic while nothing above the threshold is picked up, so what leaves
/// the mic branch between phrases is exact digital silence rather than room
/// noise and hum. Receiving apps that decide "talking" from the signal
/// therefore stop as soon as the speaker does.
///
/// Opens within a couple of milliseconds, holds for a while after the level
/// drops so natural pauses do not chop words, then ramps down to zero. Disabled
/// means unity gain, reached through the same ramp so toggling never clicks.
/// </summary>
internal sealed class NoiseGateSampleProvider : ISampleProvider
{
    private const float AttackSeconds = 0.002f;
    private const float HoldSeconds = 0.25f;
    private const float ReleaseSeconds = 0.04f;

    private readonly ISampleProvider _source;
    private readonly Func<bool> _enabled;
    private readonly Func<float> _thresholdDb;
    private readonly int _channels;
    private readonly int _holdFrames;
    private readonly float _attackStep;
    private readonly float _releaseStep;
    private int _holdRemaining;
    private float _gain = 1f;
    private bool _isOpen = true;
    private float _inputPeak;

    public NoiseGateSampleProvider(ISampleProvider source, Func<bool> enabled, Func<float> thresholdDb)
    {
        _source = source;
        _enabled = enabled;
        _thresholdDb = thresholdDb;
        _channels = source.WaveFormat.Channels;

        int sampleRate = source.WaveFormat.SampleRate;
        _holdFrames = (int)(HoldSeconds * sampleRate);
        _attackStep = 1f / Math.Max(1f, AttackSeconds * sampleRate);
        _releaseStep = 1f / Math.Max(1f, ReleaseSeconds * sampleRate);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Whether any signal currently passes; always true while disabled.</summary>
    public bool IsOpen => Volatile.Read(ref _isOpen);

    /// <summary>
    /// Highest pre-gate peak since the last call, then resets. Accumulated rather
    /// than sampled so a meter polling every 50 ms sees every block, including
    /// the short transients that would reopen the gate.
    /// </summary>
    public float ReadAndResetInputPeak() => Interlocked.Exchange(ref _inputPeak, 0f);

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        Span<float> samples = buffer[..read];

        bool enabled = _enabled();
        if (!enabled && _gain == 1f)
        {
            Volatile.Write(ref _isOpen, true);
            return read;
        }

        float threshold = MathF.Pow(10f, _thresholdDb() / 20f);
        float blockPeak = 0f;

        // Whole frames only; a trailing partial frame from a short read is left as is.
        int frameSamples = samples.Length / _channels * _channels;
        for (int offset = 0; offset < frameSamples; offset += _channels)
        {
            float framePeak = 0f;
            for (int channel = 0; channel < _channels; channel++)
            {
                framePeak = Math.Max(framePeak, Math.Abs(samples[offset + channel]));
            }

            blockPeak = Math.Max(blockPeak, framePeak);

            bool open;
            if (!enabled)
            {
                _holdRemaining = 0;
                open = true;
            }
            else if (framePeak >= threshold)
            {
                _holdRemaining = _holdFrames;
                open = true;
            }
            else if (_holdRemaining > 0)
            {
                _holdRemaining--;
                open = true;
            }
            else
            {
                open = false;
            }

            if (open)
            {
                _gain = Math.Min(1f, _gain + _attackStep);
            }
            else
            {
                _gain = Math.Max(0f, _gain - _releaseStep);
            }

            if (_gain == 1f)
            {
                continue;
            }

            for (int channel = 0; channel < _channels; channel++)
            {
                samples[offset + channel] *= _gain;
            }
        }

        if (blockPeak > Volatile.Read(ref _inputPeak))
        {
            Volatile.Write(ref _inputPeak, blockPeak);
        }

        Volatile.Write(ref _isOpen, _gain > 0f);
        return read;
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
}
