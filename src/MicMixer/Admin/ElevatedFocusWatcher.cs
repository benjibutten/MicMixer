using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MicMixer.Admin;

/// <summary>
/// Notices a program running as administrator taking focus. While one has focus,
/// Windows hides the keyboard from a MicMixer that is not elevated and drops the keys
/// it sends. Remembers the program until it exits, so the warning outlives the moment
/// it had focus and goes away once it no longer applies.
/// </summary>
internal sealed class ElevatedFocusWatcher : IDisposable
{
    private const int STILL_ACTIVE = 259;

    private int _lastForegroundProcessId;
    private SafeProcessHandle? _elevatedProcess;

    /// <summary>A readable name for the last elevated program that had focus, or null once it has exited.</summary>
    public string? ElevatedAppName { get; private set; }

    /// <summary>Looks at the foreground window once. Returns true when <see cref="ElevatedAppName"/> changed.</summary>
    public bool Poll()
    {
        string? before = ElevatedAppName;

        if (_elevatedProcess != null && HasExited(_elevatedProcess))
        {
            Forget();
        }

        _ = GetWindowThreadProcessId(GetForegroundWindow(), out int processId);
        if (processId != 0 && processId != _lastForegroundProcessId)
        {
            _lastForegroundProcessId = processId;
            SafeProcessHandle process = Elevation.OpenForQuery(processId);
            if (!process.IsInvalid && Elevation.IsProcessElevated(process))
            {
                Forget();
                _elevatedProcess = process;
                ElevatedAppName = DescribeProcess(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return ElevatedAppName != before;
    }

    public void Dispose() => Forget();

    private void Forget()
    {
        _elevatedProcess?.Dispose();
        _elevatedProcess = null;
        ElevatedAppName = null;
    }

    private static bool HasExited(SafeProcessHandle process) =>
        !GetExitCodeProcess(process, out int exitCode) || exitCode != STILL_ACTIVE;

    // "Task Manager" reads better than "Taskmgr"; the file description is what
    // Windows itself shows for the program.
    private static string DescribeProcess(SafeProcessHandle process)
    {
        var path = new StringBuilder(1024);
        int length = path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length))
        {
            return "A program";
        }

        string imagePath = path.ToString();
        try
        {
            if (FileVersionInfo.GetVersionInfo(imagePath).FileDescription is { Length: > 0 } description)
            {
                return description.Trim();
            }
        }
        catch (FileNotFoundException)
        {
            // The name from the path below still says which program it is.
        }

        return Path.GetFileNameWithoutExtension(imagePath);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder exeName, ref int size);
}
