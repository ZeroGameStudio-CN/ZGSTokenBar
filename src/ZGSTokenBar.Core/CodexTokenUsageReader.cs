using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record CodexTokenUsageSummary(
    long TodayTokens,
    long LocalTokens,
    int SessionCount,
    DateTimeOffset CapturedAt,
    long? InputTokens = null,
    long? CachedInputTokens = null,
    long? TodayInputTokens = null,
    long? TodayCachedInputTokens = null,
    CodexSpendPeriod? TodaySpend = null,
    CodexSpendPeriod? YesterdaySpend = null,
    CodexSpendPeriod? Last30DaysSpend = null,
    CodexSpendHistory? SpendHistory = null)
{
    public static CodexTokenUsageSummary? ApplyCumulativeFloor(
        CodexTokenUsageSummary? summary,
        long? cumulativeFloor,
        DateTimeOffset capturedAt)
    {
        if (cumulativeFloor is not >= 0) return summary;
        if (summary is null)
        {
            return new CodexTokenUsageSummary(0, cumulativeFloor.Value, 0, capturedAt);
        }
        return cumulativeFloor > summary.LocalTokens
            ? summary with { LocalTokens = cumulativeFloor.Value }
            : summary;
    }

    public double? TodayCacheHitPercent => CacheHitPercent(TodayInputTokens, TodayCachedInputTokens);
    public double? TotalCacheHitPercent => CacheHitPercent(InputTokens, CachedInputTokens);

    private static double? CacheHitPercent(long? inputTokens, long? cachedInputTokens) =>
        inputTokens is > 0
        && cachedInputTokens is >= 0
        && cachedInputTokens <= inputTokens
            ? cachedInputTokens.Value * 100d / inputTokens.Value
            : null;
}

public sealed record CodexSpendDay(
    string LocalDate,
    CodexSpendPeriod Spend);

public sealed record CodexSpendModel(
    string Model,
    CodexSpendPeriod Spend);

public sealed record CodexSpendHistory(
    IReadOnlyList<CodexSpendDay> Days,
    IReadOnlyList<CodexSpendModel> Models,
    CodexSpendPeriod Last7DaysSpend);

public sealed class CodexTokenUsageIndex
{
    public const int CurrentSchemaVersion = 7;
    public const int CurrentAccountingVersion = 2;
    public const int CurrentSpendAccountingVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<CodexTokenUsageFileIndex> Files { get; set; } = [];
}

public sealed record CodexDailyModelUsage(
    string LocalDate,
    string? Model,
    string PricingTier,
    bool IsLongContext,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long UnattributedTokens = 0,
    long CacheWriteInputTokens = 0);

public sealed record CodexTokenUsageFileIndex(
    string Key,
    long Length,
    long LastWriteTimeUtcTicks,
    bool HasTokenData,
    long TotalTokens,
    long LastTotalTokens,
    string? LatestLocalDate,
    long LatestDayTokens,
    long? InputTokens = null,
    long? CachedInputTokens = null,
    long? LastInputTokens = null,
    long? LastCachedInputTokens = null,
    long? LatestDayInputTokens = null,
    long? LatestDayCachedInputTokens = null,
    int AccountingVersion = 0,
    bool? LegacyLifetimeOnly = null,
    List<CodexDailyModelUsage>? DailyModelUsage = null,
    string? SpendCurrentModel = null,
    string? SpendCurrentServiceTier = null,
    long? SpendLastTotalTokens = null,
    long? SpendLastInputTokens = null,
    long? SpendLastCachedInputTokens = null,
    long? SpendLastOutputTokens = null,
    int SpendAccountingVersion = 0,
    long? SpendLastCacheWriteInputTokens = null,
    long? SpendScannedLength = null);

public sealed record CodexTokenUsageReadResult(
    CodexTokenUsageSummary? Summary,
    CodexTokenUsageIndex Index,
    bool Changed);

internal sealed record CodexTokenUsageEvent(
    DateTimeOffset CapturedAt,
    long TotalTokens,
    long? InputTokens,
    long? CachedInputTokens,
    long? CacheWriteInputTokens,
    long? OutputTokens,
    long? LastTotalTokens,
    long? LastInputTokens,
    long? LastCachedInputTokens,
    long? LastCacheWriteInputTokens,
    long? LastOutputTokens);

internal sealed record CodexForkMetadata(
    string ParentThreadId,
    DateTimeOffset ForkedAt);

internal sealed record CodexForkBaseline(
    bool IsFork,
    CodexTokenUsageEvent? InheritedUsage);

internal sealed record CodexForkBaselineRequest(
    string Key,
    DateTimeOffset ForkedAt);

public sealed class CodexTokenUsageReader
{
    internal const int MaximumLineBytes = 256 * 1024;
    internal const int InitialTailBytes = 256 * 1024;
    internal const int MaximumTailProbeBytes = 8 * 1024 * 1024;
    internal const int SpendRetentionDays = 32;
    private const string StandardPricingTier = "standard";
    private const int MaximumSpendModelDisplayLength = 64;
    private const string UnknownSpendModel = "unknown";

    private readonly Dictionary<string, CodexTokenUsageFileIndex> _files;

