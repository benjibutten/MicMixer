using System.IO;
using System.Security;
using MicMixer.Admin;
using Serilog;

namespace MicMixer.Settings;

/// <summary>
/// The elevated "start with Windows": a Task Scheduler task that starts MicMixer as
/// administrator at the current user's sign-in, without a UAC prompt.
///
/// Registering, updating and deleting the task need an elevated process. Checking
/// whether it exists does not.
/// </summary>
internal sealed class StartupTaskService
{
    // The Run key cannot do this: Windows skips Run entries that need elevation.

    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
    private const int E_FILE_NOT_FOUND = unchecked((int)0x80070002);
    private const int E_ACCESS_DENIED = unchecked((int)0x80070005);

    /// <summary>The task's name in the Task Scheduler library, one per Windows account.</summary>
    public static string TaskName { get; } = $"MicMixer ({Environment.UserName})";

    /// <summary>
    /// True when the task is registered. A task this account may not read counts as
    /// registered: it exists, and something elevated put it there. False when Task
    /// Scheduler cannot be asked at all, so the caller falls back to the Run key
    /// rather than leaving MicMixer with no way to start.
    /// </summary>
    public bool Exists() => Exists(TaskName);

    internal static bool Exists(string taskName)
    {
        try
        {
            _ = GetRootFolder().GetTask(taskName);
            return true;
        }
        // Task Scheduler's errors surface as whatever exception .NET maps the HRESULT
        // to — FileNotFoundException, UnauthorizedAccessException — not COMException,
        // so they are told apart by HResult alone.
        catch (Exception ex) when (ex.HResult == E_FILE_NOT_FOUND)
        {
            return false;
        }
        catch (Exception ex) when (ex.HResult == E_ACCESS_DENIED)
        {
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not ask Task Scheduler for the startup task.");
            return false;
        }
    }

    /// <summary>
    /// Registers the task to start <paramref name="exePath"/> into the tray at sign-in,
    /// or deletes it. Must run elevated; throws <see cref="UnauthorizedAccessException"/>
    /// otherwise.
    /// </summary>
    public void Sync(bool register, string exePath)
    {
        dynamic folder = GetRootFolder();

        if (register)
        {
            folder.RegisterTask(TaskName, BuildDefinition(exePath), TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
            return;
        }

        try
        {
            folder.DeleteTask(TaskName, 0);
        }
        catch (Exception ex) when (ex.HResult == E_FILE_NOT_FOUND)
        {
            // Already gone.
        }
    }

    private static dynamic GetRootFolder()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Task Scheduler is not available.");

        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder(@"\");
    }

    /// <summary>The task's XML definition, starting <paramref name="exePath"/> into the tray at this account's sign-in.</summary>
    internal static string BuildDefinition(string exePath)
    {
        // Three settings differ from what Task Scheduler would pick on its own: the
        // default time limit ends MicMixer after three days, the default priority runs
        // it below normal, where audio routing competes with every other program for
        // the CPU, and the default battery rules never start it on an unplugged laptop.
        string userSid = Elevation.CurrentUser().Value;
        string command = SecurityElement.Escape(exePath);
        string workingDirectory = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? string.Empty);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts MicMixer as administrator at sign-in, so its hotkeys also work while a program or game running as administrator has focus.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{userSid}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{userSid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{App.MinimizedArgument}</Arguments>
                  <WorkingDirectory>{workingDirectory}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
