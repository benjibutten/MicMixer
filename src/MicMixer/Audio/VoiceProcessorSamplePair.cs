using System.Diagnostics;
using MicMixer.Dsp;
using NAudio.Wave;

namespace MicMixer.Audio;

internal interface ISamplePair : IDisposable
{
    WaveFormat WaveFormat { get; }
    bool HasSecondary { get; }
    int Read(Span<float> primary, Span<float> secondary);
}

internal sealed class IndependentSamplePair : ISamplePair
{
    private readonly ISampleProvider _primary;
    private readonly ISampleProvider? _secondary;

    public IndependentSamplePair(ISampleProvider primary, ISampleProvider? secondary)
    {
        _primary = primary;
        _secondary = secondary;
        WaveFormat = primary.WaveFormat;

        if (secondary != null && !WaveFormatsMatch(primary.WaveFormat, secondary.WaveFormat))
        {
            throw new ArgumentException("The two microphone sources must use the same format.", nameof(secondary));
        }
    }

    public WaveFormat WaveFormat { get; }
    public bool HasSecondary => _secondary != null;

    public int Read(Span<float> primary, Span<float> secondary)
    {
        if (primary.Length != secondary.Length)
        {
            throw new ArgumentException("Both destination buffers must have the same length.", nameof(secondary));
        }

        Fill(_primary, primary);
        if (_secondary != null)
        {
            Fill(_secondary, secondary);
        }
        else
        {
            secondary.Clear();
        }

        return primary.Length;
    }

    public void Dispose()
    {
    }

    private static void Fill(ISampleProvider source, Span<float> destination)
    {
        int read = source.Read(destination);
        if (read < destination.Length)
        {
            destination[read..].Clear();
        }
    }

    private static bool WaveFormatsMatch(WaveFormat left, WaveFormat right) =>
        left.SampleRate == right.SampleRate && left.Channels == right.Channels;
}

internal sealed class VoiceProcessorSamplePair : ISamplePair
{
    private readonly ISampleProvider _source;
    private readonly IVoiceProcessor _processor;
    private readonly float[] _delayLine;
    private float[] _input = Array.Empty<float>();
    private int _delayPosition;
    private float _processedPeak;
    private readonly Func<float>? _outputVolume;
    private float _currentGain = 1f;
    private float _targetGain = 1f;
    private float _gainStep;
    private int _gainFramesRemaining;
    private bool _gainInitialized;
    private bool _disposed;

    public VoiceProcessorSamplePair(
        ISampleProvider source,
        IVoiceProcessor processor,
        VoiceProcessorRuntimeDiagnostics diagnostics,
        Func<float>? outputVolume = null)
    {
        _source = source;
        _processor = processor;
        Diagnostics = diagnostics;
        _outputVolume = outputVolume;
        WaveFormat = source.WaveFormat;
        _delayLine = new float[checked(processor.LatencySamples * WaveFormat.Channels)];
    }

    public WaveFormat WaveFormat { get; }
    public bool HasSecondary => true;
    public VoiceProcessorRuntimeDiagnostics Diagnostics { get; }
    public float ProcessedPeak => Volatile.Read(ref _processedPeak);

    public int Read(Span<float> primary, Span<float> secondary)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (primary.Length != secondary.Length || primary.Length % WaveFormat.Channels != 0)
        {
            throw new ArgumentException("Both buffers must contain the same number of complete frames.", nameof(secondary));
        }

        EnsureCapacity(primary.Length);
        Span<float> input = _input.AsSpan(0, primary.Length);
        int read = _source.Read(input);
        if (read < input.Length)
        {
            input[read..].Clear();
            Diagnostics.RecordUnderrun();
        }

        long started = Stopwatch.GetTimestamp();
        _processor.Process(input, secondary);
        ApplyOutputVolume(secondary);
        long elapsed = Stopwatch.GetTimestamp() - started;
        long budget = Math.Max(1, primary.Length / WaveFormat.Channels) * Stopwatch.Frequency / WaveFormat.SampleRate;
        Diagnostics.RecordCallback(elapsed, elapsed > budget);

