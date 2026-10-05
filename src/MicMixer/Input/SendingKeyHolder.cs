namespace MicMixer.Input;

/// <summary>
/// Holds <see cref="Key"/> down while MicMixer sends to the cable and lets go
/// <see cref="ReleaseDelay"/> after it stops, so an app whose push-to-talk is bound
/// to that key transmits exactly when MicMixer does.
/// </summary>
internal sealed class SendingKeyHolder
{
    /// <summary>How long the key stays down after sending stops.</summary>
    // The listening app drops sound still on its way through the cable once it sees
    // the key go up, which would clip the end of a phrase.
    public static readonly TimeSpan ReleaseDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Shortest time between two resends of a state Windows has not taken on.</summary>
    public static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(1);

    private readonly Action<FunctionKey, bool> _sendKey;
    private readonly Func<FunctionKey, bool> _isKeyDown;
    private readonly Func<nint> _foregroundWindow;
    private nint _lastForegroundWindow;
    private FunctionKey? _key;
    /// <summary>A key this holder let go of that Windows has not yet reported up.</summary>
    private FunctionKey? _unconfirmedRelease;
    /// <summary>A key this holder let go of since focus last moved.</summary>
    private FunctionKey? _releaseToRepeat;
    private bool _isDown;
    private bool _wasSending;
    private TimeSpan _stoppedSendingAt;
    private TimeSpan _lastSentAt;

    /// <param name="sendKey">Injects a key-down (true) or key-up (false) for the key.</param>
    /// <param name="isKeyDown">Reads whether Windows currently has the key down.</param>
    /// <param name="foregroundWindow">Reads the handle of the window that has focus.</param>
    public SendingKeyHolder(Action<FunctionKey, bool> sendKey, Func<FunctionKey, bool> isKeyDown, Func<nint> foregroundWindow)
    {
        _sendKey = sendKey;
        _isKeyDown = isKeyDown;
        _foregroundWindow = foregroundWindow;
    }

    /// <summary>The key to hold, or null when the feature is off. Changing it releases the old key.</summary>
    public FunctionKey? Key
    {
        get => _key;
        set
        {
            if (value == _key)
            {
                return;
            }

            if (_isDown)
            {
                _sendKey(_key!, false);
                _unconfirmedRelease = _key;
                _releaseToRepeat = _key;
                _isDown = false;
            }

            _key = value;
        }
    }

    /// <summary>
    /// Presses or releases the key to match <paramref name="sending"/>, and sends the
    /// wanted state again when Windows has not taken it on. When another window takes
    /// focus, the last press or release is sent again. With <paramref name="repeatPress"/>
    /// the press is also sent again every <see cref="ResendInterval"/> while the key is
    /// held. Call it whenever sending may have changed, and regularly, also while routing
    /// is off: the release delay, the resends and the focus check only take effect on a call.
    /// </summary>
    public void Update(bool sending, TimeSpan now, bool repeatPress = false)
    {
        if (_wasSending && !sending)
        {
            _stoppedSendingAt = now;
        }

        _wasSending = sending;

        nint foregroundWindow = _foregroundWindow();
        bool focusMoved = foregroundWindow != _lastForegroundWindow;
        _lastForegroundWindow = foregroundWindow;

        // An elevated MicMixer's key events reach programs that read raw input only
        // while the focused window is not elevated either, so a press or release made
        // while MicMixer has focus goes unseen by the game even though Windows takes it
        // on. Sending it again on the next focus change reaches the new window; a press
        // arrives as a key repeat, with no key-up in between.
        if (focusMoved)
        {
            RepeatRelease();
        }

        if (_key != null)
        {
            bool down = sending || (_isDown && now - _stoppedSendingAt < ReleaseDelay);
            if (down != _isDown)
            {
                Send(_key, down, now);
                return;
            }

            if (down && focusMoved)
            {
                Send(_key, true, now);
                return;
            }

            // Windows drops injected keys without telling the sender while a window of a
            // higher integrity level has focus, or while the UAC prompt or the lock screen
            // shows. Resending only on a mismatch keeps an idle MicMixer from injecting
            // input, which would stop the screen saver, sleep and "away" statuses.
            // A game forgets a held key when it loses focus, and can miss the press sent
            // as it gets focus back while Windows still has the key down; repeating the
            // press brings its push-to-talk back without any mismatch to see.
            if (down && (repeatPress || !_isKeyDown(_key)) && now - _lastSentAt >= ResendInterval)
            {
                Send(_key, true, now);
                return;
            }
        }

        // A dropped key-up leaves the key down for every other app, which would keep
        // one bound to it transmitting. Only a key this holder let go of is released
        // again, so a key the user holds down themselves is left alone.
        if (_unconfirmedRelease is { } released && !(_isDown && released == _key))
        {
            if (!_isKeyDown(released))
            {
                _unconfirmedRelease = null;
            }
            else if (now - _lastSentAt >= ResendInterval)
            {
                _sendKey(released, false);
                _lastSentAt = now;
            }
        }
    }

    /// <summary>Sends a key-up for <see cref="Key"/> at once, whether or not it is held.</summary>
    public void Release()
    {
        if (_key is { } key)
        {
            _sendKey(key, false);
            _unconfirmedRelease = key;
            _releaseToRepeat = key;
        }

        _isDown = false;
        _wasSending = false;
    }

    private void RepeatRelease()
    {
        if (_releaseToRepeat is not { } released)
        {
            return;
        }

        _releaseToRepeat = null;
        // Down in Windows means the user holds the key, or this holder pressed it again.
        if (!_isKeyDown(released))
        {
            _sendKey(released, false);
        }
    }

    private void Send(FunctionKey key, bool down, TimeSpan now)
    {
        _sendKey(key, down);
        _isDown = down;
        _lastSentAt = now;
        if (!down)
        {
            _unconfirmedRelease = key;
            _releaseToRepeat = key;
        }
    }
}
