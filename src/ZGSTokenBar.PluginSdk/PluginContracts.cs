using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.PluginSdk;

public static class ZgsHostApi
{
    public const int SchemaVersion = 1;
    public const int Major = 1;
    public const int Minor = 0;
    public const int MaximumFrameBytes = 64 * 1024;
}

public enum PluginRuntime
{
    [JsonStringEnumMemberName("builtin")]
    Builtin,
    [JsonStringEnumMemberName("process")]
    Process,
}

public enum PluginHealthCode
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,
    [JsonStringEnumMemberName("current")]
    Current,
    [JsonStringEnumMemberName("cached")]
    Cached,
    [JsonStringEnumMemberName("loading")]
    Loading,
    [JsonStringEnumMemberName("waiting")]
    Waiting,
    [JsonStringEnumMemberName("missing_credentials")]
    MissingCredentials,
    [JsonStringEnumMemberName("endpoint_blocked")]
    EndpointBlocked,
    [JsonStringEnumMemberName("oauth_expired")]
    OAuthExpired,
    [JsonStringEnumMemberName("rate_limited")]
    RateLimited,
    [JsonStringEnumMemberName("http_error")]
    HttpError,
    [JsonStringEnumMemberName("timeout")]
    Timeout,
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
    [JsonStringEnumMemberName("disabled")]
    Disabled,
    [JsonStringEnumMemberName("invalid_contribution")]
    InvalidContribution,
    [JsonStringEnumMemberName("trust_failed")]
    TrustFailed,
}

public enum ContributionKind
{
    [JsonStringEnumMemberName("quota")]
    Quota,
    [JsonStringEnumMemberName("balance")]
    Balance,
    [JsonStringEnumMemberName("metric")]
    Metric,
}

public enum RevisionDomain
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("config")]
    Config,
    [JsonStringEnumMemberName("ui")]
    Ui,
    [JsonStringEnumMemberName("data")]
    Data,
}

public sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string Version,
    int HostApiMajor,
    int HostApiMinMinor,
    PluginRuntime Runtime,
    bool Required,
    string CommandNamespace,
    IReadOnlyList<string> Capabilities,
    bool DefaultEnabled,
    int Order,
    IReadOnlyList<string> Requires)
{
    public string? DisplayName { get; init; }
    public string? Entrypoint { get; init; }
    public IReadOnlyList<PluginPackageFile> Files { get; init; } = [];
    public string? Icon { get; init; }
    public IReadOnlyList<string> Locales { get; init; } = [];
    public IReadOnlyList<string> CredentialSlots { get; init; } = [];
    public int? HandshakeTimeoutSeconds { get; init; }
    public int? CallTimeoutSeconds { get; init; }
    public int? DisposeTimeoutSeconds { get; init; }
}

public sealed record PluginPackageFile(string Path, long Bytes, string Sha256);

public sealed record PluginStartContext(
    string Profile,
    string DataRoot,
    DateTimeOffset StartedAt);

public sealed record PluginRefreshContext(
    DateTimeOffset Now,
    string Reason,
    long PreviousDataRevision);

public sealed record PluginHealth(
    PluginHealthCode Code,
    bool Connected,
    bool Retryable,
    DateTimeOffset SampledAt,
    string MessageKey,
    int? HttpStatus = null,
    DateTimeOffset? RetryAt = null);

public sealed record ContributionValue(
    string Kind,
    string? Text = null,
    decimal? Decimal = null,
    double? Number = null,
    long? Integer = null,
    bool? Boolean = null,
    DateTimeOffset? Timestamp = null);

public sealed record ContributionSummaryItem(
    string LabelKey,
    ContributionValue Value,
    string? Status = null);

public sealed record MiniCardContribution(
    string Id,
    string PluginId,
    string GroupId,
    ContributionKind Kind,
    int Order,
    string TitleKey,
    string IconResourceKey,
    string AccentToken,
    IReadOnlyList<ContributionSummaryItem> Summary,
    string? PrimaryActionId = null,
    string? SecondaryActionId = null);

public sealed record DetailRowContribution(
    string LabelKey,
    ContributionValue Value,
    string? Status = null,
    DateTimeOffset? ObservedAt = null);

public sealed record DetailSectionContribution(
    string Id,
    string TitleKey,
    int Order,
    IReadOnlyList<DetailRowContribution> Rows);

public sealed record DetailContribution(
    string Id,
    string PluginId,
    IReadOnlyList<DetailSectionContribution> Sections);

