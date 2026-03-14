using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MicMixer.Audio;

public sealed class AudioRouter : IDisposable
{
    private readonly object _syncRoot = new();
    private WasapiOut? _player;
    private InputRoute? _normalRoute;
    private InputRoute? _moddedRoute;
    private bool _useModdedInput;
    private bool _disposed;

    public bool IsRouting => _player?.PlaybackState == PlaybackState.Playing;
    public bool UseModdedInput => Volatile.Read(ref _useModdedInput);
    public float NormalPeak => _normalRoute?.CurrentPeak ?? 0f;
    public float ModdedPeak => _moddedRoute?.CurrentPeak ?? 0f;

    public event EventHandler<string>? Error;

    public void Start(MMDevice normalInputDevice, MMDevice moddedInputDevice, MMDevice outputDevice)
    {
        Stop();

        InputRoute? normalRoute = null;
        InputRoute? moddedRoute = null;
        WasapiOut? player = null;

        try
        {
            var targetFormat = outputDevice.AudioClient.MixFormat;

            normalRoute = new InputRoute(normalInputDevice, targetFormat, RaiseError);
            moddedRoute = new InputRoute(moddedInputDevice, targetFormat, RaiseError);

            var source = new SwitchingSampleProvider(normalRoute, moddedRoute, () => UseModdedInput);

            player = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 50);
            player.PlaybackStopped += OnPlaybackStopped;
            player.Init(CreateOutputProvider(source, targetFormat));

            normalRoute.Start();
            moddedRoute.Start();
            player.Play();

            lock (_syncRoot)
            {
                _normalRoute = normalRoute;
                _moddedRoute = moddedRoute;
                _player = player;
            }
        }
        catch (Exception ex)
        {
            if (player != null)
            {
                player.PlaybackStopped -= OnPlaybackStopped;
                player.Dispose();
            }

            normalRoute?.Dispose();
            moddedRoute?.Dispose();
            Stop();
            RaiseError(ex.Message);
        }
    }

    public void SetUseModdedInput(bool useModdedInput)
    {
        Volatile.Write(ref _useModdedInput, useModdedInput);
    }

    public void Stop()
    {
        WasapiOut? player;
        InputRoute? normalRoute;
        InputRoute? moddedRoute;

        lock (_syncRoot)
        {
            player = _player;
            normalRoute = _normalRoute;
            moddedRoute = _moddedRoute;

            _player = null;
            _normalRoute = null;
            _moddedRoute = null;
        }

        if (player != null)
        {
            player.PlaybackStopped -= OnPlaybackStopped;

            try
            {
                if (player.PlaybackState != PlaybackState.Stopped)
                {
                    player.Stop();
                }
            }
            catch
            {
                // The device may already be unavailable.
            }

            player.Dispose();
        }

        normalRoute?.Dispose();
        moddedRoute?.Dispose();

        Volatile.Write(ref _useModdedInput, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            RaiseError(e.Exception.Message);
        }
    }

    private void RaiseError(string message)
    {
        Error?.Invoke(this, message);
    }

    private static IWaveProvider CreateOutputProvider(ISampleProvider source, WaveFormat targetFormat)
    {
        return new SampleToTargetWaveProvider(source, targetFormat);
    }

    // Write bytes directly instead of using NAudio's generic float adapter, which can
    // surface runtime array type issues on modern .NET when the destination buffer is a byte overlay.
    private sealed class SampleToTargetWaveProvider : IWaveProvider
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

    private sealed class InputRoute : IDisposable
    {
        private readonly Action<string> _errorHandler;
        private readonly WasapiCapture _capture;
        private readonly BufferedWaveProvider _buffer;
        private readonly MeteringSampleProvider _meter;
        private float _currentPeak;
        private bool _disposed;

        public InputRoute(MMDevice device, WaveFormat targetFormat, Action<string> errorHandler)
        {
            _errorHandler = errorHandler;
            _capture = new WasapiCapture(device);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(200),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            var source = CreateNormalizedProvider(_buffer, targetFormat);
            var samplesPerNotification = Math.Max(targetFormat.SampleRate * Math.Max(targetFormat.Channels, 1) / 20, targetFormat.Channels);
            _meter = new MeteringSampleProvider(source, samplesPerNotification);
            _meter.StreamVolume += OnStreamVolume;
        }

        public float CurrentPeak => Volatile.Read(ref _currentPeak);

        public WaveFormat OutputFormat => _meter.WaveFormat;

        public void Start()
        {
            _capture.StartRecording();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = _meter.Read(buffer, offset, count);

            if (samplesRead == 0)
            {
                Volatile.Write(ref _currentPeak, 0f);
            }

            return samplesRead;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_capture.CaptureState == CaptureState.Capturing)
                {
                    _capture.StopRecording();
                }
            }
            catch
            {
                // The device may already be unavailable.
            }

            _meter.StreamVolume -= OnStreamVolume;
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();

            Volatile.Write(ref _currentPeak, 0f);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                _errorHandler(e.Exception.Message);
            }
        }

        private void OnStreamVolume(object? sender, StreamVolumeEventArgs e)
        {
            float peak = e.MaxSampleValues.Length == 0 ? 0f : e.MaxSampleValues.Max();
            Volatile.Write(ref _currentPeak, Math.Clamp(peak, 0f, 1f));
        }
    }

    private static ISampleProvider CreateNormalizedProvider(BufferedWaveProvider buffer, WaveFormat targetFormat)
    {
        ISampleProvider provider = buffer.ToSampleProvider();

        if (provider.WaveFormat.SampleRate != targetFormat.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetFormat.SampleRate);
        }

        if (provider.WaveFormat.Channels != targetFormat.Channels)
        {
            provider = new ChannelAdapterSampleProvider(provider, targetFormat.Channels);
        }

        return provider;
    }

    private sealed class SwitchingSampleProvider : ISampleProvider
    {
        private readonly InputRoute _normalRoute;
        private readonly InputRoute _moddedRoute;
        private readonly Func<bool> _useModdedInput;
        private float[] _normalBuffer = Array.Empty<float>();
        private float[] _moddedBuffer = Array.Empty<float>();

        public SwitchingSampleProvider(InputRoute normalRoute, InputRoute moddedRoute, Func<bool> useModdedInput)
        {
            _normalRoute = normalRoute;
            _moddedRoute = moddedRoute;
            _useModdedInput = useModdedInput;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                normalRoute.OutputFormat.SampleRate,
                normalRoute.OutputFormat.Channels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            EnsureCapacity(count);

            int normalRead = _normalRoute.Read(_normalBuffer, 0, count);
            int moddedRead = _moddedRoute.Read(_moddedBuffer, 0, count);

            bool useModdedInput = _useModdedInput();
            float[] selectedBuffer = useModdedInput ? _moddedBuffer : _normalBuffer;
            int selectedSamples = useModdedInput ? moddedRead : normalRead;

            Array.Copy(selectedBuffer, 0, buffer, offset, selectedSamples);

            if (selectedSamples < count)
            {
                Array.Clear(buffer, offset + selectedSamples, count - selectedSamples);
            }

            return count;
        }

        private void EnsureCapacity(int count)
        {
            if (_normalBuffer.Length < count)
            {
                _normalBuffer = new float[count];
            }

            if (_moddedBuffer.Length < count)
            {
                _moddedBuffer = new float[count];
            }
        }
    }

    private sealed class ChannelAdapterSampleProvider : ISampleProvider
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

            if (samplesWritten < count)
            {
                Array.Clear(buffer, offset + samplesWritten, count - samplesWritten);
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
}
