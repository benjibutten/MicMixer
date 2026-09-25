using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace MicMixer.Diagnostics;

/// <summary>
/// Logs when another process starts or stops playing audio to the device MicMixer
/// sends to. A virtual cable mixes every app that plays to it into the signal the
/// game hears as the mic, and none of that passes through MicMixer's gates.
/// </summary>
public sealed class OutputDeviceAppWatcher : IDisposable
{
    // -60 dBFS. Browsers, Discord and games keep sessions active while they play
    // silence, so an active session alone does not mean the game hears anything.
    private const float AudibleThreshold = 0.001f;

    // Speech and music have short pauses; an app counts as stopped only after this
    // much silence, so one sound is one start and one stop line.
    private static readonly TimeSpan SilenceBeforeStopped = TimeSpan.FromSeconds(2);

    // MicMixer's own stream still plays its buffered audio (about 50 ms) after the
    // push-to-talk gate closes; the cable is judged only once that has drained.
    private static readonly TimeSpan OwnOutputTail = TimeSpan.FromMilliseconds(300);

    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly List<(int ProcessId, AudioSessionControl Session)> _sessions = new();
    private readonly Dictionary<int, PlayingApp> _playing = new();
    private MMDevice? _endpoint;
    private bool _endpointFailed;
    private bool _sessionReadFailing;
    private DateTime? _micMixerSilentSince;
    private CableSound? _cableSound;

