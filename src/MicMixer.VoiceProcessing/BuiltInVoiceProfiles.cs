namespace MicMixer.Dsp;

/// <summary>Generic DSP starting points, never recordings or personal voice data.</summary>
public static class BuiltInVoiceProfiles
{
    public const string FeminineId = "e664b168-15b7-477d-95f9-8b8ee917c731";
    public const string MasculineId = "e3bac9bb-6077-4536-9827-31b8c1bba702";

    // The backend uses compensatePitch:false: pitch moves the spectral envelope
    // too. These extra formant adjustments temper that movement instead of
    // doubling it. In the tonal region the approximate net shifts are +/-3 st.
    public static IReadOnlyList<VoiceProfile> All { get; } = Array.AsReadOnly<VoiceProfile>(
    [
        new()
        {
            FormatVersion = 1, Id = FeminineId, DisplayName = "Feminine (starter)",
            Parameters = new()
            {
                PitchSemitones = 5, FormantSemitones = -2,
                HighPassHz = 100, LowMidGainDb = -2, PresenceGainDb = 1.5f, AirGainDb = 1,
                CompressorRatio = 2, CompressorThresholdDb = -18, CompressorMakeupDb = 1,
            }
        },
        new()
        {
            FormatVersion = 1, Id = MasculineId, DisplayName = "Masculine (starter)",
            Parameters = new()
            {
                PitchSemitones = -5, FormantSemitones = 2,
                HighPassHz = 65, LowMidGainDb = 2, PresenceGainDb = -.5f, AirGainDb = -1,
                CompressorRatio = 2, CompressorThresholdDb = -18, CompressorMakeupDb = 1,
            }
        }
    ]);

    public static VoiceProfile? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string? Description(string? id) => id switch
    {
        FeminineId => "A brighter, higher starting point for a lower voice. Adjust Pitch first, then Resonance adjustment. Your original voice still matters.",
        MasculineId => "A fuller, lower starting point for a higher voice. Adjust Pitch first, then Resonance adjustment. Your original voice still matters.",
        _ => null
    };
}
