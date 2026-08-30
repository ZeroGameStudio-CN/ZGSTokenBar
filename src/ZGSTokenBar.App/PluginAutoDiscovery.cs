using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.App;

internal static class PluginAutoDiscovery
{
    private const string Capability = "local-credentials";

    public static async Task<bool> ApplyAsync(
        AppSettingsStore store,
        AppSettings settings,
        IReadOnlyList<IZgsPlugin> plugins,
        CancellationToken cancellationToken = default)
    {
        var original = EnablementState.Capture(settings);
        var changed = false;
        try
        {
            foreach (var plugin in plugins
                         .Where(plugin => plugin.Manifest.Capabilities.Contains(
                             Capability,
                             StringComparer.Ordinal))
                         .OrderBy(plugin => plugin.Manifest.Order)
                         .ThenBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pluginId = plugin.Manifest.Id;
                if (settings.HasExplicitPluginEnablement(pluginId)
                    || settings.WasPluginAutoEnabled(pluginId)
                    || plugin is not ILocalCredentialProbe probe)
                {
                    continue;
                }

                var started = false;
                try
                {
                    var pluginRoot = Path.Combine(store.DataDirectory, "plugin-data", pluginId);
                    Directory.CreateDirectory(pluginRoot);
                    await plugin.StartAsync(
                        new PluginStartContext("desktop", pluginRoot, DateTimeOffset.UtcNow),
                        cancellationToken);
                    started = true;
                    if (!await probe.HasLocalCredentialsAsync(cancellationToken)) continue;

                    settings.SetPluginAutoEnabled(pluginId);
                    changed = true;
                    foreach (var dependent in plugins.Where(candidate =>
                                 candidate.Manifest.Requires.Contains(pluginId, StringComparer.Ordinal)
                                 && !settings.HasExplicitPluginEnablement(candidate.Manifest.Id)))
                    {
                        settings.SetPluginAutoEnabled(dependent.Manifest.Id);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Local discovery is best-effort; normal Provider health remains authoritative.
                }
                finally
                {
                    if (started)
                    {
                        try { await plugin.StopAsync(CancellationToken.None); }
                        catch { }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            original.Restore(settings);
            throw;
        }

        if (!changed) return false;
        try
        {
            store.Save(settings);
            return true;
        }
        catch
        {
            original.Restore(settings);
            return false;
        }
    }

    private sealed record EnablementState(
        Dictionary<string, bool> PluginEnabled,
        string[] PluginEnablementDecisions,
        string[] AutoEnabledPlugins,
        string[] EnabledProviders,
        bool EnableRadar,
        bool EnableRadarAlerts,
        bool EnableAiGatewayBalance)
    {
        public static EnablementState Capture(AppSettings settings) => new(
            new Dictionary<string, bool>(
                settings.PluginEnabled ?? new Dictionary<string, bool>(),
                StringComparer.Ordinal),
            [.. settings.PluginEnablementDecisions ?? []],
            [.. settings.AutoEnabledPlugins ?? []],
            [.. settings.EnabledProviders ?? []],
            settings.EnableRadar,
            settings.EnableRadarAlerts,
            settings.EnableAiGatewayBalance);

        public void Restore(AppSettings settings)
        {
            settings.PluginEnabled = new Dictionary<string, bool>(PluginEnabled, StringComparer.Ordinal);
            settings.PluginEnablementDecisions = [.. PluginEnablementDecisions];
            settings.AutoEnabledPlugins = [.. AutoEnabledPlugins];
            settings.EnabledProviders = [.. EnabledProviders];
            settings.EnableRadar = EnableRadar;
            settings.EnableRadarAlerts = EnableRadarAlerts;
            settings.EnableAiGatewayBalance = EnableAiGatewayBalance;
        }
    }
}
