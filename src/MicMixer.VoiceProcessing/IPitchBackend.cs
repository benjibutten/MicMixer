namespace MicMixer.Dsp;

internal interface IPitchBackend : IVoiceProcessor
{
    int InputLatencySamples { get; }
    int OutputLatencySamples { get; }
    int BlockSamples { get; }
    int IntervalSamples { get; }
    void Flush(Span<float> output);
}
