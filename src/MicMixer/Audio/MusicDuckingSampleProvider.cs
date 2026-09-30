using NAudio.Wave;

namespace MicMixer.Audio;

/// <summary>
/// Lowers the music to a set fraction of its volume while the mic is heard, and
/// brings it back once the mic goes quiet. Glides quickly down and slowly up, so
/// the voice is on top from its first word and the music does not jump back
/// between phrases.
/// </summary>
internal sealed class MusicDuckingSampleProvider : ISampleProvider
{
    private const float DownTimeConstantSeconds = 0.02f;
    private const float UpTimeConstantSeconds = 0.15f;
    // Below this distance the glide snaps to its target, so a steady state can
    // take the fast paths instead of approaching it forever.
    private const float SnapDistance = 0.0005f;

    private readonly ISampleProvider _source;
    private readonly Func<float> _targetGain;
    private readonly int _channels;
    private readonly float _downCoefficient;
    private readonly float _upCoefficient;
    private float _gain = 1f;

    /// <param name="source">Music source.</param>
    /// <param name="targetGain">Gain to glide towards, read once per block: 1 for full volume, lower while ducking.</param>
    public MusicDuckingSampleProvider(ISampleProvider source, Func<float> targetGain)
    {
        _source = source;
        _targetGain = targetGain;
        _channels = source.WaveFormat.Channels;

        int sampleRate = source.WaveFormat.SampleRate;
        _downCoefficient = 1f - MathF.Exp(-1f / (DownTimeConstantSeconds * sampleRate));
        _upCoefficient = 1f - MathF.Exp(-1f / (UpTimeConstantSeconds * sampleRate));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int read = _source.Read(buffer);
        Span<float> samples = buffer[..read];
        float target = Math.Clamp(_targetGain(), 0f, 1f);

        if (_gain == target)
        {
            if (target != 1f)
            {
                Scale(samples, target);
            }

            return read;
        }

        float coefficient = target < _gain ? _downCoefficient : _upCoefficient;
        for (int offset = 0; offset + _channels <= samples.Length; offset += _channels)
        {
            _gain += (target - _gain) * coefficient;
            if (Math.Abs(target - _gain) < SnapDistance)
            {
                _gain = target;
            }

            for (int channel = 0; channel < _channels; channel++)
            {
                samples[offset + channel] *= _gain;
            }
        }

        return read;
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    private static void Scale(Span<float> samples, float gain)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }
}
