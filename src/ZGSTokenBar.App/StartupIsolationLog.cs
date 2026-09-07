using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.App;

internal static class StartupIsolationLog
{
    internal const int MaximumBytes = 64 * 1024;

    internal static void WriteDefault(HostJobIsolationDiagnostic diagnostic) => Write(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZGSTokenBar", "diagnostics"), diagnostic);

    internal static void Write(string directory, HostJobIsolationDiagnostic diagnostic)
    {
        try
        {
            var path = Path.Combine(Path.GetFullPath(directory), "startup-isolation.jsonl");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            using var mutex = new Mutex(false, @"Local\ZGSTokenBar.IsolationLog." + identity);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(250)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                // Deliberately omit command lines, environment, accounts and exception messages.
                var line = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    processId = Environment.ProcessId,
                    moduleId = typeof(StartupIsolationLog).Module.ModuleVersionId,
                    diagnostic.Outcome,
                    diagnostic.QuerySucceeded,
                    diagnostic.IsInJob,
                    diagnostic.LimitFlags,
                    diagnostic.ErrorCode,
                });
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
        catch
        {
            // Read-only or unavailable diagnostics storage must not prevent startup.
        }
    }
}
