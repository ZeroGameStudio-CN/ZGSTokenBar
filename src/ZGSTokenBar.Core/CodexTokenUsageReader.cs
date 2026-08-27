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
    long? TodayCachedInputTokens = null)
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

public sealed class CodexTokenUsageIndex
{
    public const int CurrentSchemaVersion = 5;
    public const int CurrentAccountingVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<CodexTokenUsageFileIndex> Files { get; set; } = [];
}

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
    bool? LegacyLifetimeOnly = null);

public sealed record CodexTokenUsageReadResult(
    CodexTokenUsageSummary? Summary,
    CodexTokenUsageIndex Index,
    bool Changed);

internal sealed record CodexTokenUsageEvent(
    DateTimeOffset CapturedAt,
    long TotalTokens,
    long? InputTokens,
    long? CachedInputTokens,
    long? LastTotalTokens,
    long? LastInputTokens,
    long? LastCachedInputTokens);

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
                && (file.InputTokens is null
                    || file.CachedInputTokens is null
                    || file.CachedInputTokens <= file.InputTokens)
                && (file.LastInputTokens is null
                    || file.LastCachedInputTokens is null
                    || file.LastCachedInputTokens <= file.LastInputTokens)
                && (file.LatestDayInputTokens is null
                    || file.LatestDayCachedInputTokens is null
                    || file.LatestDayCachedInputTokens <= file.LatestDayInputTokens))
            .GroupBy(file => file.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    }

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

        var today = LocalDate(now);
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
            if (file.AccountingVersion != CodexTokenUsageIndex.CurrentAccountingVersion)
            {
                if (file.LegacyLifetimeOnly == true && !candidates.ContainsKey(file.Key))
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
            if (candidates.ContainsKey(file.Key))
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

        var summary = sessionCount == 0
            ? null
            : new CodexTokenUsageSummary(
                todayTokens,
                localTokens,
                sessionCount,
                now,
                cacheSessionCount > 0 ? inputTokens : null,
                cacheSessionCount > 0 ? cachedInputTokens : null,
                todayCacheStatsComplete && todayCacheSessionCount > 0 ? todayInputTokens : null,
                todayCacheStatsComplete && todayCacheSessionCount > 0 ? todayCachedInputTokens : null);
        return new CodexTokenUsageReadResult(
            summary,
            new CodexTokenUsageIndex
            {
                Files = _files.Values.OrderBy(file => file.Key, StringComparer.Ordinal).ToList(),
            },
            changed);
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
            if (TryReadNonNegativeInt64(totalUsage, "input_tokens", out var parsedInputTokens)
                && TryReadNonNegativeInt64(totalUsage, "cached_input_tokens", out var parsedCachedInputTokens)
                && parsedCachedInputTokens <= parsedInputTokens)
            {
                inputTokens = parsedInputTokens;
                cachedInputTokens = parsedCachedInputTokens;
            }

            long? lastTotalTokens = null;
            long? lastInputTokens = null;
            long? lastCachedInputTokens = null;
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
                }
            }

            item = new CodexTokenUsageEvent(
                capturedAt,
                totalTokens,
                inputTokens,
                cachedInputTokens,
                lastTotalTokens,
                lastInputTokens,
                lastCachedInputTokens);
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
            var needsBaseline = previous is null
                || previous.AccountingVersion != CodexTokenUsageIndex.CurrentAccountingVersion
                || pair.Value.Length < previous.Length
                || !previous.HasTokenData
                || previous.InputTokens is null
                || previous.CachedInputTokens is null
                || previous.LastInputTokens is null
                || previous.LastCachedInputTokens is null;
            if (!needsBaseline) continue;

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
            return canIncrement
                ? previous! with { Length = length, LastWriteTimeUtcTicks = lastWriteTicks }
                : EmptyIndex(key, length, lastWriteTicks);
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
            return previous with
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
        return new CodexTokenUsageFileIndex(
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

    private static string LocalDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
}
