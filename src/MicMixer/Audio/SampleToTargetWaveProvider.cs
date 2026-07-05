using NAudio.Wave;

namespace MicMixer.Audio;

// Write bytes directly instead of using NAudio's generic float adapter, which can
// surface runtime array type issues on modern .NET when the destination buffer is a byte overlay.
internal sealed class SampleToTargetWaveProvider : IWaveProvider
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly ISampleProvider _source;
    private readonly OutputSampleFormat _outputSampleFormat;
    private float[] _sourceBuffer = Array.Empty<float>();

    public SampleToTargetWaveProvider(ISampleProvider source, WaveFormat targetFormat)
    {
        _source = source;
        WaveFormat = targetFormat;
        _outputSampleFormat = DetermineOutputSampleFormat(targetFormat);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesPerSample = Math.Max(WaveFormat.BitsPerSample / 8, 1);
        int alignedByteCount = count - (count % bytesPerSample);
        int samplesRequested = alignedByteCount / bytesPerSample;

        EnsureCapacity(samplesRequested);

        int samplesRead = _source.Read(_sourceBuffer, 0, samplesRequested);
        int bytesWritten = _outputSampleFormat switch
        {
            OutputSampleFormat.Float32 => WriteFloat32(buffer, offset, samplesRead),
            OutputSampleFormat.Pcm16 => WritePcm16(buffer, offset, samplesRead),
            OutputSampleFormat.Pcm24 => WritePcm24(buffer, offset, samplesRead),
            OutputSampleFormat.Pcm32 => WritePcm32(buffer, offset, samplesRead),
            _ => throw new NotSupportedException($"Unsupported output format: {WaveFormat.Encoding} {WaveFormat.BitsPerSample}-bit")
        };

        if (bytesWritten < count)
        {
            Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);
        }

        return count;
    }

    private int WriteFloat32(byte[] buffer, int offset, int samplesRead)
    {
        // Clamp to full scale: mixed sources (mic + music) can sum above ±1.0.
        for (int i = 0; i < samplesRead; i++)
        {
            float value = _sourceBuffer[i];
            if (value > 1f)
            {
                _sourceBuffer[i] = 1f;
            }
            else if (value < -1f)
            {
                _sourceBuffer[i] = -1f;
            }
        }

        int bytesWritten = samplesRead * sizeof(float);
        Buffer.BlockCopy(_sourceBuffer, 0, buffer, offset, bytesWritten);
        return bytesWritten;
    }

    private int WritePcm16(byte[] buffer, int offset, int samplesRead)
    {
        for (int i = 0; i < samplesRead; i++)
        {
            short value = ConvertToPcm16(_sourceBuffer[i]);
            int writeOffset = offset + (i * 2);
            buffer[writeOffset] = (byte)value;
            buffer[writeOffset + 1] = (byte)(value >> 8);
        }

        return samplesRead * 2;
    }

    private int WritePcm24(byte[] buffer, int offset, int samplesRead)
    {
        for (int i = 0; i < samplesRead; i++)
        {
            int value = ConvertToPcm24(_sourceBuffer[i]);
            int writeOffset = offset + (i * 3);
            buffer[writeOffset] = (byte)value;
            buffer[writeOffset + 1] = (byte)(value >> 8);
            buffer[writeOffset + 2] = (byte)(value >> 16);
        }

        return samplesRead * 3;
    }

    private int WritePcm32(byte[] buffer, int offset, int samplesRead)
    {
        for (int i = 0; i < samplesRead; i++)
        {
            int value = ConvertToPcm32(_sourceBuffer[i]);
            int writeOffset = offset + (i * 4);
            buffer[writeOffset] = (byte)value;
            buffer[writeOffset + 1] = (byte)(value >> 8);
            buffer[writeOffset + 2] = (byte)(value >> 16);
            buffer[writeOffset + 3] = (byte)(value >> 24);
        }

        return samplesRead * 4;
    }

    private void EnsureCapacity(int sampleCount)
    {
        if (_sourceBuffer.Length < sampleCount)
        {
            _sourceBuffer = new float[sampleCount];
        }
    }

    private static OutputSampleFormat DetermineOutputSampleFormat(WaveFormat targetFormat)
    {
        bool isFloat = targetFormat.Encoding == WaveFormatEncoding.IeeeFloat
            || targetFormat is WaveFormatExtensible floatExtensible && floatExtensible.SubFormat == FloatSubFormat;

        bool isPcm = targetFormat.Encoding == WaveFormatEncoding.Pcm
            || targetFormat is WaveFormatExtensible pcmExtensible && pcmExtensible.SubFormat == PcmSubFormat;

        return (targetFormat.BitsPerSample, isFloat, isPcm) switch
        {
            (32, true, _) => OutputSampleFormat.Float32,
            (16, _, true) => OutputSampleFormat.Pcm16,
            (24, _, true) => OutputSampleFormat.Pcm24,
            (32, _, true) => OutputSampleFormat.Pcm32,
            _ => throw new NotSupportedException(
                $"Output format {targetFormat.Encoding} ({targetFormat.BitsPerSample}-bit) is not supported.")
        };
    }

    private static short ConvertToPcm16(float value)
    {
        if (value >= 1f)
        {
            return short.MaxValue;
        }

        if (value <= -1f)
        {
            return short.MinValue;
        }

        return (short)Math.Round(value * short.MaxValue);
    }

    private static int ConvertToPcm24(float value)
    {
        const int maxValue = 8_388_607;
        const int minValue = -8_388_608;

        if (value >= 1f)
        {
            return maxValue;
        }

        if (value <= -1f)
        {
            return minValue;
        }

        return (int)Math.Round(value * maxValue);
    }

    private static int ConvertToPcm32(float value)
    {
        const int maxValue = int.MaxValue;
        const int minValue = int.MinValue;

        if (value >= 1f)
        {
            return maxValue;
        }

        if (value <= -1f)
        {
            return minValue;
        }

        return (int)Math.Round(value * maxValue);
    }

    private enum OutputSampleFormat
    {
        Float32,
        Pcm16,
        Pcm24,
        Pcm32
    }
}