    public OutputDeviceAppWatcher(string deviceId, string deviceName)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
    }

    /// <summary>
    /// Reads the device's audio sessions again, so apps that opened a stream since
    /// the previous call are measured by <see cref="Sample"/>.
    /// </summary>
    public void Poll()
    {
        List<(int ProcessId, AudioSessionControl Session)> sessions;
        try
        {
            sessions = ReadOtherSessions();
        }
        catch (Exception ex)
        {
            // Once per failure streak: the read repeats every 2 s.
            if (!_sessionReadFailing)
            {
                _sessionReadFailing = true;
                Log.Debug(ex, "Failed to read audio sessions on {Device}.", _deviceName);
            }

            return;
        }

        if (_sessionReadFailing)
        {
            _sessionReadFailing = false;
            Log.Debug("Reading audio sessions on {Device} works again.", _deviceName);
        }

        DisposeSessions();
        _sessions.AddRange(sessions);
        MeasureSessions(DateTime.Now);
    }

    /// <summary>
    /// Measures the device and the sessions from the last <see cref="Poll"/>. Called
    /// often, so short sounds are not missed.
    /// </summary>
    /// <param name="micMixerSilent">
    /// Whether MicMixer sends silence to the device (push-to-talk closed, no music).
    /// Any sound on the device is then another app's, including one whose stream
    /// opened and closed between two polls.
    /// </param>
    public void Sample(bool micMixerSilent)
    {
        DateTime now = DateTime.Now;
        SampleCable(now, micMixerSilent);
        MeasureSessions(now);
        if (_cableSound != null)
        {
            _cableSound.Apps.UnionWith(_playing.Values.Select(app => app.Name));
            _cableSound.OpenStreamProcessIds.UnionWith(ActiveSessionProcessIds());
        }
    }

    /// <summary>
    /// Writes one log line when an app becomes audible on the device and one when it
    /// has been silent for <see cref="SilenceBeforeStopped"/>.
    /// </summary>
    private void MeasureSessions(DateTime now)
    {
        foreach (var (processId, session) in _sessions)
        {
            float peak;
            try
            {
                peak = session.AudioMeterInformation.MasterPeakValue;
            }
            catch (Exception)
            {
                // The session ended since the last poll; the next poll drops it.
                continue;
            }

            if (peak < AudibleThreshold)
            {
                continue;
            }

            if (_playing.TryGetValue(processId, out var app))
            {
                app.LastAudible = now;
                app.Peak = Math.Max(app.Peak, peak);
                continue;
            }

            app = new PlayingApp(DescribeProcess(processId), now) { LastAudible = now, Peak = peak };
            _playing[processId] = app;
            Log.Information(
                "Other app started playing to {Device}: {App} (pid {ProcessId}), peak {PeakDb:0} dBFS",
                _deviceName, app.Name, processId, ToDecibels(peak));
        }

        foreach (int processId in _playing.Where(pair => now - pair.Value.LastAudible >= SilenceBeforeStopped)
                     .Select(pair => pair.Key).ToList())
        {
            LogStopped(processId, stillPlaying: false);
        }
    }

    /// <summary>Apps audible on the device as of the last <see cref="Sample"/>, for a marker line.</summary>
    public string DescribePlaying()
    {
        if (_playing.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", _playing.Values.Select(app => $"{app.Name} since {app.Since:HH:mm:ss}"));
    }

    /// <summary>
    /// Writes the lines still pending, for when routing stops or the app exits:
    /// the current cable period and every app that is still playing.
    /// </summary>
    public void Flush()
    {
        EndCableSound();
        foreach (int processId in _playing.Keys.ToList())
        {
            LogStopped(processId, stillPlaying: true);
        }
    }

    public void Dispose()
    {
        DisposeSessions();
        _endpoint?.Dispose();
        _endpoint = null;
    }

    /// <summary>
    /// Windows' default playback devices. Apps without their own device choice
    /// follow these, so a default that points at the cable sends their sound to the game.
    /// </summary>
    public static string DescribeWindowsDefaults()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return $"{DefaultName(enumerator, Role.Multimedia)} (communications: {DefaultName(enumerator, Role.Communications)})";
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.Message})";
        }
    }

    private static string DefaultName(MMDeviceEnumerator enumerator, Role role)
    {
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, role))
        {
            return "none";
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
        return device.FriendlyName;
    }

    /// <summary>
    /// One log line per period in which the device carried sound while MicMixer sent
    /// silence, written after <see cref="SilenceBeforeStopped"/> of quiet or when
    /// MicMixer starts sending again.
    /// </summary>
    private void SampleCable(DateTime now, bool micMixerSilent)
    {
        if (!micMixerSilent)
        {
            _micMixerSilentSince = null;
            EndCableSound();
            return;
        }

        _micMixerSilentSince ??= now;
        if (now - _micMixerSilentSince < OwnOutputTail || ReadEndpointPeak() is not { } peak)
        {
            return;
        }

        if (peak >= AudibleThreshold)
        {
            if (_cableSound == null)
            {
                _cableSound = new CableSound(now);
                // A short sound may come from a stream opened since the last poll.
                Poll();
            }

            _cableSound.LastAudible = now;
            _cableSound.Peak = Math.Max(_cableSound.Peak, peak);
        }
        else if (_cableSound != null && now - _cableSound.LastAudible >= SilenceBeforeStopped)
        {
            EndCableSound();
        }
    }

    private void EndCableSound()
    {
        if (_cableSound is not { } sound)
        {
            return;
        }

        Log.Information(
            "{Device} carried sound while MicMixer sent silence: {Start:HH:mm:ss.fff} for {Seconds:0.00} s, peak {PeakDb:0} dBFS, apps: {Apps}; open streams from other apps: {OpenStreams}",
            _deviceName,
            sound.Start,
            (sound.LastAudible - sound.Start).TotalSeconds,
            ToDecibels(sound.Peak),
            sound.Apps.Count == 0 ? "none measured audible" : string.Join(", ", sound.Apps),
            sound.OpenStreamProcessIds.Count == 0
                ? "none"
                : string.Join(", ", sound.OpenStreamProcessIds.Select(id => $"{DescribeProcess(id)} (pid {id})")));
        _cableSound = null;
    }

    private float? ReadEndpointPeak()
    {
        if (_endpointFailed)
        {
            return null;
        }

        try
        {
            if (_endpoint == null)
            {
                using var enumerator = new MMDeviceEnumerator();
                _endpoint = enumerator.GetDevice(_deviceId);
            }

            return _endpoint.AudioMeterInformation.MasterPeakValue;
        }
        catch (Exception ex)
        {
            // Not retried: the device is gone or has no meter until routing restarts.
            _endpointFailed = true;
            Log.Debug(ex, "Failed to read the level of {Device}.", _deviceName);
            return null;
        }
    }

    /// <summary>
    /// Processes with an active stream on the device, whether or not their level
    /// could be read: a protected process's meter can refuse access while its sound
    /// still reaches the device.
    /// </summary>
    private IEnumerable<int> ActiveSessionProcessIds()
    {
        foreach (var (processId, session) in _sessions)
        {
            bool active;
            try
            {
                active = session.State == AudioSessionState.AudioSessionStateActive;
            }
            catch (Exception)
            {
                continue;
            }

            if (active)
            {
                yield return processId;
            }
        }
    }

    private void LogStopped(int processId, bool stillPlaying)
    {
        var app = _playing[processId];
        Log.Information(
            "Other app {Change} to {Device}: {App} (pid {ProcessId}) after {Seconds:0.0} s, peak {PeakDb:0} dBFS",
            stillPlaying ? "still playing when the log stopped watching" : "stopped playing",
            _deviceName, app.Name, processId, (app.LastAudible - app.Since).TotalSeconds, ToDecibels(app.Peak));
        _playing.Remove(processId);
    }

    private List<(int ProcessId, AudioSessionControl Session)> ReadOtherSessions()
    {
        var result = new List<(int ProcessId, AudioSessionControl Session)>();
        int ownProcessId = Environment.ProcessId;

        // A fresh device per poll: a session manager only lists the sessions that
        // existed when it was created.
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(_deviceId);
        var sessions = device.AudioSessionManager.Sessions;
        if (sessions == null)
        {
            return result;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            int processId = (int)session.GetProcessID;
            if (processId == ownProcessId || session.State == AudioSessionState.AudioSessionStateExpired)
            {
                session.Dispose();
                continue;
            }

            result.Add((processId, session));
        }

        return result;
    }

    private void DisposeSessions()
    {
        foreach (var (_, session) in _sessions)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private static double ToDecibels(float peak) => 20 * Math.Log10(Math.Max(peak, 1e-6f));

    private static string DescribeProcess(int processId)
    {
        // Windows plays its notification sounds from a session without a process.
        if (processId == 0)
        {
            return "Windows system sounds";
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            // The process exited between enumeration and lookup.
            return "exited process";
        }
    }

    private sealed class CableSound(DateTime start)
    {
        public DateTime Start { get; } = start;
        public DateTime LastAudible { get; set; } = start;
        public float Peak { get; set; }
        public HashSet<string> Apps { get; } = new();
        public HashSet<int> OpenStreamProcessIds { get; } = new();
    }

    private sealed class PlayingApp(string name, DateTime since)
    {
        public string Name { get; } = name;
        public DateTime Since { get; } = since;
        public DateTime LastAudible { get; set; }
        public float Peak { get; set; }
    }
}
