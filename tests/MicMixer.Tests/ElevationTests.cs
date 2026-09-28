using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using MicMixer.Admin;
using MicMixer.Settings;
using Xunit;

namespace MicMixer.Tests;

public sealed class ElevationTests
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    private static DirectorySecurity Folder(SecurityIdentifier owner, params FileSystemAccessRule[] rules)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        foreach (FileSystemAccessRule rule in rules)
        {
            security.AddAccessRule(rule);
        }

        return security;
    }

    private static FileSystemAccessRule Allow(SecurityIdentifier sid, FileSystemRights rights, PropagationFlags propagation = PropagationFlags.None) =>
        new(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, propagation, AccessControlType.Allow);

    [Fact]
    public void A_program_files_style_folder_is_protected()
    {
        DirectorySecurity security = Folder(
            Administrators,
            Allow(Administrators, FileSystemRights.FullControl),
            Allow(System, FileSystemRights.FullControl),
            Allow(Users, FileSystemRights.ReadAndExecute),
            Allow(CreatorOwner, FileSystemRights.FullControl, PropagationFlags.InheritOnly));

        Assert.True(Elevation.IsProtected(security, Elevation.ModifyingRights));
    }

    [Fact]
    public void A_folder_every_user_may_modify_is_not_protected()
    {
        // What a folder created directly under C:\ inherits: C:\Tools on a stock install.
        DirectorySecurity security = Folder(
            Administrators,
            Allow(Administrators, FileSystemRights.FullControl),
            Allow(AuthenticatedUsers, FileSystemRights.Modify));

        Assert.False(Elevation.IsProtected(security, Elevation.ModifyingRights));
    }

    [Fact]
    public void A_folder_owned_by_the_user_is_not_protected()
    {
        // An owner can rewrite the permissions, whatever they say today.
        DirectorySecurity security = Folder(Elevation.CurrentUser(), Allow(Administrators, FileSystemRights.FullControl));

        Assert.False(Elevation.IsProtected(security, Elevation.ModifyingRights));
    }

    [Fact]
    public void Only_takeover_rights_matter_above_the_install_folder()
    {
        // C:\ lets every user create folders; that alone cannot touch Program Files.
        DirectorySecurity root = Folder(
            Administrators,
            Allow(Administrators, FileSystemRights.FullControl),
            Allow(AuthenticatedUsers, FileSystemRights.AppendData));

        Assert.True(Elevation.IsProtected(root, Elevation.AncestorTakeoverRights));
        Assert.False(Elevation.IsProtected(root, Elevation.ModifyingRights));
    }

    [Fact]
    public void A_file_in_the_temp_folder_is_not_protected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MicMixer-elevation-test-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, string.Empty);
        try
        {
            Assert.False(Elevation.IsProtectedFromNonAdministrators(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_in_the_windows_folder_is_protected()
    {
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        Assert.True(Elevation.IsProtectedFromNonAdministrators(notepad));
    }

    [Fact]
    public void This_process_reads_its_own_elevation()
    {
        using var process = Elevation.OpenForQuery(Environment.ProcessId);

        Assert.False(process.IsInvalid);
        Assert.Equal(Elevation.IsElevated, Elevation.IsProcessElevated(process));
    }

    [Fact]
    public void A_startup_task_that_is_not_registered_does_not_exist()
    {
        // Asks the real Task Scheduler, which reports a missing task through an
        // exception type chosen by the COM interop layer rather than COMException.
        Assert.False(StartupTaskService.Exists($"MicMixer test {Guid.NewGuid():N}"));
    }

    [Fact]
    public void The_startup_task_runs_elevated_at_normal_priority_with_no_time_limit()
    {
        const string exePath = @"C:\Program Files\Mic & Mixer\MicMixer.exe";
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        XDocument task = XDocument.Parse(StartupTaskService.BuildDefinition(exePath));

        Assert.Equal("HighestAvailable", task.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("PT0S", task.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("4", task.Descendants(ns + "Priority").Single().Value);
        Assert.Equal("false", task.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal(exePath, task.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--minimized", task.Descendants(ns + "Arguments").Single().Value);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\Music Folder\", "\"C:\\Music Folder\\\\\"")]
    [InlineData(@"C:\Music\%(title)s.%(ext)s", @"C:\Music\%(title)s.%(ext)s")]
    public void Command_line_arguments_are_quoted_the_way_programs_split_them(string argument, string expected)
    {
        string commandLine = UnelevatedProcess.BuildCommandLine("tool.exe", [argument]);

        Assert.Equal($"tool.exe {expected}", commandLine);
    }
}
