using System.Text.Json;
using ZGSTokenBar.Builtins;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;

namespace ZGSTokenBar.Cli;

internal static partial class CliApplication
{
    private static async Task<int> PluginNamespaceCommandAsync(CliArguments options)
    {
        var namespaceName = options.Command;
        var subcommand = Subcommand(options.Arguments, "help");
        var online = await InvokeApiAsync("commands.list", null, options);
        if (online.Ok)
        {
            var commands = online.Result?.Deserialize(
                    PluginSdkJsonContext.Default.CommandDescriptorArray)
                ?? [];
            var descriptor = commands.FirstOrDefault(command =>
                string.Equals(command.Namespace, namespaceName, StringComparison.Ordinal)
                && string.Equals(command.Name, subcommand, StringComparison.Ordinal));
            if (descriptor is not null)
            {
                if (options.Profile == "headless" && !descriptor.OfflineSafe)
                {
                    CliOutput.Write(
                        options.Json,
                        $"{descriptor.Namespace} {descriptor.Name}",
                        null,
                        new("app_not_running", "This plugin command requires the running App."));
                    return 3;
                }
                return descriptor.SecretSlots.Count > 0
                    ? await RunSecretCommandAsync(
                        descriptor,
                        options,
                        appRunning: options.Profile != "headless")
                    : await InvokeOnlineCommandAsync(descriptor, options);
            }
        }

        var store = new AppSettingsStore();
        var settings = store.Load();
        var plugins = LoadPlugins(store);
        var target = plugins.FirstOrDefault(plugin =>
            string.Equals(
                plugin.Manifest.CommandNamespace,
                namespaceName,
                StringComparison.Ordinal));
        if (target is null)
        {
            foreach (var candidate in plugins)
            {
                await candidate.DisposeAsync();
            }
            return CliOutput.Unknown(options.CommandText, options.Json);
        }
        settings.PluginEnabled[target.Manifest.Id] = true;
        EnableDependencies(target.Manifest, plugins, settings.PluginEnabled);
        var profile = ComposeProfile(options.Profile, settings, plugins);
        await using var host = new ZgsTokenBarHost(
            plugins,
            profile,
            ProductVersion(),
            store.DataDirectory,
            persistProfileState: false);
        try
        {
            await host.StartAsync();
        }
        catch (Exception exception) when (
            exception is HostCommandException or InvalidOperationException)
        {
            CliOutput.Write(
                options.Json,
                $"{namespaceName} {subcommand}",
                null,
                new("trust_failed", "Plugin could not start."));
            return 6;
        }
        var offlineDescriptor = host.ListPlugins()
            .SelectMany(plugin => plugin.Commands)
            .FirstOrDefault(command =>
                string.Equals(command.Namespace, namespaceName, StringComparison.Ordinal)
                && string.Equals(command.Name, subcommand, StringComparison.Ordinal));
        if (offlineDescriptor is null)
        {
            return CliOutput.Unknown($"{namespaceName} {subcommand}", options.Json);
        }
        if (!offlineDescriptor.OfflineSafe)
        {
            CliOutput.Write(
                options.Json,
                $"{namespaceName} {subcommand}",
                null,
                new("app_not_running", "This plugin command requires the running App."));
            return 3;
        }
        if (offlineDescriptor.SecretSlots.Count > 0)
        {
            return await RunSecretCommandAsync(
                offlineDescriptor,
                options,
                appRunning: false);
        }
        var request = Request(
            "commands.invoke",
            CliOutput.ObjectElement(
                ("commandId", offlineDescriptor.Id),
                ("arguments", CommandArguments(options.Arguments))));
        var response = await host.DispatchAsync(request, CancellationToken.None);
        if (!response.Ok)
        {
            return WriteApiFailure($"{namespaceName} {subcommand}", options.Json, response);
        }
        return CliOutput.Result(
            options.Json,
            $"{namespaceName} {subcommand}",
            response.Result ?? CliOutput.EmptyObject());
    }

    private static async Task<int> InvokeOnlineCommandAsync(
        CommandDescriptor descriptor,
        CliArguments options)
    {
        return await UnaryAsync(
            $"{descriptor.Namespace} {descriptor.Name}",
            "commands.invoke",
            CliOutput.ObjectElement(
                ("commandId", descriptor.Id),
                ("arguments", CommandArguments(options.Arguments))),
            options);
    }

