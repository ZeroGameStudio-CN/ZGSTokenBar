using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ZGSTokenBar.App;

internal static class HostJobLifetimeIsolation
{
    internal const uint KillOnJobClose = 0x00002000;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint ProcessCreateProcess = 0x00000080;
    private const uint ExtendedStartupInformationPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const nuint ParentProcessAttribute = 0x00020000;
    private const string RelaunchAttemptVariable = "ZGSTOKENBAR_JOB_BREAKAWAY_ATTEMPTED";

    internal static bool TryRelaunchOutsideTerminatingJob(
        bool hasIsolatedDataRoot,
        IReadOnlyList<string> arguments)
    {
        try
        {
            var alreadyAttempted = string.Equals(
                Environment.GetEnvironmentVariable(RelaunchAttemptVariable),
                "1",
                StringComparison.Ordinal);
            if (!TryReadCurrentJob(out var isInJob, out var limitFlags)
                || !ShouldRelaunch(hasIsolatedDataRoot, alreadyAttempted, isInJob, limitFlags))
            {
                return false;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath)) return false;
            var commandInterpreter = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");

            var previousAttempt = Environment.GetEnvironmentVariable(RelaunchAttemptVariable);
            Environment.SetEnvironmentVariable(RelaunchAttemptVariable, "1");
            try
            {
                if (!TryCreateWithDesktopShellParent(
                        commandInterpreter,
                        BuildBrokerCommandLine(
                            commandInterpreter,
                        BuildCommandLine(executablePath, arguments)),
                        Environment.CurrentDirectory,
                        out var errorCode))
                {
                    Trace.TraceWarning(
                        "ZGSTokenBar could not detach from its terminating host job (Win32 {0}).",
                        errorCode);
                    return false;
                }
                return true;
            }
            finally
            {
                Environment.SetEnvironmentVariable(RelaunchAttemptVariable, previousAttempt);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "ZGSTokenBar host-job lifetime isolation failed ({0}).",
                exception.GetType().Name);
            return false;
        }
    }

    internal static bool ShouldRelaunch(
        bool hasIsolatedDataRoot,
        bool relaunchAlreadyAttempted,
        bool isInJob,
        uint limitFlags) =>
        !hasIsolatedDataRoot
        && !relaunchAlreadyAttempted
        && isInJob
        && (limitFlags & KillOnJobClose) != 0;

    internal static string BuildCommandLine(
        string executablePath,
        IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { QuoteArgument(executablePath) }.Concat(arguments.Select(QuoteArgument)));

    internal static string BuildBrokerCommandLine(
        string commandInterpreter,
        string applicationCommandLine) =>
        $"{QuoteArgument(commandInterpreter)} /d /s /v:off /c \"{applicationCommandLine}\"";

    internal static bool TryReadCurrentJob(out bool isInJob, out uint limitFlags)
    {
        limitFlags = 0;
        if (!IsProcessInJob(GetCurrentProcess(), nint.Zero, out isInJob)) return false;
        if (!isInJob) return true;

        var limits = new JobObjectExtendedLimitInformation();
        if (!QueryInformationJobObject(
                nint.Zero,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                Marshal.SizeOf<JobObjectExtendedLimitInformation>(),
                nint.Zero))
        {
            return false;
        }

        limitFlags = limits.BasicLimitInformation.LimitFlags;
        return true;
    }

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static bool TryCreateWithDesktopShellParent(
        string executablePath,
        string commandLine,
        string currentDirectory,
        out int errorCode)
    {
        errorCode = 0;
        using var currentProcess = Process.GetCurrentProcess();
        foreach (var candidate in Process.GetProcessesByName("explorer"))
        {
            using (candidate)
            {
                try
                {
                    if (candidate.SessionId != currentProcess.SessionId) continue;
                    var parentProcess = OpenProcess(
                        ProcessCreateProcess,
                        false,
                        (uint)candidate.Id);
                    if (parentProcess == nint.Zero)
                    {
                        errorCode = Marshal.GetLastWin32Error();
                        continue;
                    }

                    try
                    {
                        if (TryCreateWithParent(
                                parentProcess,
                                executablePath,
                                commandLine,
                                currentDirectory,
                                out errorCode))
                        {
                            return true;
                        }
                    }
                    finally
                    {
                        CloseHandle(parentProcess);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Explorer can exit or restart while its process metadata is read.
                }
            }
        }

        return false;
    }

    private static bool TryCreateWithParent(
        nint parentProcess,
        string executablePath,
        string commandLine,
        string currentDirectory,
        out int errorCode)
    {
        errorCode = 0;
        nuint attributeListSize = 0;
        _ = InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref attributeListSize);
        if (attributeListSize == 0)
        {
            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        var attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
        var parentValue = nint.Zero;
        var environment = nint.Zero;
        var attributeListInitialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }
            attributeListInitialized = true;

            parentValue = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(parentValue, parentProcess);
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ParentProcessAttribute,
                    parentValue,
                    (nuint)nint.Size,
                    nint.Zero,
                    nint.Zero))
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            environment = CreateEnvironmentBlock();

            var startup = new StartupInformationEx
            {
                StartupInformation = new StartupInformation
                {
                    Size = Marshal.SizeOf<StartupInformationEx>(),
                },
                AttributeList = attributeList,
            };
            if (!CreateProcess(
                    executablePath,
                    new StringBuilder(commandLine),
                    nint.Zero,
                    nint.Zero,
                    false,
                    ExtendedStartupInformationPresent | CreateUnicodeEnvironment | CreateNoWindow,
                    environment,
                    currentDirectory,
                    ref startup,
                    out var process))
            {
                errorCode = Marshal.GetLastWin32Error();
                return false;
            }

            CloseHandle(process.ThreadHandle);
            CloseHandle(process.ProcessHandle);
            return true;
        }
        finally
        {
            if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
            if (environment != nint.Zero) Marshal.FreeHGlobal(environment);
            if (parentValue != nint.Zero) Marshal.FreeHGlobal(parentValue);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static nint CreateEnvironmentBlock()
    {
        var entries = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .Select(entry => $"{entry.Key}={entry.Value}")
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase);
        return Marshal.StringToHGlobalUni(string.Join('\0', entries) + "\0\0");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx
    {
        public StartupInformation StartupInformation;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint ProcessHandle;
        public nint ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        nint processHandle,
        nint jobHandle,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        nint jobHandle,
        uint informationClass,
        ref JobObjectExtendedLimitInformation information,
        int informationLength,
        nint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        uint flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInformationEx startupInformation,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
