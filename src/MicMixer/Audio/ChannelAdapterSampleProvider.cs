using NAudio.Wave;

namespace MicMixer.Audio;

internal sealed class ChannelAdapterSampleProvider : ISampleProvider
{
    private const int FrontChannels = 2;

    private readonly ISampleProvider _source;
    private readonly WaveFormat _waveFormat;
    private float[] _sourceBuffer = Array.Empty<float>();

    public ChannelAdapterSampleProvider(ISampleProvider source, int targetChannels)
    {
        _source = source;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(Span<float> buffer)
    {
        int targetChannels = _waveFormat.Channels;
        int sourceChannels = _source.WaveFormat.Channels;
        int framesRequested = buffer.Length / targetChannels;
        int sourceSamplesNeeded = framesRequested * sourceChannels;

        EnsureCapacity(sourceSamplesNeeded);

        int sourceSamplesRead = _source.Read(_sourceBuffer.AsSpan(0, sourceSamplesNeeded));
        int framesRead = sourceSamplesRead / sourceChannels;
        int samplesWritten = framesRead * targetChannels;

        for (int frame = 0; frame < framesRead; frame++)
        {
            int sourceFrameOffset = frame * sourceChannels;
            int targetFrameOffset = frame * targetChannels;

            if (sourceChannels == 1)
            {
                // Mono plays on the front pair only, for the same reason as below.
                float sample = _sourceBuffer[sourceFrameOffset];
                for (int channel = 0; channel < targetChannels; channel++)
                {
                    buffer[targetFrameOffset + channel] = channel < FrontChannels ? sample : 0f;
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
                // Channels beyond the source stay silent: in a surround layout they
                // are centre, LFE and rear speakers, and a copy of one stereo side
                // there would make that side louder and send full-range audio to the
                // subwoofer channel.
                for (int channel = 0; channel < targetChannels; channel++)
                {
                    buffer[targetFrameOffset + channel] = channel < sourceChannels
                        ? _sourceBuffer[sourceFrameOffset + channel]
                        : 0f;
                }
            }
        }

        return samplesWritten;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    private void EnsureCapacity(int sampleCount)
    {
        if (_sourceBuffer.Length < sampleCount)
        {
            _sourceBuffer = new float[sampleCount];
        }
    }
}