        DelayDry(input, primary);
        MeasureProcessedPeak(secondary);
        return primary.Length;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_delayLine);
        _delayPosition = 0;
        _gainInitialized = false;
        _gainFramesRemaining = 0;
        Volatile.Write(ref _processedPeak, 0f);
        _processor.Reset();
        Diagnostics.RecordReset();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Reset();
        _disposed = true;
        _processor.Dispose();
    }

    private void DelayDry(ReadOnlySpan<float> input, Span<float> output)
    {
        if (_delayLine.Length == 0)
        {
            input.CopyTo(output);
            return;
        }

        for (int i = 0; i < input.Length; i++)
        {
            output[i] = _delayLine[_delayPosition];
            _delayLine[_delayPosition] = input[i];
            _delayPosition++;
            if (_delayPosition == _delayLine.Length)
            {
                _delayPosition = 0;
            }
        }
    }

    private void ApplyOutputVolume(Span<float> samples)
    {
        float requested = _outputVolume?.Invoke() ?? 1f;
        requested = float.IsFinite(requested) ? Math.Clamp(requested, 0f, 1f) : 1f;
        if (!_gainInitialized)
        {
            _currentGain = _targetGain = requested;
            _gainInitialized = true;
        }
        else if (requested != _targetGain)
        {
            _targetGain = requested;
            _gainFramesRemaining = Math.Max(1, WaveFormat.SampleRate / 100);
            _gainStep = (_targetGain - _currentGain) / _gainFramesRemaining;
        }

        // Ten milliseconds of ramping prevents slider changes from creating clicks.
        // Apply the same gain to every channel, after all nonlinear processing.
        for (int offset = 0; offset < samples.Length; offset += WaveFormat.Channels)
        {
            if (_gainFramesRemaining > 0)
            {
                _currentGain += _gainStep;
                if (--_gainFramesRemaining == 0)
                {
                    _currentGain = _targetGain;
                }
            }

            for (int channel = 0; channel < WaveFormat.Channels; channel++)
            {
                samples[offset + channel] *= _currentGain;
            }
        }
    }

    private void MeasureProcessedPeak(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        foreach (float sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        Volatile.Write(ref _processedPeak, Math.Clamp(peak, 0f, 1f));
    }

    private void EnsureCapacity(int count)
    {
        if (_input.Length < count)
        {
            _input = new float[count];
        }
    }
}

internal sealed class VoiceProcessorRuntimeDiagnostics
{
    private long _callbackCount;
    private long _totalProcessTicks;
    private long _maximumProcessTicks;
    private long _underruns;
    private long _overruns;
    private long _resets;

    public void RecordCallback(long elapsedTicks, bool overrun)
    {
        Interlocked.Increment(ref _callbackCount);
        Interlocked.Add(ref _totalProcessTicks, elapsedTicks);

        long observed = Volatile.Read(ref _maximumProcessTicks);
        while (elapsedTicks > observed)
        {
            long previous = Interlocked.CompareExchange(ref _maximumProcessTicks, elapsedTicks, observed);
            if (previous == observed)
            {
                break;
            }

            observed = previous;
        }

        if (overrun)
        {
            Interlocked.Increment(ref _overruns);
        }
    }

    public void RecordUnderrun() => Interlocked.Increment(ref _underruns);
    public void RecordReset() => Interlocked.Increment(ref _resets);

    public VoiceProcessorDiagnosticsSnapshot Snapshot()
    {
        long callbacks = Volatile.Read(ref _callbackCount);
        long totalTicks = Volatile.Read(ref _totalProcessTicks);
        return new VoiceProcessorDiagnosticsSnapshot(
            callbacks,
            callbacks == 0 ? 0d : totalTicks * 1_000d / Stopwatch.Frequency / callbacks,
            Volatile.Read(ref _maximumProcessTicks) * 1_000d / Stopwatch.Frequency,
            Volatile.Read(ref _underruns),
            Volatile.Read(ref _overruns),
            Volatile.Read(ref _resets));
    }
}

internal readonly record struct VoiceProcessorDiagnosticsSnapshot(
    long CallbackCount,
    double AverageProcessMilliseconds,
    double MaximumProcessMilliseconds,
    long Underruns,
    long Overruns,
    long Resets);