    private static async Task<int> RunSecretCommandAsync(
        CommandDescriptor descriptor,
        CliArguments options,
        bool appRunning)
    {
        var clear = string.Equals(descriptor.Name, "disconnect", StringComparison.Ordinal);
        if (!clear
            && !options.Arguments.Contains("--secret-stdin", StringComparer.OrdinalIgnoreCase))
        {
            return CliOutput.Invalid(
                options.Json,
                $"{descriptor.Namespace} {descriptor.Name}",
                "Secret commands require --secret-stdin.");
        }
        var slot = Option(options.Arguments, "--slot");
        if (slot is null && descriptor.SecretSlots.Count == 1)
        {
            slot = descriptor.SecretSlots[0];
        }
        if (slot is null || !descriptor.SecretSlots.Contains(slot, StringComparer.Ordinal))
        {
            return CliOutput.Invalid(
                options.Json,
                $"{descriptor.Namespace} {descriptor.Name}",
                "A declared --slot is required.");
        }
        var store = new PluginCredentialStore();
        var previous = store.Read(descriptor.PluginId, slot);
        try
        {
            if (clear)
            {
                store.Delete(descriptor.PluginId, slot);
            }
            else
            {
                var secret = Console.In.ReadToEnd();
                if (string.IsNullOrEmpty(secret))
                {
                    return CliOutput.Invalid(
                        options.Json,
                        $"{descriptor.Namespace} {descriptor.Name}",
                        "Secret input is empty.");
                }
                store.Write(descriptor.PluginId, slot, secret.TrimEnd((char)13, (char)10));
            }
            if (appRunning)
            {
                var reconcile = await InvokeApiAsync(
                    "plugin.reconcileCredentials",
                    CliOutput.ObjectElement(("pluginId", descriptor.PluginId)),
                    options);
                if (!reconcile.Ok)
                {
                    if (previous is null) store.Delete(descriptor.PluginId, slot);
                    else store.Write(descriptor.PluginId, slot, previous);
                    return WriteApiFailure(
                        $"{descriptor.Namespace} {descriptor.Name}",
                        options.Json,
                        reconcile);
                }
            }
            return CliOutput.Result(
                options.Json,
                $"{descriptor.Namespace} {descriptor.Name}",
                CliOutput.ObjectElement(
                    ("pluginId", descriptor.PluginId),
                    ("slot", slot),
                    ("configured", !clear)),
                clear ? "Plugin credential removed." : "Plugin credential configured.");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or PlatformNotSupportedException
                or ArgumentException)
        {
            CliOutput.Write(
                options.Json,
                $"{descriptor.Namespace} {descriptor.Name}",
                null,
                new("credential_required", "Plugin credential could not be updated."));
            return 6;
        }
    }

    private static void EnableDependencies(
        PluginManifest manifest,
        IReadOnlyList<IZgsPlugin> plugins,
        IDictionary<string, bool> enabled)
    {
        enabled[manifest.Id] = true;
        foreach (var dependency in manifest.Requires)
        {
            var plugin = plugins.First(item => item.Manifest.Id == dependency);
            EnableDependencies(plugin.Manifest, plugins, enabled);
        }
    }

