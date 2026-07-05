using NAudio.Wave;

namespace MicMixer.Audio;

internal sealed class ChannelAdapterSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly WaveFormat _waveFormat;
    private float[] _sourceBuffer = Array.Empty<float>();

    public ChannelAdapterSampleProvider(ISampleProvider source, int targetChannels)
    {
        _source = source;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int targetChannels = _waveFormat.Channels;
        int sourceChannels = _source.WaveFormat.Channels;
        int framesRequested = count / targetChannels;
        int sourceSamplesNeeded = framesRequested * sourceChannels;

        EnsureCapacity(sourceSamplesNeeded);

        int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceSamplesRead / sourceChannels;
        int samplesWritten = framesRead * targetChannels;

        for (int frame = 0; frame < framesRead; frame++)
        {
            int sourceFrameOffset = frame * sourceChannels;
            int targetFrameOffset = offset + (frame * targetChannels);

            if (sourceChannels == 1)
            {
                float sample = _sourceBuffer[sourceFrameOffset];
                for (int channel = 0; channel < targetChannels; channel++)
                {
                    buffer[targetFrameOffset + channel] = sample;
                }
            }
            else if (targetChannels == 1)
            {
                float sum = 0f;
                for (int channel = 0; channel < sourceChannels; channel++)
                {
                    sum += _sourceBuffer[sourceFrameOffset + channel];
                }

                buffer[targetFrameOffset] = sum / sourceChannels;
            }
            else
            {
                for (int channel = 0; channel < targetChannels; channel++)
                {
                    int sourceChannel = Math.Min(channel, sourceChannels - 1);
                    buffer[targetFrameOffset + channel] = _sourceBuffer[sourceFrameOffset + sourceChannel];
                }
            }
        }

        return samplesWritten;
    }

    private void EnsureCapacity(int sampleCount)
    {
        if (_sourceBuffer.Length < sampleCount)
        {
            _sourceBuffer = new float[sampleCount];
        }
    }
}
