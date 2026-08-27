using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.Core;

public static class CodexMiniDisplayModes
{
    public const string Accounts = "accounts";
    public const string Pool = "pool";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Pool => Pool,
        _ => Accounts,
    };
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;
    public const string DefaultBackgroundPalette = "midnight";
    public const int CurrentPlacementSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Dictionary<string, bool> PluginEnabled { get; set; } = new(StringComparer.Ordinal);
    public string[] EnabledProviders { get; set; } = ["claude", "codex"];
    public int RefreshMinutes { get; set; } = 5;
    public bool AutoRefreshClaudeOAuth { get; set; } = true;
    public bool OpenAtLogin { get; set; }
    public bool KeepRunning { get; set; }
    public bool EnableAlerts { get; set; } = true;
    public bool UseTaskbarRings { get; set; }
    public bool TaskbarDocked { get; set; } = true;
    public bool EnableAnimations { get; set; } = true;
    public bool EnableRadar { get; set; }
    public bool EnableRadarAlerts { get; set; }
    public bool EnableCodexEconomyBar { get; set; } = true;
    public bool EnableAiGatewayBalance { get; set; }
    public bool EnableSub2ApiPool { get; set; }
    public bool MiniProviderAreaCollapsed { get; set; }
    public Dictionary<string, MiniAreaLayout> MiniAreaLayouts { get; set; } = new(StringComparer.Ordinal);
    public string[] MiniAreaOrder { get; set; } = [];
    // Legacy read-only migration fields from the per-card collapse prototypes.
    public bool AiGatewayMiniCollapsed { get; set; }
    public string[] CollapsedMiniGroups { get; set; } = [];
    public string CodexMiniDisplayMode { get; set; } = CodexMiniDisplayModes.Accounts;
    public string BackgroundPalette { get; set; } = DefaultBackgroundPalette;
    public string Locale { get; set; } = "zh-CN";
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double? TaskbarPosition { get; set; }
    public string? TaskbarMonitor { get; set; }
    public Dictionary<string, double> TaskbarPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int PlacementSchemaVersion { get; set; } = CurrentPlacementSchemaVersion;
    public PlacementMigrationSeed? PlacementMigrationSeed { get; set; }
    public Dictionary<string, WindowPlacementProfile> PlacementProfiles { get; set; } = new(StringComparer.Ordinal);

    public bool IsEnabled(ProviderKind provider) => provider == ProviderKind.AiGateway
        ? EnableAiGatewayBalance
        : EnabledProviders.Contains(
            provider == ProviderKind.Claude ? "claude" : "codex",
            StringComparer.OrdinalIgnoreCase);

    public bool IsPluginEnabled(string pluginId, bool fallback = false) =>
        PluginEnabled.TryGetValue(pluginId, out var enabled) ? enabled : fallback;

    public void SetPluginEnabled(string pluginId, bool enabled)
    {
        PluginEnabled[pluginId] = enabled;
        switch (pluginId)
        {
            case "zgstokenbar.provider.claude":
                SetLegacyProvider("claude", enabled);
                break;
            case "zgstokenbar.provider.codex":
                SetLegacyProvider("codex", enabled);
                if (!enabled) PluginEnabled["zgstokenbar.usage.codex-local"] = false;
                break;
            case "zgstokenbar.usage.codex-local":
                break;
            case "zgstokenbar.intelligence.radar":
                EnableRadar = enabled;
                if (!enabled) EnableRadarAlerts = false;
                break;
            case "zgstokenbar.provider.ai-gateway":
                EnableAiGatewayBalance = enabled;
                break;
            case "zgstokenbar.metrics.system":
                break;
        }
    }

    private void SetLegacyProvider(string provider, bool enabled)
    {
        var providers = EnabledProviders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabled) providers.Add(provider);
        else providers.Remove(provider);
        EnabledProviders = providers.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static bool IsPluginId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        var previousSeparator = true;
        foreach (var character in value)
        {
            if (character is '.' or '-')
            {
                if (previousSeparator) return false;
                previousSeparator = true;
                continue;
            }
            if (!char.IsAsciiLetterOrDigit(character) || char.IsAsciiLetterUpper(character)) return false;
            previousSeparator = false;
        }
        return !previousSeparator;
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        EnabledProviders = (EnabledProviders ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value is "claude" or "codex")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        PluginEnabled = (PluginEnabled ?? new Dictionary<string, bool>())
            .Where(entry => IsPluginId(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        PluginEnabled["zgstokenbar.metrics.system"] =
            PluginEnabled.TryGetValue("zgstokenbar.metrics.system", out var systemMetrics)
                ? systemMetrics
                : true;
        PluginEnabled["zgstokenbar.provider.claude"] = EnabledProviders.Contains("claude", StringComparer.Ordinal);
        PluginEnabled["zgstokenbar.provider.codex"] = EnabledProviders.Contains("codex", StringComparer.Ordinal);
        PluginEnabled["zgstokenbar.usage.codex-local"] =
            PluginEnabled["zgstokenbar.provider.codex"]
            && (PluginEnabled.TryGetValue("zgstokenbar.usage.codex-local", out var localUsage)
                ? localUsage
                : true);
        PluginEnabled["zgstokenbar.intelligence.radar"] = EnableRadar;
        PluginEnabled["zgstokenbar.provider.ai-gateway"] = EnableAiGatewayBalance;
        if (!PluginEnabled["zgstokenbar.provider.codex"])
        {
            EnableSub2ApiPool = false;
        }

        RefreshMinutes = Math.Clamp(RefreshMinutes, 1, 60);
        if (KeepRunning) OpenAtLogin = true;
        var miniAreaLayouts = new Dictionary<string, MiniAreaLayout>(StringComparer.Ordinal);
        foreach (var entry in MiniAreaLayouts ?? new Dictionary<string, MiniAreaLayout>())
        {
            if (!IsPluginId(entry.Key) || entry.Value is null) continue;
            var normalized = entry.Value.Normalized(entry.Key);
            miniAreaLayouts[entry.Key] = string.Equals(
                entry.Key,
                MiniAreaIds.CodexEconomy,
                StringComparison.Ordinal)
                ? normalized with { Width = null }
                : normalized;
        }
        if (MiniProviderAreaCollapsed)
        {
            miniAreaLayouts[MiniAreaIds.Claude] = new(true, miniAreaLayouts.GetValueOrDefault(MiniAreaIds.Claude)?.Width);
            miniAreaLayouts[MiniAreaIds.Codex] = new(true, miniAreaLayouts.GetValueOrDefault(MiniAreaIds.Codex)?.Width);
            miniAreaLayouts[MiniAreaIds.AiGateway] = new(true, miniAreaLayouts.GetValueOrDefault(MiniAreaIds.AiGateway)?.Width);
        }
        if (AiGatewayMiniCollapsed)
        {
            miniAreaLayouts[MiniAreaIds.AiGateway] = new(true, miniAreaLayouts.GetValueOrDefault(MiniAreaIds.AiGateway)?.Width);
        }
        foreach (var legacyGroup in CollapsedMiniGroups ?? [])
        {
            var areaId = legacyGroup?.Trim().ToLowerInvariant() switch
            {
                { } value when value.StartsWith("claude:", StringComparison.Ordinal) => MiniAreaIds.Claude,
                { } value when value.StartsWith("codex:", StringComparison.Ordinal) => MiniAreaIds.Codex,
                _ => null,
            };
            if (areaId is null) continue;
            miniAreaLayouts[areaId] = new(true, miniAreaLayouts.GetValueOrDefault(areaId)?.Width);
        }
        MiniAreaLayouts = miniAreaLayouts;
        MiniAreaOrder = (MiniAreaOrder ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(IsPluginId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        MiniProviderAreaCollapsed = false;
        AiGatewayMiniCollapsed = false;
        CollapsedMiniGroups = [];
        CodexMiniDisplayMode = CodexMiniDisplayModes.Normalize(CodexMiniDisplayMode);
        BackgroundPalette = NormalizeBackgroundPalette(BackgroundPalette);
        Locale = string.Equals(Locale?.Trim(), "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "zh-CN";
        if (!EnableRadar) EnableRadarAlerts = false;
        if (TaskbarPosition is { } taskbarPosition)
        {
            TaskbarPosition = double.IsFinite(taskbarPosition)
                ? Math.Clamp(taskbarPosition, 0, 1)
                : null;
        }
        TaskbarMonitor = string.IsNullOrWhiteSpace(TaskbarMonitor)
            ? null
            : TaskbarMonitor.Trim();

        var taskbarPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in TaskbarPositions ?? new Dictionary<string, double>())
        {
            var monitor = entry.Key?.Trim();
            if (string.IsNullOrWhiteSpace(monitor) || !double.IsFinite(entry.Value)) continue;
            taskbarPositions[monitor] = Math.Clamp(entry.Value, 0, 1);
        }
        TaskbarPositions = taskbarPositions;

        PlacementSchemaVersion = CurrentPlacementSchemaVersion;
        PlacementMigrationSeed?.Normalize();
        var placementProfiles = new Dictionary<string, WindowPlacementProfile>(StringComparer.Ordinal);
        foreach (var entry in PlacementProfiles ?? new Dictionary<string, WindowPlacementProfile>())
        {
            var topologyKey = entry.Key?.Trim();
            if (!PlacementKey.IsTopology(topologyKey) || entry.Value is null) continue;
            entry.Value.Normalize();
            placementProfiles[topologyKey!] = entry.Value;
        }
        PlacementProfiles = placementProfiles;
    }

    public void CapturePlacementMigrationSeed()
    {
        PlacementMigrationSeed ??= new PlacementMigrationSeed
        {
            TaskbarDocked = TaskbarDocked,
            WindowX = WindowX,
            WindowY = WindowY,
            TaskbarPosition = TaskbarPosition,
            TaskbarMonitor = TaskbarMonitor,
            TaskbarPositions = CopyTaskbarPositions(TaskbarPositions),
        };
        PlacementSchemaVersion = CurrentPlacementSchemaVersion;
    }

    public void CopyPlacementStateFrom(AppSettings source)
    {
        PlacementSchemaVersion = CurrentPlacementSchemaVersion;
        PlacementMigrationSeed = source.PlacementMigrationSeed?.Copy();
        PlacementProfiles = CopyPlacementProfiles(source.PlacementProfiles);
        TaskbarDocked = source.TaskbarDocked;
        WindowX = source.WindowX;
        WindowY = source.WindowY;
        TaskbarPosition = source.TaskbarPosition;
        TaskbarMonitor = source.TaskbarMonitor;
        TaskbarPositions = CopyTaskbarPositions(source.TaskbarPositions);
    }

    public void CopyMiniAreaLayoutsFrom(AppSettings source)
    {
        MiniAreaLayouts = CopyMiniAreaLayouts(source.MiniAreaLayouts);
        MiniAreaOrder = CopyMiniAreaOrder(source.MiniAreaOrder);
    }

    public static Dictionary<string, MiniAreaLayout> CopyMiniAreaLayouts(
        IReadOnlyDictionary<string, MiniAreaLayout>? source) =>
        (source ?? new Dictionary<string, MiniAreaLayout>())
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value with { }, StringComparer.Ordinal);

    public static string[] CopyMiniAreaOrder(IEnumerable<string>? source) =>
        (source ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Where(IsPluginId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static Dictionary<string, WindowPlacementProfile> CopyPlacementProfiles(
        IEnumerable<KeyValuePair<string, WindowPlacementProfile>>? profiles)
    {
        var copy = new Dictionary<string, WindowPlacementProfile>(StringComparer.Ordinal);
        if (profiles is null) return copy;
        foreach (var entry in profiles)
        {
            if (!PlacementKey.IsTopology(entry.Key) || entry.Value is null) continue;
            copy[entry.Key] = entry.Value.Copy();
        }
        return copy;
    }

    internal static Dictionary<string, double> CopyTaskbarPositions(
        IEnumerable<KeyValuePair<string, double>>? positions)
    {
        var copy = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (positions is null) return copy;
        foreach (var entry in positions)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || !double.IsFinite(entry.Value)) continue;
            copy[entry.Key.Trim()] = Math.Clamp(entry.Value, 0, 1);
        }
        return copy;
    }

    public static string NormalizeBackgroundPalette(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "midnight" or "graphite" or "navy" or "plum"
            ? normalized
            : DefaultBackgroundPalette;
    }
}

public sealed class PlacementMigrationSeed
{
    public bool TaskbarDocked { get; set; } = true;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double? TaskbarPosition { get; set; }
    public string? TaskbarMonitor { get; set; }
    public Dictionary<string, double> TaskbarPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PlacementMigrationSeed Copy() => new()
    {
        TaskbarDocked = TaskbarDocked,
        WindowX = WindowX,
        WindowY = WindowY,
        TaskbarPosition = TaskbarPosition,
        TaskbarMonitor = TaskbarMonitor,
        TaskbarPositions = AppSettings.CopyTaskbarPositions(TaskbarPositions),
    };

    internal void Normalize()
    {
        if (TaskbarPosition is { } position)
        {
            TaskbarPosition = double.IsFinite(position) ? Math.Clamp(position, 0, 1) : null;
        }
        TaskbarMonitor = string.IsNullOrWhiteSpace(TaskbarMonitor) ? null : TaskbarMonitor.Trim();
        TaskbarPositions = AppSettings.CopyTaskbarPositions(TaskbarPositions);
    }
}

public sealed class WindowPlacementProfile
{
    public bool IsDocked { get; set; } = true;
    public string? DockedMonitorKey { get; set; }
    public Dictionary<string, double> TaskbarPositions { get; set; } = new(StringComparer.Ordinal);
    public string? FloatingMonitorKey { get; set; }
    public double? FloatingX { get; set; }
    public double? FloatingY { get; set; }

    public WindowPlacementProfile Copy() => new()
    {
        IsDocked = IsDocked,
        DockedMonitorKey = DockedMonitorKey,
        TaskbarPositions = new Dictionary<string, double>(
            TaskbarPositions ?? new Dictionary<string, double>(),
            StringComparer.Ordinal),
        FloatingMonitorKey = FloatingMonitorKey,
        FloatingX = FloatingX,
        FloatingY = FloatingY,
    };

    internal void Normalize()
    {
        DockedMonitorKey = PlacementKey.IsMonitor(DockedMonitorKey?.Trim()) ? DockedMonitorKey!.Trim() : null;
        FloatingMonitorKey = PlacementKey.IsMonitor(FloatingMonitorKey?.Trim()) ? FloatingMonitorKey!.Trim() : null;
        FloatingX = NormalizeRatio(FloatingX);
        FloatingY = NormalizeRatio(FloatingY);

        var positions = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in TaskbarPositions ?? new Dictionary<string, double>())
        {
            var monitorKey = entry.Key?.Trim();
            if (!PlacementKey.IsMonitor(monitorKey) || !double.IsFinite(entry.Value)) continue;
            positions[monitorKey!] = Math.Clamp(entry.Value, 0, 1);
        }
        TaskbarPositions = positions;
    }

    private static double? NormalizeRatio(double? value) => value is { } ratio && double.IsFinite(ratio)
        ? Math.Clamp(ratio, 0, 1)
        : null;
}