    private static IReadOnlyList<string> CommandArguments(string[] arguments)
    {
        var result = new List<string>();
        var skippedSubcommand = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!skippedSubcommand && !arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                skippedSubcommand = true;
                continue;
            }
            if (arguments[index] is "--secret-stdin" or "--token-stdin") continue;
            result.Add(arguments[index]);
        }
        return result;
    }

    private static async Task<int> PluginCommandAsync(string[] commandArgs, CliArguments options)
    {
        var subcommand = Subcommand(commandArgs, "list");
        if (subcommand is "list" or "describe" or "doctor")
        {
            return await OfflinePluginCommandAsync(subcommand, commandArgs, options);
        }
        if (subcommand == "data")
        {
            var pluginId = Positional(commandArgs, 1);
            if (pluginId is null)
            {
                return CliOutput.Invalid(options.Json, "plugin data", "plugin data requires an ID.");
            }
            return await UnaryAsync(
                "plugin data",
                "plugin.data.get",
                CliOutput.ObjectElement(
                    ("pluginId", pluginId),
                    ("cursor", Option(commandArgs, "--cursor")),
                    ("pageSize", IntOption(commandArgs, "--page-size") ?? 20)),
                options);
        }
        if (subcommand is "enable" or "disable")
        {
            var pluginId = Positional(commandArgs, 1);
            if (pluginId is null)
            {
                return CliOutput.Invalid(options.Json, $"plugin {subcommand}", "Plugin ID is required.");
            }
            if (options.Profile == "headless")
            {
                return SetPluginEnabledOffline(pluginId, subcommand == "enable", options);
            }
            var description = await InvokeApiAsync("host.describe", null, options);
            if (!description.Ok)
            {
                return WriteApiFailure($"plugin {subcommand}", options.Json, description);
            }
            var host = description.Result?.Deserialize(ApiJsonContext.Default.HostDescription)
                ?? throw new InvalidDataException();
            return await UnaryAsync(
                $"plugin {subcommand}",
                "plugin.setEnabled",
                CliOutput.ObjectElement(
                    ("pluginId", pluginId),
                    ("enabled", subcommand == "enable"),
                    ("expectedConfigRevision", host.Revisions.ConfigRevision)),
                options);
        }
        if (subcommand == "refresh")
        {
            var pluginId = Positional(commandArgs, 1);
            return await UnaryAsync(
                "plugin refresh",
                "plugin.refresh",
                pluginId is null
                    ? CliOutput.EmptyObject()
                    : CliOutput.ObjectElement(("pluginId", pluginId)),
                options);
        }
        if (subcommand == "install") return InstallPlugin(commandArgs, options);
        if (subcommand == "remove") return RemovePlugin(commandArgs, options);
        return CliOutput.Unknown($"plugin {subcommand}", options.Json);
    }

    private static async Task<int> OfflinePluginCommandAsync(
        string subcommand,
        string[] commandArgs,
        CliArguments options)
    {
        var store = new AppSettingsStore();
        var settings = store.Load();
        var plugins = LoadPlugins(store);
        var profile = ComposeProfile(options.Profile, settings, plugins);
        await using var host = new ZgsTokenBarHost(
            plugins,
            profile,
            ProductVersion(),
            store.DataDirectory,
            persistProfileState: false);
        try
        {
            await host.StartAsync();
        }
        catch (Exception exception) when (
            exception is HostCommandException or InvalidOperationException)
        {
            CliOutput.Write(
                options.Json,
                $"plugin {subcommand}",
                null,
                new("trust_failed", "Plugin catalog could not start."));
            return 6;
        }
        if (subcommand == "list")
        {
            return CliOutput.Result(
                options.Json,
                "plugin list",
                JsonSerializer.SerializeToElement(
                    host.ListPlugins().ToArray(),
                    ApiJsonContext.Default.PluginStatusArray));
        }
        if (subcommand == "describe")
        {
            var pluginId = Positional(commandArgs, 1);
            if (pluginId is null)
            {
                return CliOutput.Invalid(options.Json, "plugin describe", "Plugin ID is required.");
            }
            var plugin = host.DescribePlugin(pluginId);
            if (plugin is null)
            {
                CliOutput.Write(
                    options.Json,
                    "plugin describe",
                    null,
                    new("plugin_not_found", "Plugin was not found."));
                return 4;
            }
            return CliOutput.Result(
                options.Json,
                "plugin describe",
                JsonSerializer.SerializeToElement(plugin, ApiJsonContext.Default.PluginStatus));
        }

        var errors = PluginValidation.ValidateCatalog(plugins.Select(plugin => plugin.Manifest).ToArray());
        var installed = new PluginPackageManager(new AppSettingsStore().DataDirectory).InspectInstalled();
        var result = new PluginDoctorResult(
            errors.Count == 0 && installed.All(item => item.Valid),
            errors,
            installed);
        var element = JsonSerializer.SerializeToElement(result, CliJsonContext.Default.PluginDoctorResult);
        if (result.Healthy)
        {
            return CliOutput.Result(
                options.Json,
                "plugin doctor",
                element,
                "Plugin catalog is healthy.");
        }
        CliOutput.Write(
            options.Json,
            "plugin doctor",
            element,
            new("trust_failed", "Plugin validation failed."));
        return 6;
    }

    private static int SetPluginEnabledOffline(
        string pluginId,
        bool enabled,
        CliArguments options)
    {
        if (ProcessStatus().Running)
        {
            CliOutput.Write(options.Json, enabled ? "plugin enable" : "plugin disable", null,
                new("app_running", "Stop ZGSTokenBar before changing headless configuration."));
            return 4;
        }
        var store = new AppSettingsStore();
        var settings = store.Load();
        var plugins = LoadPlugins(store, withCredentials: false);
        try
        {
            var manifest = plugins.Select(plugin => plugin.Manifest).FirstOrDefault(manifest =>
                string.Equals(manifest.Id, pluginId, StringComparison.Ordinal));
            if (manifest is null)
            {
                CliOutput.Write(options.Json, enabled ? "plugin enable" : "plugin disable", null,
                    new("plugin_not_found", "Plugin was not found."));
                return 4;
            }
            if (!enabled && manifest.Required)
            {
                CliOutput.Write(options.Json, "plugin disable", null,
                    new("plugin_required", "Required plugin cannot be disabled."));
                return 4;
            }
            if (enabled && manifest.Requires.Any(dependency =>
                    !settings.IsPluginEnabled(dependency, plugins
                        .First(plugin => plugin.Manifest.Id == dependency).Manifest.DefaultEnabled)))
            {
                CliOutput.Write(options.Json, "plugin enable", null,
                    new("plugin_disabled", "Enable plugin dependencies first."));
                return 4;
            }
            if (!enabled && plugins.Any(plugin =>
                    settings.IsPluginEnabled(plugin.Manifest.Id, plugin.Manifest.DefaultEnabled)
                    && plugin.Manifest.Requires.Contains(pluginId, StringComparer.Ordinal)))
            {
                CliOutput.Write(options.Json, "plugin disable", null,
                    new("plugin_required", "Disable dependent plugins first."));
                return 4;
            }
            settings.SetPluginEnabled(pluginId, enabled);
            store.Save(settings);
            return CliOutput.Result(
                options.Json,
                enabled ? "plugin enable" : "plugin disable",
                CliOutput.ObjectElement(("pluginId", pluginId), ("enabled", enabled)));
        }
        finally
        {
            foreach (var plugin in plugins) plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int InstallPlugin(string[] commandArgs, CliArguments options)
    {
        if (ProcessStatus().Running)
        {
            CliOutput.Write(
                options.Json,
                "plugin install",
                null,
                new("app_running", "Stop ZGSTokenBar before installing a plugin."));
            return 4;
        }
        var package = Option(commandArgs, "--package");
        var digest = Option(commandArgs, "--sha256");
        if (package is null || digest is null)
        {
            return CliOutput.Invalid(
                options.Json,
                "plugin install",
                "plugin install requires --package and --sha256.");
        }
        try
        {
            var installed = new PluginPackageManager(new AppSettingsStore().DataDirectory)
                .Install(package, digest);
            return CliOutput.Result(
                options.Json,
                "plugin install",
                JsonSerializer.SerializeToElement(
                    installed,
                    CliJsonContext.Default.InstalledPluginStatus),
                $"Installed {installed.PluginId} {installed.Version}.");
        }
        catch (PluginTrustException exception)
        {
            CliOutput.Write(
                options.Json,
                "plugin install",
                null,
                new("trust_failed", exception.SafeMessage));
            return 6;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            CliOutput.Write(
                options.Json,
                "plugin install",
                null,
                new("trust_failed", "Plugin package could not be verified."));
            return 6;
        }
    }

    private static int RemovePlugin(string[] commandArgs, CliArguments options)
    {
        if (ProcessStatus().Running)
        {
            CliOutput.Write(
                options.Json,
                "plugin remove",
                null,
                new("app_running", "Stop ZGSTokenBar before removing a plugin."));
            return 4;
        }
        var pluginId = Positional(commandArgs, 1);
        if (pluginId is null || !commandArgs.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            return CliOutput.Invalid(
                options.Json,
                "plugin remove",
                "plugin remove requires an exact ID and --yes.");
        }
        try
        {
            var removed = new PluginPackageManager(new AppSettingsStore().DataDirectory).Remove(pluginId);
            return CliOutput.Result(
                options.Json,
                "plugin remove",
                CliOutput.ObjectElement(("pluginId", pluginId), ("removed", removed)),
                removed ? $"Removed {pluginId}." : $"{pluginId} is not installed.");
        }
        catch (PluginTrustException exception)
        {
            CliOutput.Write(
                options.Json,
                "plugin remove",
                null,
                new("trust_failed", exception.SafeMessage));
            return 6;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            CliOutput.Write(
                options.Json,
                "plugin remove",
                null,
                new("internal", "Plugin could not be removed."));
            return 4;
        }
    }
}
