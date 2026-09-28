using System.Diagnostics;
using System.IO;

namespace MicMixer.Admin;

internal static class ShellLauncher
{
    /// <summary>
    /// Opens a web address or folder the way double-clicking it would. From a MicMixer
    /// running as administrator it goes through Explorer, which hands it to the desktop
    /// shell, so the browser or folder window runs without administrator rights.
    /// </summary>
    public static void Open(string target)
    {
        if (!Elevation.IsElevated)
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return;
        }

        // A full path: an elevated process must not pick up an explorer.exe placed
        // next to MicMixer.
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var startInfo = new ProcessStartInfo(explorer) { UseShellExecute = false };
        startInfo.ArgumentList.Add(target);
        Process.Start(startInfo);
    }
}
