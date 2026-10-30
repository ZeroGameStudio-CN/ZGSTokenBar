using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.PluginSdk;

public sealed record ApiRequestEnvelope(
    int SchemaVersion,
    string RequestId,
    string Method,
    JsonElement? Params);

public sealed record ApiResponseEnvelope(
    int SchemaVersion,
    string RequestId,
    bool Ok,
    JsonElement? Result,
    PluginError? Error);

public sealed record HostRevisions(
    long Revision,
    long ConfigRevision,
    long UiRevision,
    IReadOnlyDictionary<string, long> DataRevisions);

public sealed record HostDescription(
    int ApiMajor,
    int ApiMinor,
    string ProductVersion,
    int ProcessId,
    string Profile,
    IReadOnlyList<string> Capabilities,
    HostRevisions Revisions);

public sealed record PluginStatus(
    PluginManifest Manifest,
    bool Enabled,
    long DataRevision,
    PluginHealth Health,
    IReadOnlyList<CommandDescriptor> Commands,
    IReadOnlyList<SettingsContribution> Settings);

public sealed record ProfilePlugin(
    string Id,
    string Version,
    bool Enabled,
    int Order,
    IReadOnlyDictionary<string, JsonElement> Configuration);

public sealed record EffectiveProfile(
    int SchemaVersion,
    string Name,
    IReadOnlyList<string> Bundles,
    IReadOnlyList<ProfilePlugin> Plugins);

public sealed record UiBounds(int X, int Y, int Width, int Height);

public sealed record MiniAreaState(
    string AreaId,
    string Title,
    bool Collapsed,
    int Width,
    int MinimumWidth,
    int MaximumWidth);

public sealed record MiniState(
    bool Collapsed,
    bool TaskbarDocked,
    UiBounds Bounds,
    string Anchor,
    long UiRevision,
    IReadOnlyList<MiniAreaState> Areas);

public sealed record MiniMutationResult(
    long Revision,
    long UiRevision,
    bool Collapsed,
    UiBounds BeforeBounds,
    UiBounds AfterBounds,
    bool AnchorPreserved,
    bool Persisted,
    string? AreaId,
    int? Width,
    IReadOnlyList<MiniAreaState> Areas);

public sealed record WindowInspection(
    bool Running,
    int ProcessId,
    string? Executable,
    bool Responsive,
    UiBounds Bounds,
    bool Topmost,
    int Dpi)
{
    public string? AppModuleId { get; init; }
    public string? CoreModuleId { get; init; }
    public string? DataDirectory { get; init; }
    public bool TaskbarDocked { get; init; }
    public double? ObservedOutputTokensPerSecond { get; init; }
    public DateTimeOffset? OutputRateCapturedAt { get; init; }
    public double? MeasuredOutputTokensPerSecond { get; init; }
    public double? EstimatedOutputTokensPerSecond { get; init; }
    public int OutputRateWindowSeconds { get; init; }
    public int GeneratingSessionCount { get; init; }
    public int ToolsSessionCount { get; init; }
    public double? NetworkReceiveBytesPerSecond { get; init; }
    public double? NetworkSendBytesPerSecond { get; init; }
    public DateTimeOffset? NetworkCapturedAt { get; init; }
    public int NetworkInterfaceCount { get; init; }
    public long? NetworkReceivedTotalBytes { get; init; }
    public long? NetworkSentTotalBytes { get; init; }
    public long? NetworkTotalBytes { get; init; }
    public DateTimeOffset? NetworkHistorySince { get; init; }
    public DateTimeOffset? NetworkHistorySavedAt { get; init; }
    public bool NetworkHistorySaveFailed { get; init; }
    public long? DisplayedTokenTotal { get; init; }
    public bool RadarVisible { get; init; }
    public bool RouterVisible { get; init; }
    public string? RouterProjectKey { get; init; }
    public string? RouterRequestId { get; init; }
    public DateTimeOffset? RouterObservedAt { get; init; }
    public string[]? RadarModelGroups { get; init; }
    public string[]? CachedRadarModelGroups { get; init; }
    public DateTimeOffset? RadarTrialObservedAt { get; init; }
    public int RecentConversationCount { get; init; }
    public int PendingConversationCount { get; init; }
    public double? LatestTurnOutputTokensPerSecond { get; init; }
}

public sealed record SnapshotSummary(
    HostRevisions Revisions,
    IReadOnlyList<PluginSnapshotSummary> Plugins);

public sealed record CodexHistoryExportRequest(string Period = "retained", string? Model = null,
    string? Project = null, string Search = "", int SortColumn = -1, bool Descending = true,
    int Offset = 0, string? Snapshot = null);

public sealed record CodexHistoryExportChunk(string Snapshot, int NextOffset, int TotalBytes, byte[] Data,
    int TurnCount, int ObservedCount, DateTimeOffset CapturedAt, int PendingCount);

public sealed record PluginSnapshotSummary(
    string PluginId,
    bool Enabled,
    PluginHealth Health,
    long DataRevision,
    IReadOnlyList<MiniCardContribution>? Cards);

public sealed record PagedPluginData(
    string PluginId,
    long DataRevision,
    IReadOnlyList<JsonElement> Items,
    string? NextCursor);

public sealed record HostEvent(
    string EventId,
    long Revision,
    string Type,
    JsonElement Payload);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApiRequestEnvelope))]
[JsonSerializable(typeof(ApiResponseEnvelope))]
[JsonSerializable(typeof(HostDescription))]
[JsonSerializable(typeof(HostRevisions))]
[JsonSerializable(typeof(PluginStatus))]
[JsonSerializable(typeof(PluginStatus[]))]
[JsonSerializable(typeof(EffectiveProfile))]
[JsonSerializable(typeof(MiniAreaState))]
[JsonSerializable(typeof(MiniAreaState[]))]
[JsonSerializable(typeof(MiniState))]
[JsonSerializable(typeof(MiniMutationResult))]
[JsonSerializable(typeof(WindowInspection))]
[JsonSerializable(typeof(CodexHistoryExportRequest))]
[JsonSerializable(typeof(CodexHistoryExportChunk))]
[JsonSerializable(typeof(SnapshotSummary))]
[JsonSerializable(typeof(PagedPluginData))]
[JsonSerializable(typeof(HostEvent))]
[JsonSerializable(typeof(HostEvent[]))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
public partial class ApiJsonContext : JsonSerializerContext;
