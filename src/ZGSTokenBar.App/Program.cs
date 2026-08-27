using System.Threading;

namespace ZGSTokenBar.App;

internal static class Program
{
    internal const string MutexName = @"Local\ZGSTokenBar.App.SingleInstance";
    private const string ActivationEventName = @"Local\ZGSTokenBar.App.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        if (WatchdogManager.IsWatchdogRequest(args))
        {
            WatchdogManager.Run();
            return;
        }

        var openSettingsOnStart = args.Any(value =>
            string.Equals(value, "--settings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "settings", StringComparison.OrdinalIgnoreCase));
        using var activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivationEventName);
        using var mutex = new Mutex(true, MutexName, out var firstInstance);
        if (!firstInstance)
        {
            activationEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        var context = new QuotaApplicationContext(activationEvent, openSettingsOnStart);
        Application.Run(context);
    }
}
