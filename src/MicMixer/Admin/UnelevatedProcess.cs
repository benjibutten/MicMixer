using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MicMixer.Admin;

/// <summary>
/// Starts a program as the signed-in user without administrator rights, from a
/// MicMixer that has them. It borrows the desktop shell's token, which is what the
/// program would get if the user started it from Explorer.
/// </summary>
internal static class UnelevatedProcess
{
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const short SW_HIDE = 0;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int MaxCommandLineLength = 1024;

    /// <summary>
    /// Runs <paramref name="fileName"/> to completion, passing every line it writes to
    /// <paramref name="standardOutput"/> or <paramref name="standardError"/>, and returns
    /// its exit code. Cancelling ends the program and everything it started, then throws
    /// <see cref="OperationCanceledException"/>. Throws <see cref="InvalidOperationException"/>
    /// when the desktop shell is not running, and <see cref="Win32Exception"/> when Windows
    /// refuses to start the program.
    /// </summary>
    public static async Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        Action<string> standardOutput,
        Action<string> standardError,
        CancellationToken cancellationToken)
    {
        string commandLine = BuildCommandLine(fileName, arguments);
        if (commandLine.Length >= MaxCommandLineLength)
        {
            throw new InvalidOperationException("The command is too long to start without administrator rights.");
        }

        using SafeAccessTokenHandle token = DuplicateShellToken();
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using SafeFileHandle job = CreateKillOnCloseJob();

        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW,
            wShowWindow = SW_HIDE,
            hStdInput = input.ClientSafePipeHandle.DangerousGetHandle(),
            hStdOutput = output.ClientSafePipeHandle.DangerousGetHandle(),
            hStdError = error.ClientSafePipeHandle.DangerousGetHandle()
        };

        IntPtr environmentBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(environment));
        PROCESS_INFORMATION processInfo;
        try
        {
            if (!CreateProcessWithTokenW(
                    token,
                    0,
                    fileName,
                    new StringBuilder(commandLine, MaxCommandLineLength),
                    CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    environmentBlock,
                    Path.GetDirectoryName(fileName),
                    ref startupInfo,
                    out processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(environmentBlock);
        }

        using var process = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
        using (new SafeFileHandle(processInfo.hThread, ownsHandle: true))
        {
            // In the job before it runs a single instruction, so nothing it starts can
            // outlive a cancelled download.
            if (!AssignProcessToJobObject(job, process))
            {
                TerminateProcess(process, 1);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _ = ResumeThread(processInfo.hThread);
        }

        // The child holds its own copies now; ours would keep the pipes from ever ending.
        input.DisposeLocalCopyOfClientHandle();
        output.DisposeLocalCopyOfClientHandle();
        error.DisposeLocalCopyOfClientHandle();
        input.Dispose();

        Task readOutput = ReadLinesAsync(output, standardOutput);
        Task readError = ReadLinesAsync(error, standardError);

        using (cancellationToken.Register(() => TerminateJobObject(job, 1)))
        {
            await WaitForExitAsync(process);
            await Task.WhenAll(readOutput, readError);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return GetExitCodeProcess(process, out int exitCode) ? exitCode : -1;
    }

    private static SafeAccessTokenHandle DuplicateShellToken()
    {
        IntPtr shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("The Windows desktop is not running, so MicMixer cannot start this without administrator rights.");
        }

        _ = GetWindowThreadProcessId(shellWindow, out int shellProcessId);
        using SafeProcessHandle shell = Elevation.OpenForQuery(shellProcessId);
        if (shell.IsInvalid || !OpenProcessToken(shell, TOKEN_DUPLICATE, out SafeAccessTokenHandle shellToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (shellToken)
        {
            const uint access = TOKEN_QUERY | TOKEN_DUPLICATE | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!DuplicateTokenEx(shellToken, access, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out SafeAccessTokenHandle primary))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return primary;
        }
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            int errorCode = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(errorCode);
        }

        return job;
    }

    private static Task WaitForExitAsync(SafeProcessHandle process)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitable = new ProcessWaitHandle(process);
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            waitable, (_, _) => exited.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);

        return exited.Task.ContinueWith(_ =>
        {
            registration.Unregister(null);
            waitable.Dispose();
        }, TaskScheduler.Default);
    }

    private static async Task ReadLinesAsync(Stream stream, Action<string> onLine)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            onLine(line);
        }
    }

    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach ((string name, string value) in overrides)
        {
            variables[name] = value;
        }

        var block = new StringBuilder();
        foreach ((string name, string value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        // StringToHGlobalUni adds the terminator that ends the block.
        return block.ToString();
    }

    /// <summary>Quotes each argument the way the Microsoft C runtime splits them back apart.</summary>
    internal static string BuildCommandLine(string fileName, IEnumerable<string> arguments)
    {
        var commandLine = new StringBuilder();
        AppendArgument(commandLine, fileName);
        foreach (string argument in arguments)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, argument);
        }

        return commandLine.ToString();
    }

    private static void AppendArgument(StringBuilder commandLine, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            commandLine.Append(argument);
            return;
        }

        commandLine.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless a quote follows them, when each one must be doubled.
            commandLine.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            commandLine.Append(c);
        }

        commandLine.Append('\\', backslashes * 2).Append('"');
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken, uint desiredAccess, IntPtr tokenAttributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle token, int logonFlags, string applicationName, StringBuilder commandLine,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int informationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION information, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out int exitCode);
}