public static class PlacementKey
{
    private const string MonitorPrefix = "monitor-v1:";
    private const string TopologyPrefix = "topology-v1:";

    public static bool IsMonitor(string? value) => IsHashKey(value, MonitorPrefix);
    public static bool IsTopology(string? value) => IsHashKey(value, TopologyPrefix);

    private static bool IsHashKey(string? value, string prefix)
    {
        if (value is null || value.Length != prefix.Length + 64 || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        return value.AsSpan(prefix.Length).ToString()
            .All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

internal sealed class WindowPlacementProfilesJsonConverter
    : JsonConverter<Dictionary<string, WindowPlacementProfile>>
{
    public override Dictionary<string, WindowPlacementProfile> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var profiles = new Dictionary<string, WindowPlacementProfile>(StringComparer.Ordinal);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return profiles;

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            try
            {
                var profile = entry.Value.Deserialize(
                    AppSettingsJsonContext.Default.WindowPlacementProfile);
                if (profile is not null) profiles[entry.Name] = profile;
            }
            catch (JsonException)
            {
                // One damaged topology must not invalidate unrelated settings or profiles.
            }
        }
        return profiles;
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, WindowPlacementProfile> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var entry in value)
        {
            writer.WritePropertyName(entry.Key);
            JsonSerializer.Serialize(
                writer,
                entry.Value,
                AppSettingsJsonContext.Default.WindowPlacementProfile);
        }
        writer.WriteEndObject();
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(WindowPlacementProfile))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

public sealed class AppSettingsStore
{
    private const int SettingsLoadAttempts = 3;
    private const int SettingsLoadRetryMilliseconds = 25;
    private readonly string? _legacySettingsPath;
    private readonly object _writeProtectionSync = new();
    private readonly HashSet<string> _writeProtectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new WindowPlacementProfilesJsonConverter() },
    };
    private static readonly AppSettingsJsonContext SettingsJsonContext = new(
        new JsonSerializerOptions(JsonOptions));

    public AppSettingsStore(
        string? dataDirectory = null,
        string? legacySettingsPath = null)
    {
        var roamingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        DataDirectory = dataDirectory ?? Path.Combine(roamingDirectory, "ZGSTokenBar");
        _legacySettingsPath = legacySettingsPath;
    }

    public string DataDirectory { get; }

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string CachePath => Path.Combine(DataDirectory, "quota-cache.json");
    public string QuotaRateHistoryPath => Path.Combine(DataDirectory, "quota-rate-history.json");
    public string CodexTokenUsageIndexPath => Path.Combine(DataDirectory, "codex-token-usage-index.json");
    public string CodexQuotaTokenHistoryPath => Path.Combine(DataDirectory, "codex-quota-token-history.json");
    public string RadarStatePath => Path.Combine(DataDirectory, "radar-state.json");
    public string RadarRoutingPath => Path.Combine(DataDirectory, "radar-routing.json");

    public AppSettings Load()
    {
        var loaded = TryLoadSettingsFile(SettingsPath, out var invalidContents);
        if (loaded is not null) return loaded;

        if (invalidContents is not null) PreserveCorruptSettings(invalidContents);
        if (File.Exists(SettingsPath + ".corrupt.bak"))
        {
            var recovered = TryLoadSettingsFile(SettingsPath + ".corrupt.bak", out _);
            if (recovered is not null) return recovered;
        }

        var legacy = ImportLegacySettings();
        var imported = legacy ?? new AppSettings();
        if (legacy is not null) imported.CapturePlacementMigrationSeed();
        imported.Normalize();
        return imported;
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(DataDirectory);
        if (!CredentialSupport.AtomicWrite(
                SettingsPath,
                JsonSerializer.Serialize(settings, SettingsJsonContext.AppSettings)))
        {
            throw new IOException("Settings file is busy. Please retry.");
        }
    }

    public void SetAiGatewayBalanceEnabled(bool enabled)
    {
        JsonObject settings;
        string? expectedContents = null;
        if (File.Exists(SettingsPath))
        {
            try
            {
                var contents = File.ReadAllText(SettingsPath);
                expectedContents = contents;
                settings = JsonNode.Parse(contents) as JsonObject
                    ?? throw new JsonException("Settings root must be an object.");
            }
            catch (JsonException exception)
            {
                throw new IOException("Settings file is invalid. Please repair it before changing this setting.", exception);
            }
        }
        else
        {
            settings = new JsonObject();
        }

        settings["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        settings["enableAiGatewayBalance"] = enabled;
        var pluginEnabled = settings["pluginEnabled"] as JsonObject ?? new JsonObject();
        pluginEnabled["zgstokenbar.provider.ai-gateway"] = enabled;
        settings["pluginEnabled"] = pluginEnabled;
        if (!CredentialSupport.AtomicWrite(
                SettingsPath,
                settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                expectedContents))
        {
            throw new IOException("Settings file is busy. Please retry.");
        }
    }

    public void SetSub2ApiPoolEnabled(bool enabled)
    {
        JsonObject settings;
        string? expectedContents = null;
        if (File.Exists(SettingsPath))
        {
            try
            {
                var contents = File.ReadAllText(SettingsPath);
                expectedContents = contents;
                settings = JsonNode.Parse(contents) as JsonObject
                    ?? throw new JsonException("Settings root must be an object.");
            }
            catch (JsonException exception)
            {
                throw new IOException("Settings file is invalid. Please repair it before changing this setting.", exception);
            }
        }
        else
        {
            settings = new JsonObject();
        }

        settings["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        settings["enableSub2ApiPool"] = enabled;
        if (!CredentialSupport.AtomicWrite(
                SettingsPath,
                settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                expectedContents))
        {
            throw new IOException("Settings file is busy. Please retry.");
        }
    }

    public bool IsAiGatewayBalanceEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var settings = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            return settings?["enableAiGatewayBalance"]?.GetValue<bool>() == true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsSub2ApiPoolEnabled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var settings = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            return settings?["enableSub2ApiPool"]?.GetValue<bool>() == true;
        }
        catch
        {
            return false;
        }
    }

    public QuotaSnapshot? LoadCache(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var snapshot = JsonSerializer.Deserialize<QuotaSnapshot>(File.ReadAllText(CachePath), JsonOptions);
            if (snapshot is null || snapshot.CapturedAt > now.AddMinutes(1) || now - snapshot.CapturedAt > TimeSpan.FromDays(7))
            {
                return null;
            }

            var freshCards = snapshot.Cards
                .Where(card => QuotaCoordinator.IsFresh(card.CapturedAt ?? snapshot.CapturedAt, now))
                .Select(card => NormalizeCachedCard(card, now))
                .ToArray();
            return snapshot with { Cards = freshCards };
        }
        catch
        {
            return null;
        }
    }

    public void SaveCache(QuotaSnapshot snapshot)
    {
        Directory.CreateDirectory(DataDirectory);
        if (!CredentialSupport.AtomicWrite(CachePath, JsonSerializer.Serialize(snapshot, JsonOptions)))
        {
            throw new IOException("Quota cache file is busy. Please retry.");
        }
    }

    private static QuotaCard NormalizeCachedCard(QuotaCard card, DateTimeOffset now)
    {
        var normalized = card;
        if (card.Balance is { } balance)
        {
            var status = AiGatewayBalancePolicy.EffectiveStatus(balance, now);
            if (status != balance.Status) normalized = normalized with { Balance = balance with { Status = status } };
        }
        if (normalized.Sub2ApiPool is { } pool)
        {
            var status = Sub2ApiPoolPolicy.EffectiveStatus(pool, now);
            if (status != pool.Status) normalized = normalized with { Sub2ApiPool = pool with { Status = status } };
        }
        if (normalized.Sub2ApiUsage is { } usage)
        {
            var status = Sub2ApiUsagePolicy.EffectiveStatus(usage, now);
            if (status != usage.Status) normalized = normalized with { Sub2ApiUsage = usage with { Status = status } };
        }
        if (normalized.Sub2ApiQuota is { } quota)
        {
            var status = Sub2ApiQuotaPolicy.EffectiveStatus(quota, now);
            if (status != quota.Status) normalized = normalized with { Sub2ApiQuota = quota with { Status = status } };
        }
        if (normalized.Sub2ApiAccountAvailability is { } availability)
        {
            var effective = Sub2ApiAccountAvailabilityPolicy.EffectiveAvailability(availability, now);
            if (effective != availability) normalized = normalized with { Sub2ApiAccountAvailability = effective };
        }
        return normalized;
    }

    public QuotaRateHistory LoadQuotaRateHistory(DateTimeOffset now)
    {
        try
        {
            var json = ReadTextWithRetry(QuotaRateHistoryPath);
            using var document = JsonDocument.Parse(json);
            var schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out var schema)
                && schema.TryGetInt32(out var parsedSchema)
                    ? parsedSchema
                    : 1;
            if (schemaVersion is not (1 or QuotaRateHistory.CurrentSchemaVersion))
            {
                throw new JsonException("Unsupported quota rate history schema.");
            }

            var history = JsonSerializer.Deserialize<QuotaRateHistory>(
                json,
                JsonOptions);
            if (history is null)
            {
                throw new JsonException("Invalid quota rate history.");
            }

            var result = new QuotaPaceTracker(history).Export(now);
            ClearWriteProtection(QuotaRateHistoryPath);
            return result;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            ClearWriteProtection(QuotaRateHistoryPath);
            return new QuotaRateHistory();
        }
        catch (IOException)
        {
            ProtectWritePath(QuotaRateHistoryPath);
            return new QuotaRateHistory();
        }
        catch (UnauthorizedAccessException)
        {
            ProtectWritePath(QuotaRateHistoryPath);
            return new QuotaRateHistory();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            PreserveCorruptFile(QuotaRateHistoryPath);
            return new QuotaRateHistory();
        }
    }

    public void SaveQuotaRateHistory(QuotaRateHistory history)
    {
        EnsureWritable(QuotaRateHistoryPath, "Quota rate history file is busy. Please retry.");
        Directory.CreateDirectory(DataDirectory);
        history.SchemaVersion = QuotaRateHistory.CurrentSchemaVersion;
        if (!CredentialSupport.AtomicWrite(
                QuotaRateHistoryPath,
                JsonSerializer.Serialize(history, JsonOptions)))
        {
            throw new IOException("Quota rate history file is busy. Please retry.");
        }
    }

    public CodexTokenUsageIndex LoadCodexTokenUsageIndex()
    {
        try
        {
            var json = ReadTextWithRetry(CodexTokenUsageIndexPath);
            using var document = JsonDocument.Parse(json);
            var schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out var schema)
                && schema.TryGetInt32(out var parsedSchema)
                    ? parsedSchema
                    : 1;
            if (schemaVersion is not (1 or 2 or 3 or 4 or CodexTokenUsageIndex.CurrentSchemaVersion))
            {
                throw new JsonException("Unsupported Codex token usage index schema.");
            }

            var index = JsonSerializer.Deserialize<CodexTokenUsageIndex>(
                json,
                JsonOptions);
            if (index is null)
            {
                throw new JsonException("Invalid Codex token usage index.");
            }
            index.Files ??= [];
            index.SchemaVersion = CodexTokenUsageIndex.CurrentSchemaVersion;
            ClearWriteProtection(CodexTokenUsageIndexPath);
            return index;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            ClearWriteProtection(CodexTokenUsageIndexPath);
            return new CodexTokenUsageIndex();
        }
        catch (IOException)
        {
            ProtectWritePath(CodexTokenUsageIndexPath);
            return new CodexTokenUsageIndex();
        }
        catch (UnauthorizedAccessException)
        {
            ProtectWritePath(CodexTokenUsageIndexPath);
            return new CodexTokenUsageIndex();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            PreserveCorruptFile(CodexTokenUsageIndexPath);
            return new CodexTokenUsageIndex();
        }
    }

    public void SaveCodexTokenUsageIndex(CodexTokenUsageIndex index)
    {
        EnsureWritable(CodexTokenUsageIndexPath, "Codex token usage index is busy. Please retry.");
        Directory.CreateDirectory(DataDirectory);
        index.SchemaVersion = CodexTokenUsageIndex.CurrentSchemaVersion;
        if (!CredentialSupport.AtomicWrite(
                CodexTokenUsageIndexPath,
                JsonSerializer.Serialize(index, JsonOptions)))
        {
            throw new IOException("Codex token usage index is busy. Please retry.");
        }
    }

    public CodexQuotaTokenHistory LoadCodexQuotaTokenHistory()
    {
        try
        {
            var json = ReadTextWithRetry(CodexQuotaTokenHistoryPath);
            using var document = JsonDocument.Parse(json);
            var schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out var schema)
                && schema.TryGetInt32(out var parsedSchema)
                    ? parsedSchema
                    : 1;
            if (schemaVersion != CodexQuotaTokenHistory.CurrentSchemaVersion)
            {
                throw new JsonException("Unsupported Codex quota token history schema.");
            }

            var history = JsonSerializer.Deserialize<CodexQuotaTokenHistory>(json, JsonOptions);
            if (!CodexQuotaTokenTracker.IsValidHistory(history))
            {
                throw new JsonException("Invalid Codex quota token history.");
            }

            ClearWriteProtection(CodexQuotaTokenHistoryPath);
            return history!;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            ClearWriteProtection(CodexQuotaTokenHistoryPath);
            return new CodexQuotaTokenHistory();
        }
        catch (IOException)
        {
            ProtectWritePath(CodexQuotaTokenHistoryPath);
            return new CodexQuotaTokenHistory();
        }
        catch (UnauthorizedAccessException)
        {
            ProtectWritePath(CodexQuotaTokenHistoryPath);
            return new CodexQuotaTokenHistory();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            PreserveCorruptFile(CodexQuotaTokenHistoryPath);
            return new CodexQuotaTokenHistory();
        }
    }

    public void SaveCodexQuotaTokenHistory(CodexQuotaTokenHistory history)
    {
        if (!CodexQuotaTokenTracker.IsValidHistory(history))
        {
            throw new ArgumentException("Invalid Codex quota token history.", nameof(history));
        }

        EnsureWritable(CodexQuotaTokenHistoryPath, "Codex quota token history is busy. Please retry.");
        Directory.CreateDirectory(DataDirectory);
        history.SchemaVersion = CodexQuotaTokenHistory.CurrentSchemaVersion;
        if (!CredentialSupport.AtomicWrite(
                CodexQuotaTokenHistoryPath,
                JsonSerializer.Serialize(history, JsonOptions)))
        {
            throw new IOException("Codex quota token history file is busy. Please retry.");
        }
    }

    public RadarAlertState LoadRadarState()
    {
        try
        {
            var state = JsonSerializer.Deserialize<RadarAlertState>(
                ReadTextWithRetry(RadarStatePath),
                JsonOptions) ?? new RadarAlertState();
            state.NotifiedEventIds = (state.NotifiedEventIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .TakeLast(50)
                .ToList();
            state.ViewedEventIdsBySurface = NormalizeRadarViewedEvents(state.ViewedEventIdsBySurface);
            if (state.LastSnapshot is { } snapshot) state.LastSnapshot = RadarSnapshotLimits.Trim(snapshot);
            ClearWriteProtection(RadarStatePath);
            return state;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            ClearWriteProtection(RadarStatePath);
            return new RadarAlertState();
        }
        catch (IOException)
        {
            ProtectWritePath(RadarStatePath);
            return new RadarAlertState();
        }
        catch (UnauthorizedAccessException)
        {
            ProtectWritePath(RadarStatePath);
            return new RadarAlertState();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            PreserveCorruptFile(RadarStatePath);
            return new RadarAlertState();
        }
    }

    public void SaveRadarState(RadarAlertState state)
    {
        EnsureWritable(RadarStatePath, "Radar state file is busy. Please retry.");
        Directory.CreateDirectory(DataDirectory);
        state.NotifiedEventIds = (state.NotifiedEventIds ?? []).TakeLast(50).ToList();
        state.ViewedEventIdsBySurface = NormalizeRadarViewedEvents(state.ViewedEventIdsBySurface);
        if (state.LastSnapshot is { } snapshot)
        {
            state.LastSnapshot = RadarSnapshotLimits.Trim(snapshot);
            if (!CredentialSupport.AtomicWrite(
                    RadarRoutingPath,
                    JsonSerializer.Serialize(RadarRoutingSnapshotBuilder.Build(state.LastSnapshot), JsonOptions)))
            {
                throw new IOException("Radar routing file is busy. Please retry.");
            }
        }
        else if (!CredentialSupport.AtomicWrite(
                     RadarRoutingPath,
                     JsonSerializer.Serialize(new { error = "radar_missing" }, JsonOptions)))
        {
            throw new IOException("Radar routing file is busy. Please retry.");
        }
        if (!CredentialSupport.AtomicWrite(RadarStatePath, JsonSerializer.Serialize(state, JsonOptions)))
        {
            throw new IOException("Radar state file is busy. Please retry.");
        }
    }

    private static string ReadTextWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException
                && exception is not FileNotFoundException and not DirectoryNotFoundException
                && attempt + 1 < SettingsLoadAttempts)
            {
                Thread.Sleep(SettingsLoadRetryMilliseconds);
            }
        }
    }

    private void ProtectWritePath(string path)
    {
        lock (_writeProtectionSync) _writeProtectedPaths.Add(path);
    }

    private void ClearWriteProtection(string path)
    {
        lock (_writeProtectionSync) _writeProtectedPaths.Remove(path);
    }

    private void EnsureWritable(string path, string message)
    {
        lock (_writeProtectionSync)
        {
            if (_writeProtectedPaths.Contains(path)) throw new IOException(message);
        }
    }

    private static Dictionary<string, string> NormalizeRadarViewedEvents(
        IReadOnlyDictionary<string, string>? values) =>
        (values ?? new Dictionary<string, string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key)
                && !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private AppSettings? TryLoadSettingsFile(string path, out string? invalidContents)
    {
        invalidContents = null;
        for (var attempt = 0; attempt < SettingsLoadAttempts; attempt++)
        {
            string? json = null;
            try
            {
                if (!File.Exists(path)) return null;
                json = File.ReadAllText(path);
                using var document = JsonDocument.Parse(json);
                var schemaVersion = document.RootElement.TryGetProperty("schemaVersion", out var schema)
                    && schema.TryGetInt32(out var parsedSchema)
                        ? parsedSchema
                        : 1;
                if (schemaVersion is not (1 or AppSettings.CurrentSchemaVersion))
                {
                    throw new JsonException("Unsupported settings schema.");
                }
                var hasLocale = document.RootElement.TryGetProperty("locale", out _);
                var hasTaskbarDocked = document.RootElement.TryGetProperty("taskbarDocked", out _);
                var hasLegacyTaskbarMode = document.RootElement.TryGetProperty("useTaskbarRings", out _);
                var hasPlacementSchema = document.RootElement.TryGetProperty("placementSchemaVersion", out _);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.AppSettings)
                    ?? new AppSettings();
                if (!hasLocale) loaded.Locale = "en";
                if (!hasTaskbarDocked && hasLegacyTaskbarMode)
                {
                    loaded.TaskbarDocked = loaded.UseTaskbarRings;
                }
                if (!hasPlacementSchema) loaded.CapturePlacementMigrationSeed();
                loaded.Normalize();
                if (schemaVersion == 1
                    && string.Equals(path, SettingsPath, StringComparison.OrdinalIgnoreCase))
                {
                    PreserveV1Settings(json);
                    Save(loaded);
                }
                return loaded;
            }
            catch (Exception) when (attempt + 1 < SettingsLoadAttempts)
            {
                Thread.Sleep(SettingsLoadRetryMilliseconds);
            }
            catch (Exception)
            {
                invalidContents = json;
                return null;
            }
        }

        return null;
    }

    private void PreserveCorruptSettings(string contents)
    {
        try
        {
            var backupPath = SettingsPath + ".corrupt.bak";
            if (!File.Exists(backupPath)) File.WriteAllText(backupPath, contents);
        }
        catch
        {
            // Recovery is best-effort and must not prevent startup.
        }
    }

    private void PreserveV1Settings(string contents)
    {
        var backupPath = SettingsPath + ".v1.bak";
        if (File.Exists(backupPath)) return;
        Directory.CreateDirectory(DataDirectory);
        if (!CredentialSupport.AtomicWrite(backupPath, contents))
        {
            throw new IOException("Version 1 settings backup could not be written.");
        }
    }

    private static void PreserveCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backupPath = path + ".corrupt.bak";
            if (File.Exists(backupPath)) return;
            File.Copy(path, backupPath, false);
        }
        catch
        {
            // Recovery is best-effort and must not prevent startup.
        }
    }

    private AppSettings? ImportLegacySettings()
    {
        if (string.IsNullOrWhiteSpace(_legacySettingsPath) || !File.Exists(_legacySettingsPath)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_legacySettingsPath));
            var root = document.RootElement;
            var settings = new AppSettings();
            if (root.TryGetProperty("enabledProviders", out var providers) && providers.ValueKind == JsonValueKind.Array)
            {
                settings.EnabledProviders = providers.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToArray();
            }

            if (root.TryGetProperty("autoRefreshClaudeOAuth", out var oauth) && oauth.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.AutoRefreshClaudeOAuth = oauth.GetBoolean();
            }

            if (root.TryGetProperty("openAtLogin", out var login) && login.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.OpenAtLogin = login.GetBoolean();
            }

            if (root.TryGetProperty("enableAlerts", out var alerts) && alerts.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.EnableAlerts = alerts.GetBoolean();
            }

            if (root.TryGetProperty("locale", out var locale) && locale.ValueKind == JsonValueKind.String)
            {
                settings.Locale = locale.GetString() ?? "zh-CN";
            }

            if (root.TryGetProperty("pinnedBarPosition", out var position) && position.ValueKind == JsonValueKind.Object)
            {
                if (position.TryGetProperty("x", out var x) && x.TryGetInt32(out var xValue)) settings.WindowX = xValue;
                if (position.TryGetProperty("y", out var y) && y.TryGetInt32(out var yValue)) settings.WindowY = yValue;
            }

            return settings;
        }
        catch
        {
            return null;
        }
    }
}
