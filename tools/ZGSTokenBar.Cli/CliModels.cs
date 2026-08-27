using System.Text.Json;
using System.Text.Json.Serialization;
using ZGSTokenBar.Host;

namespace ZGSTokenBar.Cli;

internal sealed record CliArguments(
    bool Json,
    string Profile,
    TimeSpan Timeout,
    string Command,
    string[] Arguments,
    string? Error)
{
    public string CommandText => Command.Length == 0 ? "help" : Command;

    public static CliArguments Parse(string[] values)
    {
        var json = values.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var normalized = values
            .Where(value => !string.Equals(value, "--json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var profile = "desktop";
        var timeout = TimeSpan.FromSeconds(15);
        var index = 0;
        while (index < normalized.Length && normalized[index].StartsWith("--", StringComparison.Ordinal))
        {
            var option = normalized[index].ToLowerInvariant();
            if (option == "--profile")
            {
                if (++index >= normalized.Length) return ErrorResult(json, "--profile requires a value.");
                profile = normalized[index].ToLowerInvariant();
                if (profile is not ("desktop" or "headless"))
                {
                    return ErrorResult(json, "--profile must be desktop or headless.");
                }
            }
            else if (option == "--timeout")
            {
                if (++index >= normalized.Length
                    || !double.TryParse(
                        normalized[index],
                        System.Globalization.NumberStyles.AllowDecimalPoint,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var seconds)
                    || seconds is <= 0 or > 300)
                {
                    return ErrorResult(json, "--timeout must be between 0 and 300 seconds.");
                }
                timeout = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                break;
            }
            index++;
        }
        var command = index < normalized.Length
            ? normalized[index].TrimStart('-').ToLowerInvariant()
            : "help";
        var arguments = index + 1 < normalized.Length ? normalized[(index + 1)..] : [];
        return new(json, profile, timeout, command, arguments, null);
    }

    private static CliArguments ErrorResult(bool json, string error) =>
        new(json, "desktop", TimeSpan.FromSeconds(15), string.Empty, [], error);
}

internal sealed record CliEnvelope(
    int SchemaVersion,
    string Command,
    bool Ok,
    JsonElement? Result,
    CliError? Error);

internal sealed record CliError(
    string Code,
    string Message,
    bool Retryable = false,
    IReadOnlyDictionary<string, string>? Details = null);

internal sealed record CliEventEnvelope(
    int SchemaVersion,
    string EventId,
    long Revision,
    string Type,
    JsonElement Payload);

internal sealed record CliActionResult(
    bool Ok,
    string? Action,
    string? Command,
    int? Pid,
    string? Error,
    string? Path,
    string? Message)
{
    public bool? DeprecatedAlias { get; init; }
}

internal sealed record CliStatus(
    string Name,
    string Version,
    bool Running,
    int? Pid,
    string? Executable,
    string DataDirectory,
    string? BuildId)
{
    public bool? DeprecatedAlias { get; init; }
}

internal sealed record AppProcessStatus(
    bool Running,
    int? Pid,
    string? Executable,
    bool ApiAvailable,
    string DataDirectory,
    string? BuildId);

internal sealed record CliVersion(
    string Name,
    string Version,
    string ApplicationPath,
    string? BuildId);
internal sealed record CliHelp(string Name, string[] Commands);
internal sealed record AiGatewayCliStatus(string Name, bool Enabled, bool Configured, string? Endpoint);
internal sealed record Sub2ApiPoolCliStatus(string Name, bool Enabled, bool Configured, string? Endpoint);
internal sealed record Sub2ApiPoolProvisionResult(string Endpoint, string ObserverTokenSha256);
internal sealed record PluginDoctorResult(
    bool Healthy,
    IReadOnlyList<string> CatalogErrors,
    IReadOnlyList<InstalledPluginStatus> Installed);
internal sealed record ProcessAcceptanceArtifact(
    string PluginId,
    bool Discovered,
    bool Handshake,
    bool Refresh,
    bool CredentialBridge,
    bool ErrorIsolation,
    bool CancellationIsolation,
    bool TimeoutIsolation,
    bool DigestDriftDetected);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CliEnvelope))]
[JsonSerializable(typeof(CliError))]
[JsonSerializable(typeof(CliEventEnvelope))]
[JsonSerializable(typeof(CliActionResult))]
[JsonSerializable(typeof(CliStatus))]
[JsonSerializable(typeof(AppProcessStatus))]
[JsonSerializable(typeof(CliVersion))]
[JsonSerializable(typeof(CliHelp))]
[JsonSerializable(typeof(AiGatewayCliStatus))]
[JsonSerializable(typeof(Sub2ApiPoolCliStatus))]
[JsonSerializable(typeof(Sub2ApiPoolProvisionResult))]
[JsonSerializable(typeof(PluginDoctorResult))]
[JsonSerializable(typeof(MigrationStatus))]
[JsonSerializable(typeof(InstalledPluginStatus))]
[JsonSerializable(typeof(AcceptanceResult))]
[JsonSerializable(typeof(ProcessAcceptanceArtifact))]
[JsonSerializable(typeof(string[]))]
internal partial class CliJsonContext : JsonSerializerContext;
