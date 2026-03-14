using System.Runtime.InteropServices;

namespace MicMixer.Input;

public sealed class GlobalHotkeyListener : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly object _syncRoot = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _pollLoopTask;
    private HotkeyBinding? _binding;
    private bool _isPressed;
    private bool _disposed;

    public GlobalHotkeyListener()
    {
        _pollLoopTask = Task.Run(() => PollLoopAsync(_disposeCts.Token));
    }

    public bool IsPressed => Volatile.Read(ref _isPressed);

    public event EventHandler<bool>? PressedStateChanged;

    public void UpdateBinding(HotkeyBinding? binding)
    {
        lock (_syncRoot)
        {
            _binding = binding;
        }

        VerifyPressedState();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();

        try
        {
            _pollLoopTask.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch (AggregateException)
        {
            // Cancellation is expected during shutdown.
        }

        _disposeCts.Dispose();
        UpdatePressedState(false);
    }

    public void VerifyPressedState()
    {
        // Poll global input state instead of installing low-level hooks so the app
        // does not add latency to the system mouse path in games or on the desktop.
        UpdatePressedState(ReadPressedState());
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                VerifyPressedState();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private bool ReadPressedState()
    {
        HotkeyBinding? binding;

        lock (_syncRoot)
        {
            binding = _binding;
        }

        if (binding == null)
        {
            return false;
        }

        foreach (int code in binding.Codes)
        {
            int virtualKey = binding.DeviceKind == HotkeyDeviceKind.Keyboard
                ? code
                : MouseCodeToVirtualKey(code);

            if (virtualKey != 0 && (GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdatePressedState(bool isPressed)
    {
        bool shouldRaise = false;

        lock (_syncRoot)
        {
            if (_isPressed == isPressed)
            {
                return;
            }

            _isPressed = isPressed;
            shouldRaise = true;
        }

        if (shouldRaise)
        {
            PressedStateChanged?.Invoke(this, isPressed);
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}