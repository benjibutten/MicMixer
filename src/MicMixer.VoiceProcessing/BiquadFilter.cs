namespace MicMixer.Dsp;

/// <summary>Allocation-free direct-form-II-transposed biquad for interleaved audio.</summary>
internal sealed class BiquadFilter
{
    private readonly int _channels;
    private readonly float[] _z1;
    private readonly float[] _z2;
    private readonly float _b0;
    private readonly float _b1;
    private readonly float _b2;
    private readonly float _a1;
    private readonly float _a2;

    private BiquadFilter(int channels, double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _channels = channels;
        _z1 = new float[channels];
        _z2 = new float[channels];
        _b0 = (float)(b0 / a0);
        _b1 = (float)(b1 / a0);
        _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0);
        _a2 = (float)(a2 / a0);
    }

    public static BiquadFilter HighPass(int sampleRate, int channels, float frequencyHz, float q = 0.70710678f)
    {
        double omega = 2d * Math.PI * frequencyHz / sampleRate;
        double cosine = Math.Cos(omega);
        double alpha = Math.Sin(omega) / (2d * q);
        return new BiquadFilter(
            channels,
            (1d + cosine) / 2d,
            -(1d + cosine),
            (1d + cosine) / 2d,
            1d + alpha,
            -2d * cosine,
            1d - alpha);
    }

    public static BiquadFilter Peaking(int sampleRate, int channels, float frequencyHz, float gainDb, float q)
    {
        double amplitude = Math.Pow(10d, gainDb / 40d);
        double omega = 2d * Math.PI * frequencyHz / sampleRate;
        double cosine = Math.Cos(omega);
        double alpha = Math.Sin(omega) / (2d * q);
        return new BiquadFilter(
            channels,
            1d + alpha * amplitude,
            -2d * cosine,
            1d - alpha * amplitude,
            1d + alpha / amplitude,
            -2d * cosine,
            1d - alpha / amplitude);
    }

    public static BiquadFilter HighShelf(int sampleRate, int channels, float frequencyHz, float gainDb)
    {
        double amplitude = Math.Pow(10d, gainDb / 40d);
        double omega = 2d * Math.PI * frequencyHz / sampleRate;
        double cosine = Math.Cos(omega);
        double sine = Math.Sin(omega);
        double alpha = sine / 2d * Math.Sqrt(2d);
        double twoSqrtAAlpha = 2d * Math.Sqrt(amplitude) * alpha;

        return new BiquadFilter(
            channels,
            amplitude * ((amplitude + 1d) + (amplitude - 1d) * cosine + twoSqrtAAlpha),
            -2d * amplitude * ((amplitude - 1d) + (amplitude + 1d) * cosine),
            amplitude * ((amplitude + 1d) + (amplitude - 1d) * cosine - twoSqrtAAlpha),
            (amplitude + 1d) - (amplitude - 1d) * cosine + twoSqrtAAlpha,
            2d * ((amplitude - 1d) - (amplitude + 1d) * cosine),
            (amplitude + 1d) - (amplitude - 1d) * cosine - twoSqrtAAlpha);
    }

    public void ProcessInPlace(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            int channel = i % _channels;
            float input = samples[i];
            float output = _b0 * input + _z1[channel];
            _z1[channel] = _b1 * input - _a1 * output + _z2[channel];
            _z2[channel] = _b2 * input - _a2 * output;
            samples[i] = output;
        }
    }

    public void Reset()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
    }
}
