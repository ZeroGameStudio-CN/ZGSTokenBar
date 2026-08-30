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

    private static Task<int> AiGatewayCommandAsync(string[] commandLine, bool asJson)
    {
        var subcommand = Subcommand(commandLine, "help");
        var command = subcommand is "help" or "h" or "?"
            ? "ai-gateway"
            : $"ai-gateway {subcommand}";
        CliOutput.Write(
            asJson,
            command,
            null,
            new(
                "retired_command",
                "AI Gateway commands are retired. DeepSeek Harness access is discovered automatically; ZGSTokenBar does not accept an API key."));
        return Task.FromResult(2);
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

}
