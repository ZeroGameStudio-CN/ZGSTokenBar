using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ZGSTokenBar.App;

internal static class HostJobLifetimeFixture
{
    internal static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 1 && args[0] == "--host-job-regression")
        {
            Test();
            Console.WriteLine("PASS host job exit survival and diagnostics");
            return true;
        }
        if (args.Length != 2 || !args[0].StartsWith("--job-fixture-", StringComparison.Ordinal)) return false;
        var directory = args[1];
        if (args[0] == "--job-fixture-host")
        {
            // The nearest job deliberately has no kill flag; the outer one does.
            using var outer = CreateJob(HostJobLifetimeIsolation.KillOnJobClose);
            using var inner = CreateJob(0);
            Require(HostJobLifetimeIsolation.TryReadCurrentJob(out var inJob, out var flags)
                && inJob && flags == 0, "fixture must expose a non-terminating nearest job");
            using var sentinel = Start("--job-fixture-sentinel", directory);
            using var launcher = Start("--job-fixture-child", directory);
            WaitForFile(Path.Combine(directory, "child.json"));
            WaitForFile(Path.Combine(directory, "sentinel.ready"));
            File.WriteAllText(Path.Combine(directory, "host.json"), JsonSerializer.Serialize(new
            {
                sentinelId = sentinel.Id,
                nearestFlags = flags,
            }));
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return true;
        }
        if (args[0] == "--job-fixture-child")
        {
            if (HostJobLifetimeIsolation.TryRelaunchOutsideTerminatingJob(false, args,
                    diagnostic => StartupIsolationLog.Write(directory, diagnostic))) return true;
            var readable = HostJobLifetimeIsolation.TryReadCurrentJob(out var inJob, out var flags);
            File.WriteAllText(Path.Combine(directory, "child.json"), JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId,
                readable,
                inJob,
                flags,
                markerCleared = Environment.GetEnvironmentVariable("ZGSTOKENBAR_JOB_BREAKAWAY_ATTEMPTED") is null,
            }));
            WaitForFile(Path.Combine(directory, "release"));
            File.WriteAllText(Path.Combine(directory, "survived"), "ok");
            return true;
        }
        if (args[0] == "--job-fixture-sentinel")
        {
            File.WriteAllText(Path.Combine(directory, "sentinel.ready"), "ready");
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return true;
        }
        return false;
    }

    internal static void Test()
    {
        Require(!HostJobLifetimeIsolation.RunRelaunchAttempt(_ => false, TimeSpan.Zero),
            "failed process creation must keep the original instance");
        Require(!HostJobLifetimeIsolation.RunRelaunchAttempt(_ => true, TimeSpan.FromMilliseconds(20)),
            "a broker without a verified replacement must not end the original instance");
        var directory = Path.Combine(Path.GetTempPath(), "zgstokenbar-job-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? host = null;
        Process? child = null;
        Process? sentinel = null;
        try
        {
            var logDirectory = Path.Combine(directory, "log-test");
            var diagnostic = new HostJobIsolationDiagnostic("continuing-host-bound", true, true, 0, 5);
            StartupIsolationLog.Write(logDirectory, diagnostic);
            var logPath = Path.Combine(logDirectory, "startup-isolation.jsonl");
            using (var line = JsonDocument.Parse(File.ReadAllText(logPath)))
            {
                Require(line.RootElement.GetProperty("Outcome").GetString() == diagnostic.Outcome,
                    "startup failures must remain observable");
                Require(line.RootElement.EnumerateObject().Count() == 8, "diagnostics contain only bounded technical fields");
            }
            File.WriteAllText(logPath, new string(' ', StartupIsolationLog.MaximumBytes));
            StartupIsolationLog.Write(logDirectory, diagnostic);
            Require(File.Exists(logPath + ".previous") && new FileInfo(logPath).Length < 1024,
                "diagnostics rotate instead of growing indefinitely");
            StartupIsolationLog.Write(logPath, diagnostic); // A file is not a writable directory.

            var previousMarker = Environment.GetEnvironmentVariable("ZGSTOKENBAR_JOB_BREAKAWAY_ATTEMPTED");
            try
            {
                Environment.SetEnvironmentVariable("ZGSTOKENBAR_JOB_BREAKAWAY_ATTEMPTED", Guid.NewGuid().ToString("N"));
                Require(HostJobLifetimeIsolation.TryRelaunchOutsideTerminatingJob(false, []),
                    "an orphan replacement must exit instead of starting another instance");
                Require(Environment.GetEnvironmentVariable("ZGSTOKENBAR_JOB_BREAKAWAY_ATTEMPTED") is null,
                    "one-shot handshake state must not leak into descendants");
                var reports = 0;
                Require(!HostJobLifetimeIsolation.TryRelaunchOutsideTerminatingJob(true, [], _ => reports++),
                    "custom-root instances retain launcher ownership");
                Require(reports == 0, "isolated acceptance must not write production diagnostics");
            }
            finally
            {
                Environment.SetEnvironmentVariable("ZGSTOKENBAR_JOB_BREAKAWAY_ATTEMPTED", previousMarker);
            }

            using var current = Process.GetCurrentProcess();
            var hasShell = Process.GetProcessesByName("explorer").Any(process =>
            {
                using (process)
                {
                    try { return process.SessionId == current.SessionId; }
                    catch (InvalidOperationException) { return false; }
                }
            });
            if (!hasShell)
            {
                Console.WriteLine("SKIP nested host survival: no same-session Explorer on this runner");
                return;
            }

            host = Start("--job-fixture-host", directory);
            WaitForFile(Path.Combine(directory, "host.json"));
            using var hostData = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "host.json")));
            using var childData = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "child.json")));
            child = Process.GetProcessById(childData.RootElement.GetProperty("processId").GetInt32());
            sentinel = Process.GetProcessById(hostData.RootElement.GetProperty("sentinelId").GetInt32());
            Require(childData.RootElement.GetProperty("readable").GetBoolean()
                && !childData.RootElement.GetProperty("inJob").GetBoolean()
                && childData.RootElement.GetProperty("flags").GetUInt32() == 0,
                "the actual replacement must leave every inherited job");
            Require(childData.RootElement.GetProperty("markerCleared").GetBoolean(), "replacement consumes its marker");
            host.Kill(); // Only the fixture host, never Codex or the user's application.
            Require(host.WaitForExit(5000), "fixture host exits");
            Require(sentinel.WaitForExit(5000), "closing the fixture job really kills its bound child");
            Require(!child.HasExited, "detached replacement survives the same host exit");
            File.WriteAllText(Path.Combine(directory, "release"), "release");
            Require(child.WaitForExit(5000) && File.Exists(Path.Combine(directory, "survived")),
                "replacement continues executing after host destruction");
        }
        finally
        {
            foreach (var process in new[] { host, child, sentinel })
            {
                if (process is null) continue;
                using (process)
                {
                    if (!process.HasExited) process.Kill();
                    process.WaitForExit(5000);
                }
            }
            // Only this fixture's unique temporary directory contains generated evidence.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Process Start(string mode, string directory)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(directory);
        start.Environment.Remove("ZGSTOKENBAR_JOB_BREAKAWAY_ATTEMPTED");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start job fixture");
    }

    private static void WaitForFile(string path) => Require(
        SpinWait.SpinUntil(() => File.Exists(path) && new FileInfo(path).Length > 0, TimeSpan.FromSeconds(20)),
        "fixture timed out: " + Path.GetFileName(path));

    private static SafeFileHandle CreateJob(uint flags)
    {
        var job = CreateJobObject(nint.Zero, null);
        try
        {
            Require(!job.IsInvalid, "create fixture job");
            var limits = new JobLimits { Basic = new BasicLimits { Flags = flags } };
            Require(SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobLimits>()), "set fixture job limits");
            Require(AssignProcessToJobObject(job, new nint(-1)), "assign fixture host to nested job");
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
