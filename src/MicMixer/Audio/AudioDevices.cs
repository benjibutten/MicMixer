using NAudio.CoreAudioApi;

namespace MicMixer.Audio;

internal sealed record AudioDeviceOption(string Id, string FriendlyName);

/// <summary>
/// Active Windows audio endpoints, and how MicMixer recognizes and pre-selects them.
/// Windows has no "virtual device" flag, so recognition is by driver naming.
/// </summary>
internal static class AudioDevices
{
    public static (List<AudioDeviceOption> Inputs, List<AudioDeviceOption> Outputs) EnumerateActive()
    {
        using var enumerator = new MMDeviceEnumerator();
        return (Read(enumerator, DataFlow.Capture), Read(enumerator, DataFlow.Render));
    }

    private static List<AudioDeviceOption> Read(MMDeviceEnumerator enumerator, DataFlow dataFlow)
    {
        var options = new List<AudioDeviceOption>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active))
        {
            using (device)
            {
                options.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
            }
        }

        return options;
    }

    /// <summary>
    /// Either end of VB-CABLE (all variants), Virtual Audio Cable ("Line 1 (Virtual Audio Cable)")
    /// or Voicemeeter.
    /// </summary>
    public static bool LooksLikeVirtualCable(AudioDeviceOption device)
    {
        string name = device.FriendlyName;
        return name.Contains("vb-audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("virtual cable", StringComparison.OrdinalIgnoreCase)
            || name.Contains("virtual audio cable", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cable input", StringComparison.OrdinalIgnoreCase)
            || name.Contains("cable output", StringComparison.OrdinalIgnoreCase)
            || name.Contains("voicemeeter", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeVoiceModDevice(AudioDeviceOption device)
    {
        string name = device.FriendlyName;
        return name.Contains("voicemod", StringComparison.OrdinalIgnoreCase)
            || name.Contains("voice mod", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A physical microphone: neither a voice changer nor the recording end of a cable.</summary>
    public static bool LooksLikeNormalMic(AudioDeviceOption device) =>
        !LooksLikeVoiceModDevice(device) && !LooksLikeVirtualCable(device);

    /// <summary>
    /// The recording device a game should use for the cable MicMixer plays into:
    /// VB-CABLE pairs "CABLE Input" with "CABLE Output", Virtual Audio Cable uses
    /// the same name for both ends.
    /// </summary>
    public static AudioDeviceOption? FindRecordingEnd(AudioDeviceOption playbackEnd, IReadOnlyList<AudioDeviceOption> inputs)
    {
        string swapped = playbackEnd.FriendlyName.Replace("input", "Output", StringComparison.OrdinalIgnoreCase);
        return inputs.FirstOrDefault(device => string.Equals(device.FriendlyName, swapped, StringComparison.OrdinalIgnoreCase))
            ?? inputs.FirstOrDefault(device => string.Equals(device.FriendlyName, playbackEnd.FriendlyName, StringComparison.OrdinalIgnoreCase))
            ?? inputs.FirstOrDefault(LooksLikeVirtualCable);
    }

    public static AudioDeviceOption? SelectInput(
        IReadOnlyList<AudioDeviceOption> devices,
        string? preferredId,
        Func<AudioDeviceOption, bool> heuristic,
        string? excludedId = null)
    {
        // The recording end of a cable is a microphone to Windows, but as a source it
        // would feed the mix back into itself. It is never guessed; no device is safer.
        return devices.FirstOrDefault(device => device.Id == preferredId)
            ?? devices.FirstOrDefault(device => device.Id != excludedId && heuristic(device))
            ?? devices.FirstOrDefault(device => device.Id != excludedId && !LooksLikeVirtualCable(device));
    }

    public static AudioDeviceOption? SelectCableOutput(IReadOnlyList<AudioDeviceOption> devices, string? preferredId)
    {
        return devices.FirstOrDefault(device => device.Id == preferredId)
            ?? devices.FirstOrDefault(LooksLikeVirtualCable)
            ?? devices.FirstOrDefault();
    }

    public static AudioDeviceOption? SelectMonitor(IReadOnlyList<AudioDeviceOption> devices, string? preferredId)
    {
        return devices.FirstOrDefault(device => device.Id == preferredId)
            ?? devices.FirstOrDefault(device => !LooksLikeVirtualCable(device))
            ?? devices.FirstOrDefault();
    }
}
