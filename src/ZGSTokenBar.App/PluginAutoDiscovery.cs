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
        if (store.SettingsReadPending) return false;
        var original = EnablementState.Capture(settings);
        var byId = plugins.ToDictionary(plugin => plugin.Manifest.Id, StringComparer.Ordinal);
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
                        new PluginStartContext("desktop", pluginRoot, DateTimeOffset.UtcNow)
                        {
                            HostDataRoot = store.DataDirectory,
                        },
                        cancellationToken);
                    started = true;
                    if (!await probe.HasLocalCredentialsAsync(cancellationToken)) continue;

                    // A discovered plugin may depend on a disabled optional plugin.
                    // Persisting only the discovered node would make the next host
                    // composition invalid, so resolve the required dependency closure
                    // before changing any settings.
                    if (!CanEnableDependencies(
                            plugin.Manifest,
                            byId,
                            settings,
                            new HashSet<string>(StringComparer.Ordinal)))
                    {
                        continue;
                    }

                    EnableDependencies(
                        plugin.Manifest,
                        byId,
                        settings,
                        new HashSet<string>(StringComparer.Ordinal));

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

    private static bool CanEnableDependencies(
        PluginManifest manifest,
        IReadOnlyDictionary<string, IZgsPlugin> plugins,
        AppSettings settings,
        HashSet<string> visiting)
    {
        if (!visiting.Add(manifest.Id)) return false;
        try
        {
            foreach (var dependencyId in manifest.Requires)
            {
                if (!plugins.TryGetValue(dependencyId, out var dependency)) return false;
                if (settings.HasExplicitPluginEnablement(dependencyId)
                    && !settings.IsPluginEnabled(dependencyId))
                {
                    return false;
                }
                if (!CanEnableDependencies(dependency.Manifest, plugins, settings, visiting))
                {
                    return false;
                }
            }
            return true;
        }
        finally
        {
            visiting.Remove(manifest.Id);
        }
    }

    private static void EnableDependencies(
        PluginManifest manifest,
        IReadOnlyDictionary<string, IZgsPlugin> plugins,
        AppSettings settings,
        HashSet<string> visited)
    {
        if (!visited.Add(manifest.Id)) return;
        foreach (var dependencyId in manifest.Requires)
        {
            if (plugins.TryGetValue(dependencyId, out var dependency))
            {
                EnableDependencies(dependency.Manifest, plugins, settings, visited);
                settings.SetPluginAutoEnabled(dependencyId);
            }
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
