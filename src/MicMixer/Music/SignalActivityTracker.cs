namespace MicMixer.Music;

/// <summary>
/// Turns a bursty audio level into a stable active/inactive state. Audio packets
/// and natural gaps between beats must not make UI state flap on every sample.
/// </summary>
internal sealed class SignalActivityTracker
{
    private readonly float _activationThreshold;
    private readonly TimeSpan _holdDuration;
    private TimeSpan _lastSignalAt;
    private bool _hasSeenSignal;

    public SignalActivityTracker(float activationThreshold, TimeSpan holdDuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(activationThreshold);
        ArgumentOutOfRangeException.ThrowIfLessThan(holdDuration, TimeSpan.Zero);

        _activationThreshold = activationThreshold;
        _holdDuration = holdDuration;
    }

    public bool IsActive { get; private set; }

    /// <summary>Returns true when the stable activity state changed.</summary>
    public bool Observe(float level, TimeSpan now)
    {
        bool wasActive = IsActive;

        if (float.IsFinite(level) && level >= _activationThreshold)
        {
            _lastSignalAt = now;
            _hasSeenSignal = true;
            IsActive = true;
        }
        else if (_hasSeenSignal && now - _lastSignalAt > _holdDuration)
        {
            IsActive = false;
        }

        return wasActive != IsActive;
    }

    public void Reset()
    {
        _lastSignalAt = TimeSpan.Zero;
        _hasSeenSignal = false;
        IsActive = false;
    }
}
