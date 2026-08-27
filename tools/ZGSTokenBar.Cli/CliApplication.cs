using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ZGSTokenBar.Builtins;
using ZGSTokenBar.Core;
using ZGSTokenBar.Host;
using ZGSTokenBar.PluginSdk;
using ZGSTokenBar.Transport.NamedPipe;

namespace ZGSTokenBar.Cli;

internal static partial class CliApplication
{
    private const string ActivationEventName = @"Local\ZGSTokenBar.App.Activate";

    public static async Task<int> RunAsync(string[] args)
    {
        var parsed = CliArguments.Parse(args);
        if (parsed.Error is not null)
        {
            CliOutput.Write(parsed.Json, "launcher", null, new("invalid_arguments", parsed.Error));
            return 2;
        }

        try
        {
            return await DispatchAsync(parsed);
        }
        catch (OperationCanceledException)
        {
            CliOutput.Write(parsed.Json, parsed.CommandText, null, new("timeout", "Operation timed out.", true));
            return 3;
        }
        catch
        {
            CliOutput.Write(parsed.Json, parsed.CommandText, null, new("internal", "Command failed."));
            return 4;
        }
    }

    private static async Task<int> DispatchAsync(CliArguments commandLine)
    {
        var command = commandLine.Command;
        var commandArgs = commandLine.Arguments;
        if (command is "settings") return OpenSettingsAlias(commandLine.Json);
        if (command is "status") return PrintStatusAlias(commandLine.Json);
        if (command is "ai-gateway") return await AiGatewayCommandAsync(commandArgs, commandLine.Json);
        if (command is "sub2api") return await Sub2ApiCommandAsync(commandArgs, commandLine.Json);
        if (command is "economy") return EconomyCommand(commandArgs, commandLine.Json);
        if (command is "version") return PrintVersion(commandLine.Json);
        if (command is "help" or "h" or "?") return PrintHelp(commandLine.Json);

        return command switch
        {
            "app" => await AppCommandAsync(commandArgs, commandLine),
            "api" => await ApiCommandAsync(commandArgs, commandLine),
            "profile" => ProfileCommand(commandArgs, commandLine),
            "config" => ConfigCommand(commandArgs, commandLine),
            "plugin" => await PluginCommandAsync(commandArgs, commandLine),
            "snapshot" => await SnapshotCommandAsync(commandArgs, commandLine),
            "mini" => await MiniCommandAsync(commandArgs, commandLine),
            "window" => await WindowCommandAsync(commandArgs, commandLine),
            "watch" => await WatchCommandAsync(commandArgs, commandLine),
            "acceptance" => await AcceptanceCommandAsync(commandArgs, commandLine),
            _ => await PluginNamespaceCommandAsync(commandLine),
        };
    }

    private static async Task<int> AppCommandAsync(string[] commandArgs, CliArguments options)
    {
        var subcommand = Subcommand(commandArgs, "status");
        if (options.Profile == "headless")
        {
            CliOutput.Write(options.Json, $"app {subcommand}", null,
                new("command_requires_desktop", "App commands require the desktop profile."));
            return 4;
        }
        return subcommand switch
        {
            "status" => await AppStatusAsync(options),
            "settings" => await AppSettingsAsync(options),
            "refresh" => await UnaryAsync("app refresh", "app.requestRefresh", null, options),
            "quit" => await UnaryAsync("app quit", "app.requestExit", null, options),
            _ => CliOutput.Unknown($"app {subcommand}", options.Json),
        };
    }

    private static async Task<int> AppStatusAsync(CliArguments options)
    {
        var response = await InvokeApiAsync("host.describe", null, options);
        if (response.Ok)
        {
            var hostResultPayload = response.Result;
            var processId = hostResultPayload is JsonElement hostResult ? ProcessId(hostResult) : null;
            if (hostResultPayload is JsonElement hostResultValue)
            {
                hostResultPayload = AddRunningBuildIdentity(hostResultValue);
            }
            CliOutput.Write(
                options.Json,
                "app status",
                hostResultPayload,
                null,
                processId is int pid
                    ? $"ZGSTokenBar is running (PID {pid})."
                    : "ZGSTokenBar status is available.");
            return 0;
        }

        var status = ProcessStatus();
        var result = JsonSerializer.SerializeToElement(
            new AppProcessStatus(
                status.Running,
                status.Pid,
                status.Executable,
                false,
                status.DataDirectory,
                status.BuildId),
            CliJsonContext.Default.AppProcessStatus);
        CliOutput.Write(
            options.Json,
            "app status",
            result,
            null,
            status.Running
                ? $"ZGSTokenBar is running without API support (PID {status.Pid})."
                : "ZGSTokenBar is not running.");
        return 0;
    }

