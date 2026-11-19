using System.Text;
using System.Text.Json;
using ZGSTokenBar.Core;
using ZGSTokenBar.Plugin.Router;

namespace ZGSTokenBar.App;

/// <summary>
/// Last-resort error sink. UI-thread and background exceptions are written to
/// <c>%LOCALAPPDATA%\ZGSTokenBar\diagnostics\errors.log</c> (bounded, newest last) instead of the
/// stock .NET "unhandled exception" dialog, which would otherwise block or kill the taskbar bar.
/// </summary>
internal static class AppDiagnostics
{
    private const long MaximumLogBytes = 512 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Path, string Component, string Operation, string Target, string Code), DateTimeOffset> MonitorFailures = [];
    private static int _uiFaults;

    internal static string LogPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZGSTokenBar",
        "diagnostics",
        "errors.log");

    internal static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) =>
        {
            Record("ui-thread", args.Exception);
            // A fault storm (e.g. a paint loop throwing every frame) must not spin forever.
            if (Interlocked.Increment(ref _uiFaults) > 200) Environment.Exit(3);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record(args.IsTerminating ? "fatal" : "background", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record("unobserved-task", args.Exception);
            args.SetObserved();
        };
    }

    internal static void Record(string source, Exception? error)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(source).Append("] ")
                .Append(SecretRedactor.Redact(error?.ToString() ?? "(no exception object)"))
                .AppendLine()
                .ToString();
            lock (Gate)
                AppendBounded(LogPath, line);

            System.Diagnostics.Trace.WriteLine("ZGSTokenBar " + source + ": " + SecretRedactor.Redact(error?.ToString()));
        }
        catch
        {
            // Diagnostics must never throw from an exception handler.
        }
    }

    internal static string MonitorFailureCode(Exception error) => error switch
    {
        OperationCanceledException => "timeout",
        RouterMonitorHttpException { IsLocalDataUnavailable: true } => "queue_unavailable",
        RouterMonitorHttpException => "http_error",
        RouterCompatibilityException => "api_incompatible",
        HttpRequestException => "connection_failed",
        UnauthorizedAccessException => "access_denied",
        System.ComponentModel.Win32Exception => "process_start_failed",
        JsonException or InvalidDataException => "invalid_response",
        _ => "read_failed",
    };

    internal static void RecordMonitorFailure(string component, string operation, string target, string code, int? status = null)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(LogPath)!, "monitor-reads.jsonl");
            var now = DateTimeOffset.UtcNow;
            var key = (path, component, operation, target, code);
            lock (Gate)
            {
                if (MonitorFailures.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromMinutes(1)) return;
                if (MonitorFailures.Count >= 256) MonitorFailures.Clear();
                AppendBounded(path, JsonSerializer.Serialize(new { at = now, component, operation, target, code, status }) + Environment.NewLine);
                MonitorFailures[key] = now;
            }
        }
        catch
        {
            // Diagnostics are best effort and never own the observation.
        }
    }

    private static void AppendBounded(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaximumLogBytes) File.Move(path, path + ".previous", true);
        File.AppendAllText(path, line, Utf8);
    }
}
