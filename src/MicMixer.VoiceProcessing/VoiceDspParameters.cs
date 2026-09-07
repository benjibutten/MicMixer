namespace MicMixer.Dsp;

/// <summary>
/// Settings for the CPU-only voice processor.
/// </summary>
public sealed record VoiceDspParameters
{
    public static VoiceDspParameters Initial { get; } = new();

    // Engine-specific fields are optional for legacy format-1 profiles.
    public PitchEngine PitchEngine { get; init; } = PitchEngine.Signalsmith;
    public float TimeDomainWindowMilliseconds { get; init; } = 24f;
    public float TimeDomainSearchMilliseconds { get; init; } = 8f;

    public float PitchSemitones { get; init; } = 0f;
    public float FormantSemitones { get; init; } = 0f;
    public float FormantBaseHz { get; init; } = 0f;
    public float TonalityLimitHz { get; init; } = 8_000f;

    // A smaller analysis window than Signalsmith's default preset keeps the
    // first real-time candidate inside the requested latency envelope. The CLI
    // prints the latency reported by the algorithm for every run.
    public float BlockMilliseconds { get; init; } = 40f;
    public float IntervalMilliseconds { get; init; } = 10f;

    public float HighPassHz { get; init; } = 75f;
    public float LowMidHz { get; init; } = 220f;
    public float LowMidGainDb { get; init; } = 0f;
    public float PresenceHz { get; init; } = 3_200f;
    public float PresenceGainDb { get; init; } = 0f;
    public float AirHz { get; init; } = 7_500f;
    public float AirGainDb { get; init; } = 0f;

    public float CompressorThresholdDb { get; init; } = -18f;
    public float CompressorRatio { get; init; } = 1f;
    public float CompressorAttackMilliseconds { get; init; } = 5f;
    public float CompressorReleaseMilliseconds { get; init; } = 80f;
    public float CompressorMakeupDb { get; init; } = 0f;
    public float SaturationDrive { get; init; } = 0f;

    public void Validate(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);

        if (!Enum.IsDefined(PitchEngine)) throw new ArgumentOutOfRangeException(nameof(PitchEngine));
        RequireFinite(TimeDomainWindowMilliseconds, nameof(TimeDomainWindowMilliseconds), 10f, 50f);
        RequireFinite(TimeDomainSearchMilliseconds, nameof(TimeDomainSearchMilliseconds), 0f, 20f);
        if (PitchEngine == PitchEngine.TimeDomain)
        {
            RequireFinite(PitchSemitones, nameof(PitchSemitones), -12f, 12f);
            if (FormantSemitones != 0f)
                throw new ArgumentException("Independent resonance adjustment is not supported by the time-domain engine. Use 0.", nameof(FormantSemitones));
        }
        RequireFinite(PitchSemitones, nameof(PitchSemitones), -24f, 24f);
        RequireFinite(FormantSemitones, nameof(FormantSemitones), -24f, 24f);
        RequireFinite(FormantBaseHz, nameof(FormantBaseHz), 0f, sampleRate / 2f);
        RequireFinite(TonalityLimitHz, nameof(TonalityLimitHz), 0f, sampleRate / 2f);
        RequireFinite(BlockMilliseconds, nameof(BlockMilliseconds), 10f, 250f);
        RequireFinite(IntervalMilliseconds, nameof(IntervalMilliseconds), 1f, BlockMilliseconds / 2f);

        float nyquist = sampleRate / 2f;
        RequireFinite(HighPassHz, nameof(HighPassHz), 10f, nyquist * 0.95f);
        RequireFinite(LowMidHz, nameof(LowMidHz), 10f, nyquist * 0.95f);
        RequireFinite(PresenceHz, nameof(PresenceHz), 10f, nyquist * 0.95f);
        RequireFinite(AirHz, nameof(AirHz), 10f, nyquist * 0.95f);
        RequireFinite(LowMidGainDb, nameof(LowMidGainDb), -24f, 24f);
        RequireFinite(PresenceGainDb, nameof(PresenceGainDb), -24f, 24f);
        RequireFinite(AirGainDb, nameof(AirGainDb), -24f, 24f);

        RequireFinite(CompressorThresholdDb, nameof(CompressorThresholdDb), -60f, 0f);
        RequireFinite(CompressorRatio, nameof(CompressorRatio), 1f, 20f);
        RequireFinite(CompressorAttackMilliseconds, nameof(CompressorAttackMilliseconds), 0.1f, 500f);
        RequireFinite(CompressorReleaseMilliseconds, nameof(CompressorReleaseMilliseconds), 1f, 2_000f);
        RequireFinite(CompressorMakeupDb, nameof(CompressorMakeupDb), -12f, 24f);
        RequireFinite(SaturationDrive, nameof(SaturationDrive), 0f, 8f);
    }

    private static void RequireFinite(float value, string name, float minimum, float maximum)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Value must be finite and in [{minimum}, {maximum}].");
        }
    }
}
