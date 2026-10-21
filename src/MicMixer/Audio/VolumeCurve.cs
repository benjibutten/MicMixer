namespace MicMixer.Audio;

/// <summary>
/// Maps a volume slider position (0 to 1) to the gain applied to the audio, and
/// back. The gain is the cube of the position, so the slider moves evenly in
/// loudness: half way is about -18 dB and a tenth is -60 dB, while the ends stay
/// silence and unity.
/// </summary>
internal static class VolumeCurve
{
    public static float ToGain(double position) => (float)Math.Pow(Math.Clamp(position, 0d, 1d), 3d);

    public static double ToPosition(float gain) => Math.Cbrt(Math.Clamp(gain, 0f, 1f));
}
