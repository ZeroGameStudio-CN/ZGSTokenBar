using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZGSTokenBar.Core;
using ZGSTokenBar.Transport.NamedPipe;

namespace ZGSTokenBar.Cli;

internal static partial class CliApplication
{
    private static async Task<int> Sub2ApiCommandAsync(string[] commandLine, bool asJson)
    {
        var subcommand = Subcommand(commandLine, "help");
        return subcommand switch
        {
            "provision" => await ProvisionSub2ApiAsync(commandLine, asJson),
            "configure" => await ConfigureSub2ApiAsync(commandLine, asJson),
            "status" => PrintSub2ApiStatus(asJson),
            "disconnect" => await DisconnectSub2ApiAsync(asJson),
            "help" or "h" or "?" => PrintSub2ApiHelp(asJson),
            _ => CliOutput.Unknown($"sub2api {subcommand}", asJson),
        };
    }

    private static async Task<int> ProvisionSub2ApiAsync(string[] commandLine, bool asJson)
    {
        if (!Sub2ApiPoolEndpoint.TryNormalize(Option(commandLine, "--endpoint"), out var endpoint))
        {
            return CliOutput.Invalid(
                asJson,
                "sub2api provision",
                "Usage: sub2api provision --endpoint <private-url>");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(tokenBytes);
        try
        {
            new Sub2ApiPoolConnectionStore().Write(new Sub2ApiPoolConnection(endpoint, token));
            new AppSettingsStore().SetSub2ApiPoolEnabled(true);
            var syncError = await ReloadRunningAppSettingsAsync();
            if (syncError is not null)
            {
                CliOutput.Write(asJson, "sub2api provision", null, syncError);
                return 3;
            }
            var payload = new Sub2ApiPoolProvisionResult(
                Sub2ApiPoolEndpoint.Mask(endpoint),
                tokenHash);
            return CliOutput.Result(
                asJson,
                "sub2api provision",
                JsonSerializer.SerializeToElement(
                    payload,
                    CliJsonContext.Default.Sub2ApiPoolProvisionResult),
                $"Sub2API observer token stored. Deploy the observer with SHA-256: {tokenHash}");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or PlatformNotSupportedException)
        {
            CliOutput.Write(
                asJson,
                "sub2api provision",
                null,
                new("credential_write_failed", "Unable to store the Sub2API observer credential."));
            return 3;
        }
    }

    private static async Task<int> ConfigureSub2ApiAsync(string[] commandLine, bool asJson)
    {
        var token = commandLine.Contains("--token-stdin", StringComparer.OrdinalIgnoreCase)
            ? Console.In.ReadToEnd().Trim()
            : string.Empty;
        if (!Sub2ApiPoolEndpoint.TryNormalize(Option(commandLine, "--endpoint"), out var endpoint)
            || string.IsNullOrWhiteSpace(token)
            || token.Length > 4096
            || token.Contains('\r')
            || token.Contains('\n'))
        {
            return CliOutput.Invalid(
                asJson,
                "sub2api configure",
                "Usage: sub2api configure --endpoint <private-url> --token-stdin");
        }

        try
        {
            new Sub2ApiPoolConnectionStore().Write(new Sub2ApiPoolConnection(endpoint, token));
            new AppSettingsStore().SetSub2ApiPoolEnabled(true);
            var syncError = await ReloadRunningAppSettingsAsync();
            if (syncError is not null)
            {
                CliOutput.Write(asJson, "sub2api configure", null, syncError);
                return 3;
            }
            CliOutput.Write(
                asJson,
                "sub2api configure",
                CliOutput.ObjectElement(("configured", true)),
                null,
                "Sub2API usage observer configured.");
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or PlatformNotSupportedException)
        {
            CliOutput.Write(
                asJson,
                "sub2api configure",
                null,
                new("credential_write_failed", "Unable to store the Sub2API observer credential."));
            return 3;
        }
    }

    private static int PrintSub2ApiStatus(bool asJson)
    {
        try
        {
            var connection = new Sub2ApiPoolConnectionStore().Read();
            var status = new Sub2ApiPoolCliStatus(
                "Sub2API observer",
                new AppSettingsStore().IsSub2ApiPoolEnabled(),
                connection is not null,
                connection is null ? null : Sub2ApiPoolEndpoint.Mask(connection.Endpoint));
            CliOutput.Legacy(
                asJson,
                status,
                connection is null
                    ? "Sub2API observer is not configured."
                    : $"Sub2API observer is {(status.Enabled ? "enabled" : "disabled")} at {status.Endpoint}.",
                CliJsonContext.Default.Sub2ApiPoolCliStatus);
            return 0;
        }
        catch
        {
            CliOutput.Legacy(
                asJson,
                new Sub2ApiPoolCliStatus("Sub2API observer", false, false, null),
                "Sub2API observer status is unavailable.",
                CliJsonContext.Default.Sub2ApiPoolCliStatus);
            return 3;
        }
    }

    private static async Task<int> DisconnectSub2ApiAsync(bool asJson)
    {
        try
        {
            new Sub2ApiPoolConnectionStore().Delete();
            new AppSettingsStore().SetSub2ApiPoolEnabled(false);
            var syncError = await ReloadRunningAppSettingsAsync();
            if (syncError is not null)
            {
                CliOutput.Write(asJson, "sub2api disconnect", null, syncError);
                return 3;
            }
            CliOutput.Write(
                asJson,
                "sub2api disconnect",
                CliOutput.ObjectElement(("disconnected", true)),
                null,
                "Sub2API observer disconnected.");
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CliOutput.Write(
                asJson,
                "sub2api disconnect",
                null,
                new("credential_delete_failed", "Unable to remove the Sub2API observer credential."));
            return 3;
        }
    }

    private static int PrintSub2ApiHelp(bool asJson)
    {
        var commands = new[]
        {
            "provision --endpoint <private-url>             Generate/store a token and print its deployment hash",
            "configure --endpoint <private-url> --token-stdin  Store an existing read-only observer token",
            "status                                          Report observer configuration",
            "disconnect                                      Remove observer credential",
        };
        CliOutput.Legacy(
            asJson,
            new CliHelp("Sub2API observer CLI", commands),
            "Sub2API observer CLI\n\n" + string.Join(Environment.NewLine, commands),
            CliJsonContext.Default.CliHelp);
        return 0;
    }
}
