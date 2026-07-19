using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Serilog;

namespace MicMixer.Audio;

/// <summary>
/// Captures the audio rendered by a single process tree (e.g. Spotify) using WASAPI
/// process loopback (Windows 10 2004+). The app keeps playing through its own output
/// device untouched; this taps a copy of its stream.
///
/// The captured audio is exposed as <see cref="SampleProvider"/>, a never-ending source
/// that <see cref="MusicPlaybackEngine"/> mixes exactly like file playback. The internal
/// buffer uses the same trim-oldest policy as the engine's mix buffer so latency stays
/// bounded even when nothing drains it (routing stopped).
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

    // Keep at most ~350 ms buffered; when exceeded, drop down to ~120 ms.
    private const double HighWatermarkSeconds = 0.35;
    private const double TrimTargetSeconds = 0.12;

    private readonly int _processId;
    private readonly ManualResetEventSlim _stopRequested = new(false);

    private AudioClient? _audioClient;
    private EventWaitHandle? _frameEvent;
    private BufferedWaveProvider? _buffer;
    private Thread? _captureThread;
    private byte[] _trimBuffer = Array.Empty<byte>();
    private int _peakBits; // float peak since last read, stored as bits for Interlocked
    private bool _started;
    private bool _disposed;

    public event EventHandler<string>? Error;

    public ProcessLoopbackCapture(int processId)
    {
        _processId = processId;
    }

    /// <summary>Valid after <see cref="Start"/> has completed.</summary>
    public ISampleProvider? SampleProvider { get; private set; }

    /// <summary>Peak sample level since the last call (for a UI level meter).</summary>
    public float ReadAndResetPeak()
    {
        int bits = Interlocked.Exchange(ref _peakBits, 0);
        return BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>
    /// Activates the process-loopback audio client and starts the capture thread.
    /// Blocking; call from a background (MTA) thread.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new InvalidOperationException("Capture already started.");
        }

        if (!IsSupported)
        {
            throw new NotSupportedException("Per-app audio capture requires Windows 10 version 2004 or later.");
        }

        AudioClient? audioClient = null;
        EventWaitHandle? frameEvent = null;

        try
        {
            audioClient = ActivateProcessLoopbackClient(_processId);

            // The loopback engine converts to whatever format we ask for. Prefer the mix
            // engine's native format (float 48 kHz stereo); fall back to CD-quality PCM.
            WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
            try
            {
                Initialize(audioClient, format);
            }
            catch (Exception floatEx)
            {
                Log.Debug(floatEx, "Process loopback rejected float format; retrying with PCM 16.");
                audioClient.Dispose();
                audioClient = null;
                audioClient = ActivateProcessLoopbackClient(_processId);
                format = new WaveFormat(44_100, 16, 2);
                Initialize(audioClient, format);
            }

            frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            audioClient.SetEventHandle(frameEvent.SafeWaitHandle.DangerousGetHandle());

            _buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };
            SampleProvider = _buffer.ToSampleProvider();
            _audioClient = audioClient;
            _frameEvent = frameEvent;

            audioClient.Start();
            _captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "ProcessLoopbackCapture"
            };
            _captureThread.Start();
            _started = true;

            Log.Information("Process loopback capture started for PID {ProcessId} ({Format}).", _processId, format);
        }
        catch
        {
            _audioClient = null;
            _frameEvent = null;
            _buffer = null;
            SampleProvider = null;
            frameEvent?.Dispose();
            audioClient?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopRequested.Set();

        // Wake the capture thread immediately instead of waiting out its 100 ms poll.
        try
        {
            _frameEvent?.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_captureThread != null && !_captureThread.Join(TimeSpan.FromSeconds(5)))
        {
            // The WASAPI thread is stuck inside the audio stack. Leak the client and
            // event handles rather than disposing objects the thread may still touch.
            Log.Warning("Capture thread for PID {ProcessId} did not stop in time; leaking audio client.", _processId);
            return;
        }

        try
        {
            _audioClient?.Stop();
        }
        catch
        {
            // Client may already be invalidated.
        }

        _audioClient?.Dispose();
        _audioClient = null;
        _frameEvent?.Dispose();
        _frameEvent = null;
        _stopRequested.Dispose();
    }

    private static void Initialize(AudioClient audioClient, WaveFormat format)
    {
        audioClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            2_000_000, // 200 ms in 100-ns units
            0,
            format,
            Guid.Empty);
    }

    private void CaptureLoop()
    {
        var audioClient = _audioClient!;
        var buffer = _buffer!;
        var frameEvent = _frameEvent!;
        int blockAlign = buffer.WaveFormat.BlockAlign;
        bool isFloat = buffer.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        byte[] scratch = Array.Empty<byte>();

        try
        {
            var captureClient = audioClient.AudioCaptureClient;

            while (!_stopRequested.IsSet)
            {
                if (!frameEvent.WaitOne(100))
                {
                    continue;
                }

                while (!_stopRequested.IsSet && captureClient.GetNextPacketSize() > 0)
                {
                    IntPtr data = captureClient.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int byteCount = frames * blockAlign;

                    if (byteCount > 0)
                    {
                        if (scratch.Length < byteCount)
                        {
                            scratch = new byte[byteCount];
                        }

                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            Array.Clear(scratch, 0, byteCount);
                        }
                        else
                        {
                            Marshal.Copy(data, scratch, 0, byteCount);
                            UpdatePeak(scratch, byteCount, isFloat);
                        }

                        TrimIfNeeded(buffer);
                        buffer.AddSamples(scratch, 0, byteCount);
                    }

                    captureClient.ReleaseBuffer(frames);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Process loopback capture loop failed for PID {ProcessId}.", _processId);
            if (!_stopRequested.IsSet)
            {
                Error?.Invoke(this, $"Audio capture stopped: {ex.Message}");
            }
        }
    }

    private void TrimIfNeeded(BufferedWaveProvider buffer)
    {
        int bytesPerSecond = buffer.WaveFormat.AverageBytesPerSecond;
        int highWatermark = (int)(bytesPerSecond * HighWatermarkSeconds);

        if (buffer.BufferedBytes <= highWatermark)
        {
            return;
        }

        int discard = buffer.BufferedBytes - (int)(bytesPerSecond * TrimTargetSeconds);
        if (_trimBuffer.Length < discard)
        {
            _trimBuffer = new byte[discard];
        }

        buffer.Read(_trimBuffer, 0, discard);
    }

    private void UpdatePeak(byte[] data, int byteCount, bool isFloat)
    {
        float peak = 0f;

        if (isFloat)
        {
            for (int i = 0; i + sizeof(float) <= byteCount; i += sizeof(float))
            {
                float sample = Math.Abs(BitConverter.ToSingle(data, i));
                if (sample > peak)
                {
                    peak = sample;
                }
            }
        }
        else
        {
            for (int i = 0; i + sizeof(short) <= byteCount; i += sizeof(short))
            {
                float sample = Math.Abs(BitConverter.ToInt16(data, i) / 32768f);
                if (sample > peak)
                {
                    peak = sample;
                }
            }
        }

        // Keep the max since the UI last sampled it.
        int newBits = BitConverter.SingleToInt32Bits(peak);
        int currentBits;
        do
        {
            currentBits = Volatile.Read(ref _peakBits);
            if (BitConverter.Int32BitsToSingle(currentBits) >= peak)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _peakBits, newBits, currentBits) != currentBits);
    }

    // --- Activation interop -------------------------------------------------

    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1; // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
    private const int LoopbackModeIncludeTargetProcessTree = 0; // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
    private const ushort VtBlob = 0x41; // VT_BLOB

    private static AudioClient ActivateProcessLoopbackClient(int processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS with the process-loopback union member.
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = processId,
            ProcessLoopbackMode = LoopbackModeIncludeTargetProcessTree
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr propVariantPtr = IntPtr.Zero;

        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propVariant = new PropVariantBlob
            {
                Vt = VtBlob,
                BlobSize = (uint)paramsSize,
                BlobData = paramsPtr
            };
            propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var handler = new ActivationHandler();
            Guid audioClientIid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"); // IID_IAudioClient

            int hr = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref audioClientIid,
                propVariantPtr,
                handler,
                out IActivateAudioInterfaceAsyncOperation operation);
            Marshal.ThrowExceptionForHR(hr);

            object activated = handler.WaitForCompletion(TimeSpan.FromSeconds(5));
            GC.KeepAlive(operation);

            return new AudioClient((IAudioClient)activated);
        }
        finally
        {
            if (propVariantPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(propVariantPtr);
            }

            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public int TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>PROPVARIANT restricted to the VT_BLOB member used here.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    /// <summary>
    /// Managed CCWs are apartment-agile, so the completion callback (which arrives on a
    /// WASAPI worker thread) can safely signal the waiting starter thread.
    /// </summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _completed = new(false);
        private int _activateResult;
        private object? _activatedInterface;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out _activateResult, out _activatedInterface);
            }
            catch (Exception ex)
            {
                _activateResult = ex.HResult;
            }

            _completed.Set();
        }

        public object WaitForCompletion(TimeSpan timeout)
        {
            if (!_completed.Wait(timeout))
            {
                throw new TimeoutException("Audio capture activation did not respond.");
            }

            Marshal.ThrowExceptionForHR(_activateResult);
            return _activatedInterface
                ?? throw new InvalidOperationException("Audio capture was activated without an interface.");
        }
    }
}
