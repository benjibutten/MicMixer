using NAudio.Wave;

namespace MicMixer.Audio;

internal sealed class SwitchingSampleProvider : ISampleProvider, IDisposable
{
    private const double HalfPi = Math.PI / 2d;
    private readonly ISamplePair _pair;
    private readonly Func<bool> _useSecondary;
    private readonly float[] _fadeOut;
    private readonly float[] _fadeIn;
    private float[] _primaryBuffer = Array.Empty<float>();
    private float[] _secondaryBuffer = Array.Empty<float>();
    private bool _targetSecondary;
    private int _fadePosition;
    private bool _initialized;

    public SwitchingSampleProvider(
        ISampleProvider primary,
        ISampleProvider? secondary,
        Func<bool> useSecondary,
        int crossfadeMilliseconds = 8)
        : this(new IndependentSamplePair(primary, secondary), useSecondary, crossfadeMilliseconds)
    {
    }

    public SwitchingSampleProvider(
        ISamplePair pair,
        Func<bool> useSecondary,
        int crossfadeMilliseconds = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(crossfadeMilliseconds);
        _pair = pair;
        _useSecondary = useSecondary;
        WaveFormat = pair.WaveFormat;

        int fadeFrames = Math.Max(1, WaveFormat.SampleRate * crossfadeMilliseconds / 1_000);
        _fadeOut = new float[fadeFrames];
        _fadeIn = new float[fadeFrames];
        for (int frame = 0; frame < fadeFrames; frame++)
        {
            double progress = fadeFrames == 1 ? 1d : frame / (double)(fadeFrames - 1);
            _fadeOut[frame] = (float)Math.Cos(progress * HalfPi);
            _fadeIn[frame] = (float)Math.Sin(progress * HalfPi);
        }
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        if (buffer.Length % WaveFormat.Channels != 0)
        {
            throw new ArgumentException("The destination must contain complete frames.", nameof(buffer));
        }

        EnsureCapacity(buffer.Length);
        Span<float> primary = _primaryBuffer.AsSpan(0, buffer.Length);
        Span<float> secondary = _secondaryBuffer.AsSpan(0, buffer.Length);
        _pair.Read(primary, secondary);

        bool requestedSecondary = _pair.HasSecondary && _useSecondary();
        if (!_initialized)
        {
            _initialized = true;
            _targetSecondary = requestedSecondary;
            _fadePosition = requestedSecondary ? _fadeOut.Length - 1 : 0;
        }
        else
        {
            _targetSecondary = requestedSecondary;
        }

        int channels = WaveFormat.Channels;
        for (int offset = 0; offset < buffer.Length; offset += channels)
        {
            if (_fadeOut.Length == 1)
            {
                ReadOnlySpan<float> selected = _targetSecondary ? secondary : primary;
                selected.Slice(offset, channels).CopyTo(buffer.Slice(offset, channels));
                continue;
            }

            float primaryGain = _fadeOut[_fadePosition];
            float secondaryGain = _fadeIn[_fadePosition];
            for (int channel = 0; channel < channels; channel++)
            {
                buffer[offset + channel] =
                    primary[offset + channel] * primaryGain + secondary[offset + channel] * secondaryGain;
            }

            if (_targetSecondary && _fadePosition < _fadeOut.Length - 1)
            {
                _fadePosition++;
            }
            else if (!_targetSecondary && _fadePosition > 0)
            {
                _fadePosition--;
            }
        }

        return buffer.Length;
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public void Dispose() => _pair.Dispose();

    private void EnsureCapacity(int count)
    {
        if (_primaryBuffer.Length < count)
        {
            _primaryBuffer = new float[count];
        }

        if (_secondaryBuffer.Length < count)
        {
            _secondaryBuffer = new float[count];
        }
    }
}
