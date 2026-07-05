using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMixer.Audio;

internal static class FormatNormalizer
{
    public static ISampleProvider Normalize(ISampleProvider provider, int targetSampleRate, int targetChannels)
    {
        if (provider.WaveFormat.SampleRate != targetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        }

        if (provider.WaveFormat.Channels != targetChannels)
        {
            provider = new ChannelAdapterSampleProvider(provider, targetChannels);
        }

        return provider;
    }

    public static ISampleProvider Normalize(ISampleProvider provider, WaveFormat targetFormat)
    {
        return Normalize(provider, targetFormat.SampleRate, targetFormat.Channels);
    }
}