    public CodexTokenUsageReader(CodexTokenUsageIndex? index = null)
    {
        _files = (index?.Files ?? [])
            .Where(file => !string.IsNullOrWhiteSpace(file.Key)
                && file.Length >= 0
                && file.TotalTokens >= 0
                && file.LastTotalTokens >= 0
                && file.LatestDayTokens >= 0
                && file.AccountingVersion is >= 0 and <= CodexTokenUsageIndex.CurrentAccountingVersion
                && file.InputTokens is null or >= 0
                && file.CachedInputTokens is null or >= 0
                && file.LastInputTokens is null or >= 0
                && file.LastCachedInputTokens is null or >= 0
                && file.LatestDayInputTokens is null or >= 0
                && file.LatestDayCachedInputTokens is null or >= 0
                && file.SpendAccountingVersion is >= 0 and <= CodexTokenUsageIndex.CurrentSpendAccountingVersion
                && file.SpendLastTotalTokens is null or >= 0
                && file.SpendLastInputTokens is null or >= 0
                && file.SpendLastCachedInputTokens is null or >= 0
                && file.SpendLastOutputTokens is null or >= 0
                && file.SpendLastCacheWriteInputTokens is null or >= 0
                && file.SpendScannedLength is null or >= 0
                && (file.SpendScannedLength is null || file.SpendScannedLength <= file.Length)
                && (file.InputTokens is null
                    || file.CachedInputTokens is null
                    || file.CachedInputTokens <= file.InputTokens)
                && (file.LastInputTokens is null
                    || file.LastCachedInputTokens is null
                    || file.LastCachedInputTokens <= file.LastInputTokens)
                && (file.LatestDayInputTokens is null
                    || file.LatestDayCachedInputTokens is null
                    || file.LatestDayCachedInputTokens <= file.LatestDayInputTokens)
                && (file.SpendLastInputTokens is null
                    || file.SpendLastCachedInputTokens is null
                    || file.SpendLastCacheWriteInputTokens is null
                    || file.SpendLastCacheWriteInputTokens <= file.SpendLastInputTokens
                        && file.SpendLastCachedInputTokens
                            <= file.SpendLastInputTokens - file.SpendLastCacheWriteInputTokens)
                && (file.DailyModelUsage is null
                    || file.DailyModelUsage.All(IsValidDailyUsage)))
            .GroupBy(file => file.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    }

    public CodexTokenUsageSummary? Snapshot(DateTimeOffset now) =>
        BuildSummary(
            now,
            activeFiles: null,
            capturedAt: LatestIndexedWriteAt(now));

    public CodexTokenUsageReadResult Refresh(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Refresh(CodexQuotaService.CodexHome(), now, cancellationToken);

    internal CodexTokenUsageReadResult Refresh(
        string codexHome,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var candidates = CandidateFiles(codexHome);
        var forkBaselines = ResolveForkBaselines(candidates, now, cancellationToken);
        var next = new Dictionary<string, CodexTokenUsageFileIndex>(_files, StringComparer.Ordinal);
        var changed = false;
        foreach (var file in _files.Values.Where(file =>
            file.AccountingVersion != CodexTokenUsageIndex.CurrentAccountingVersion
            && file.LegacyLifetimeOnly is null))
        {
            next[file.Key] = file with { LegacyLifetimeOnly = !candidates.ContainsKey(file.Key) };
            changed = true;
        }
        foreach (var pair in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files.TryGetValue(pair.Key, out var previous);
            try
            {
                forkBaselines.TryGetValue(pair.Key, out var forkBaseline);
                var current = RefreshFile(
                    pair.Value,
                    pair.Key,
                    previous,
                    forkBaseline,
                    now,
                    cancellationToken);
                next[pair.Key] = current;
                if (previous != current) changed = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                if (previous is not null) next[pair.Key] = previous;
            }
        }

        _files.Clear();
        foreach (var pair in next) _files[pair.Key] = pair.Value;

        var summary = BuildSummary(now, candidates, now);
        return new CodexTokenUsageReadResult(
            summary,
            new CodexTokenUsageIndex
            {
                Files = _files.Values.OrderBy(file => file.Key, StringComparer.Ordinal).ToList(),
            },
            changed);
    }

    private CodexTokenUsageSummary? BuildSummary(
        DateTimeOffset now,
        IReadOnlyDictionary<string, FileInfo>? activeFiles,
        DateTimeOffset capturedAt)
    {
        var spendWindowDates = SpendWindowDates(now, TimeZoneInfo.Local);
        var today = spendWindowDates.Today;
        long localTokens = 0;
        long todayTokens = 0;
        long inputTokens = 0;
        long cachedInputTokens = 0;
        long todayInputTokens = 0;
        long todayCachedInputTokens = 0;
        var todayCacheStatsComplete = true;
        var cacheSessionCount = 0;
        var todayCacheSessionCount = 0;
        var sessionCount = 0;
        foreach (var file in _files.Values.Where(file => file.HasTokenData))
        {
            var isActive = activeFiles?.ContainsKey(file.Key) ?? true;
            if (file.AccountingVersion != CodexTokenUsageIndex.CurrentAccountingVersion)
            {
                if (file.LegacyLifetimeOnly == true
                    && (activeFiles is null || !isActive))
                {
                    localTokens = SaturatingAdd(localTokens, file.TotalTokens);
                    sessionCount++;
                }
                continue;
            }
            localTokens = SaturatingAdd(localTokens, file.TotalTokens);
            if (file.LatestLocalDate == today)
            {
                todayTokens = SaturatingAdd(todayTokens, file.LatestDayTokens);
            }
            if (isActive)
            {
                if (file.InputTokens is { } input && file.CachedInputTokens is { } cached)
                {
                    cacheSessionCount++;
                    inputTokens = SaturatingAdd(inputTokens, input);
                    cachedInputTokens = SaturatingAdd(cachedInputTokens, cached);
                }
                if (file.LatestLocalDate == today)
                {
                    todayCacheSessionCount++;
                    if (file.LatestDayInputTokens is { } todayInput
                        && file.LatestDayCachedInputTokens is { } todayCached)
                    {
                        todayInputTokens = SaturatingAdd(todayInputTokens, todayInput);
                        todayCachedInputTokens = SaturatingAdd(todayCachedInputTokens, todayCached);
                    }
                    else
                    {
                        todayCacheStatsComplete = false;
                    }
                }
            }
            sessionCount++;
        }

        var yesterday = spendWindowDates.Yesterday;
        var last30DaysCutoff = spendWindowDates.Last30DaysCutoff;
        var spendBuckets = _files.Values
            .Where(file => file.SpendAccountingVersion
                == CodexTokenUsageIndex.CurrentSpendAccountingVersion)
            .SelectMany(file => file.DailyModelUsage ?? [])
            .ToArray();
        var todaySpend = SummarizeSpendPeriod(
            spendBuckets.Where(item => item.LocalDate == today));
        var yesterdaySpend = SummarizeSpendPeriod(
            spendBuckets.Where(item => item.LocalDate == yesterday));
        var last30DaysSpend = SummarizeSpendPeriod(
            spendBuckets.Where(item =>
                string.CompareOrdinal(item.LocalDate, last30DaysCutoff) >= 0
                && string.CompareOrdinal(item.LocalDate, today) <= 0));
        var spendHistory = BuildSpendHistory(
            spendBuckets.Where(item =>
                    string.CompareOrdinal(item.LocalDate, last30DaysCutoff) >= 0
                    && string.CompareOrdinal(item.LocalDate, today) <= 0)
                .ToArray(),
            LocalCalendarDate(now, TimeZoneInfo.Local));

        return sessionCount == 0
            ? null
            : new CodexTokenUsageSummary(
                todayTokens,
                localTokens,
                sessionCount,
                capturedAt,
                cacheSessionCount > 0 ? inputTokens : null,
                cacheSessionCount > 0 ? cachedInputTokens : null,
                todayCacheStatsComplete && todayCacheSessionCount > 0 ? todayInputTokens : null,
                todayCacheStatsComplete && todayCacheSessionCount > 0 ? todayCachedInputTokens : null,
                todaySpend,
                yesterdaySpend,
                last30DaysSpend,
                spendHistory);
    }

    private DateTimeOffset LatestIndexedWriteAt(DateTimeOffset now)
    {
        var latestTicks = _files.Values
            .Where(file => file.HasTokenData
                && file.LastWriteTimeUtcTicks > DateTimeOffset.MinValue.UtcTicks
                && file.LastWriteTimeUtcTicks <= DateTimeOffset.MaxValue.UtcTicks)
            .Select(file => file.LastWriteTimeUtcTicks)
            .DefaultIfEmpty(now.UtcTicks)
            .Max();
        var indexedAt = new DateTimeOffset(latestTicks, TimeSpan.Zero);
        return indexedAt <= now.ToUniversalTime()
            ? indexedAt
            : now.ToUniversalTime();
    }

    internal static bool TryParseLine(string line, out CodexTokenUsageEvent? item)
    {
        item = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var capturedAt)
                || !root.TryGetProperty("type", out var outerType)
                || outerType.GetString() != "event_msg"
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var payloadType)
                || payloadType.GetString() != "token_count"
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("total_token_usage", out var totalUsage)
                || totalUsage.ValueKind != JsonValueKind.Object
                || !totalUsage.TryGetProperty("total_tokens", out var totalTokensElement)
                || !totalTokensElement.TryGetInt64(out var totalTokens)
                || totalTokens < 0)
            {
                return false;
            }

            long? inputTokens = null;
            long? cachedInputTokens = null;
            long? cacheWriteInputTokens = null;
            long? outputTokens = null;
            if (TryReadNonNegativeInt64(totalUsage, "input_tokens", out var parsedInputTokens)
                && TryReadNonNegativeInt64(totalUsage, "cached_input_tokens", out var parsedCachedInputTokens)
                && parsedCachedInputTokens <= parsedInputTokens)
            {
                inputTokens = parsedInputTokens;
                cachedInputTokens = parsedCachedInputTokens;
                if (TryReadNonNegativeInt64(
                        totalUsage,
                        "cache_write_input_tokens",
                        out var parsedCacheWriteInputTokens)
                    && parsedCacheWriteInputTokens <= parsedInputTokens - parsedCachedInputTokens)
                {
                    cacheWriteInputTokens = parsedCacheWriteInputTokens;
                }
            }
            if (TryReadNonNegativeInt64(totalUsage, "output_tokens", out var parsedOutputTokens)
                && parsedOutputTokens <= totalTokens)
            {
                outputTokens = parsedOutputTokens;
            }

