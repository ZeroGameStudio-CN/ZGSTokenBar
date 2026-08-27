using System.Diagnostics;
using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal static class WatchdogManager
{
    internal const string Argument = "--watchdog";
    internal const string MutexName = @"Local\ZGSTokenBar.App.Watchdog";
    internal const string StopEventName = @"Local\ZGSTokenBar.App.Watchdog.Stop";
    internal const int PollMilliseconds = 2_000;

    public static bool IsWatchdogRequest(IEnumerable<string> args) =>
        args.Any(value => string.Equals(value, Argument, StringComparison.OrdinalIgnoreCase));

    public static void Apply(bool enabled)
    {
        if (enabled)
        {
            try
            {
                using var stopEvent = OpenStopEvent();
                stopEvent.Reset();
            }
            catch
            {
                // The next health check can retry without interrupting the main app.
            }
            EnsureRunning();
            return;
        }

        Stop();
    }

    public static void Stop()
    {
        try
        {
            using var stopEvent = OpenStopEvent();
            stopEvent.Set();
        }
        catch
        {
            // Stop signaling is best-effort and must not block settings changes or session shutdown.
        }
    }

    public static void EnsureRunning()
    {
        try
        {
            if (IsWatchdogRunning()) return;
            var executablePath = CurrentExecutablePath();
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = Argument,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // Keep-running is best-effort and must never prevent the main app from running.
        }
    }

    public static void Run()
    {
        using var mutex = new Mutex(true, MutexName, out var firstInstance);
        if (!firstInstance) return;

        using var stopEvent = OpenStopEvent();
        var store = new AppSettingsStore();
        while (true)
        {
            if (stopEvent.WaitOne(0)) return;

            if (!IsApplicationRunning())
            {
                var keepRunning = KeepRunningEnabled(store);
                if (!ShouldStartApplication(keepRunning, applicationRunning: false)) return;
                StartApplication();
            }

            if (stopEvent.WaitOne(PollMilliseconds)) return;
        }
    }

    internal static bool ShouldStartApplication(bool keepRunning, bool applicationRunning) =>
        keepRunning && !applicationRunning;

    private static EventWaitHandle OpenStopEvent() =>
        new(false, EventResetMode.ManualReset, StopEventName);

    private static bool IsWatchdogRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsApplicationRunning()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(Program.MutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool KeepRunningEnabled(AppSettingsStore store)
    {
        try
        {
            return store.Load().KeepRunning;
        }
        catch
        {
            return false;
        }
    }

    private static void StartApplication()
    {
        try
        {
            var executablePath = CurrentExecutablePath();
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
            });
        }
        catch
        {
            // Polling provides bounded retries for transient launch failures.
        }
    }

    private static string CurrentExecutablePath() =>
        Environment.ProcessPath ?? Application.ExecutablePath;
}
