using System.Runtime.InteropServices;

namespace MicMixer.Input;

internal readonly record struct KeyEvent(ushort VirtualKey, ushort ScanCode, uint Flags);

/// <summary>Injects keyboard events into the system input stream, as if typed on a keyboard.</summary>
internal static class KeyInjector
{
    public const uint ExtendedKey = 0x0001;
    public const uint KeyUp = 0x0002;

    private const uint InputKeyboard = 1;

    /// <summary>
    /// Sends <paramref name="events"/> in one call, so no other input lands between them.
    /// Windows drops injected input without reporting it while a window of a higher
    /// integrity level has focus, or while the UAC prompt or the lock screen shows.
    /// </summary>
    public static void Send(params ReadOnlySpan<KeyEvent> events)
    {
        var inputs = new Input[events.Length];
        for (int i = 0; i < events.Length; i++)
        {
            inputs[i] = new Input
            {
                Type = InputKeyboard,
                Data = new KeyboardInput
                {
                    VirtualKey = events[i].VirtualKey,
                    ScanCode = events[i].ScanCode,
                    Flags = events[i].Flags
                }
            };
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    /// <summary>The hardware scan code Windows assigns to <paramref name="virtualKey"/>.</summary>
    public static ushort ScanCodeOf(ushort virtualKey) => (ushort)MapVirtualKey(virtualKey, MapVkToVsc);

    /// <summary>True while Windows has <paramref name="virtualKey"/> down, whether pressed or injected.</summary>
    public static bool IsKeyDown(ushort virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private const uint MapVkToVsc = 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

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
