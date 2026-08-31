using System.Threading;
using ZGSTokenBar.Builtins;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal static class Program
{
    internal const string MutexName = @"Local\ZGSTokenBar.App.SingleInstance";
    internal const string DataDirectoryArgument = "--data-directory";
    private const string ActivationEventName = @"Local\ZGSTokenBar.App.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        var dataDirectory = ResolveDataDirectoryOverride(args);
        var allowGlobalStartupRegistration = dataDirectory is null;

        var openSettingsOnStart = args.Any(value =>
            string.Equals(value, "--settings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "settings", StringComparison.OrdinalIgnoreCase));
        var store = new AppSettingsStore(dataDirectory);
        var settings = store.Load();
        if (allowGlobalStartupRegistration)
        {
            StartupManager.ReconcileRegistration(
                Environment.ProcessPath ?? Application.ExecutablePath,
                settings.OpenAtLogin);
        }
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

        var bundledPluginInstallFailed = BundledPluginInstaller
            .EnsureInstalled(store.DataDirectory)
            .Any(status => !status.Valid);
        ApplyProviderAutoDiscovery(store, settings);

        ApplicationConfiguration.Initialize();
        var context = new QuotaApplicationContext(
            activationEvent,
            openSettingsOnStart,
            store,
            allowGlobalStartupRegistration,
            bundledPluginInstallFailed);
        Application.Run(context);
    }

    private static void ApplyProviderAutoDiscovery(AppSettingsStore store, AppSettings settings)
    {
        var plugins = GeneratedBuiltinPluginRegistry.Create().ToList();
        var processPlugins = new PluginPackageManager(store.DataDirectory).LoadProcessPlugins(
            new PluginCredentialStore());
        var selection = PluginCatalogComposer.SelectOptional(plugins, processPlugins);
        plugins.AddRange(selection.Accepted);
        try
        {
            PluginAutoDiscovery.ApplyAsync(store, settings, plugins)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            foreach (var plugin in plugins.Concat(selection.Rejected))
            {
                try
                {
                    plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // Optional discovery cleanup must not prevent the desktop UI from starting.
                }
            }
        }
    }

    internal static string? ResolveDataDirectoryArgument(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(
                    args[index],
                    DataDirectoryArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (index + 1 >= args.Count
                || string.IsNullOrWhiteSpace(args[index + 1])
                || !Path.IsPathFullyQualified(args[index + 1]))
            {
                throw new ArgumentException($"{DataDirectoryArgument} requires a fully qualified path.");
            }
            return Path.GetFullPath(args[index + 1]);
        }
        return null;
    }

    internal static string? ResolveDataDirectoryOverride(IReadOnlyList<string> args)
    {
        var argument = ResolveDataDirectoryArgument(args);
        if (argument is not null) return argument;
        var configured = Environment.GetEnvironmentVariable(
            AppSettingsStore.DataDirectoryEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(configured)
            && Path.IsPathFullyQualified(configured)
                ? configured
                : null;
    }
}
