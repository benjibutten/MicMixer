using NAudio.Wave;

namespace MicMixer.Audio;

/// <summary>
/// Multiplies the stream by 0 or 1 depending on the gate state, with a short
/// gain ramp on transitions so open/close never produces an audible click.
/// </summary>
internal sealed class GateSampleProvider : ISampleProvider
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
