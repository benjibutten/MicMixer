namespace MicMixer.Dsp;

/// <summary>Simple channel-linked feed-forward compressor for voice material.</summary>
internal sealed class LinkedCompressor
{
    private const float OutputCeiling = 0.98f;
    private readonly int _channels;
    private readonly float _thresholdDb;
    private readonly float _ratio;
    private readonly float _attackCoefficient;
    private readonly float _releaseCoefficient;
    private readonly float _makeupDb;
    private float _envelope;

    public LinkedCompressor(
        int sampleRate,
        int channels,
        float thresholdDb,
        float ratio,
        float attackMilliseconds,
        float releaseMilliseconds,
        float makeupDb)
    {
        _channels = channels;
        _thresholdDb = thresholdDb;
        _ratio = ratio;
        _attackCoefficient = TimeCoefficient(sampleRate, attackMilliseconds);
        _releaseCoefficient = TimeCoefficient(sampleRate, releaseMilliseconds);
        _makeupDb = makeupDb;
    }

    public void ProcessInPlace(Span<float> samples)
    {
        int frameCount = samples.Length / _channels;
        for (int frame = 0; frame < frameCount; frame++)
        {
            int offset = frame * _channels;
            float peak = 0f;
            for (int channel = 0; channel < _channels; channel++)
            {
                peak = Math.Max(peak, Math.Abs(samples[offset + channel]));
            }

            float coefficient = peak > _envelope ? _attackCoefficient : _releaseCoefficient;
            _envelope = coefficient * _envelope + (1f - coefficient) * peak;

            float levelDb = 20f * MathF.Log10(Math.Max(_envelope, 1e-12f));
            float reductionDb = levelDb > _thresholdDb
                ? _thresholdDb + (levelDb - _thresholdDb) / _ratio - levelDb
                : 0f;
            float gain = MathF.Pow(10f, (reductionDb + _makeupDb) / 20f);
            float limitedPeak = peak * gain;
            if (limitedPeak > OutputCeiling)
            {
                gain *= OutputCeiling / limitedPeak;
            }

            for (int channel = 0; channel < _channels; channel++)
            {
                samples[offset + channel] *= gain;
            }
        }
    }

    public void Reset()
    {
        _envelope = 0f;
    }

    private static float TimeCoefficient(int sampleRate, float milliseconds)
    {
        return MathF.Exp(-1f / (sampleRate * milliseconds / 1_000f));
    }
}
