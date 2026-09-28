using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace MicMixer.Audio;

/// <summary>
/// Names the two ends of VB-CABLE "MicMixer Input" and "MicMixer Output". The installer
/// runs MicMixer with <see cref="Argument"/> right after it installs VB-CABLE.
/// </summary>
internal static class VirtualCableNamer
{
    public const string Argument = "--name-virtual-cable";

    private const string CableInterfaceName = "VB-Audio Virtual Cable";
    private const short VT_LPWSTR = 31;
    private static readonly TimeSpan WaitForCable = TimeSpan.FromSeconds(30);

    // The part of the name Windows lets the user change: "CABLE Input" in
    // "CABLE Input (VB-Audio Virtual Cable)".
    private static readonly PropertyKey DeviceDescription = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey InterfaceFriendlyName = new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);

    private static readonly (DataFlow Flow, string CableName, string MicMixerName)[] Ends =
    [
        (DataFlow.Render, "CABLE Input", "MicMixer Input"),
        (DataFlow.Capture, "CABLE Output", "MicMixer Output")
    ];

    public static bool IsNameMode(string[] args) =>
        args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Renames each end that still has VB-CABLE's own name, waiting up to 30 seconds for
    /// a freshly installed driver to show both. Ends with any other name are left alone,
    /// so a name the user chose is never overwritten.
    /// </summary>
    public static void Run()
    {
        DateTime deadline = DateTime.UtcNow + WaitForCable;
        using var enumerator = new MMDeviceEnumerator();
        while (true)
        {
            int found = 0;
            foreach ((DataFlow flow, string cableName, string micMixerName) in Ends)
            {
                if (TryRename(enumerator, flow, cableName, micMixerName))
                {
                    found++;
                }
            }

            if (found == Ends.Length)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Log.Warning("VB-CABLE did not show both of its ends in time; they keep their own names.");
                return;
            }

            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>True when the end exists, whatever it is called now.</summary>
    private static bool TryRename(MMDeviceEnumerator enumerator, DataFlow flow, string cableName, string micMixerName)
    {
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                PropertyStore properties = device.Properties;
                if (!properties.Contains(InterfaceFriendlyName)
                    || properties[InterfaceFriendlyName].Value as string != CableInterfaceName
                    || !properties.Contains(DeviceDescription))
                {
                    continue;
                }

                string? name = properties[DeviceDescription].Value as string;
                if (name == micMixerName)
                {
                    return true;
                }

                if (name != cableName)
                {
                    continue;
                }

                device.GetPropertyInformation(StorageAccessMode.ReadWrite);
                SetString(device.Properties, DeviceDescription, micMixerName);
                Log.Information("Renamed {CableName} to {MicMixerName}.", cableName, micMixerName);
                return true;
            }
        }

        return false;
    }

    private static void SetString(PropertyStore properties, PropertyKey key, string value)
    {
        var variant = new PropVariant { vt = VT_LPWSTR, pointerValue = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            properties.SetValue(key, variant);
            properties.Commit();
        }
        finally
        {
            Marshal.FreeCoTaskMem(variant.pointerValue);
        }
    }
}
