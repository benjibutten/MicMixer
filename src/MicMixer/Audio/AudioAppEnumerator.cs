using System.Diagnostics;
using NAudio.CoreAudioApi;
using Serilog;

namespace MicMixer.Audio;

/// <summary>An application that has an audio session on some render device.</summary>
public sealed class AudioAppOption
{
    public AudioAppOption(int processId, string processName, string displayName, bool isPlaying)
    {
        ProcessId = processId;
        ProcessName = processName;
        DisplayName = displayName;
        IsPlaying = isPlaying;
    }

    public int ProcessId { get; }

    /// <summary>Executable name without extension; used to remember the choice across sessions.</summary>
    public string ProcessName { get; }

    public string DisplayName { get; }

    public bool IsPlaying { get; }

    public string FriendlyName => IsPlaying ? $"{DisplayName} — spelar ljud" : DisplayName;
}

/// <summary>
/// Lists applications that currently hold an audio session on any active render device,
/// so the user can pick a capture target without knowing anything about process ids.
/// </summary>
public static class AudioAppEnumerator
{
    public static IReadOnlyList<AudioAppOption> GetAudioApps()
    {
        var byProcessId = new Dictionary<int, (string Name, string Display, bool Playing)>();
        int ownProcessId = Environment.ProcessId;

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    if (sessions == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.IsSystemSoundsSession)
                        {
                            continue;
                        }

                        if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                        {
                            continue;
                        }

                        int processId = (int)session.GetProcessID;
                        if (processId == 0 || processId == ownProcessId)
                        {
                            continue;
                        }

                        bool playing = session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive;

                        if (byProcessId.TryGetValue(processId, out var existing))
                        {
                            if (playing && !existing.Playing)
                            {
                                byProcessId[processId] = (existing.Name, existing.Display, true);
                            }

                            continue;
                        }

                        if (TryDescribeProcess(processId, out string name, out string display))
                        {
                            byProcessId[processId] = (name, display, playing);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Failed to enumerate audio sessions on device {Device}.", device.ID);
                }
            }
        }

        return byProcessId
            .Select(pair => new AudioAppOption(pair.Key, pair.Value.Name, pair.Value.Display, pair.Value.Playing))
            .OrderByDescending(app => app.IsPlaying)
            .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryDescribeProcess(int processId, out string processName, out string displayName)
    {
        processName = "";
        displayName = "";

        try
        {
            using var process = Process.GetProcessById(processId);
            processName = process.ProcessName;
            displayName = processName;

            try
            {
                // Prefer the product description ("Spotify") over the exe name when readable.
                string? description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    displayName = description.Trim();
                }
            }
            catch
            {
                // Access to MainModule is denied for elevated processes; the exe name is fine.
            }

            return true;
        }
        catch
        {
            // The process exited between enumeration and lookup.
            return false;
        }
    }
}
