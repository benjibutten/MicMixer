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
    private FunctionKey? _key;
    /// <summary>A key this holder let go of that Windows has not yet reported up.</summary>
    private FunctionKey? _unconfirmedRelease;
    private bool _isDown;
    private bool _wasSending;
    private TimeSpan _stoppedSendingAt;
    private TimeSpan _lastSentAt;

    /// <param name="sendKey">Injects a key-down (true) or key-up (false) for the key.</param>
    /// <param name="isKeyDown">Reads whether Windows currently has the key down.</param>
    public SendingKeyHolder(Action<FunctionKey, bool> sendKey, Func<FunctionKey, bool> isKeyDown)
    {
        _sendKey = sendKey;
        _isKeyDown = isKeyDown;
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
                _isDown = false;
            }

            _key = value;
        }
    }

    /// <summary>
    /// Presses or releases the key to match <paramref name="sending"/>, and sends the
    /// wanted state again when Windows has not taken it on. Call it whenever sending
    /// may have changed, and regularly, also while routing is off: the release delay
    /// and the resends only take effect on a call.
    /// </summary>
    public void Update(bool sending, TimeSpan now)
    {
        if (_wasSending && !sending)
        {
            _stoppedSendingAt = now;
        }

        _wasSending = sending;

        if (_key != null)
        {
            bool down = sending || (_isDown && now - _stoppedSendingAt < ReleaseDelay);
            if (down != _isDown)
            {
                Send(_key, down, now);
                return;
            }

            // Windows drops injected keys without telling the sender while a window of a
            // higher integrity level has focus, or while the UAC prompt or the lock screen
            // shows. Resending only on a mismatch keeps an idle MicMixer from injecting
            // input, which would stop the screen saver, sleep and "away" statuses.
            if (down && !_isKeyDown(_key) && now - _lastSentAt >= ResendInterval)
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
        }

        _isDown = false;
        _wasSending = false;
    }

    private void Send(FunctionKey key, bool down, TimeSpan now)
    {
        _sendKey(key, down);
        _isDown = down;
        _lastSentAt = now;
        if (!down)
        {
            _unconfirmedRelease = key;
        }
    }
}
