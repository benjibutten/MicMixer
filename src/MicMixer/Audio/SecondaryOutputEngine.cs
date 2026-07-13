using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace MicMixer.Audio;

/// <summary>
/// Optional second render output that plays the finished mic + music mix taken
/// before the push-to-talk gate, so e.g. OBS can capture the mix from a chosen
/// device while the virtual cable stays gated.
///
/// The primary routing output remains the master clock: <see cref="AudioRouter"/>
/// tees each pre-gate block into a bounded buffer via <see cref="Write"/>, and the
/// secondary WasapiOut drains that buffer at its own pace (the same drift-absorption
/// pattern as the music monitor fanout). The secondary device can never block or
/// stop the primary chain — a failure here tears down only this engine.
/// </summary>
public sealed class SecondaryOutputEngine : IDisposable
{
    private readonly object _syncRoot = new();
    private volatile SecondaryTapBranch? _branch;
    private WasapiOut? _out;
    private bool _enabled;
    private string? _deviceId;
    private float _volume = 1f;
    private volatile bool _ignorePushToTalk = true;
    private bool _disposed;

    /// <summary>Raised (possibly from an audio thread) when the secondary output fails or disconnects.</summary>
    public event EventHandler<string>? Error;

    public bool IsRunning
    {
        get { lock (_syncRoot) return _out != null; }
    }

    /// <summary>Whether the next routing start should open the secondary output.</summary>
    public bool Enabled
    {
        get { lock (_syncRoot) return _enabled; }
        set { lock (_syncRoot) _enabled = value; }
    }

    public string? DeviceId
    {
        get { lock (_syncRoot) return _deviceId; }
        set { lock (_syncRoot) _deviceId = value; }
    }

    /// <summary>
    /// When true the secondary output keeps playing while push-to-talk holds the
    /// cable silent; when false it follows the same gate state as the cable.
    /// Takes effect immediately, also while running.
    /// </summary>
    public bool IgnorePushToTalk
    {
        get => _ignorePushToTalk;
        set => _ignorePushToTalk = value;
    }

    /// <summary>Gain for the secondary branch only. Takes effect immediately, also while running.</summary>
    public float Volume
    {
        get { lock (_syncRoot) return _volume; }
        set
        {
            lock (_syncRoot)
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_branch is { } branch)
                {
                    branch.Volume = _volume;
                }
            }
        }
    }

    /// <summary>
    /// Starts the secondary output if it is enabled and has a device configured.
    /// Returns true when the caller should install the pre-gate tap. Failures are
    /// logged and surfaced via <see cref="Error"/> but never thrown, so a broken
    /// secondary device can never prevent the cable routing from starting.
    /// </summary>
    public bool TryStartForRouting(WaveFormat sourceFormat, Func<bool> primaryGateOpen)
    {
        string? deviceId;

        lock (_syncRoot)
        {
            if (!_enabled || _deviceId == null)
            {
                return false;
            }

            deviceId = _deviceId;
        }

        try
        {
            Start(deviceId, sourceFormat, primaryGateOpen);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start secondary output on device {DeviceId}.", deviceId);
            RaiseError(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Copies one pre-gate block from the primary audio thread into the bounded
    /// fanout buffer. Non-blocking, and a no-op while the secondary output is stopped.
    /// A failure here must never travel up the primary chain's Read into the cable's
    /// WasapiOut: the branch is detached immediately and torn down off-thread.
    /// </summary>
    public void Write(float[] buffer, int offset, int count)
    {
        var branch = _branch;
        if (branch == null)
        {
            return;
        }

        try
        {
            branch.Write(buffer, offset, count);
        }
        catch (Exception ex)
        {
            WasapiOut? failedOutput;

            lock (_syncRoot)
            {
                // A delayed failure from an old branch must not detach a newer
                // routing session that has already replaced it.
                if (!ReferenceEquals(_branch, branch))
                {
                    return;
                }

                _branch = null;
                failedOutput = _out;
                _out = null;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Log.Error(ex, "Secondary output write failed; closing only the secondary branch.");
                DisposeOutput(failedOutput);
                RaiseError(ex.Message);
            });
        }
    }

    public void Stop()
    {
        WasapiOut? output;

        lock (_syncRoot)
        {
            output = _out;
            _out = null;
            _branch = null;
        }

        if (output == null)
        {
            return;
        }

        DisposeOutput(output);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void Start(string deviceId, WaveFormat sourceFormat, Func<bool> primaryGateOpen)
    {
        Stop();

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(deviceId);
        var mixFormat = device.AudioClient.MixFormat;

        // The branch gate follows the cable's push-to-talk state unless the user
        // opted out — IgnorePushToTalk keeps the gate permanently open.
        var branch = new SecondaryTapBranch(sourceFormat, () => _ignorePushToTalk || primaryGateOpen());
        var normalized = FormatNormalizer.Normalize(branch, mixFormat);

        var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        output.PlaybackStopped += OnPlaybackStopped;

        try
        {
            output.Init(new SampleToTargetWaveProvider(normalized, mixFormat));

            lock (_syncRoot)
            {
                branch.Volume = _volume;
                _branch = branch;
                _out = output;

                // Play only signals the render thread; it does not block on it,
                // so it is safe to call while holding the lock.
                output.Play();
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                if (_out == output)
                {
                    _out = null;
                    _branch = null;
                }
            }

            DisposeOutput(output);
            throw;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Deliberate Stop() unhooks this handler before stopping, so any event
        // that still arrives is an unexpected stop. Tear down even without an
        // exception — otherwise IsRunning stays true, the UI keeps showing
        // "Aktiv", and the primary thread keeps filling a buffer nobody drains.
        if (sender is not WasapiOut stoppedOutput)
        {
            return;
        }

        lock (_syncRoot)
        {
            // PlaybackStopped can already be queued when Stop() unhooks the
            // handler. Ignore that stale event if a newer session now owns _out.
            if (!ReferenceEquals(_out, stoppedOutput))
            {
                return;
            }

            _out = null;
            _branch = null;
        }

        stoppedOutput.PlaybackStopped -= OnPlaybackStopped;

        // Dispose and notify away from the render thread. Intentional Stop()
        // unhooks this handler, so every event reaching here is unexpected.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            DisposeOutput(stoppedOutput);

            if (e.Exception != null)
            {
                Log.Error(e.Exception, "Secondary output playback stopped with exception.");
                RaiseError(e.Exception.Message);
            }
            else
            {
                const string message = "Ljudenheten stoppades oväntat.";
                Log.Warning("Secondary output stopped unexpectedly without an exception.");
                RaiseError(message);
            }
        });
    }

    /// <summary>
    /// Best-effort teardown. A broken secondary device must never make Stop(),
    /// the next routing start, or the primary cable path fail.
    /// </summary>
    private void DisposeOutput(WasapiOut? output)
    {
        if (output == null)
        {
            return;
        }

        output.PlaybackStopped -= OnPlaybackStopped;

        try
        {
            if (output.PlaybackState != PlaybackState.Stopped)
            {
                output.Stop();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Secondary output could not be stopped cleanly.");
        }

        try
        {
            output.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Secondary output could not be disposed cleanly.");
        }
    }

    private void RaiseError(string message)
    {
        try
        {
            Error?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            // A UI/status subscriber must not be able to destabilize an audio thread.
            Log.Error(ex, "Secondary output error subscriber failed.");
        }
    }
}