            long? lastTotalTokens = null;
            long? lastInputTokens = null;
            long? lastCachedInputTokens = null;
            long? lastCacheWriteInputTokens = null;
            long? lastOutputTokens = null;
            if (info.TryGetProperty("last_token_usage", out var lastUsage)
                && lastUsage.ValueKind == JsonValueKind.Object)
            {
                if (TryReadNonNegativeInt64(lastUsage, "total_tokens", out var parsedLastTotalTokens)
                    && parsedLastTotalTokens <= totalTokens)
                {
                    lastTotalTokens = parsedLastTotalTokens;
                }
                if (TryReadNonNegativeInt64(lastUsage, "input_tokens", out var parsedLastInputTokens)
                    && TryReadNonNegativeInt64(lastUsage, "cached_input_tokens", out var parsedLastCachedInputTokens)
                    && parsedLastCachedInputTokens <= parsedLastInputTokens
                    && inputTokens is { } cumulativeInput
                    && cachedInputTokens is { } cumulativeCached
                    && parsedLastInputTokens <= cumulativeInput
                    && parsedLastCachedInputTokens <= cumulativeCached)
                {
                    lastInputTokens = parsedLastInputTokens;
                    lastCachedInputTokens = parsedLastCachedInputTokens;
                    if (TryReadNonNegativeInt64(
                            lastUsage,
                            "cache_write_input_tokens",
                            out var parsedLastCacheWriteInputTokens)
                        && parsedLastCacheWriteInputTokens
                            <= parsedLastInputTokens - parsedLastCachedInputTokens
                        && cacheWriteInputTokens is { } cumulativeCacheWrite
                        && parsedLastCacheWriteInputTokens <= cumulativeCacheWrite)
                    {
                        lastCacheWriteInputTokens = parsedLastCacheWriteInputTokens;
                    }
                }
                if (TryReadNonNegativeInt64(lastUsage, "output_tokens", out var parsedLastOutputTokens)
                    && outputTokens is { } cumulativeOutput
                    && parsedLastOutputTokens <= cumulativeOutput)
                {
                    lastOutputTokens = parsedLastOutputTokens;
                }
            }

            item = new CodexTokenUsageEvent(
                capturedAt,
                totalTokens,
                inputTokens,
                cachedInputTokens,
                cacheWriteInputTokens,
                outputTokens,
                lastTotalTokens,
                lastInputTokens,
                lastCachedInputTokens,
                lastCacheWriteInputTokens,
                lastOutputTokens);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private Dictionary<string, CodexForkBaseline> ResolveForkBaselines(
        IReadOnlyDictionary<string, FileInfo> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, CodexForkBaseline>(StringComparer.Ordinal);
        var sessionFiles = candidates.Values
            .Select(file => (SessionId: SessionIdFromFileName(file.Name), File: file))
            .Where(item => item.SessionId is not null)
            .GroupBy(item => item.SessionId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.File.Length).First().File,
                StringComparer.OrdinalIgnoreCase);
        var requestsByParent = new Dictionary<string, List<CodexForkBaselineRequest>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files.TryGetValue(pair.Key, out var previous);
            pair.Value.Refresh();
            var needsAccountingBaseline = previous is null
                || previous.AccountingVersion != CodexTokenUsageIndex.CurrentAccountingVersion
                || pair.Value.Length < previous.Length
                || !previous.HasTokenData
                || previous.InputTokens is null
                || previous.CachedInputTokens is null
                || previous.LastInputTokens is null
                || previous.LastCachedInputTokens is null;
            var needsSpendBaseline = previous is null
                || previous.SpendAccountingVersion
                    != CodexTokenUsageIndex.CurrentSpendAccountingVersion;
            if (!needsAccountingBaseline
                && needsSpendBaseline
                && ShouldPruneHistoricalSpend(
                    previous!.LatestLocalDate,
                    new DateTimeOffset(pair.Value.LastWriteTimeUtc, TimeSpan.Zero),
                    now,
                    TimeZoneInfo.Local))
            {
                continue;
            }
            if (!needsAccountingBaseline && !needsSpendBaseline) continue;

            CodexForkMetadata? metadata;
            try
            {
                metadata = TryReadForkMetadata(pair.Value.FullName, now.AddMinutes(5), cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
            if (metadata is null) continue;

            resolved[pair.Key] = new CodexForkBaseline(true, null);
            var replayBaseline = TryReadForkReplayBaseline(
                pair.Value.FullName,
                pair.Value.Length,
                cancellationToken);
            if (replayBaseline is not null)
            {
                resolved[pair.Key] = new CodexForkBaseline(true, replayBaseline);
                continue;
            }
            if (!sessionFiles.ContainsKey(metadata.ParentThreadId)) continue;
            if (!requestsByParent.TryGetValue(metadata.ParentThreadId, out var requests))
            {
                requests = [];
                requestsByParent[metadata.ParentThreadId] = requests;
            }
            requests.Add(new CodexForkBaselineRequest(pair.Key, metadata.ForkedAt));
        }

        foreach (var pair in requestsByParent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sessionFiles.TryGetValue(pair.Key, out var parent)) continue;
            try
            {
                foreach (var baseline in ReadParentBaselines(parent, pair.Value, now, cancellationToken))
                {
                    resolved[baseline.Key] = new CodexForkBaseline(true, baseline.InheritedUsage);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unresolved forks stay excluded and are retried on the next refresh.
            }
        }
        return resolved;
    }

    private static CodexTokenUsageEvent? TryReadForkReplayBaseline(
        string path,
        long length,
        CancellationToken cancellationToken)
    {
        var probeBytes = Math.Min(length, InitialTailBytes);
        while (probeBytes > 0)
        {
            var result = ScanForkReplayBoundary(path, length, probeBytes, cancellationToken);
            if (result.BoundaryFound && result.Baseline is not null) return result.Baseline;
            if (probeBytes >= length || probeBytes >= MaximumTailProbeBytes) return null;
            probeBytes = Math.Min(length, Math.Min(MaximumTailProbeBytes, probeBytes * 2));
        }
        return null;
    }

