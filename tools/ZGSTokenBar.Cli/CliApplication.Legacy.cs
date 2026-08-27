using System.Diagnostics;
using System.Text.Json;
using ZGSTokenBar.Core;
using ZGSTokenBar.Transport.NamedPipe;

namespace ZGSTokenBar.Cli;

internal static partial class CliApplication
{
    private static int OpenSettingsAlias(bool asJson)
    {
        var result = OpenSettingsLegacy();
        var payload = result.Payload with { DeprecatedAlias = true };
        CliOutput.Legacy(asJson, payload, result.Text, CliJsonContext.Default.CliActionResult);
        return result.ExitCode;
    }

    private static int OpenSettingsCanonical(bool asJson)
    {
        var result = OpenSettingsLegacy();
        var element = JsonSerializer.SerializeToElement(
            result.Payload,
            CliJsonContext.Default.CliActionResult);
        if (result.ExitCode == 0)
        {
            return CliOutput.Result(asJson, "app settings", element, result.Text);
        }
        CliOutput.Write(
            asJson,
            "app settings",
            null,
            new(result.Payload.Error ?? "internal", result.Payload.Message ?? result.Text));
        return result.ExitCode;
    }

    private static (int ExitCode, CliActionResult Payload, string Text) OpenSettingsLegacy()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
            return (
                0,
                new(true, "signaled", "settings", null, null, null, null),
                "Settings request sent.");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        var applicationPath = ApplicationPath();
        if (!File.Exists(applicationPath))
        {
            return (
                3,
                new(false, null, "settings", null, "app_not_found", applicationPath, null),
                $"ZGSTokenBar.exe was not found beside the CLI: {applicationPath}");
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo(applicationPath, "--settings")
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            var payload = new CliActionResult(
                true,
                "launched",
                "settings",
                process?.Id,
                null,
                null,
                null);
            var text = $"ZGSTokenBar started with Settings requested (PID {process?.Id}).";
            process?.Dispose();
            return (0, payload, text);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (
                4,
                new(false, null, "settings", null, "launch_failed", null, exception.Message),
                $"Unable to start ZGSTokenBar: {exception.Message}");
        }
    }

    private static int PrintStatusAlias(bool asJson)
    {
        var status = ProcessStatus() with { DeprecatedAlias = true };
        CliOutput.Legacy(
            asJson,
            status,
            status.Running
                ? $"ZGSTokenBar {status.Version} is running (PID {status.Pid})."
                : $"ZGSTokenBar {status.Version} is not running.",
            CliJsonContext.Default.CliStatus);
        return 0;
    }

    private static int PrintVersion(bool asJson)
    {
        var version = ProductVersion();
        var applicationPath = CandidateApplicationPath();
        return CliOutput.Result(
            asJson,
            "version",
            JsonSerializer.SerializeToElement(
                new CliVersion(
                    "ZGSTokenBar",
                    version,
                    applicationPath,
                    BuildIdForArtifact(applicationPath)),
                CliJsonContext.Default.CliVersion),
            version);
    }

    private static int PrintHelp(bool asJson)
    {
        var commands = new[]
        {
            "app status|settings|refresh|quit",
            "api describe",
            "profile list|show|dump|validate",
            "config migration status|restore-v1",
            "plugin list|describe|data|enable|disable|refresh|doctor|install|remove",
            "snapshot [--plugin <id>] [--include-values]",
            "mini status|collapse|expand|toggle [area-id]",
            "mini width <area-id> <logical-px>",
            "mini move <area-id> [before-area-id]",
            "window inspect",
            "watch [--include-values]",
            "acceptance run --isolated --artifacts <dir>",
            "ai-gateway configure|status|disconnect",
            "sub2api provision|configure|status|disconnect",
            "economy status|install|set off|ask|on [--codex-home <dir>]",
            "version",
            "help",
        };
        return CliOutput.Result(
            asJson,
            "help",
            JsonSerializer.SerializeToElement(
                new CliHelp("ZGSTokenBar CLI", commands),
                CliJsonContext.Default.CliHelp),
            "ZGSTokenBar CLI\n\n" + string.Join(Environment.NewLine, commands)
            + "\n\nGlobal options before the command: --profile desktop|headless --timeout <seconds>. Add --json for machine-readable output.");
    }

    private static async Task<int> AiGatewayCommandAsync(string[] commandLine, bool asJson)
    {
        var subcommand = Subcommand(commandLine, "help");
        return subcommand switch
        {
            "configure" => await ConfigureAiGatewayAsync(commandLine, asJson),
            "status" => PrintAiGatewayStatus(asJson),
            "disconnect" => await DisconnectAiGatewayAsync(asJson),
            "help" or "h" or "?" => PrintAiGatewayHelp(asJson),
            _ => CliOutput.Unknown($"ai-gateway {subcommand}", asJson),
        };
    }

    private static async Task<int> ConfigureAiGatewayAsync(string[] commandLine, bool asJson)
    {
        var endpointValue = Option(commandLine, "--endpoint");
        var token = commandLine.Contains("--token-stdin", StringComparer.OrdinalIgnoreCase)
            ? Console.In.ReadToEnd().Trim()
            : string.Empty;
        if (!AiGatewayEndpoint.TryNormalize(endpointValue, out var endpoint)
            || string.IsNullOrWhiteSpace(token)
            || token.Length > 4096
            || token.Contains('\r')
            || token.Contains('\n'))
        {
            CliOutput.Legacy(
                asJson,
                new CliActionResult(
                    false,
                    null,
                    "ai-gateway configure",
                    null,
                    "invalid_arguments",
                    null,
                    null),
                "Usage: ai-gateway configure --endpoint <private-url> --token-stdin",
                CliJsonContext.Default.CliActionResult);
            return 2;
        }
        try
        {
            new AiGatewayConnectionStore().Write(new AiGatewayConnection(endpoint, token));
            new AppSettingsStore().SetAiGatewayBalanceEnabled(true);
            var syncError = await ReloadRunningAppSettingsAsync();
            if (syncError is not null)
            {
                return WriteLegacySettingsSyncFailure(
                    "ai-gateway configure",
                    asJson,
                    syncError);
            }
            CliOutput.Legacy(
                asJson,
                new CliActionResult(
                    true,
                    "configured",
                    "ai-gateway configure",
                    null,
                    null,
                    null,
                    null),
                "AI Gateway read-only balance observer configured.",
                CliJsonContext.Default.CliActionResult);
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or PlatformNotSupportedException)
        {
            CliOutput.Legacy(
                asJson,
                new CliActionResult(
                    false,
                    null,
                    "ai-gateway configure",
                    null,
                    "credential_write_failed",
                    null,
                    null),
                "Unable to store the AI Gateway observer credential.",
                CliJsonContext.Default.CliActionResult);
            return 3;
        }
    }

    private static async Task<CliError?> ReloadRunningAppSettingsAsync()
    {
        try
        {
            var response = await new ZgsNamedPipeClient().InvokeAsync(
                Request(
                    "app.requestRefresh",
                    CliOutput.ObjectElement(("reloadSettings", true))),
                TimeSpan.FromSeconds(3));
            if (response.Ok) return null;
            if (response.Error?.Code == "app_not_running" && !ProcessStatus().Running) return null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or OperationCanceledException
                or JsonException)
        {
            if (!ProcessStatus().Running) return null;
        }

        return new CliError(
            "runtime_sync_failed",
            "Configuration was saved, but the running app could not reload it. Retry or restart ZGSTokenBar.",
            true);
    }

    private static int PrintAiGatewayStatus(bool asJson)
    {
        try
        {
            var connection = new AiGatewayConnectionStore().Read();
            var status = new AiGatewayCliStatus(
                "AI Gateway",
                new AppSettingsStore().IsAiGatewayBalanceEnabled(),
                connection is not null,
                connection is null ? null : AiGatewayEndpoint.Mask(connection.Endpoint));
            CliOutput.Legacy(
                asJson,
                status,
                connection is null
                    ? "AI Gateway observer is not configured."
                    : $"AI Gateway observer is {(status.Enabled ? "enabled" : "disabled")} at {status.Endpoint}.",
                CliJsonContext.Default.AiGatewayCliStatus);
            return 0;
        }
        catch
        {
            CliOutput.Legacy(
                asJson,
                new AiGatewayCliStatus("AI Gateway", false, false, null),
                "AI Gateway observer status is unavailable.",
                CliJsonContext.Default.AiGatewayCliStatus);
            return 3;
        }
    }

    private static async Task<int> DisconnectAiGatewayAsync(bool asJson)
    {
        try
        {
            new AiGatewayConnectionStore().Delete();
            new AppSettingsStore().SetAiGatewayBalanceEnabled(false);
            var syncError = await ReloadRunningAppSettingsAsync();
            if (syncError is not null)
            {
                return WriteLegacySettingsSyncFailure(
                    "ai-gateway disconnect",
                    asJson,
                    syncError);
            }
            CliOutput.Legacy(
                asJson,
                new CliActionResult(
                    true,
                    "disconnected",
                    "ai-gateway disconnect",
                    null,
                    null,
                    null,
                    null),
                "AI Gateway observer disconnected.",
                CliJsonContext.Default.CliActionResult);
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CliOutput.Legacy(
                asJson,
                new CliActionResult(
                    false,
                    null,
                    "ai-gateway disconnect",
                    null,
                    "credential_delete_failed",
                    null,
                    null),
                "Unable to remove the AI Gateway observer credential.",
                CliJsonContext.Default.CliActionResult);
            return 3;
        }
    }

    private static int WriteLegacySettingsSyncFailure(
        string command,
        bool asJson,
        CliError error)
    {
        CliOutput.Legacy(
            asJson,
            new CliActionResult(
                false,
                null,
                command,
                null,
                error.Code,
                null,
                error.Message),
            error.Message,
            CliJsonContext.Default.CliActionResult);
        return 3;
    }

    private static int PrintAiGatewayHelp(bool asJson)
    {
        var commands = new[]
        {
            "configure --endpoint <private-url> --token-stdin  Store the read-only observer token",
            "status                                      Report observer state",
            "disconnect                                  Remove observer credential",
        };
        CliOutput.Legacy(
            asJson,
            new CliHelp("AI Gateway CLI", commands),
            "AI Gateway CLI\n\n" + string.Join(Environment.NewLine, commands),
            CliJsonContext.Default.CliHelp);
        return 0;
    }
}
