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

    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly List<(int ProcessId, AudioSessionControl Session)> _sessions = new();
    private readonly Dictionary<int, PlayingApp> _playing = new();

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
            Log.Debug(ex, "Failed to read audio sessions on {Device}.", _deviceName);
            return;
        }

        DisposeSessions();
        _sessions.AddRange(sessions);
        Sample();
    }

    /// <summary>
    /// Measures the sessions from the last <see cref="Poll"/> and writes one log line
    /// when an app becomes audible on the device and one when it has been silent for
    /// <see cref="SilenceBeforeStopped"/>. Called often, so short sounds are not missed.
    /// </summary>
    public void Sample()
    {
        DateTime now = DateTime.Now;
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
            LogStopped(processId);
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

    public void Dispose()
    {
        DisposeSessions();
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

    private void LogStopped(int processId)
    {
        var app = _playing[processId];
        Log.Information(
            "Other app stopped playing to {Device}: {App} (pid {ProcessId}) after {Seconds:0.0} s, peak {PeakDb:0} dBFS",
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

    private sealed class PlayingApp(string name, DateTime since)
    {
        public string Name { get; } = name;
        public DateTime Since { get; } = since;
        public DateTime LastAudible { get; set; }
        public float Peak { get; set; }
    }
}
