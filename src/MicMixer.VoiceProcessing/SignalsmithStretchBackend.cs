using System.Runtime.InteropServices;

namespace MicMixer.Dsp;

/// <summary>
/// Minimal owned binding to the interleaved-float C ABI shipped by
/// SignalsmithStretch-CS. Keeping the ABI here avoids leaking a third-party API
/// into MicMixer and fixes frame-vs-interleaved-sample counting at the boundary.
/// </summary>
internal sealed partial class SignalsmithStretchBackend : IPitchBackend
{
    private const string LibraryName = "SignalsmithStretch";
    private IntPtr _handle;

    public SignalsmithStretchBackend(int sampleRate, int channels, VoiceDspParameters preset)
    {
        preset.Validate(sampleRate, channels);
        SampleRate = sampleRate;
        Channels = channels;

        _handle = Native.Create();
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Signalsmith Stretch could not be created.");
        }

        try
        {
            // The packaged C ABI records the interleaving stride in PresetDefault.
            // Configure then replaces the large default analysis window with our
            // explicit low-latency candidate. Split computation is disabled because
            // it adds one complete output interval of algorithmic latency.
            Native.PresetDefault(_handle, channels, sampleRate, splitComputation: false);

            int blockSamples = Math.Max(16, (int)MathF.Round(sampleRate * preset.BlockMilliseconds / 1_000f));
            int intervalSamples = Math.Max(1, (int)MathF.Round(sampleRate * preset.IntervalMilliseconds / 1_000f));
            if (intervalSamples >= blockSamples)
            {
                throw new ArgumentException("The Signalsmith interval must be shorter than its analysis block.", nameof(preset));
            }

            Native.Configure(_handle, channels, blockSamples, intervalSamples, splitComputation: false);
            Native.SetTransposeSemitones(
                _handle,
                preset.PitchSemitones,
                tonalityLimit: preset.TonalityLimitHz / sampleRate);
            Native.SetFormantSemitones(_handle, preset.FormantSemitones, compensatePitch: false);
            Native.SetFormantBase(_handle, preset.FormantBaseHz / sampleRate);

            InputLatencySamples = Native.InputLatency(_handle);
            OutputLatencySamples = Native.OutputLatency(_handle);
            BlockSamples = Native.BlockSamples(_handle);
            IntervalSamples = Native.IntervalSamples(_handle);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int InputLatencySamples { get; }
    public int OutputLatencySamples { get; }
    public int LatencySamples => checked(InputLatencySamples + OutputLatencySamples);
    public int BlockSamples { get; }
    public int IntervalSamples { get; }

    public unsafe void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (input.Length != output.Length)
        {
            throw new ArgumentException("Input and output spans must have the same length.", nameof(output));
        }

        if (input.Length % Channels != 0)
        {
            throw new ArgumentException("The interleaved sample count must contain complete frames.", nameof(input));
        }

        if (input.Overlaps(output))
        {
            throw new ArgumentException("Signalsmith requires separate input and output buffers.", nameof(output));
        }

        if (input.IsEmpty)
        {
            return;
        }

        int frames = input.Length / Channels;
        fixed (float* inputPointer = input)
        fixed (float* outputPointer = output)
        {
            Native.Process(_handle, inputPointer, frames, outputPointer, frames);
        }
    }

    public unsafe void Flush(Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (output.Length % Channels != 0)
        {
            throw new ArgumentException("The interleaved sample count must contain complete frames.", nameof(output));
        }

        if (output.IsEmpty)
        {
            return;
        }

        int frames = output.Length / Channels;
        fixed (float* outputPointer = output)
        {
            Native.Flush(_handle, outputPointer, frames, playbackRate: 1d);
        }
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        Native.Reset(_handle);
    }

    public void Dispose()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            Native.Release(handle);
        }

        GC.SuppressFinalize(this);
    }

    ~SignalsmithStretchBackend()
    {
        Dispose();
    }

    private static partial class Native
    {
        [LibraryImport(LibraryName, EntryPoint = "Stretch_Create")]
        internal static partial IntPtr Create();

        [LibraryImport(LibraryName, EntryPoint = "Stretch_Release")]
        internal static partial void Release(IntPtr stretch);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_PresetDefault")]
        internal static partial void PresetDefault(
            IntPtr stretch,
            int channels,
            float sampleRate,
            [MarshalAs(UnmanagedType.I1)] bool splitComputation);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_Configure")]
        internal static partial void Configure(
            IntPtr stretch,
            int channels,
            int blockSamples,
            int intervalSamples,
            [MarshalAs(UnmanagedType.I1)] bool splitComputation);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_SetTransposeSemitones")]
        internal static partial void SetTransposeSemitones(IntPtr stretch, float semitones, float tonalityLimit);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_SetFormantSemitones")]
        internal static partial void SetFormantSemitones(
            IntPtr stretch,
            float semitones,
            [MarshalAs(UnmanagedType.I1)] bool compensatePitch);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_SetFormantBase")]
        internal static partial void SetFormantBase(IntPtr stretch, float baseFrequency);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_InputLatency")]
        internal static partial int InputLatency(IntPtr stretch);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_OutputLatency")]
        internal static partial int OutputLatency(IntPtr stretch);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_BlockSamples")]
        internal static partial int BlockSamples(IntPtr stretch);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_IntervalSamples")]
        internal static partial int IntervalSamples(IntPtr stretch);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_Reset")]
        internal static partial void Reset(IntPtr stretch);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_Flush")]
        internal static unsafe partial void Flush(
            IntPtr stretch,
            float* output,
            int outputFrames,
            double playbackRate);

        [LibraryImport(LibraryName, EntryPoint = "Stretch_Process")]
        internal static unsafe partial void Process(
            IntPtr stretch,
            float* input,
            int inputFrames,
            float* output,
            int outputFrames);
    }
}
