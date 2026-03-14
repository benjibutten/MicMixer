using System.Runtime.InteropServices;

namespace MicMixer.Input;

public sealed class GlobalHotkeyListener : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;

    private readonly object _syncRoot = new();
    private readonly HashSet<int> _pressedInputs = new();
    private readonly LowLevelKeyboardProc _keyboardHookProc;
    private readonly LowLevelMouseProc _mouseHookProc;
    private nint _keyboardHookHandle;
    private nint _mouseHookHandle;
    private HotkeyBinding? _binding;
    private bool _isPressed;
    private bool _disposed;

    public GlobalHotkeyListener()
    {
        _keyboardHookProc = KeyboardHookCallback;
        _mouseHookProc = MouseHookCallback;
    }

    public bool IsPressed => Volatile.Read(ref _isPressed);

    public event EventHandler<bool>? PressedStateChanged;
    public event EventHandler<string>? Error;

    public void UpdateBinding(HotkeyBinding? binding)
    {
        lock (_syncRoot)
        {
            _binding = binding;
            _pressedInputs.Clear();
            _isPressed = false;
        }

        PressedStateChanged?.Invoke(this, false);
        EnsureHooksInstalled();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_keyboardHookHandle != 0)
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = 0;
        }

        if (_mouseHookHandle != 0)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = 0;
        }
    }

    private void EnsureHooksInstalled()
    {
        if (_keyboardHookHandle != 0 && _mouseHookHandle != 0)
        {
            return;
        }

        // For low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL), the callback runs in the
        // installing thread's context. GetModuleHandle(null) reliably returns the current
        // process module handle across all .NET runtimes, unlike the fragile
        // Process.GetCurrentProcess().MainModule approach that can return null or the
        // shared dotnet host module in .NET Core/5+.
        nint moduleHandle = GetModuleHandle(null);

        if (_keyboardHookHandle == 0)
        {
            _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, moduleHandle, 0);

            if (_keyboardHookHandle == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Error?.Invoke(this, $"Kunde inte installera global keyboard-hook (Win32 {error}).");
            }
        }

        if (_mouseHookHandle == 0)
        {
            _mouseHookHandle = SetWindowsHookEx(WhMouseLl, _mouseHookProc, moduleHandle, 0);

            if (_mouseHookHandle == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Error?.Invoke(this, $"Kunde inte installera global mouse-hook (Win32 {error}).");
            }
        }
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            HotkeyBinding? binding;

            lock (_syncRoot)
            {
                binding = _binding;
            }

            if (binding?.DeviceKind == HotkeyDeviceKind.Keyboard)
            {
                int message = unchecked((int)wParam);
                bool isKeyDown = message is WmKeyDown or WmSysKeyDown;
                bool isKeyUp = message is WmKeyUp or WmSysKeyUp;

                if (isKeyDown || isKeyUp)
                {
                    var keyboardData = Marshal.PtrToStructure<Kbdllhookstruct>(lParam);
                    int virtualKey = unchecked((int)keyboardData.vkCode);

                    if (binding.MatchesKeyboard(virtualKey))
                    {
                        UpdatePressedState(virtualKey, isKeyDown);
                    }
                }
            }
        }

        return CallNextHookEx(_keyboardHookHandle, code, wParam, lParam);
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            HotkeyBinding? binding;

            lock (_syncRoot)
            {
                binding = _binding;
            }

            if (binding?.DeviceKind == HotkeyDeviceKind.Mouse)
            {
                int message = unchecked((int)wParam);
                if (TryGetMouseBindingCode(message, lParam, out int buttonCode, out bool isButtonDown) && binding.MatchesMouse(buttonCode))
                {
                    UpdatePressedState(buttonCode, isButtonDown);
                }
            }
        }

        return CallNextHookEx(_mouseHookHandle, code, wParam, lParam);
    }

    private void UpdatePressedState(int inputCode, bool isPressed)
    {
        bool shouldRaise = false;
        bool newState = false;

        lock (_syncRoot)
        {
            if (isPressed)
            {
                _pressedInputs.Add(inputCode);
            }
            else
            {
                _pressedInputs.Remove(inputCode);
            }

            newState = _pressedInputs.Count > 0;
            if (_isPressed != newState)
            {
                _isPressed = newState;
                shouldRaise = true;
            }
        }

        if (shouldRaise)
        {
            PressedStateChanged?.Invoke(this, newState);
        }
    }

    /// <summary>
    /// Reconciles the internal pressed state with actual physical key/button state via
    /// GetAsyncKeyState. Call periodically from a UI timer to recover from missed
    /// key-up events or hooks that have been silently removed by Windows.
    /// </summary>
    public void VerifyPressedState()
    {
        bool shouldRaise = false;
        bool newState = false;

        lock (_syncRoot)
        {
            if (_binding == null)
            {
                return;
            }

            bool changed = false;

            foreach (int code in _binding.Codes)
            {
                int vk = _binding.DeviceKind == HotkeyDeviceKind.Keyboard
                    ? code
                    : MouseCodeToVirtualKey(code);

                if (vk == 0) continue;

                bool physicallyPressed = (GetAsyncKeyState(vk) & 0x8000) != 0;

                if (physicallyPressed && !_pressedInputs.Contains(code))
                {
                    _pressedInputs.Add(code);
                    changed = true;
                }
                else if (!physicallyPressed && _pressedInputs.Contains(code))
                {
                    _pressedInputs.Remove(code);
                    changed = true;
                }
            }

            if (changed)
            {
                newState = _pressedInputs.Count > 0;
                if (_isPressed != newState)
                {
                    _isPressed = newState;
                    shouldRaise = true;
                }
            }
        }

        if (shouldRaise)
        {
            PressedStateChanged?.Invoke(this, newState);
        }
    }

    private static int MouseCodeToVirtualKey(int mouseCode)
    {
        return mouseCode switch
        {
            (int)MouseHotkeyCode.LeftButton => 0x01,
            (int)MouseHotkeyCode.RightButton => 0x02,
            (int)MouseHotkeyCode.MiddleButton => 0x04,
            (int)MouseHotkeyCode.XButton1 => 0x05,
            (int)MouseHotkeyCode.XButton2 => 0x06,
            _ => 0
        };
    }

    private static bool TryGetMouseBindingCode(int message, nint lParam, out int buttonCode, out bool isButtonDown)
    {
        switch (message)
        {
            case WmLButtonDown:
                buttonCode = (int)MouseHotkeyCode.LeftButton;
                isButtonDown = true;
                return true;
            case WmLButtonUp:
                buttonCode = (int)MouseHotkeyCode.LeftButton;
                isButtonDown = false;
                return true;
            case WmRButtonDown:
                buttonCode = (int)MouseHotkeyCode.RightButton;
                isButtonDown = true;
                return true;
            case WmRButtonUp:
                buttonCode = (int)MouseHotkeyCode.RightButton;
                isButtonDown = false;
                return true;
            case WmMButtonDown:
                buttonCode = (int)MouseHotkeyCode.MiddleButton;
                isButtonDown = true;
                return true;
            case WmMButtonUp:
                buttonCode = (int)MouseHotkeyCode.MiddleButton;
                isButtonDown = false;
                return true;
            case WmXButtonDown:
            case WmXButtonUp:
                var mouseData = Marshal.PtrToStructure<Msllhookstruct>(lParam);
                buttonCode = DecodeXButton(mouseData.mouseData);
                isButtonDown = message == WmXButtonDown;
                return buttonCode != 0;
            default:
                buttonCode = 0;
                isButtonDown = false;
                return false;
        }
    }

    private static int DecodeXButton(uint mouseData)
    {
        uint xButton = mouseData >> 16;
        return xButton switch
        {
            1 => (int)MouseHotkeyCode.XButton1,
            2 => (int)MouseHotkeyCode.XButton2,
            _ => 0
        };
    }

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);
    private delegate nint LowLevelMouseProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Kbdllhookstruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pointstruct
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msllhookstruct
    {
        public Pointstruct pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}