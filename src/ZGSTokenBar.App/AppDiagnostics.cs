using System.Text;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

/// <summary>
/// Last-resort error sink. UI-thread and background exceptions are written to
/// <c>%LOCALAPPDATA%\ZGSTokenBar\diagnostics\errors.log</c> (bounded, newest last) instead of the
/// stock .NET "unhandled exception" dialog, which would otherwise block or kill the taskbar bar.
/// </summary>
internal static class AppDiagnostics
{
    private const long MaximumLogBytes = 512 * 1024;
    private static readonly object Gate = new();
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
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaximumLogBytes)
                {
                    File.Move(LogPath, LogPath + ".previous", true);
                }

                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }

            System.Diagnostics.Trace.WriteLine("ZGSTokenBar " + source + ": " + SecretRedactor.Redact(error?.ToString()));
        }
        catch
        {
            // Diagnostics must never throw from an exception handler.
        }
    }
}