public sealed record RadarModelRow(
    string Model,
    double? Score,
    double? Average24Hours,
    int? AverageMinutes,
    decimal? EstimatedTaskCost,
    IReadOnlyList<string> ScenarioMarkers);

public sealed record RadarContribution(
    string Id,
    string PluginId,
    DateTimeOffset SourceAt,
    IReadOnlyList<RadarModelRow> Rows,
    IReadOnlyList<ContributionSummaryItem> Footer);

public sealed record SettingsFieldContribution(
    string Id,
    string LabelKey,
    string Kind,
    JsonElement? DefaultValue,
    int? Minimum = null,
    int? Maximum = null,
    IReadOnlyList<string>? AllowedValues = null,
    string? SecretSlot = null);

public sealed record SettingsContribution(
    string Id,
    string PluginId,
    IReadOnlyList<SettingsFieldContribution> Fields);

public sealed record PluginDataSnapshot(
    string PluginId,
    DateTimeOffset CapturedAt,
    PluginHealth Health,
    IReadOnlyList<MiniCardContribution> MiniCards,
    IReadOnlyList<DetailContribution> Details,
    IReadOnlyList<RadarContribution> Radar,
    IReadOnlyDictionary<string, JsonElement>? SafeMetadata = null);

public sealed record CommandDescriptor(
    string Id,
    string PluginId,
    string Namespace,
    string Name,
    string Summary,
    bool ReadOnly,
    bool OfflineSafe,
    bool RequiresDesktop,
    IReadOnlyList<string> SecretSlots,
    RevisionDomain RevisionDomain);

public sealed record CommandInvocation(
    string CommandId,
    IReadOnlyList<string> Arguments,
    JsonElement? Parameters,
    long? ExpectedRevision);

public sealed record CommandResult(bool Ok, JsonElement? Value = null, PluginError? Error = null);

public sealed record ProcessHandshakeResult(
    int ApiMajor,
    int ApiMinor,
    string PluginId,
    string Version,
    string FilesDigest);

public sealed record ProcessPluginDescription(
    IReadOnlyList<CommandDescriptor> Commands,
    IReadOnlyList<SettingsContribution> Settings);

public sealed record PluginError(
    string Code,
    string Message,
    bool Retryable = false,
    IReadOnlyDictionary<string, string>? Details = null);

public interface IZgsPlugin : IAsyncDisposable
{
    PluginManifest Manifest { get; }
    IReadOnlyList<CommandDescriptor> Commands { get; }
    IReadOnlyList<SettingsContribution> Settings { get; }
    ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IDataSource
{
    ValueTask<PluginDataSnapshot> RefreshAsync(
        PluginRefreshContext context,
        CancellationToken cancellationToken);
}

public interface ILocalCredentialProbe
{
    ValueTask<bool> HasLocalCredentialsAsync(CancellationToken cancellationToken);
}

public interface ICommandContributor
{
    ValueTask<CommandResult> InvokeAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken);
}

public interface IViewContributor
{
    PluginDataSnapshot Project(DateTimeOffset now);
}

public interface IHealthContributor
{
    PluginHealth GetHealth(DateTimeOffset now);
}

public interface ISettingsContributor
{
    IReadOnlyList<SettingsContribution> DescribeSettings();
}

public interface IPluginCredentialBroker
{
    ValueTask<string?> ResolveAsync(
        string pluginId,
        string slot,
        CancellationToken cancellationToken);
}

public abstract class BuiltinPluginBase : IZgsPlugin
{
    public abstract PluginManifest Manifest { get; }
    public virtual IReadOnlyList<CommandDescriptor> Commands => [];
    public virtual IReadOnlyList<SettingsContribution> Settings => [];

    public virtual ValueTask StartAsync(
        PluginStartContext context,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public virtual ValueTask StopAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginManifest[]))]
[JsonSerializable(typeof(PluginDataSnapshot))]
[JsonSerializable(typeof(PluginDataSnapshot[]))]
[JsonSerializable(typeof(CommandDescriptor))]
[JsonSerializable(typeof(CommandDescriptor[]))]
[JsonSerializable(typeof(CommandInvocation))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(ProcessHandshakeResult))]
[JsonSerializable(typeof(ProcessPluginDescription))]
[JsonSerializable(typeof(PluginError))]
[JsonSerializable(typeof(MiniCardContribution))]
[JsonSerializable(typeof(DetailContribution))]
[JsonSerializable(typeof(RadarContribution))]
[JsonSerializable(typeof(JsonElement))]
public partial class PluginSdkJsonContext : JsonSerializerContext;
