using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MicMixer.Audio;

// Write bytes directly instead of using NAudio's generic float adapter, which can
// surface runtime array type issues on modern .NET when the destination buffer is a byte overlay.
internal sealed class SampleToTargetWaveProvider : IWaveProvider
{
    private const int Float32BitsPerSample = 32;
    private const int Pcm16BitsPerSample = 16;
    private const int Pcm24BitsPerSample = 24;
    private const int Pcm32BitsPerSample = 32;
    private const int Pcm16BytesPerSample = sizeof(short);
    private const int Pcm24BytesPerSample = 3;
    private const int Pcm32BytesPerSample = sizeof(int);
    private const int BitsPerByte = 8;
    private const int MinimumBytesPerSample = 1;
    private const float MinimumSampleValue = -1f;
    private const float MaximumSampleValue = 1f;

    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    private readonly ISampleProvider _source;
    private readonly OutputSampleFormat _outputSampleFormat;
    private readonly bool _padSilence;
    private float[] _sourceBuffer = Array.Empty<float>();

    public SampleToTargetWaveProvider(ISampleProvider source, WaveFormat targetFormat, bool padSilence = true)
    {
        _source = source;
        WaveFormat = targetFormat;
        _outputSampleFormat = DetermineOutputSampleFormat(targetFormat);
        _padSilence = padSilence;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<byte> buffer)
    {
        int bytesPerSample = Math.Max(WaveFormat.BitsPerSample / BitsPerByte, MinimumBytesPerSample);
        int alignedByteCount = buffer.Length - (buffer.Length % bytesPerSample);
        int samplesRequested = alignedByteCount / bytesPerSample;

        EnsureCapacity(samplesRequested);

        int samplesRead = _source.Read(_sourceBuffer.AsSpan(0, samplesRequested));
        int bytesWritten = _outputSampleFormat switch
        {
            OutputSampleFormat.Float32 => WriteFloat32(buffer, samplesRead),
            OutputSampleFormat.Pcm16 => WritePcm16(buffer, samplesRead),
            OutputSampleFormat.Pcm24 => WritePcm24(buffer, samplesRead),
            OutputSampleFormat.Pcm32 => WritePcm32(buffer, samplesRead),
            _ => throw new NotSupportedException($"Unsupported output format: {WaveFormat.Encoding} {WaveFormat.BitsPerSample}-bit")
        };

        if (bytesWritten < buffer.Length)
        {
            buffer[bytesWritten..].Clear();
        }

        // Live routes need an endless clock; finite preview clips must signal EOF.
        return _padSilence ? buffer.Length : bytesWritten;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    private int WriteFloat32(Span<byte> buffer, int samplesRead)
    {
        // Clamp to full scale: mixed sources (mic + music) can sum above ±1.0.
        for (int i = 0; i < samplesRead; i++)
        {
            float value = _sourceBuffer[i];
            if (value > MaximumSampleValue)
            {
                _sourceBuffer[i] = MaximumSampleValue;
            }
            else if (value < MinimumSampleValue)
            {
                _sourceBuffer[i] = MinimumSampleValue;
            }
        }

        int bytesWritten = samplesRead * sizeof(float);
        MemoryMarshal.AsBytes(_sourceBuffer.AsSpan(0, samplesRead)).CopyTo(buffer);
        return bytesWritten;
    }

    private int WritePcm16(Span<byte> buffer, int samplesRead)
    {
        for (int i = 0; i < samplesRead; i++)
        {
            short value = ConvertToPcm16(_sourceBuffer[i]);
            int writeOffset = i * Pcm16BytesPerSample;
            buffer[writeOffset] = (byte)value;
            buffer[writeOffset + 1] = (byte)(value >> 8);
        }

        return samplesRead * Pcm16BytesPerSample;
    }

    private int WritePcm24(Span<byte> buffer, int samplesRead)
    {
        for (int i = 0; i < samplesRead; i++)
        {
            int value = ConvertToPcm24(_sourceBuffer[i]);
            int writeOffset = i * Pcm24BytesPerSample;
            buffer[writeOffset] = (byte)value;
            buffer[writeOffset + 1] = (byte)(value >> 8);
            buffer[writeOffset + 2] = (byte)(value >> 16);
        }

        return samplesRead * Pcm24BytesPerSample;
    }

    private int WritePcm32(Span<byte> buffer, int samplesRead)
    {
        for (int i = 0; i < samplesRead; i++)
        {
            int value = ConvertToPcm32(_sourceBuffer[i]);
            int writeOffset = i * Pcm32BytesPerSample;
            buffer[writeOffset] = (byte)value;
            buffer[writeOffset + 1] = (byte)(value >> 8);
            buffer[writeOffset + 2] = (byte)(value >> 16);
            buffer[writeOffset + 3] = (byte)(value >> 24);
        }

        return samplesRead * Pcm32BytesPerSample;
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
            (Float32BitsPerSample, true, _) => OutputSampleFormat.Float32,
            (Pcm16BitsPerSample, _, true) => OutputSampleFormat.Pcm16,
            (Pcm24BitsPerSample, _, true) => OutputSampleFormat.Pcm24,
            (Pcm32BitsPerSample, _, true) => OutputSampleFormat.Pcm32,
            _ => throw new NotSupportedException(
                $"Output format {targetFormat.Encoding} ({targetFormat.BitsPerSample}-bit) is not supported.")
        };
    }

    private static short ConvertToPcm16(float value)
    {
        if (value >= MaximumSampleValue)
        {
            return short.MaxValue;
        }

        if (value <= MinimumSampleValue)
        {
            return short.MinValue;
        }

        return (short)Math.Round(value * short.MaxValue);
    }

    private static int ConvertToPcm24(float value)
    {
        const int maxValue = 8_388_607;
        const int minValue = -8_388_608;

        if (value >= MaximumSampleValue)
        {
            return maxValue;
        }

        if (value <= MinimumSampleValue)
        {
            return minValue;
        }

        return (int)Math.Round(value * maxValue);
    }

    private static int ConvertToPcm32(float value)
    {
        const int maxValue = int.MaxValue;
        const int minValue = int.MinValue;

        if (value >= MaximumSampleValue)
        {
            return maxValue;
        }

        if (value <= MinimumSampleValue)
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