    private static async Task<int> AppSettingsAsync(CliArguments options)
    {
        var response = await InvokeApiAsync("app.openSettings", null, options);
        if (response.Ok)
        {
            CliOutput.Write(options.Json, "app settings", response.Result, null, "Settings request sent.");
            return 0;
        }
        return OpenSettingsCanonical(options.Json);
    }

    private static async Task<int> ApiCommandAsync(string[] commandArgs, CliArguments options)
    {
        var subcommand = Subcommand(commandArgs, "describe");
        return subcommand == "describe"
            ? await UnaryAsync("api describe", "host.describe", null, options)
            : CliOutput.Unknown($"api {subcommand}", options.Json);
    }

    private static int ProfileCommand(string[] commandArgs, CliArguments options)
    {
        var subcommand = Subcommand(commandArgs, "show");
        var profile = EffectiveProfileFor(options.Profile);
        return subcommand switch
        {
            "list" => CliOutput.Result(
                options.Json,
                "profile list",
                JsonSerializer.SerializeToElement(
                    new[] { "desktop", "headless" },
                    CliJsonContext.Default.StringArray),
                "desktop\nheadless"),
            "show" => CliOutput.Result(
                options.Json,
                "profile show",
                JsonSerializer.SerializeToElement(profile, ApiJsonContext.Default.EffectiveProfile)),
            "dump" => CliOutput.Result(
                options.Json,
                "profile dump",
                JsonSerializer.SerializeToElement(profile, ApiJsonContext.Default.EffectiveProfile)),
            "validate" => ValidateProfile(options.Json, profile),
            _ => CliOutput.Unknown($"profile {subcommand}", options.Json),
        };
    }

