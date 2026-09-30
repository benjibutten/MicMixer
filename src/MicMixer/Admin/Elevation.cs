using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MicMixer.Admin;

/// <summary>
/// Whether a process runs as administrator, whether a file sits where only
/// administrators can change it, and restarting MicMixer as administrator.
/// </summary>
internal static class Elevation
{
    /// <summary>
    /// Passed to a MicMixer started to take over from this one. The new instance waits
    /// for the process with that id to exit, since it still holds the single-instance mutex.
    /// </summary>
    public const string ProcessIdArgument = "--process-id";

    private const int ERROR_CANCELLED = 1223;
    private const int ProcessRedirectionTrustPolicy = 16;
    private const int EnforceRedirectionTrust = 0x1;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevationClass = 20;
    private const int TokenElevationTypeClass = 18;
    private const int KernelObject = 6;
    private const int DaclSecurityInformation = 0x4;
    private const int ProcessSynchronize = 0x00100000;
    // An administrator's filtered token, which UAC can elevate to the full one.
    private const int TokenElevationTypeLimited = 3;

    // Generic rights are normally mapped to specific ones before they are stored,
    // but an entry that kept them still grants everything they stand for.
    private const FileSystemRights GenericWriteOrAll = (FileSystemRights)0x50000000;

    // Rights that let a principal replace, delete, rename or re-permission an entry.
    internal const FileSystemRights ModifyingRights =
        FileSystemRights.WriteData
        | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.WriteAttributes
        | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    // Rights on a folder further up that let a principal move the install folder
    // away, or take the folder over and grant itself the rest. Write access alone
    // does not: it creates new entries, and cannot touch existing ones.
    internal const FileSystemRights AncestorTakeoverRights =
        FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier[] TrustedPrincipals =
    [
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464") // NT SERVICE\TrustedInstaller
    ];

    public static bool IsElevated { get; } = ReadIsElevated();

    /// <summary>
    /// True when this process runs as administrator or its account can get there
    /// through a UAC prompt. False for a standard account, which UAC would ask for
    /// another account's password and then run MicMixer as that other account.
    /// </summary>
    public static bool CanRunAsAdministrator { get; } = IsElevated || ReadHasLimitedAdministratorToken();

