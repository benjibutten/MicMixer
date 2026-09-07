namespace MicMixer.Dsp;

/// <summary>
/// CPU-only voice candidate: independent pitch/formant shifting followed by a
/// small voice EQ, channel-linked compression, and gentle soft saturation.
/// </summary>
public sealed class ProfileVoiceProcessor : IVoiceProcessor
{
    private readonly IPitchBackend _stretch;
    private readonly BiquadFilter[] _equalizers;
    private readonly LinkedCompressor _compressor;
    private readonly float _saturationDrive;
    private readonly float _saturationNormalization;
    private bool _disposed;

    public ProfileVoiceProcessor(int sampleRate, int channels, VoiceDspParameters? preset = null)
    {
        Preset = preset ?? VoiceDspParameters.Initial;
        Preset.Validate(sampleRate, channels);
        SampleRate = sampleRate;
        Channels = channels;

        _stretch = Preset.PitchEngine == PitchEngine.TimeDomain
            ? new TimeDomainStretchBackend(sampleRate, channels, Preset)
            : new SignalsmithStretchBackend(sampleRate, channels, Preset);
        _equalizers =
        [
            BiquadFilter.HighPass(sampleRate, channels, Preset.HighPassHz),
            BiquadFilter.Peaking(sampleRate, channels, Preset.LowMidHz, Preset.LowMidGainDb, q: 0.8f),
            BiquadFilter.Peaking(sampleRate, channels, Preset.PresenceHz, Preset.PresenceGainDb, q: 0.9f),
            BiquadFilter.HighShelf(sampleRate, channels, Preset.AirHz, Preset.AirGainDb)
        ];
        _compressor = new LinkedCompressor(
            sampleRate,
            channels,
            Preset.CompressorThresholdDb,
            Preset.CompressorRatio,
            Preset.CompressorAttackMilliseconds,
            Preset.CompressorReleaseMilliseconds,
            Preset.CompressorMakeupDb);
        _saturationDrive = Preset.SaturationDrive;
        _saturationNormalization = _saturationDrive > 0f ? MathF.Tanh(_saturationDrive) : 1f;
    }

    public VoiceDspParameters Preset { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int LatencySamples => _stretch.LatencySamples;
    public int InputLatencySamples => _stretch.InputLatencySamples;
    public int OutputLatencySamples => _stretch.OutputLatencySamples;
    public int AnalysisBlockSamples => _stretch.BlockSamples;
    public int ProcessingIntervalSamples => _stretch.IntervalSamples;

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stretch.Process(input, output);

        ProcessEffects(output);
    }

    public void Flush(Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stretch.Flush(output);

        ProcessEffects(output);
    }

    private void ProcessEffects(Span<float> output)
    {

        foreach (BiquadFilter equalizer in _equalizers)
        {
            equalizer.ProcessInPlace(output);
        }

        _compressor.ProcessInPlace(output);
        ApplySaturation(output);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stretch.Reset();
        foreach (BiquadFilter equalizer in _equalizers)
        {
            equalizer.Reset();
        }

        _compressor.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stretch.Dispose();
    }

    private void ApplySaturation(Span<float> samples)
    {
        if (_saturationDrive == 0f)
        {
            return;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Tanh(samples[i] * _saturationDrive) / _saturationNormalization;
        }
    }
}