    private static (bool BoundaryFound, CodexTokenUsageEvent? Baseline) ScanForkReplayBoundary(
        string path,
        long length,
        long probeBytes,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var start = Math.Max(0, length - probeBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
        if (start > 0) reader.ReadLine();

        CodexTokenUsageEvent? baseline = null;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length > MaximumLineBytes) continue;
            if (line.Contains("\"inter_agent_communication_metadata\"", StringComparison.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("type", out var type)
                        && type.GetString() == "inter_agent_communication_metadata")
                    {
                        return (true, baseline);
                    }
                }
                catch (JsonException)
                {
                    // Continue looking for a valid boundary marker.
                }
            }
            if (line.Contains("\"token_count\"", StringComparison.Ordinal)
                && TryParseLine(line, out var item))
            {
                baseline = item;
            }
        }
        return (false, null);
    }

    private static IReadOnlyList<(string Key, CodexTokenUsageEvent? InheritedUsage)> ReadParentBaselines(
        FileInfo parent,
        IReadOnlyList<CodexForkBaselineRequest> requests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        parent.Refresh();
        var events = ScanRange(parent.FullName, 0, parent.Length, false, cancellationToken)
            .Where(item => item.CapturedAt <= now.AddMinutes(5))
            .OrderBy(item => item.CapturedAt)
            .ToArray();
        var orderedRequests = requests.OrderBy(item => item.ForkedAt).ToArray();
        var resolved = new List<(string Key, CodexTokenUsageEvent? InheritedUsage)>(orderedRequests.Length);
        var eventIndex = 0;
        CodexTokenUsageEvent? inherited = null;
        foreach (var request in orderedRequests)
        {
            while (eventIndex < events.Length && events[eventIndex].CapturedAt <= request.ForkedAt)
            {
                inherited = events[eventIndex];
                eventIndex++;
            }
            resolved.Add((request.Key, inherited));
        }
        return resolved;
    }

    private static CodexForkMetadata? TryReadForkMetadata(
        string path,
        DateTimeOffset latestAllowed,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream(Math.Min(MaximumLineBytes, 64 * 1024));
        while (buffer.Length < MaximumLineBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = stream.ReadByte();
            if (value < 0 || value == '\n') break;
            buffer.WriteByte((byte)value);
        }
        if (buffer.Length == 0 || buffer.Length >= MaximumLineBytes) return null;

        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type)
            || type.GetString() != "session_meta"
            || !root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parentThreadId = TryReadString(payload, "parent_thread_id")
            ?? TryReadString(payload, "forked_from_id")
            ?? TryReadNestedParentThreadId(payload);
        if (string.IsNullOrWhiteSpace(parentThreadId)) return null;

        var timestamp = TryReadString(payload, "timestamp")
            ?? TryReadString(root, "timestamp");
        if (!DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var forkedAt)
            || forkedAt > latestAllowed)
        {
            return null;
        }
        return new CodexForkMetadata(parentThreadId, forkedAt);
    }

    private static string? TryReadNestedParentThreadId(JsonElement payload)
    {
        if (!payload.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("subagent", out var subagent)
            || subagent.ValueKind != JsonValueKind.Object
            || !subagent.TryGetProperty("thread_spawn", out var threadSpawn)
            || threadSpawn.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return TryReadString(threadSpawn, "parent_thread_id");
    }

    private static string? TryReadString(JsonElement owner, string propertyName) =>
        owner.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? SessionIdFromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 36) return null;
        var candidate = stem[^36..];
        return Guid.TryParse(candidate, out _) ? candidate : null;
    }

    private static Dictionary<string, FileInfo> CandidateFiles(string codexHome)
    {
        var files = new Dictionary<string, FileInfo>(StringComparer.Ordinal);
        foreach (var directoryName in new[] { "sessions", "archived_sessions" })
        {
            var directory = Path.Combine(codexHome, directoryName);
            if (!Directory.Exists(directory)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
                {
                    try
                    {
                        var file = new FileInfo(path);
                        if (!file.Exists || file.Length <= 0) continue;
                        var key = FileKey(file.Name);
                        if (!files.TryGetValue(key, out var existing)
                            || file.Length > existing.Length
                            || file.Length == existing.Length
                            && file.LastWriteTimeUtc > existing.LastWriteTimeUtc)
                        {
                            files[key] = file;
                        }
                    }
                    catch
                    {
                        // One disappearing session file must not block the local aggregate.
                    }
                }
            }
            catch
            {
                // Missing or unreadable Codex session directories are a normal fallback state.
            }
        }
        return files;
    }

    private static CodexTokenUsageFileIndex RefreshFile(
        FileInfo file,
        string key,
        CodexTokenUsageFileIndex? previous,
        CodexForkBaseline? forkBaseline,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        file.Refresh();
        var length = file.Length;
        var lastWriteTicks = file.LastWriteTimeUtc.Ticks;
        if (previous is not null
            && previous.AccountingVersion == CodexTokenUsageIndex.CurrentAccountingVersion
            && previous.SpendAccountingVersion == CodexTokenUsageIndex.CurrentSpendAccountingVersion
            && previous.SpendScannedLength is >= 0
            && previous.SpendScannedLength <= previous.Length
            && SpendRetentionIsCurrent(previous, now)
            && previous.Length == length
            && previous.LastWriteTimeUtcTicks == lastWriteTicks
            && previous.InputTokens is not null
            && previous.CachedInputTokens is not null
            && previous.LastInputTokens is not null
            && previous.LastCachedInputTokens is not null
            && (previous.LatestLocalDate != LocalDate(now)
                || previous.LatestDayInputTokens is not null
                && previous.LatestDayCachedInputTokens is not null))
        {
            return previous;
        }

        var canIncrement = previous is not null
            && previous.AccountingVersion == CodexTokenUsageIndex.CurrentAccountingVersion
            && length >= previous.Length;
        if (!canIncrement
            && forkBaseline is { IsFork: true, InheritedUsage: null })
        {
            return UnresolvedForkIndex(previous, key, length, lastWriteTicks);
        }
        var needsDayBaseline = !canIncrement
            || previous!.LatestLocalDate != LocalDate(now)
            || previous.LatestDayInputTokens is null
            || previous.LatestDayCachedInputTokens is null;
        var needsInitialBaseline = !canIncrement
            || !previous!.HasTokenData
            || previous.InputTokens is null
            || previous.CachedInputTokens is null
            || previous.LastInputTokens is null
            || previous.LastCachedInputTokens is null;
        var probe = ProbeFile(
            file.FullName,
            length,
            now,
            needsDayBaseline,
            needsInitialBaseline,
            cancellationToken);
        if (probe.Latest is null)
        {
            var tokenOnly = canIncrement
                ? previous! with { Length = length, LastWriteTimeUtcTicks = lastWriteTicks }
                : EmptyIndex(key, length, lastWriteTicks);
            return RefreshSpendIndex(
                file,
                tokenOnly,
                previous,
                forkBaseline,
                now,
                cancellationToken);
        }

        if (canIncrement)
        {
            var delta = previous!.HasTokenData
                ? PositiveDelta(previous.LastTotalTokens, probe.Latest.TotalTokens)
                : TokensSinceFileStart(probe.First, probe.Latest, forkBaseline?.InheritedUsage);
            var unresolvedFork = forkBaseline is { IsFork: true, InheritedUsage: null };
            var canIncrementCache = !unresolvedFork
                && previous.InputTokens.HasValue
                && previous.CachedInputTokens.HasValue
                && previous.LastInputTokens.HasValue
                && previous.LastCachedInputTokens.HasValue
                && probe.Latest.InputTokens.HasValue
                && probe.Latest.CachedInputTokens.HasValue;
            var appendedDate = LocalDate(probe.Latest.CapturedAt);
            var canIncrementTodayCache = canIncrementCache
                && previous.LatestLocalDate == appendedDate
                && previous.LatestDayInputTokens.HasValue
                && previous.LatestDayCachedInputTokens.HasValue;
            var todayCache = unresolvedFork
                ? (InputTokens: (long?)null, CachedInputTokens: (long?)null)
                : canIncrementTodayCache
                ? (
                    InputTokens: (long?)SaturatingAdd(
                        previous.LatestDayInputTokens!.Value,
                        PositiveDelta(
                            previous.LastInputTokens!.Value,
                            probe.Latest.InputTokens!.Value)),
                    CachedInputTokens: (long?)SaturatingAdd(
                        previous.LatestDayCachedInputTokens!.Value,
                        PositiveDelta(
                            previous.LastCachedInputTokens!.Value,
                            probe.Latest.CachedInputTokens!.Value)))
                : CacheTokensOnCurrentDay(
                    probe.Events,
                    probe.First,
                    probe.Latest,
                    forkBaseline?.InheritedUsage,
                    now);
            var current = previous with
            {
                Length = length,
                LastWriteTimeUtcTicks = lastWriteTicks,
                HasTokenData = true,
                TotalTokens = SaturatingAdd(previous.TotalTokens, delta),
                LastTotalTokens = probe.Latest.TotalTokens,
                LatestLocalDate = appendedDate,
                LatestDayTokens = previous.LatestLocalDate == appendedDate
                    ? SaturatingAdd(previous.LatestDayTokens, delta)
                    : TokensOnCurrentDay(
                        probe.Events,
                        probe.First,
                        probe.Latest,
                        forkBaseline?.InheritedUsage,
                        now),
                InputTokens = unresolvedFork || probe.Latest.InputTokens is null
                    ? null
                    : canIncrementCache
                        ? SaturatingAdd(
                            previous.InputTokens!.Value,
                            PositiveDelta(
                                previous.LastInputTokens!.Value,
                                probe.Latest.InputTokens.Value))
                        : CacheTokensSinceFileStart(
                            probe.First,
                            probe.Latest,
                            forkBaseline?.InheritedUsage).InputTokens,
                CachedInputTokens = unresolvedFork || probe.Latest.CachedInputTokens is null
                    ? null
                    : canIncrementCache
                        ? SaturatingAdd(
                            previous.CachedInputTokens!.Value,
                            PositiveDelta(
                                previous.LastCachedInputTokens!.Value,
                                probe.Latest.CachedInputTokens.Value))
                        : CacheTokensSinceFileStart(
                            probe.First,
                            probe.Latest,
                            forkBaseline?.InheritedUsage).CachedInputTokens,
                LastInputTokens = probe.Latest.InputTokens,
                LastCachedInputTokens = probe.Latest.CachedInputTokens,
                LatestDayInputTokens = todayCache.InputTokens,
                LatestDayCachedInputTokens = todayCache.CachedInputTokens,
            };
            return RefreshSpendIndex(
                file,
                current,
                previous,
                forkBaseline,
                now,
                cancellationToken);
        }

        var latestDate = LocalDate(probe.Latest.CapturedAt);
        var inheritedUsage = forkBaseline?.InheritedUsage;
        var initialCache = CacheTokensSinceFileStart(probe.First, probe.Latest, inheritedUsage);
        var initialTodayCache = CacheTokensOnCurrentDay(
            probe.Events,
            probe.First,
            probe.Latest,
            inheritedUsage,
            now);
        var rebuilt = new CodexTokenUsageFileIndex(
            key,
            length,
            lastWriteTicks,
            true,
            TokensSinceFileStart(probe.First, probe.Latest, inheritedUsage),
            probe.Latest.TotalTokens,
            latestDate,
            TokensOnCurrentDay(probe.Events, probe.First, probe.Latest, inheritedUsage, now),
            initialCache.InputTokens,
            initialCache.CachedInputTokens,
            probe.Latest.InputTokens,
            probe.Latest.CachedInputTokens,
            initialTodayCache.InputTokens,
            initialTodayCache.CachedInputTokens,
            CodexTokenUsageIndex.CurrentAccountingVersion);
        return RefreshSpendIndex(
            file,
            rebuilt,
            previous,
            forkBaseline,
            now,
            cancellationToken);
    }

    private static CodexTokenUsageFileIndex RefreshSpendIndex(
        FileInfo file,
        CodexTokenUsageFileIndex current,
        CodexTokenUsageFileIndex? previous,
        CodexForkBaseline? forkBaseline,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (forkBaseline is { IsFork: true, InheritedUsage: null })
        {
            return current with
            {
                DailyModelUsage = null,
                SpendCurrentModel = null,
                SpendCurrentServiceTier = null,
                SpendLastTotalTokens = null,
                SpendLastInputTokens = null,
                SpendLastCachedInputTokens = null,
                SpendLastCacheWriteInputTokens = null,
                SpendLastOutputTokens = null,
                SpendAccountingVersion = 0,
                SpendScannedLength = null,
            };
        }

        var canIncrement = previous is not null
            && previous.AccountingVersion == CodexTokenUsageIndex.CurrentAccountingVersion
            && previous.SpendAccountingVersion == CodexTokenUsageIndex.CurrentSpendAccountingVersion
            && previous.SpendScannedLength is >= 0
            && previous.SpendScannedLength <= previous.Length
            && file.Length >= previous.Length
            && (file.Length > previous.Length
                || file.LastWriteTimeUtc.Ticks == previous.LastWriteTimeUtcTicks);
        if (!canIncrement
            && ShouldPruneHistoricalSpend(
                current.LatestLocalDate,
                file.LastWriteTimeUtc,
                now,
                TimeZoneInfo.Local))
        {
            return current with
            {
                DailyModelUsage = [],
                SpendCurrentModel = null,
                SpendCurrentServiceTier = null,
                SpendLastTotalTokens = null,
                SpendLastInputTokens = null,
                SpendLastCachedInputTokens = null,
                SpendLastCacheWriteInputTokens = null,
                SpendLastOutputTokens = null,
                SpendAccountingVersion = CodexTokenUsageIndex.CurrentSpendAccountingVersion,
                SpendScannedLength = FindLastCompleteLineOffset(file.FullName, file.Length),
            };
        }
        var dailyUsage = canIncrement
            ? previous!.DailyModelUsage?.ToList() ?? []
            : [];
        SpendScanState state;
        if (canIncrement)
        {
            state = new SpendScanState
            {
                Model = previous!.SpendCurrentModel,
                ServiceTier = previous.SpendCurrentServiceTier,
                LastTotalTokens = previous.SpendLastTotalTokens,
                LastInputTokens = previous.SpendLastInputTokens,
                LastCachedInputTokens = previous.SpendLastCachedInputTokens,
                LastCacheWriteInputTokens = previous.SpendLastCacheWriteInputTokens,
                LastOutputTokens = previous.SpendLastOutputTokens,
            };
        }
        else if (forkBaseline?.InheritedUsage is { } inherited)
        {
            state = new SpendScanState
            {
                LastTotalTokens = inherited.TotalTokens,
                LastInputTokens = inherited.InputTokens,
                LastCachedInputTokens = inherited.CachedInputTokens,
                LastCacheWriteInputTokens = inherited.CacheWriteInputTokens,
                LastOutputTokens = inherited.OutputTokens,
                AwaitingForkBaseline = true,
            };
        }
        else
        {
            state = new SpendScanState();
        }

        var start = canIncrement ? previous!.SpendScannedLength!.Value : 0;
        var scannedLength = start;
        if (start < file.Length)
        {
            scannedLength = ScanSpendRange(
                file.FullName,
                start,
                file.Length,
                state,
                dailyUsage,
                now,
                cancellationToken);
        }
        dailyUsage = TrimAndCombineDailyUsage(dailyUsage, now);
        return current with
        {
            DailyModelUsage = dailyUsage,
            SpendCurrentModel = state.Model,
            SpendCurrentServiceTier = state.ServiceTier,
            SpendLastTotalTokens = state.LastTotalTokens,
            SpendLastInputTokens = state.LastInputTokens,
            SpendLastCachedInputTokens = state.LastCachedInputTokens,
            SpendLastCacheWriteInputTokens = state.LastCacheWriteInputTokens,
            SpendLastOutputTokens = state.LastOutputTokens,
            SpendAccountingVersion = CodexTokenUsageIndex.CurrentSpendAccountingVersion,
            SpendScannedLength = scannedLength,
        };
    }

    private static long ScanSpendRange(
        string path,
        long start,
        long end,
        SpendScanState state,
        ICollection<CodexDailyModelUsage> dailyUsage,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        end = Math.Min(end, stream.Length);
        start = Math.Clamp(start, 0, end);
        stream.Seek(start, SeekOrigin.Begin);
        var readBuffer = new byte[64 * 1024];
        using var lineBuffer = new MemoryStream(Math.Min(MaximumLineBytes, 64 * 1024));
        var lineOversized = false;
        long bytesRead = 0;
        var lastCompleteLineOffset = start;

        while (start + bytesRead < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(readBuffer.Length, end - start - bytesRead);
            var count = stream.Read(readBuffer, 0, requested);
            if (count <= 0) break;
            bytesRead += count;
            for (var index = 0; index < count; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    if (!lineOversized && lineBuffer.Length > 0)
                    {
                        ParseSpendBufferedLine(lineBuffer, state, dailyUsage, now);
                    }
                    lineBuffer.SetLength(0);
                    lineOversized = false;
                    lastCompleteLineOffset = start + bytesRead - count + index + 1;
                }
                else if (!lineOversized)
                {
                    if (lineBuffer.Length >= MaximumLineBytes)
                    {
                        lineBuffer.SetLength(0);
                        lineOversized = true;
                    }
                    else
                    {
                        lineBuffer.WriteByte(value);
                    }
                }
            }
        }

        return lastCompleteLineOffset;
    }

    private static void ParseSpendBufferedLine(
        MemoryStream lineBuffer,
        SpendScanState state,
        ICollection<CodexDailyModelUsage> dailyUsage,
        DateTimeOffset now)
    {
        var span = lineBuffer.GetBuffer().AsSpan(0, (int)lineBuffer.Length);
        if (span.Length > 0 && span[^1] == (byte)'\r') span = span[..^1];
        if (span.IndexOf("\"token_count\""u8) < 0
            && span.IndexOf("\"session_meta\""u8) < 0
            && span.IndexOf("\"turn_context\""u8) < 0)
        {
            return;
        }
        ProcessSpendLine(Encoding.UTF8.GetString(span), state, dailyUsage, now);
    }

    private static void ProcessSpendLine(
        string line,
        SpendScanState state,
        ICollection<CodexDailyModelUsage> dailyUsage,
        DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var outerType = typeElement.GetString();
            var payloadType = TryReadString(payload, "type");
            if (outerType is "session_meta" or "turn_context"
                || payloadType is "session_meta" or "turn_context")
            {
                if (TryReadString(payload, "model") is { } model
                    && !string.IsNullOrWhiteSpace(model))
                {
                    state.Model = NormalizeModel(model);
                }
                else if (TryReadString(payload, "model_slug") is { } modelSlug
                    && !string.IsNullOrWhiteSpace(modelSlug))
                {
                    state.Model = NormalizeModel(modelSlug);
                }
                if (payload.TryGetProperty("service_tier", out var serviceTier))
                {
                    state.ServiceTier = serviceTier.ValueKind == JsonValueKind.String
                        ? NormalizeServiceTier(serviceTier.GetString())
                        : StandardPricingTier;
                }
            }

            if (outerType != "event_msg"
                || payloadType != "token_count"
                || !TryParseLine(line, out var item)
                || item is null
                || item.CapturedAt > now.AddMinutes(5))
            {
                return;
            }
            ProcessSpendEvent(item, state, dailyUsage, now);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Malformed or partially-written JSONL records are ignored locally.
        }
    }

    private static void ProcessSpendEvent(
        CodexTokenUsageEvent item,
        SpendScanState state,
        ICollection<CodexDailyModelUsage> dailyUsage,
        DateTimeOffset now)
    {
        if (state.LastTotalTokens is null)
        {
            state.LastTotalTokens = item.LastTotalTokens is { } lastRequestTotal
                ? item.TotalTokens - lastRequestTotal
                : 0;
            state.LastInputTokens = InitialCounterBaseline(item.InputTokens, item.LastInputTokens);
            state.LastCachedInputTokens = InitialCounterBaseline(
                item.CachedInputTokens,
                item.LastCachedInputTokens);
            state.LastCacheWriteInputTokens = InitialKnownCounterBaseline(
                item.CacheWriteInputTokens,
                item.LastCacheWriteInputTokens);
            state.LastOutputTokens = InitialCounterBaseline(item.OutputTokens, item.LastOutputTokens);
        }

        if (state.AwaitingForkBaseline)
        {
            if (item.TotalTokens < state.LastTotalTokens)
            {
                return;
            }
            state.AwaitingForkBaseline = false;
        }

        var totalDelta = PositiveDelta(state.LastTotalTokens.Value, item.TotalTokens);
        var inputDelta = CounterDelta(state.LastInputTokens, item.InputTokens);
        var cachedDelta = CounterDelta(state.LastCachedInputTokens, item.CachedInputTokens);
        var cacheWriteDelta = CounterDelta(
            state.LastCacheWriteInputTokens,
            item.CacheWriteInputTokens);
        var outputDelta = CounterDelta(state.LastOutputTokens, item.OutputTokens);
        state.LastTotalTokens = item.TotalTokens;
        state.LastInputTokens = item.InputTokens;
        state.LastCachedInputTokens = item.CachedInputTokens;
        state.LastCacheWriteInputTokens = item.CacheWriteInputTokens;
        state.LastOutputTokens = item.OutputTokens;
        if (totalDelta <= 0) return;

        var localDate = LocalDate(item.CapturedAt);
        if (!IsRetainedSpendDate(localDate, now)) return;
        var model = NormalizeModel(state.Model);
        var pricingTier = NormalizeServiceTier(state.ServiceTier);
        if (inputDelta is not { } input
            || cachedDelta is not { } cached
            || cacheWriteDelta is not { } cacheWrite
            || outputDelta is not { } output
            || cacheWrite > input
            || cached > input - cacheWrite
            || input > totalDelta
            || output > totalDelta - input)
        {
            AddDailyUsage(
                dailyUsage,
                new CodexDailyModelUsage(
                    localDate,
                    model,
                    pricingTier,
                    false,
                    0,
                    0,
                    0,
                    totalDelta));
            return;
        }

        AddDailyUsage(
            dailyUsage,
            new CodexDailyModelUsage(
                localDate,
                model,
                pricingTier,
                input > CodexPricingCatalog.LongContextInputTokenThreshold,
                input,
                cached,
                output,
                totalDelta - input - output,
                cacheWrite));
    }

    private static long? InitialCounterBaseline(long? cumulative, long? lastRequest) =>
        cumulative is not { } total
            ? null
            : lastRequest is { } last && last <= total
                ? total - last
                : 0;

    private static long? InitialKnownCounterBaseline(long? cumulative, long? lastRequest) =>
        cumulative is { } total && lastRequest is { } last && last <= total
            ? total - last
            : null;

    private static long? CounterDelta(long? previous, long? current) =>
        previous is { } before && current is { } after
            ? PositiveDelta(before, after)
            : null;

    private static void AddDailyUsage(
        ICollection<CodexDailyModelUsage> dailyUsage,
        CodexDailyModelUsage item)
    {
        if (!IsValidDailyUsage(item)) return;
        if (dailyUsage is List<CodexDailyModelUsage> list)
        {
            var existingIndex = list.FindIndex(existing =>
                existing.LocalDate == item.LocalDate
                && string.Equals(existing.Model, item.Model, StringComparison.Ordinal)
                && existing.PricingTier == item.PricingTier
                && existing.IsLongContext == item.IsLongContext);
            if (existingIndex >= 0)
            {
                var existing = list[existingIndex];
                list[existingIndex] = existing with
                {
                    InputTokens = SaturatingAdd(existing.InputTokens, item.InputTokens),
                    CachedInputTokens = SaturatingAdd(
                        existing.CachedInputTokens,
                        item.CachedInputTokens),
                    CacheWriteInputTokens = SaturatingAdd(
                        existing.CacheWriteInputTokens,
                        item.CacheWriteInputTokens),
                    OutputTokens = SaturatingAdd(existing.OutputTokens, item.OutputTokens),
                    UnattributedTokens = SaturatingAdd(
                        existing.UnattributedTokens,
                        item.UnattributedTokens),
                };
                return;
            }
        }
        dailyUsage.Add(item);
    }

    private static List<CodexDailyModelUsage> TrimAndCombineDailyUsage(
        IEnumerable<CodexDailyModelUsage> dailyUsage,
        DateTimeOffset now)
    {
        var combined = new List<CodexDailyModelUsage>();
        foreach (var item in dailyUsage.Where(IsValidDailyUsage))
        {
            if (IsRetainedSpendDate(item.LocalDate, now)) AddDailyUsage(combined, item);
        }
        return combined
            .OrderBy(item => item.LocalDate, StringComparer.Ordinal)
            .ThenBy(item => item.Model, StringComparer.Ordinal)
            .ThenBy(item => item.PricingTier, StringComparer.Ordinal)
            .ThenBy(item => item.IsLongContext)
            .ToList();
    }

    private static bool SpendRetentionIsCurrent(
        CodexTokenUsageFileIndex file,
        DateTimeOffset now) =>
        file.DailyModelUsage is not null
        && file.DailyModelUsage.All(item => IsRetainedSpendDate(item.LocalDate, now));

    private static bool IsRetainedSpendDate(string localDate, DateTimeOffset now)
    {
        if (!DateOnly.TryParseExact(
                localDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return false;
        }
        var today = LocalCalendarDate(now, TimeZoneInfo.Local);
        var cutoff = today.AddDays(-(SpendRetentionDays - 1));
        return date >= cutoff && date <= today;
    }

    internal static (
        string Today,
        string Yesterday,
        string Last30DaysCutoff) SpendWindowDates(
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var today = LocalCalendarDate(now, timeZone);
        return (
            FormatLocalDate(today),
            FormatLocalDate(today.AddDays(-1)),
            FormatLocalDate(today.AddDays(-29)));
    }

    internal static bool ShouldPruneHistoricalSpend(
        string? latestLocalDate,
        DateTimeOffset lastWriteTimeUtc,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var cutoff = LocalCalendarDate(now, timeZone).AddDays(-(SpendRetentionDays - 1));
        if (LocalCalendarDate(lastWriteTimeUtc, timeZone) >= cutoff) return false;
        if (latestLocalDate is null) return true;
        return DateOnly.TryParseExact(
                latestLocalDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var latest)
            && latest < cutoff;
    }

    private static long FindLastCompleteLineOffset(string path, long expectedLength)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.RandomAccess);
        var end = Math.Min(Math.Max(0, expectedLength), stream.Length);
        if (end == 0) return 0;
        var tailLength = (int)Math.Min(end, MaximumLineBytes + 1L);
        var start = end - tailLength;
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[tailLength];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count <= 0) break;
            read += count;
        }
        var lastNewline = buffer.AsSpan(0, read).LastIndexOf((byte)'\n');
        return lastNewline >= 0 ? start + lastNewline + 1 : 0;
    }

    private static CodexSpendPeriod SummarizeSpendPeriod(
        IEnumerable<CodexDailyModelUsage> dailyUsage) =>
        CodexPricingCatalog.SummarizePeriod(dailyUsage.Select(item =>
            new CodexModelTokenUsage(
                item.PricingTier == StandardPricingTier ? item.Model : null,
                item.InputTokens,
                item.CachedInputTokens,
                item.OutputTokens,
                0,
                item.UnattributedTokens,
                item.IsLongContext,
                item.CacheWriteInputTokens)));

    private static CodexSpendHistory BuildSpendHistory(
        IReadOnlyList<CodexDailyModelUsage> last30DaysUsage,
        DateOnly today)
    {
        var usageByDay = last30DaysUsage
            .GroupBy(item => item.LocalDate, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CodexDailyModelUsage>)group.ToArray(),
                StringComparer.Ordinal);
        var days = Enumerable.Range(0, 30)
            .Select(index =>
            {
                var localDate = FormatLocalDate(today.AddDays(index - 29));
                return new CodexSpendDay(
                    localDate,
                    usageByDay.TryGetValue(localDate, out var usage)
                        ? SummarizeSpendPeriod(usage)
                        : CodexSpendPeriod.Empty);
            })
            .ToArray();

        var last7DaysCutoff = FormatLocalDate(today.AddDays(-6));
        var last7DaysSpend = SummarizeSpendPeriod(last30DaysUsage.Where(item =>
            string.CompareOrdinal(item.LocalDate, last7DaysCutoff) >= 0));
        var models = last30DaysUsage
            .GroupBy(item => SpendModelDisplay(item.Model), StringComparer.Ordinal)
            .Select(group => new CodexSpendModel(
                group.Key,
                SummarizeSpendPeriod(group)))
            .OrderByDescending(item => item.Spend.PricedApiEquivalentUsd)
            .ThenByDescending(item => item.Spend.TotalTokens)
            .ThenBy(item => item.Model, StringComparer.Ordinal)
            .ToArray();

        return new CodexSpendHistory(days, models, last7DaysSpend);
    }

    private static string SpendModelDisplay(string? model)
    {
        var normalized = NormalizeModel(model);
        if (CodexPricingCatalog.TryGetPricing(normalized, out var pricing))
        {
            normalized = pricing.CanonicalModel;
        }
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Any(char.IsControl)
            || normalized.Contains('/')
            || normalized.Contains('\\')
            || Guid.TryParse(normalized, out _))
        {
            return UnknownSpendModel;
        }
        return normalized.Length <= MaximumSpendModelDisplayLength
            ? normalized
            : string.Concat(
                normalized.AsSpan(0, MaximumSpendModelDisplayLength - 1),
                "\u2026");
    }

    private static bool IsValidDailyUsage(CodexDailyModelUsage item) =>
        item is not null
        && DateOnly.TryParseExact(
            item.LocalDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _)
        && !string.IsNullOrWhiteSpace(item.PricingTier)
        && item.InputTokens >= 0
        && item.CachedInputTokens >= 0
        && item.CacheWriteInputTokens >= 0
        && item.CacheWriteInputTokens <= item.InputTokens
        && item.CachedInputTokens <= item.InputTokens - item.CacheWriteInputTokens
        && item.OutputTokens >= 0
        && item.UnattributedTokens >= 0;

    private static string? NormalizeModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : model.Trim().ToLowerInvariant();

    private static string NormalizeServiceTier(string? serviceTier)
    {
        if (string.IsNullOrWhiteSpace(serviceTier)) return StandardPricingTier;
        var normalized = serviceTier.Trim().ToLowerInvariant();
        return normalized is "default" or "auto" ? StandardPricingTier : normalized;
    }

    private static long TokensOnCurrentDay(
        IReadOnlyList<CodexTokenUsageEvent> events,
        CodexTokenUsageEvent? first,
        CodexTokenUsageEvent latest,
        CodexTokenUsageEvent? inheritedUsage,
        DateTimeOffset now)
    {
        var today = LocalDate(now);
        if (LocalDate(latest.CapturedAt) != today) return 0;
        var baseline = events.LastOrDefault(item =>
            item.CapturedAt <= now.AddMinutes(5)
            && string.CompareOrdinal(LocalDate(item.CapturedAt), today) < 0);
        return baseline is null
            ? TokensSinceFileStart(first, latest, inheritedUsage)
            : PositiveDelta(baseline.TotalTokens, latest.TotalTokens);
    }

    private static (long? InputTokens, long? CachedInputTokens) CacheTokensOnCurrentDay(
        IReadOnlyList<CodexTokenUsageEvent> events,
        CodexTokenUsageEvent? first,
        CodexTokenUsageEvent latest,
        CodexTokenUsageEvent? inheritedUsage,
        DateTimeOffset now)
    {
        var today = LocalDate(now);
        if (LocalDate(latest.CapturedAt) != today) return (0, 0);
        if (latest.InputTokens is not { } latestInput
            || latest.CachedInputTokens is not { } latestCached)
        {
            return (null, null);
        }

        var baseline = events.LastOrDefault(item =>
            item.CapturedAt <= now.AddMinutes(5)
            && string.CompareOrdinal(LocalDate(item.CapturedAt), today) < 0);
        if (baseline is null) return CacheTokensSinceFileStart(first, latest, inheritedUsage);
        if (baseline.InputTokens is not { } baselineInput
            || baseline.CachedInputTokens is not { } baselineCached)
        {
            return (null, null);
        }

        return (
            PositiveDelta(baselineInput, latestInput),
            PositiveDelta(baselineCached, latestCached));
    }

    private static long TokensSinceFileStart(
        CodexTokenUsageEvent? first,
        CodexTokenUsageEvent latest,
        CodexTokenUsageEvent? inheritedUsage)
    {
        if (inheritedUsage is not null)
        {
            return PositiveDelta(inheritedUsage.TotalTokens, latest.TotalTokens);
        }
        if (first is null) return latest.TotalTokens;
        var inheritedBaseline = first.LastTotalTokens is { } firstRequest
            ? first.TotalTokens - firstRequest
            : 0;
        return PositiveDelta(inheritedBaseline, latest.TotalTokens);
    }

    private static (long? InputTokens, long? CachedInputTokens) CacheTokensSinceFileStart(
        CodexTokenUsageEvent? first,
        CodexTokenUsageEvent latest,
        CodexTokenUsageEvent? inheritedUsage)
    {
        if (latest.InputTokens is not { } latestInput
            || latest.CachedInputTokens is not { } latestCached)
        {
            return (null, null);
        }
        if (inheritedUsage is not null)
        {
            if (inheritedUsage.InputTokens is not { } inheritedInput
                || inheritedUsage.CachedInputTokens is not { } inheritedCached)
            {
                return (null, null);
            }
            var inheritedInputDelta = PositiveDelta(inheritedInput, latestInput);
            var inheritedCachedDelta = PositiveDelta(inheritedCached, latestCached);
            return inheritedCachedDelta <= inheritedInputDelta
                ? (inheritedInputDelta, inheritedCachedDelta)
                : (null, null);
        }
        if (first?.InputTokens is not { } firstInput
            || first.CachedInputTokens is not { } firstCached
            || first.LastInputTokens is not { } firstRequestInput
            || first.LastCachedInputTokens is not { } firstRequestCached)
        {
            return (latestInput, latestCached);
        }

        var input = PositiveDelta(firstInput - firstRequestInput, latestInput);
        var cached = PositiveDelta(firstCached - firstRequestCached, latestCached);
        return cached <= input ? (input, cached) : (null, null);
    }

    private static TokenProbe ProbeFile(
        string path,
        long length,
        DateTimeOffset now,
        bool needTodayBaseline,
        bool needInitialBaseline,
        CancellationToken cancellationToken)
    {
        var today = LocalDate(now);
        var first = needInitialBaseline
            ? TryReadFirstTokenEvent(path, now.AddMinutes(5), cancellationToken)
            : null;
        var startedToday = needTodayBaseline
            && TryReadFirstTimestamp(path, cancellationToken) is { } startedAt
            && LocalDate(startedAt) == today;
        var probeBytes = Math.Min(length, InitialTailBytes);
        while (probeBytes > 0)
        {
            var start = Math.Max(0, length - probeBytes);
            var events = ScanRange(path, start, length, start > 0, cancellationToken);
            var latest = events.LastOrDefault(item => item.CapturedAt <= now.AddMinutes(5));
            var complete = latest is not null
                && (!needTodayBaseline
                    || LocalDate(latest.CapturedAt) != today
                    || startedToday
                    || events.Any(item => string.CompareOrdinal(LocalDate(item.CapturedAt), today) < 0)
                    || start == 0);
            if (complete) return new TokenProbe(first, latest, events);
            if (start == 0) return new TokenProbe(first, latest, events);
            if (probeBytes >= MaximumTailProbeBytes)
            {
                events = ScanRange(path, 0, length, false, cancellationToken);
                latest = events.LastOrDefault(item => item.CapturedAt <= now.AddMinutes(5));
                return new TokenProbe(first, latest, events);
            }
            probeBytes = Math.Min(length, Math.Min(MaximumTailProbeBytes, probeBytes * 2));
        }
        return new TokenProbe(first, null, []);
    }

    private static IReadOnlyList<CodexTokenUsageEvent> ScanRange(
        string path,
        long start,
        long end,
        bool discardPartialLine,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        end = Math.Min(end, stream.Length);
        start = Math.Clamp(start, 0, end);
        stream.Seek(start, SeekOrigin.Begin);
        var readBuffer = new byte[64 * 1024];
        using var lineBuffer = new MemoryStream(Math.Min(MaximumLineBytes, 64 * 1024));
        var lineOversized = false;
        var events = new List<CodexTokenUsageEvent>();
        long bytesRead = 0;

        while (start + bytesRead < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(readBuffer.Length, end - start - bytesRead);
            var count = stream.Read(readBuffer, 0, requested);
            if (count <= 0) break;
            bytesRead += count;
            for (var index = 0; index < count; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    if (discardPartialLine) discardPartialLine = false;
                    else if (!lineOversized && lineBuffer.Length > 0) ParseBufferedLine(lineBuffer, events);
                    lineBuffer.SetLength(0);
                    lineOversized = false;
                }
                else if (!discardPartialLine && !lineOversized)
                {
                    if (lineBuffer.Length >= MaximumLineBytes)
                    {
                        lineBuffer.SetLength(0);
                        lineOversized = true;
                    }
                    else
                    {
                        lineBuffer.WriteByte(value);
                    }
                }
            }
        }

        if (!discardPartialLine && !lineOversized && lineBuffer.Length > 0)
        {
            ParseBufferedLine(lineBuffer, events);
        }
        return events;
    }

    private static CodexTokenUsageEvent? TryReadFirstTokenEvent(
        string path,
        DateTimeOffset latestAllowed,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var readBuffer = new byte[64 * 1024];
        using var lineBuffer = new MemoryStream(Math.Min(MaximumLineBytes, 64 * 1024));
        var lineOversized = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(readBuffer, 0, readBuffer.Length);
            if (count <= 0) break;
            for (var index = 0; index < count; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\n')
                {
                    if (!lineOversized && lineBuffer.Length > 0
                        && ParseBufferedLine(lineBuffer) is { } item
                        && item.CapturedAt <= latestAllowed)
                    {
                        return item;
                    }
                    lineBuffer.SetLength(0);
                    lineOversized = false;
                }
                else if (!lineOversized)
                {
                    if (lineBuffer.Length >= MaximumLineBytes)
                    {
                        lineBuffer.SetLength(0);
                        lineOversized = true;
                    }
                    else
                    {
                        lineBuffer.WriteByte(value);
                    }
                }
            }
        }

        return !lineOversized && lineBuffer.Length > 0
            && ParseBufferedLine(lineBuffer) is { } final
            && final.CapturedAt <= latestAllowed
                ? final
                : null;
    }

    private static void ParseBufferedLine(
        MemoryStream lineBuffer,
        ICollection<CodexTokenUsageEvent> events)
    {
        if (ParseBufferedLine(lineBuffer) is { } item) events.Add(item);
    }

    private static CodexTokenUsageEvent? ParseBufferedLine(MemoryStream lineBuffer)
    {
        var span = lineBuffer.GetBuffer().AsSpan(0, (int)lineBuffer.Length);
        if (span.Length > 0 && span[^1] == (byte)'\r') span = span[..^1];
        if (span.IndexOf("\"token_count\""u8) < 0) return null;
        return TryParseLine(Encoding.UTF8.GetString(span), out var item) ? item : null;
    }

    private static DateTimeOffset? TryReadFirstTimestamp(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream(Math.Min(MaximumLineBytes, 64 * 1024));
            while (buffer.Length < MaximumLineBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = stream.ReadByte();
                if (value < 0 || value == '\n') break;
                buffer.WriteByte((byte)value);
            }
            if (buffer.Length == 0 || buffer.Length >= MaximumLineBytes) return null;
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            var timestamp = document.RootElement.GetProperty("timestamp").GetString();
            return DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static CodexTokenUsageFileIndex EmptyIndex(string key, long length, long lastWriteTicks) =>
        new(
            key,
            length,
            lastWriteTicks,
            false,
            0,
            0,
            null,
            0,
            AccountingVersion: CodexTokenUsageIndex.CurrentAccountingVersion);

    private static CodexTokenUsageFileIndex UnresolvedForkIndex(
        CodexTokenUsageFileIndex? previous,
        string key,
        long length,
        long lastWriteTicks) =>
        previous is null
            ? new CodexTokenUsageFileIndex(
                key,
                length,
                lastWriteTicks,
                false,
                0,
                0,
                null,
                0,
                LegacyLifetimeOnly: false)
            : previous with
            {
                Length = length,
                LastWriteTimeUtcTicks = lastWriteTicks,
                AccountingVersion = 0,
                LegacyLifetimeOnly = false,
            };

    private static string FileKey(string fileName) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName))).ToLowerInvariant();

    internal static DateOnly LocalCalendarDate(DateTimeOffset value, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, timeZone).DateTime);
    }

    private static string LocalDate(DateTimeOffset value) =>
        FormatLocalDate(LocalCalendarDate(value, TimeZoneInfo.Local));

    private static string FormatLocalDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long PositiveDelta(long previous, long current) =>
        current >= previous ? current - previous : current;

    private static bool TryReadNonNegativeInt64(
        JsonElement owner,
        string propertyName,
        out long value)
    {
        value = 0;
        return owner.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && value >= 0;
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private sealed record TokenProbe(
        CodexTokenUsageEvent? First,
        CodexTokenUsageEvent? Latest,
        IReadOnlyList<CodexTokenUsageEvent> Events);

    private sealed class SpendScanState
    {
        public string? Model { get; set; }
        public string? ServiceTier { get; set; }
        public long? LastTotalTokens { get; set; }
        public long? LastInputTokens { get; set; }
        public long? LastCachedInputTokens { get; set; }
        public long? LastCacheWriteInputTokens { get; set; }
        public long? LastOutputTokens { get; set; }
        public bool AwaitingForkBaseline { get; set; }
    }
}