    private static int ValidateProfile(bool asJson, EffectiveProfile profile)
    {
        var store = new AppSettingsStore();
        var plugins = LoadPlugins(store);
        ZgsTokenBarHost? host = null;
        try
        {
            var errors = PluginValidation.ValidateCatalog(plugins.Select(plugin => plugin.Manifest).ToArray());
            if (errors.Count > 0)
            {
                CliOutput.Write(asJson, "profile validate", null,
                    new(
                        "invalid_profile",
                        "Profile validation failed.",
                        false,
                        errors.ToDictionary(value => value, _ => "true", StringComparer.Ordinal)));
                return 2;
            }
            host = new(
                plugins,
                profile,
                ProductVersion(),
                store.DataDirectory,
                persistProfileState: false);
            return CliOutput.Result(
                asJson,
                "profile validate",
                CliOutput.ObjectElement(("valid", true), ("profile", profile.Name)),
                $"Profile {profile.Name} is valid.");
        }
        finally
        {
            if (host is not null) host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            else
            {
                foreach (var plugin in plugins)
                {
                    plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
    }

    private static int ConfigCommand(string[] commandArgs, CliArguments options)
    {
        var subcommand = Subcommand(commandArgs, "migration");
        var nested = commandArgs.Length > 1 ? commandArgs[1].ToLowerInvariant() : "status";
        if (subcommand != "migration") return CliOutput.Unknown($"config {subcommand}", options.Json);
        var store = new AppSettingsStore();
        var migration = new SettingsMigrationManager(store.DataDirectory);
        return nested switch
        {
            "status" => CliOutput.Result(
                options.Json,
                "config migration status",
                JsonSerializer.SerializeToElement(
                    migration.Status(),
                    CliJsonContext.Default.MigrationStatus)),
            "restore-v1" when commandArgs.Contains("--yes", StringComparer.OrdinalIgnoreCase) =>
                RestoreV1(options.Json, migration),
            "restore-v1" => CliOutput.Invalid(
                options.Json,
                "config migration restore-v1",
                "restore-v1 requires --yes."),
            _ => CliOutput.Unknown($"config migration {nested}", options.Json),
        };
    }

    private static int RestoreV1(bool asJson, SettingsMigrationManager migration)
    {
        if (ProcessStatus().Running)
        {
            CliOutput.Write(asJson, "config migration restore-v1", null,
                new("app_running", "Stop ZGSTokenBar before restoring configuration."));
            return 4;
        }
        try
        {
            var status = migration.RestoreV1();
            return CliOutput.Result(
                asJson,
                "config migration restore-v1",
                JsonSerializer.SerializeToElement(status, CliJsonContext.Default.MigrationStatus),
                "Version 1 settings restored.");
        }
        catch (InvalidDataException)
        {
            CliOutput.Write(asJson, "config migration restore-v1", null,
                new("invalid_request", "The v1 backup is missing or invalid."));
            return 4;
        }
        catch (IOException)
        {
            CliOutput.Write(asJson, "config migration restore-v1", null,
                new("internal", "Settings restore could not be completed."));
            return 4;
        }
    }

    private static Task<int> SnapshotCommandAsync(string[] commandArgs, CliArguments options) =>
        UnaryAsync(
            "snapshot",
            "snapshot.get",
            CliOutput.ObjectElement(
                ("pluginId", Option(commandArgs, "--plugin")),
                ("includeValues", commandArgs.Contains("--include-values", StringComparer.OrdinalIgnoreCase))),
            options);

    private static async Task<int> MiniCommandAsync(string[] commandArgs, CliArguments options)
    {
        if (options.Profile == "headless")
        {
            CliOutput.Write(options.Json, "mini", null,
                new("command_requires_desktop", "Mini commands require the desktop profile."));
            return 4;
        }
        var subcommand = Subcommand(commandArgs, "status");
        if (subcommand == "status")
        {
            return await UnaryAsync("mini status", "ui.mini.get", null, options);
        }
        if (subcommand is not ("collapse" or "expand" or "toggle" or "width" or "move"))
        {
            return CliOutput.Unknown($"mini {subcommand}", options.Json);
        }
        var areaId = Positional(commandArgs, 1);
        var widthText = Positional(commandArgs, 2);
        var beforeAreaId = subcommand == "move" ? widthText : null;
        if (subcommand == "move" && areaId is null)
        {
            CliOutput.Write(
                options.Json,
                "mini move",
                null,
                new("invalid_request", "Usage: mini move <area-id> [before-area-id]."));
            return 4;
        }
        if (subcommand == "width"
            && (areaId is null || !int.TryParse(
                widthText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _)))
        {
            CliOutput.Write(
                options.Json,
                "mini width",
                null,
                new("invalid_request", "Usage: mini width <area-id> <logical-px>."));
            return 4;
        }
        var currentResponse = await InvokeApiAsync("ui.mini.get", null, options);
        if (!currentResponse.Ok) return WriteApiFailure($"mini {subcommand}", options.Json, currentResponse);
        var current = currentResponse.Result?.Deserialize(ApiJsonContext.Default.MiniState)
            ?? throw new InvalidDataException();
        var area = areaId is null
            ? null
            : current.Areas.FirstOrDefault(item => string.Equals(item.AreaId, areaId, StringComparison.Ordinal));
        if (areaId is not null && area is null)
        {
            CliOutput.Write(
                options.Json,
                $"mini {subcommand}",
                null,
                new("area_not_found", "Mini area was not found."));
            return 4;
        }
        if (beforeAreaId is not null
            && !current.Areas.Any(item => string.Equals(item.AreaId, beforeAreaId, StringComparison.Ordinal)))
        {
            CliOutput.Write(
                options.Json,
                "mini move",
                null,
                new("area_not_found", "Mini area was not found."));
            return 4;
        }
        var collapsed = subcommand switch
        {
            "collapse" => true,
            "expand" => false,
            "toggle" => !(area?.Collapsed ?? current.Collapsed),
            _ => (bool?)null,
        };
        var width = subcommand == "width"
            ? int.Parse(widthText!, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : (int?)null;
        return await UnaryAsync(
            $"mini {subcommand}",
            subcommand == "move"
                ? "ui.mini.moveArea"
                : areaId is null
                    ? "ui.mini.setCollapsed"
                    : "ui.mini.setArea",
            subcommand == "move"
                ? CliOutput.ObjectElement(
                    ("areaId", areaId),
                    ("beforeAreaId", beforeAreaId),
                    ("expectedUiRevision", current.UiRevision))
                : areaId is null
                ? CliOutput.ObjectElement(
                    ("collapsed", collapsed),
                    ("expectedUiRevision", current.UiRevision))
                : CliOutput.ObjectElement(
                    ("areaId", areaId),
                    ("collapsed", collapsed),
                    ("width", width),
                    ("expectedUiRevision", current.UiRevision)),
            options);
    }

    private static async Task<int> WindowCommandAsync(string[] commandArgs, CliArguments options)
    {
        var subcommand = Subcommand(commandArgs, "inspect");
        return subcommand == "inspect"
            ? await UnaryAsync("window inspect", "window.inspect", null, options)
            : CliOutput.Unknown($"window {subcommand}", options.Json);
    }

    private static async Task<int> WatchCommandAsync(string[] commandArgs, CliArguments options)
    {
        if (options.Profile == "headless")
        {
            CliOutput.Write(
                options.Json,
                "watch",
                null,
                new("command_requires_desktop", "Event watch requires the desktop profile."));
            return 4;
        }
        var request = Request(
            "events.watch",
            CliOutput.ObjectElement(
                ("includeValues", commandArgs.Contains("--include-values", StringComparer.OrdinalIgnoreCase))));
        using var cancellation = ConsoleCancellation();
        await foreach (var item in new ZgsNamedPipeClient().WatchAsync(request, cancellation.Token))
        {
            switch (item)
            {
                case ApiResponseEnvelope response when !response.Ok:
                    return WriteApiFailure("watch", options.Json, response);
                case ApiResponseEnvelope response:
                    if (options.Json) CliOutput.Write(true, "watch", response.Result, null);
                    else Console.WriteLine("Watching ZGSTokenBar events. Press Ctrl+C to stop.");
                    break;
                case HostEvent hostEvent:
                    if (options.Json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(
                            new CliEventEnvelope(
                                1,
                                hostEvent.EventId,
                                hostEvent.Revision,
                                hostEvent.Type,
                                hostEvent.Payload),
                            CliJsonContext.Default.CliEventEnvelope));
                    }
                    else
                    {
                        Console.WriteLine($"{hostEvent.Revision} {hostEvent.Type}");
                    }
                    break;
            }
        }
        return 0;
    }

    private static async Task<int> AcceptanceCommandAsync(string[] commandArgs, CliArguments options)
    {
        var subcommand = Subcommand(commandArgs, "run");
        if (subcommand != "run"
            || !commandArgs.Contains("--isolated", StringComparer.OrdinalIgnoreCase))
        {
            return CliOutput.Invalid(
                options.Json,
                "acceptance",
                "Use acceptance run --isolated --artifacts <directory>.");
        }
        var artifacts = Option(commandArgs, "--artifacts");
        if (string.IsNullOrWhiteSpace(artifacts))
        {
            return CliOutput.Invalid(options.Json, "acceptance", "--artifacts is required.");
        }
        var result = await IsolatedAcceptance.RunAsync(artifacts, CancellationToken.None);
        var value = JsonSerializer.SerializeToElement(result, CliJsonContext.Default.AcceptanceResult);
        if (result.Passed) return CliOutput.Result(
            options.Json,
            "acceptance run",
            value,
            "Isolated acceptance passed.");
        CliOutput.Write(options.Json, "acceptance run", value,
            new("internal", "Isolated acceptance failed."));
        return 4;
    }

    private static async Task<int> UnaryAsync(
        string command,
        string method,
        JsonElement? parameters,
        CliArguments options)
    {
        if (options.Profile == "headless"
            && (method.StartsWith("ui.", StringComparison.Ordinal)
                || method.StartsWith("window.", StringComparison.Ordinal)
                || method.StartsWith("app.", StringComparison.Ordinal)))
        {
            CliOutput.Write(options.Json, command, null,
                new("command_requires_desktop", "Command requires the desktop profile."));
            return 4;
        }
        var response = await InvokeApiAsync(method, parameters, options);
        if (!response.Ok) return WriteApiFailure(command, options.Json, response);
        return CliOutput.Result(options.Json, command, response.Result ?? CliOutput.EmptyObject());
    }

    private static async ValueTask<ApiResponseEnvelope> InvokeApiAsync(
        string method,
        JsonElement? parameters,
        CliArguments options)
    {
        if (options.Profile == "headless")
        {
            return await InvokeHeadlessAsync(method, parameters, options.Timeout);
        }
        return await new ZgsNamedPipeClient().InvokeAsync(
            Request(method, parameters),
            options.Timeout,
            CancellationToken.None);
    }

    private static async ValueTask<ApiResponseEnvelope> InvokeHeadlessAsync(
        string method,
        JsonElement? parameters,
        TimeSpan timeout)
    {
        var request = Request(method, parameters);
        using var deadline = new CancellationTokenSource(timeout);
        var store = new AppSettingsStore();
        var settings = store.Load();
        var plugins = LoadPlugins(store);
        ZgsTokenBarHost? host = null;
        try
        {
            var profile = ComposeProfile("headless", settings, plugins);
            host = new(
                plugins,
                profile,
                ProductVersion(),
                store.DataDirectory,
                persistProfileState: false);
            await host.StartAsync(deadline.Token);
            if (method == "plugin.data.get"
                && parameters is JsonElement dataParameters
                && dataParameters.TryGetProperty("pluginId", out var pluginIdValue)
                && pluginIdValue.ValueKind == JsonValueKind.String
                && pluginIdValue.GetString() is { } pluginId)
            {
                await host.RefreshPluginAsync(pluginId, "headless", deadline.Token);
            }
            else if (method == "snapshot.get")
            {
                var selected = parameters is JsonElement snapshotParameters
                    && snapshotParameters.TryGetProperty("pluginId", out var selectedValue)
                    && selectedValue.ValueKind == JsonValueKind.String
                        ? selectedValue.GetString()
                        : null;
                var targets = host.ListPlugins().Where(plugin =>
                    plugin.Enabled && (selected is null || plugin.Manifest.Id == selected));
                foreach (var plugin in targets)
                {
                    try
                    {
                        await host.RefreshPluginAsync(plugin.Manifest.Id, "headless", deadline.Token);
                    }
                    catch (HostCommandException)
                    {
                        // A snapshot retains failed plugin health and continues other refreshes.
                    }
                }
            }
            return await host.DispatchAsync(request, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            return new(1, request.RequestId, false, null, new("timeout", "Headless command timed out.", true));
        }
        catch (HostCommandException exception)
        {
            return new(1, request.RequestId, false, null,
                new(exception.Code, exception.SafeMessage, exception.Retryable, exception.Details));
        }
        catch
        {
            return new(1, request.RequestId, false, null, new("internal", "Headless command failed."));
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            else
            {
                foreach (var plugin in plugins) await plugin.DisposeAsync();
            }
        }
    }

    private static ApiRequestEnvelope Request(string method, JsonElement? parameters) =>
        new(1, Guid.NewGuid().ToString("N"), method, parameters);

    private static int WriteApiFailure(string command, bool asJson, ApiResponseEnvelope response)
    {
        var error = response.Error is null
            ? new CliError("internal", "Command failed.")
            : new CliError(
                response.Error.Code,
                response.Error.Message,
                response.Error.Retryable,
                response.Error.Details);
        CliOutput.Write(asJson, command, null, error);
        return error.Code switch
        {
            "invalid_request" or "unknown_method" or "api_version_unsupported" => 2,
            "app_not_running" or "timeout" => 3,
            "state_conflict" or "data_changed" => 5,
            "credential_required" or "credential_forbidden" or "trust_failed" => 6,
            _ => 4,
        };
    }

    private static EffectiveProfile EffectiveProfileFor(string profile)
    {
        var store = new AppSettingsStore();
        var settings = store.Load();
        var plugins = LoadPlugins(store);
        try
        {
            return ComposeProfile(profile, settings, plugins);
        }
        finally
        {
            foreach (var plugin in plugins)
            {
                plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static List<IZgsPlugin> LoadPlugins(
        AppSettingsStore store,
        bool withCredentials = true)
    {
        var plugins = GeneratedBuiltinPluginRegistry.Create().ToList();
        plugins.AddRange(withCredentials
            ? new PluginPackageManager(store.DataDirectory).LoadProcessPlugins(new PluginCredentialStore())
            : new PluginPackageManager(store.DataDirectory).LoadProcessPlugins());
        return plugins;
    }

    private static EffectiveProfile ComposeProfile(
        string profile,
        AppSettings settings,
        IReadOnlyList<IZgsPlugin> plugins) =>
        ProfileComposition.IncludePlugins(
            profile == "headless"
                ? BuiltinProfiles.Headless(settings.PluginEnabled)
                : BuiltinProfiles.Desktop(settings.PluginEnabled),
            plugins.Select(plugin => plugin.Manifest),
            settings.PluginEnabled);

    private static CliStatus ProcessStatus()
    {
        var processes = Process.GetProcessesByName("ZGSTokenBar");
        try
        {
            var running = processes.OrderBy(process => process.Id).FirstOrDefault();
            var executable = running is null ? null : ProcessExecutable(running.Id);
            return new(
                "ZGSTokenBar",
                ProductVersion(),
                running is not null,
                running?.Id,
                executable,
                new AppSettingsStore().DataDirectory,
                BuildIdForExecutable(executable));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static int? ProcessId(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("processId", out var value)
            || !value.TryGetInt32(out var processId))
        {
            return null;
        }
        return processId;
    }

    private static JsonElement AddRunningBuildIdentity(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return result;
        var executable = ProcessExecutable(ProcessId(result));
        var buildId = BuildIdForExecutable(executable);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in result.EnumerateObject())
            {
                if (property.NameEquals("executable") || property.NameEquals("buildId")) continue;
                property.WriteTo(writer);
            }
            if (executable is null) writer.WriteNull("executable");
            else writer.WriteString("executable", executable);
            if (buildId is null) writer.WriteNull("buildId");
            else writer.WriteString("buildId", buildId);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string? ProcessExecutable(int? processId)
    {
        if (processId is not int value || value <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(value);
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? BuildIdForExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        var directory = Path.GetDirectoryName(executable);
        var artifact = string.IsNullOrWhiteSpace(directory)
            ? executable
            : ApplicationArtifactPath(directory, executable);
        return BuildIdForArtifact(artifact);
    }

    private static string? BuildIdForArtifact(string? artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath)) return null;
        try
        {
            using var stream = File.OpenRead(artifactPath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException
                or CryptographicException)
        {
            return null;
        }
    }

    private static string CandidateApplicationPath() =>
        ApplicationArtifactPath(
            AppContext.BaseDirectory,
            Path.Combine(AppContext.BaseDirectory, "ZGSTokenBar.exe"));

    private static string ApplicationArtifactPath(string directory, string fallbackPath)
    {
        var libraryPath = Path.Combine(directory, "ZGSTokenBar.dll");
        return File.Exists(libraryPath) ? libraryPath : fallbackPath;
    }

    private static string ProductVersion()
    {
        var applicationPath = ApplicationPath();
        var version = File.Exists(applicationPath)
            ? FileVersionInfo.GetVersionInfo(applicationPath).ProductVersion
            : Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
        return (version ?? "unknown").Split('+', 2)[0];
    }

    private static string ApplicationPath() => Path.Combine(AppContext.BaseDirectory, "ZGSTokenBar.exe");

    private static string Subcommand(string[] values, string fallback) =>
        values.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal))
            ?.ToLowerInvariant() ?? fallback;

    private static string? Positional(string[] values, int position) =>
        values.Where(value => !value.StartsWith("--", StringComparison.Ordinal))
            .Skip(position)
            .FirstOrDefault();

    private static string? Option(string[] values, string name)
    {
        var index = Array.FindIndex(values, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
    }

    private static int? IntOption(string[] values, string name) =>
        int.TryParse(Option(values, name), out var value) ? value : null;

    private static CancellationTokenSource ConsoleCancellation()
    {
        var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        return cancellation;
    }
}
