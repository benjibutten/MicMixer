using System.Runtime.InteropServices;

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

    private const uint InputKeyboard = 1;
    private const uint KeyEventFExtendedKey = 0x0001;
    private const uint KeyEventFKeyUp = 0x0002;

    public static void SendPlayPause() => SendKey(VkMediaPlayPause);

    public static void SendNextTrack() => SendKey(VkMediaNextTrack);

    public static void SendPreviousTrack() => SendKey(VkMediaPrevTrack);

    public static void SendStop() => SendKey(VkMediaStop);

    private static void SendKey(ushort virtualKey)
    {
        var inputs = new[]
        {
            CreateKeyInput(virtualKey, KeyEventFExtendedKey),
            CreateKeyInput(virtualKey, KeyEventFExtendedKey | KeyEventFKeyUp)
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input CreateKeyInput(ushort virtualKey, uint flags)
    {
        return new Input
        {
            Type = InputKeyboard,
            Data = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = flags
            }
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInput Data;
    }

    // KEYBDINPUT padded to the size of the largest union member (MOUSEINPUT).
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
        private readonly uint _padding1;
        private readonly uint _padding2;
    }
}
