namespace MicMixer.Input;

/// <summary>
/// Sends global media transport keys (the same ones a keyboard's play/pause buttons emit),
/// letting the transport buttons steer whatever media app the system routes them to.
/// </summary>
internal static class MediaKeySender
{
    private const ushort VkMediaNextTrack = 0xB0;
    private const ushort VkMediaPrevTrack = 0xB1;
    private const ushort VkMediaStop = 0xB2;
    private const ushort VkMediaPlayPause = 0xB3;

    public static void SendPlayPause() => SendKey(VkMediaPlayPause);

    public static void SendNextTrack() => SendKey(VkMediaNextTrack);

    public static void SendPreviousTrack() => SendKey(VkMediaPrevTrack);

    public static void SendStop() => SendKey(VkMediaStop);

    private static void SendKey(ushort virtualKey)
    {
        KeyInjector.Send(
            new KeyEvent(virtualKey, 0, KeyInjector.ExtendedKey),
            new KeyEvent(virtualKey, 0, KeyInjector.ExtendedKey | KeyInjector.KeyUp));
    }
}
