namespace MicMixer.Dsp;

/// <summary>
/// Stateful, interleaved-float voice processor. Implementations must accept and
/// produce the same number of samples and must not retain either span.
/// </summary>
public interface IVoiceProcessor : IDisposable
{
    /// <summary>Total input-to-output algorithmic latency, per channel.</summary>
    int LatencySamples { get; }

    void Process(ReadOnlySpan<float> input, Span<float> output);

    void Reset();
}
