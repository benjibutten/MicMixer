using NAudio.Wave;

namespace MicMixer.Audio;

/// <summary>
/// Pass-through that hands a copy of every block it reads to a side consumer,
/// without consuming the source a second time. Used to fan the pre-gate mix
/// out to the secondary output while the primary chain keeps the same data.
/// </summary>
internal sealed class TapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Action<float[], int, int> _onBlock;

    public TapSampleProvider(ISampleProvider source, Action<float[], int, int> onBlock)
    {
        _source = source;
        _onBlock = onBlock;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);

        if (samplesRead > 0)
        {
            _onBlock(buffer, offset, samplesRead);
        }

        return samplesRead;
    }
}
