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
public sealed class OutputDeviceAppWatcher
{
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly Dictionary<int, (string Name, DateTime Since)> _playing = new();

    public OutputDeviceAppWatcher(string deviceId, string deviceName)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
    }

    /// <summary>
    /// Reads the device's audio sessions and writes one log line for every app
    /// that started or stopped playing since the previous call.
    /// </summary>
    public void Poll()
    {
        HashSet<int> playingNow;
        try
        {
            playingNow = ReadPlayingProcessIds();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read audio sessions on {Device}.", _deviceName);
            return;
        }

        foreach (int processId in _playing.Keys.Where(id => !playingNow.Contains(id)).ToList())
        {
            var (name, since) = _playing[processId];
            Log.Information(
                "Other app stopped playing to {Device}: {App} (pid {ProcessId}) after {Seconds:0.0} s",
                _deviceName, name, processId, (DateTime.Now - since).TotalSeconds);
            _playing.Remove(processId);
        }

        foreach (int processId in playingNow.Where(id => !_playing.ContainsKey(id)))
        {
            string name = DescribeProcess(processId);
            _playing[processId] = (name, DateTime.Now);
            Log.Information(
                "Other app started playing to {Device}: {App} (pid {ProcessId})",
                _deviceName, name, processId);
        }
    }

    /// <summary>Apps playing to the device as of the last <see cref="Poll"/>, for a marker line.</summary>
    public string DescribePlaying()
    {
        if (_playing.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", _playing.Select(pair => $"{pair.Value.Name} since {pair.Value.Since:HH:mm:ss}"));
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

    private HashSet<int> ReadPlayingProcessIds()
    {
        var playing = new HashSet<int>();
        int ownProcessId = Environment.ProcessId;

        // A fresh device per poll: a session manager only lists the sessions that
        // existed when it was created.
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(_deviceId);
        var sessions = device.AudioSessionManager.Sessions;
        if (sessions == null)
        {
            return playing;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            int processId = (int)session.GetProcessID;
            if (processId != ownProcessId && session.State == AudioSessionState.AudioSessionStateActive)
            {
                playing.Add(processId);
            }
        }

        return playing;
    }

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
}