    private static bool ReadIsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Stops this process from following folder junctions that were made without
    /// administrator rights. Processes it starts are not covered. Does nothing before
    /// Windows 11.
    /// </summary>
    public static void RefuseUntrustedJunctions()
    {
        // Running as administrator, MicMixer still writes and deletes files in
        // %LOCALAPPDATA%\MicMixer and in music folders. Any program the user runs could
        // turn one of those folders into a junction and point the writes anywhere.
        int policy = EnforceRedirectionTrust;
        _ = SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, ref policy, sizeof(int));
    }

    /// <summary>The Windows account this process runs as, whether elevated or not.</summary>
    public static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    /// <summary>
    /// MicMixer.Launcher.exe beside this exe, which starts MicMixer without the
    /// environment variables that load code into the .NET runtime. Everything that starts
    /// MicMixer as administrator goes through it, apart from the installer and the zip
    /// updater, which run from folders any program can write to anyway. Missing in
    /// development builds.
    /// </summary>
    public static string LauncherPath { get; } = Path.Combine(AppContext.BaseDirectory, "MicMixer.Launcher.exe");

    /// <summary>
    /// Starts an elevated copy of MicMixer that takes over once this process exits.
    /// Returns false when the UAC prompt was declined; this process should then keep running.
    /// </summary>
    public static bool TryStartElevatedCopy()
    {
        // Only a development build, which has no launcher, starts itself directly.
        string target = File.Exists(LauncherPath) ? LauncherPath : Environment.ProcessPath!;
        var startInfo = new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(ProcessIdArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        try
        {
            _ = Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits up to 30 seconds for the MicMixer named by <see cref="ProcessIdArgument"/>
    /// in <paramref name="args"/> to exit. Returns at once when there is none.
    /// </summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        int index = Array.FindIndex(args, value => string.Equals(value, ProcessIdArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int processId))
        {
            return;
        }

        try
        {
            using Process previous = Process.GetProcessById(processId);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Opens a process with the one right Windows grants on processes of every
    /// integrity level. The handle is invalid for protected system processes.
    /// </summary>
    public static SafeProcessHandle OpenForQuery(int processId) =>
        OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

    /// <summary>
    /// True when <paramref name="process"/> runs as administrator. False when it does
    /// not, or when Windows does not let this process read its token.
    /// </summary>
    public static bool IsProcessElevated(SafeProcessHandle process) =>
        TryReadTokenValue(process, TokenElevationClass, out int elevated) && elevated != 0;

    private static bool ReadHasLimitedAdministratorToken()
    {
        using SafeProcessHandle self = OpenForQuery(Environment.ProcessId);
        return TryReadTokenValue(self, TokenElevationTypeClass, out int elevationType)
            && elevationType == TokenElevationTypeLimited;
    }

    private static bool TryReadTokenValue(SafeProcessHandle process, int informationClass, out int value)
    {
        value = 0;
        if (process.IsInvalid || !OpenProcessToken(process, TOKEN_QUERY, out SafeAccessTokenHandle token))
        {
            return false;
        }

        using (token)
        {
            return GetTokenInformation(token, informationClass, out value, sizeof(int), out _);
        }
    }

    /// <summary>
    /// True when nobody but administrators, SYSTEM and TrustedInstaller can replace
    /// <paramref name="filePath"/>: not through the file itself, not through its
    /// folder, not by recreating that folder once it is gone, and not by moving it or
    /// a folder above it out of the way. False for a file on anything but a local
    /// fixed drive, reached through a link, or with any of this unreadable.
    /// </summary>
    public static bool IsProtectedFromNonAdministrators(string filePath)
    {
        try
        {
            var file = new FileInfo(filePath);
            DirectoryInfo? folder = file.Directory;
            if (!file.Exists || folder is null || !IsOnLocalFixedDrive(file.FullName))
            {
                return false;
            }

            // Every entry on the way is checked as it stands on disk. A junction or a
            // symbolic link would have the permissions read from its target while the
            // folders above that target went unchecked.
            if (!IsProtectedEntry(file, ModifyingRights) || !IsProtectedEntry(folder, ModifyingRights))
            {
                return false;
            }

            // The parent must not let anyone else create entries either, or a folder
            // deleted with the startup task still pointing into it could be recreated
            // by anyone.
            DirectoryInfo? parent = folder.Parent;
            if (parent is not null && !IsProtectedEntry(parent, ModifyingRights))
            {
                return false;
            }

            for (DirectoryInfo? ancestor = parent?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (!IsProtectedEntry(ancestor, AncestorTakeoverRights))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsProtectedEntry(FileSystemInfo entry, FileSystemRights rights)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        FileSystemSecurity security = entry switch
        {
            FileInfo file => file.GetAccessControl(),
            DirectoryInfo directory => directory.GetAccessControl(),
            _ => throw new InvalidOperationException()
        };

        return IsProtected(security, rights);
    }

    // On a network share "Administrators" means the server's administrators, and the
    // permissions that count are the server's.
    private static bool IsOnLocalFixedDrive(string fullPath)
    {
        string? root = Path.GetPathRoot(fullPath);
        return root is { Length: > 0 }
            && !root.StartsWith(@"\\", StringComparison.Ordinal)
            && new DriveInfo(root).DriveType == DriveType.Fixed;
    }

    /// <summary>
    /// True when the owner is trusted and no untrusted principal is allowed any of
    /// <paramref name="rights"/> on this entry. Entries that only pass rights on to
    /// children are ignored, since they do not apply here.
    /// </summary>
    internal static bool IsProtected(FileSystemSecurity security, FileSystemRights rights)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
        {
            return false;
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow
                || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                || (rule.FileSystemRights & (rights | GenericWriteOrAll)) == 0)
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid || !IsTrusted(sid))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Grants administrators SYNCHRONIZE on this process, so an installer running as another
    /// account can wait for it to exit. Errors are ignored; they only cost that wait.
    /// </summary>
    public static void LetAdministratorsWaitForExit()
    {
        IntPtr process = GetCurrentProcess();
        if (GetSecurityInfo(process, KernelObject, DaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, out _, IntPtr.Zero, out IntPtr descriptor) != 0)
        {
            return;
        }

        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            RawAcl? dacl = new RawSecurityDescriptor(bytes, 0).DiscretionaryAcl;
            if (dacl is null)
            {
                return;
            }

            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            dacl.InsertAce(dacl.Count, new CommonAce(
                AceFlags.None, AceQualifier.AccessAllowed, ProcessSynchronize, administrators, false, null));
            var daclBytes = new byte[dacl.BinaryLength];
            dacl.GetBinaryForm(daclBytes, 0);
            _ = SetSecurityInfo(process, KernelObject, DaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, daclBytes, IntPtr.Zero);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static bool IsTrusted(SecurityIdentifier sid) => TrustedPrincipals.Contains(sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref int buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, out int information, int informationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(
        IntPtr handle, int objectType, int securityInfo,
        IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(
        IntPtr handle, int objectType, int securityInfo,
        IntPtr owner, IntPtr group, byte[] dacl, IntPtr sacl);

    [DllImport("advapi32.dll")]
    private static extern int GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
