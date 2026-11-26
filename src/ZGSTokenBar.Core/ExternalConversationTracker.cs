using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

/// <summary>
/// Bounded, in-memory-only, stat-cached reader of recent and active conversations for non-Codex tools
/// (Antigravity, Grok CLI, and Claude Code). Never touches <c>codex-token-usage-index.json</c> or writes
/// conversation content to disk.
/// </summary>
public sealed class ExternalConversationTracker
{
    private const int MaxCandidatesPerProvider = 16;
    private const int TailReadBytes = 256 * 1024;
    private const int HeadReadBytes = 16 * 1024;
    private const long MaxMetadataFileBytes = 2 * 1024 * 1024;
    private const int StreamBufferBytes = 64 * 1024;
    private static readonly TimeSpan HardMaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan RunningTimeout = TimeSpan.FromMinutes(2);

    private readonly UsagePaths _paths;
    private readonly IUsageEnvironment _environment;
    private readonly object _sync = new();
    private readonly Dictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ExternalConversationTracker()
        : this(UsagePaths.Current(), ProcessUsageEnvironment.Instance)
    {
    }

    public ExternalConversationTracker(UsagePaths paths, IUsageEnvironment environment)
    {
        _paths = paths;
        _environment = environment;
    }

    public IReadOnlyList<CodexConversationTurn> Refresh(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var hardCutoffUtc = now.UtcDateTime - HardMaxAge;
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<CodexConversationTurn>();

            CollectAgy(now, hardCutoffUtc, activeKeys, results, cancellationToken);
            CollectGrok(now, hardCutoffUtc, activeKeys, results, cancellationToken);
            CollectClaude(now, hardCutoffUtc, activeKeys, results, cancellationToken);

            if (_cache.Count > activeKeys.Count)
            {
                foreach (var staleKey in _cache.Keys.Where(k => !activeKeys.Contains(k)).ToArray())
                    _cache.Remove(staleKey);
            }

            return results;
        }
    }

    private void CollectAgy(
        DateTimeOffset now,
        DateTime hardCutoffUtc,
        HashSet<string> activeKeys,
        List<CodexConversationTurn> results,
        CancellationToken cancellationToken)
    {
        var agyRoot = NonEmpty(_environment.Get("ANTIGRAVITY_DATA_DIR"))
            ?? (string.IsNullOrWhiteSpace(_paths.Home) ? null : Path.Combine(_paths.Home, ".gemini", "antigravity"));
        if (string.IsNullOrWhiteSpace(agyRoot)) return;

        var brainDir = Path.Combine(agyRoot, "brain");
        if (!Directory.Exists(brainDir)) return;

        var candidates = new List<(string ConvId, string Path, FileInfo Info)>();
        try
        {
            foreach (var convDir in Directory.EnumerateDirectories(brainDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(convDir) & FileAttributes.ReparsePoint) != 0) continue;
                    var transcriptPath = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                    var info = new FileInfo(transcriptPath);
                    if (!info.Exists || info.Length <= 0 || info.LastWriteTimeUtc < hardCutoffUtc) continue;
                    var convId = Path.GetFileName(convDir);
                    if (string.IsNullOrWhiteSpace(convId)) continue;
                    candidates.Add((convId, Path.GetFullPath(transcriptPath), info));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Info.LastWriteTimeUtc)
                     .Take(MaxCandidatesPerProvider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = "agy:" + candidate.Path;
            activeKeys.Add(cacheKey);
            var stamp = new FileStamp(candidate.Info.Length, candidate.Info.LastWriteTimeUtc.Ticks);
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.PrimaryStamp == stamp)
            {
                if (cached.Turn is { } data && BuildTurn(data, now) is { } projected)
                    results.Add(projected);
                continue;
            }

            var cachedTitle = cached is not null && candidate.Info.Length >= cached.PrimaryStamp.Length
                ? cached.CachedTitle
                : null;
            var parsed = ParseAgySession(candidate.ConvId, candidate.Path, candidate.Info, cachedTitle, now, out var nextTitle);
            _cache[cacheKey] = new CachedEntry(stamp, default, default, nextTitle, null, parsed);
            if (parsed is not null && BuildTurn(parsed, now) is { } turn)
                results.Add(turn);
        }
    }

    private void CollectGrok(
        DateTimeOffset now,
        DateTime hardCutoffUtc,
        HashSet<string> activeKeys,
        List<CodexConversationTurn> results,
        CancellationToken cancellationToken)
    {
        var grokRoot = NonEmpty(_environment.Get("GROK_HOME"))
            ?? (string.IsNullOrWhiteSpace(_paths.Home) ? null : Path.Combine(_paths.Home, ".grok"));
        if (string.IsNullOrWhiteSpace(grokRoot)) return;

        var sessionsDir = Path.Combine(grokRoot, "sessions");
        var candidates = new List<(string SessionId, string Dir, FileInfo? Summary, FileInfo? Usage, FileInfo? Updates, DateTime LatestUtc)>();
        foreach (var dir in EnumerateDirectoriesSafe(sessionsDir, cancellationToken))
        {
            var summary = SafeFileInfo(Path.Combine(dir, "summary.json"));
            var usage = SafeFileInfo(Path.Combine(dir, "usage.json"));
            var updates = SafeFileInfo(Path.Combine(dir, "updates.jsonl"));
            if (summary is null && usage is null && updates is null) continue;

            var latest = MaxUtc(summary?.LastWriteTimeUtc, usage?.LastWriteTimeUtc, updates?.LastWriteTimeUtc);
            if (latest < hardCutoffUtc) continue;
            var sessionId = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(sessionId)) continue;
            candidates.Add((sessionId, Path.GetFullPath(dir), summary, usage, updates, latest));
        }

        foreach (var candidate in candidates
                     .OrderByDescending(c => c.LatestUtc)
                     .Take(MaxCandidatesPerProvider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = "grok:" + candidate.Dir;
            activeKeys.Add(cacheKey);
            var sumStamp = StampOf(candidate.Summary);
            var usgStamp = StampOf(candidate.Usage);
            var updStamp = StampOf(candidate.Updates);
            if (_cache.TryGetValue(cacheKey, out var cached)
                && cached.PrimaryStamp == sumStamp
                && cached.SecondaryStamp == usgStamp
                && cached.TertiaryStamp == updStamp)
            {
                if (cached.Turn is { } data && BuildTurn(data, now) is { } projected)
                    results.Add(projected);
                continue;
            }

            var parsed = ParseGrokSession(
                candidate.SessionId,
                candidate.Summary,
                candidate.Usage,
                candidate.Updates,
                candidate.LatestUtc,
                now);
            _cache[cacheKey] = new CachedEntry(sumStamp, usgStamp, updStamp, parsed?.Title, parsed?.ProjectName, parsed);
            if (parsed is not null && BuildTurn(parsed, now) is { } turn)
                results.Add(turn);
        }
    }

    private void CollectClaude(
        DateTimeOffset now,
        DateTime hardCutoffUtc,
        HashSet<string> activeKeys,
        List<CodexConversationTurn> results,
        CancellationToken cancellationToken)
    {
        var claudeRoot = NonEmpty(_environment.Get("CLAUDE_CONFIG_DIR"))
            ?? (string.IsNullOrWhiteSpace(_paths.Home) ? null : Path.Combine(_paths.Home, ".claude"));
        if (string.IsNullOrWhiteSpace(claudeRoot)) return;

        var projectsDir = Path.Combine(claudeRoot, "projects");
        var candidates = new List<(string SessionId, string Path, FileInfo Info)>();
        foreach (var dir in EnumerateDirectoriesSafe(projectsDir, cancellationToken))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = SafeFileInfo(file);
                    if (info is null || info.Length <= 0 || info.LastWriteTimeUtc < hardCutoffUtc) continue;
                    var sessionId = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(sessionId)) continue;
                    candidates.Add((sessionId, Path.GetFullPath(file), info));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Info.LastWriteTimeUtc)
                     .Take(MaxCandidatesPerProvider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = "claude:" + candidate.Path;
            activeKeys.Add(cacheKey);
            var stamp = new FileStamp(candidate.Info.Length, candidate.Info.LastWriteTimeUtc.Ticks);
            if (_cache.TryGetValue(cacheKey, out var cached) && cached.PrimaryStamp == stamp)
            {
                if (cached.Turn is { } data && BuildTurn(data, now) is { } projected)
                    results.Add(projected);
                continue;
            }

            var keepHead = cached is not null && candidate.Info.Length >= cached.PrimaryStamp.Length;
            var cachedTitle = keepHead ? cached!.CachedTitle : null;
            var cachedProject = keepHead ? cached!.CachedProject : null;
            var parsed = ParseClaudeSession(
                candidate.SessionId,
                candidate.Path,
                candidate.Info,
                cachedTitle,
                cachedProject,
                now,
                out var nextTitle,
                out var nextProject);
            _cache[cacheKey] = new CachedEntry(stamp, default, default, nextTitle, nextProject, parsed);
            if (parsed is not null && BuildTurn(parsed, now) is { } turn)
                results.Add(turn);
        }
    }

    private static CachedTurnData? ParseAgySession(
        string convId,
        string path,
        FileInfo info,
        string? cachedTitle,
        DateTimeOffset now,
        out string? titleOut)
    {
        titleOut = cachedTitle;
        try
        {
            if (titleOut is null)
            {
                foreach (var headLine in ReadHeadLines(path, HeadReadBytes))
                {
                    if (!headLine.Contains("\"USER_INPUT\"", StringComparison.Ordinal)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(headLine);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) continue;
                        if (!string.Equals(root.StringProperty("type"), "USER_INPUT", StringComparison.Ordinal)) continue;
                        if (SanitizeTitle(root.StringProperty("content")) is { } found)
                        {
                            titleOut = found;
                            break;
                        }
                    }
                    catch (JsonException) { }
                }
            }

            var tailLines = ReadTailLines(path, TailReadBytes);
            if (tailLines.Count == 0) return null;

            var lastUserIndex = -1;
            for (var i = tailLines.Count - 1; i >= 0; i--)
            {
                if (!tailLines[i].Contains("\"USER_INPUT\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(tailLines[i]);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && string.Equals(doc.RootElement.StringProperty("type"), "USER_INPUT", StringComparison.Ordinal))
                    {
                        lastUserIndex = i;
                        break;
                    }
                }
                catch (JsonException) { }
            }

            var startIndex = lastUserIndex >= 0 ? lastUserIndex : 0;
            long rawInput = 0, cacheRead = 0, cacheCreate = 0, output = 0;
            string? model = null;
            string? lastStatus = null;
            string? lastEventType = null;
            var lastHasToolCalls = false;
            DateTimeOffset? turnStart = null;
            DateTimeOffset? lastEventAt = null;
            long? lastStep = null;

            for (var i = startIndex; i < tailLines.Count; i++)
            {
                var line = tailLines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var type = root.StringProperty("type");
                    if (string.IsNullOrWhiteSpace(type)) continue;

                    lastEventType = type;
                    if (root.TryGetProperty("step_index", out var stepEl) && stepEl.ValueKind == JsonValueKind.Number && stepEl.TryGetInt64(out var stepVal))
                        lastStep = stepVal;

                    lastStatus = root.StringProperty("status");

                    var createdAt = ParseTimestamp(root, "created_at");
                    if (createdAt is { } ts)
                    {
                        turnStart ??= ts;
                        if (lastEventAt is null || ts > lastEventAt) lastEventAt = ts;
                    }

                    if (type == "USER_INPUT" && titleOut is null)
                    {
                        titleOut = SanitizeTitle(root.StringProperty("content"));
                    }
                    else if (type == "PLANNER_RESPONSE")
                    {
                        TryReadNonNegativeInt64(root, "input_tokens", out var stepIn);
                        TryReadNonNegativeInt64(root, "cache_read_tokens", out var stepCacheRead);
                        TryReadNonNegativeInt64(root, "cache_creation_tokens", out var stepCacheCreate);
                        TryReadNonNegativeInt64(root, "output_tokens", out var stepOut);
                        rawInput = TokenActivityView.SaturatingAdd(rawInput, stepIn);
                        cacheRead = TokenActivityView.SaturatingAdd(cacheRead, stepCacheRead);
                        cacheCreate = TokenActivityView.SaturatingAdd(cacheCreate, stepCacheCreate);
                        output = TokenActivityView.SaturatingAdd(output, stepOut);
                        model = NonEmpty(root.StringProperty("model")) ?? model;
                        lastHasToolCalls = HasNonEmptyToolCalls(root);
                    }
                }
                catch (JsonException) { }
            }

            if (lastEventType is null) return null;
            var totalInput = TokenActivityView.SaturatingAdd(rawInput, TokenActivityView.SaturatingAdd(cacheRead, cacheCreate));
            var observedAt = ClampObserved(lastEventAt ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), now);
            var startedAt = turnStart is { } st && st <= observedAt ? st : observedAt;
            var completed = string.Equals(lastEventType, "PLANNER_RESPONSE", StringComparison.Ordinal)
                && !lastHasToolCalls
                && (string.IsNullOrWhiteSpace(lastStatus) || string.Equals(lastStatus, "DONE", StringComparison.OrdinalIgnoreCase));
            var elapsedSeconds = lastEventAt is { } endAt && turnStart is { } beginAt && (endAt - beginAt).TotalSeconds >= 1.0
                ? (endAt - beginAt).TotalSeconds
                : 0d;

            return new CachedTurnData(
                ExternalProviderTokenLedger.AntigravityProviderId,
                "AGY",
                convId,
                $"turn-{lastStep ?? 0}",
                model,
                null,
                titleOut,
                null,
                startedAt,
                observedAt,
                totalInput > 0 ? totalInput : null,
                totalInput > 0 && cacheRead <= totalInput ? cacheRead : null,
                totalInput > 0 && cacheCreate <= totalInput ? cacheCreate : null,
                output,
                elapsedSeconds,
                completed,
                completed && elapsedSeconds >= 1.0 && output > 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CachedTurnData? ParseGrokSession(
        string sessionId,
        FileInfo? summaryInfo,
        FileInfo? usageInfo,
        FileInfo? updatesInfo,
        DateTime latestFileUtc,
        DateTimeOffset now)
    {
        string? title = null;
        string? projectName = null;
        string? model = null;
        string? effort = null;
        DateTimeOffset? summaryAt = null;
        DateTimeOffset? usageUpdatedAt = null;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? endedAt = null;
        long input = 0, cacheRead = 0, cacheWrite = 0, output = 0;
        string turnId = "session";
        var hasUsageData = false;

        if (summaryInfo is { Exists: true, Length: > 0 and <= MaxMetadataFileBytes })
        {
            try
            {
                using var stream = OpenReadShared(summaryInfo.FullName);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    title = SanitizeTitle(root.StringProperty("generated_title"));
                    projectName = DirectoryBasename(root.ObjectProperty("info")?.StringProperty("cwd"));
                    model = NonEmpty(root.StringProperty("current_model_id"));
                    effort = NonEmpty(root.StringProperty("reasoning_effort"));
                    summaryAt = ParseTimestamp(root, "last_active_at") ?? ParseTimestamp(root, "updated_at");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }

        if (usageInfo is { Exists: true, Length: > 0 and <= MaxMetadataFileBytes })
        {
            try
            {
                using var stream = OpenReadShared(usageInfo.FullName);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    usageUpdatedAt = ParseTimestamp(root, "updatedAt");
                    JsonElement? target = null;
                    if (root.ArrayProperty("turns") is { } turns && turns.GetArrayLength() > 0)
                    {
                        var lastTurn = turns[turns.GetArrayLength() - 1];
                        if (lastTurn.ValueKind == JsonValueKind.Object)
                        {
                            target = lastTurn;
                            if (lastTurn.TryGetProperty("turnNumber", out var num) && num.ValueKind == JsonValueKind.Number && num.TryGetInt64(out var n))
                                turnId = $"turn-{n}";
                        }
                    }
                    else if (root.ObjectProperty("session") is { } session)
                    {
                        target = session;
                    }

                    if (target is { } node)
                    {
                        hasUsageData = true;
                        model = NonEmpty(node.StringProperty("primaryModelId")) ?? model;
                        TryReadNonNegativeInt64(node, "inputTokens", out input);
                        TryReadNonNegativeInt64(node, "cachedReadTokens", out cacheRead);
                        TryReadNonNegativeInt64(node, "cacheCreationTokens", out cacheWrite);
                        TryReadNonNegativeInt64(node, "outputTokens", out output);
                        if (input == 0 && TryReadNonNegativeInt64(node, "totalTokens", out var total) && total > output)
                            input = total - output;
                        startedAt = ParseTimestamp(node, "startedAt");
                        endedAt = ParseTimestamp(node, "endedAt");
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }

        string? lastSessionUpdate = null;
        DateTimeOffset? lastUpdateAt = null;
        var updatesHasLines = false;
        long updInput = 0, updCacheRead = 0, updCacheWrite = 0, updOutput = 0;
        var hasUpdateUsage = false;
        double? updateElapsedMs = null;
        if (updatesInfo is { Exists: true, Length: > 0 })
        {
            try
            {
                foreach (var line in ReadTailLines(updatesInfo.FullName, TailReadBytes))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    updatesHasLines = true;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) continue;
                        var ts = ParseTimestamp(root, "timestamp")
                            ?? (root.ObjectProperty("_meta") is { } meta ? ParseTimestamp(meta, "agentTimestampMs") : null);
                        if (ts is not null && (lastUpdateAt is null || ts > lastUpdateAt)) lastUpdateAt = ts;

                        if (root.ObjectProperty("params")?.ObjectProperty("update") is not { } update) continue;
                        var su = update.StringProperty("sessionUpdate");
                        if (!string.IsNullOrWhiteSpace(su)) lastSessionUpdate = su;
                        if (title is null && su == "user_message_chunk")
                            title = SanitizeTitle(update.StringProperty("text") ?? update.ObjectProperty("content")?.StringProperty("text"));

                        if (su == "turn_completed")
                        {
                            if (update.NumberProperty("elapsed_ms") is { } ems && double.IsFinite(ems) && ems >= 0)
                                updateElapsedMs = ems;
                            if (update.ObjectProperty("usage") is { } u)
                            {
                                hasUpdateUsage = true;
                                TryReadNonNegativeInt64(u, "inputTokens", out updInput);
                                TryReadNonNegativeInt64(u, "cachedReadTokens", out updCacheRead);
                                TryReadNonNegativeInt64(u, "cacheCreationTokens", out updCacheWrite);
                                TryReadNonNegativeInt64(u, "outputTokens", out updOutput);
                                if (updInput == 0 && TryReadNonNegativeInt64(u, "totalTokens", out var updTotal) && updTotal > updOutput)
                                    updInput = updTotal - updOutput;
                            }
                        }
                    }
                    catch (JsonException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        if (!hasUsageData && hasUpdateUsage)
        {
            input = updInput;
            cacheRead = updCacheRead;
            cacheWrite = updCacheWrite;
            output = updOutput;
            hasUsageData = true;
        }

        bool completed;
        if (lastSessionUpdate is not null && (usageInfo is null || updatesInfo!.LastWriteTimeUtc >= usageInfo.LastWriteTimeUtc))
        {
            completed = string.Equals(lastSessionUpdate, "turn_completed", StringComparison.Ordinal);
        }
        else if (usageInfo is not null && hasUsageData)
        {
            completed = true;
        }
        else if (updatesInfo is not null && (updatesHasLines || updatesInfo.Length > 0))
        {
            completed = false;
        }
        else
        {
            return null;
        }

        var latestTimestamp = MaxTimestamp(lastUpdateAt, endedAt, usageUpdatedAt, summaryAt);
        var observedAt = ClampObserved(latestTimestamp ?? new DateTimeOffset(latestFileUtc, TimeSpan.Zero), now);
        var effectiveStart = startedAt is { } st && st <= observedAt ? st : observedAt;
        var elapsedSeconds = endedAt is { } en && startedAt is { } bg && (en - bg).TotalSeconds >= 1.0
            ? (en - bg).TotalSeconds
            : updateElapsedMs is >= 1000
                ? updateElapsedMs.Value / 1000.0
                : 0d;

        return new CachedTurnData(
            ExternalProviderTokenLedger.GrokProviderId,
            "Grok",
            sessionId,
            turnId,
            model,
            effort,
            title,
            projectName,
            effectiveStart,
            observedAt,
            input > 0 ? input : null,
            input > 0 && cacheRead <= input ? cacheRead : null,
            input > 0 && cacheWrite <= input ? cacheWrite : null,
            output,
            elapsedSeconds,
            completed,
            completed && elapsedSeconds >= 1.0 && output > 0);
    }

    private static CachedTurnData? ParseClaudeSession(
        string sessionId,
        string path,
        FileInfo info,
        string? cachedTitle,
        string? cachedProject,
        DateTimeOffset now,
        out string? titleOut,
        out string? projectOut)
    {
        titleOut = cachedTitle;
        projectOut = cachedProject;
        try
        {
            if (titleOut is null || projectOut is null)
            {
                foreach (var headLine in ReadHeadLines(path, HeadReadBytes))
                {
                    if (string.IsNullOrWhiteSpace(headLine)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(headLine);
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) continue;
                        projectOut ??= DirectoryBasename(root.StringProperty("cwd"));
                        if (titleOut is null
                            && string.Equals(root.StringProperty("type"), "user", StringComparison.Ordinal)
                            && !IsTrue(root, "isMeta"))
                        {
                            titleOut = ExtractClaudeUserText(root);
                        }
                        if (titleOut is not null && projectOut is not null) break;
                    }
                    catch (JsonException) { }
                }
            }

            var tailLines = ReadTailLines(path, TailReadBytes);
            if (tailLines.Count == 0) return null;

            string? lastPromptTitle = null;
            string? effort = null;
            var lastHumanUserIndex = -1;
            var parsedEvents = new List<(string? Type, string? Cwd, string? Effort, string? LastPrompt, DateTimeOffset? Timestamp, bool IsMeta, bool IsToolResultUser, string? Model, string? StopReason, string? MsgKey, long RawIn, long CacheCreate, long CacheRead, long Output, double? DurationMs)>(tailLines.Count);

            foreach (var line in tailLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    var type = root.StringProperty("type");
                    var cwd = DirectoryBasename(root.StringProperty("cwd"));
                    if (cwd is not null) projectOut = cwd;
                    var lineEffort = NonEmpty(root.StringProperty("perTurnEffort", "effort"));
                    if (lineEffort is not null) effort = lineEffort;
                    if (type == "last-prompt")
                        lastPromptTitle = SanitizeTitle(root.StringProperty("lastPrompt")) ?? lastPromptTitle;

                    var ts = ParseTimestamp(root, "timestamp");
                    var isMeta = IsTrue(root, "isMeta");
                    var isToolResult = type == "user" && IsClaudeToolResultOnly(root);

                    string? model = null;
                    string? stopReason = null;
                    string? msgKey = null;
                    long rawIn = 0, cCreate = 0, cRead = 0, outTok = 0;
                    if (type == "assistant" && root.ObjectProperty("message") is { } msg)
                    {
                        model = NonEmpty(msg.StringProperty("model"));
                        stopReason = NonEmpty(msg.StringProperty("stop_reason"));
                        msgKey = NonEmpty(msg.StringProperty("id")) ?? NonEmpty(root.StringProperty("requestId"));
                        if (msg.ObjectProperty("usage") is { } usage)
                        {
                            TryReadNonNegativeInt64(usage, "input_tokens", out rawIn);
                            TryReadNonNegativeInt64(usage, "cache_creation_input_tokens", out cCreate);
                            TryReadNonNegativeInt64(usage, "cache_read_input_tokens", out cRead);
                            TryReadNonNegativeInt64(usage, "output_tokens", out outTok);
                        }
                    }

                    double? durationMs = null;
                    if (type == "system" && root.StringProperty("subtype") == "turn_duration"
                        && root.NumberProperty("durationMs") is { } dms && double.IsFinite(dms) && dms >= 0)
                    {
                        durationMs = dms;
                    }

                    if (type == "user" && !isMeta && !isToolResult)
                    {
                        lastHumanUserIndex = parsedEvents.Count;
                        if (titleOut is null) titleOut = ExtractClaudeUserText(root);
                    }

                    parsedEvents.Add((type, cwd, lineEffort, null, ts, isMeta, isToolResult, model, stopReason, msgKey, rawIn, cCreate, cRead, outTok, durationMs));
                }
                catch (JsonException) { }
            }

            if (lastPromptTitle is not null) titleOut = lastPromptTitle;
            if (parsedEvents.Count == 0) return null;

            var startIndex = lastHumanUserIndex >= 0 ? lastHumanUserIndex : 0;
            var keyedUsage = new Dictionary<string, (long RawIn, long CacheCreate, long CacheRead, long Output, long Total)>(StringComparer.Ordinal);
            var unkeyedUsage = new List<(long RawIn, long CacheCreate, long CacheRead, long Output)>();
            string? lastTurnEntryType = null;
            string? lastModel = null;
            string? lastStopReason = null;
            DateTimeOffset? turnStart = null;
            DateTimeOffset? lastAssistantAt = null;
            DateTimeOffset? lastEventAt = null;
            double? turnDurationMs = null;

            for (var i = startIndex; i < parsedEvents.Count; i++)
            {
                var ev = parsedEvents[i];
                if (ev.Timestamp is { } ts)
                {
                    turnStart ??= ts;
                    if (lastEventAt is null || ts > lastEventAt) lastEventAt = ts;
                }
                if (ev.Type == "user" && !ev.IsMeta)
                {
                    lastTurnEntryType = "user";
                }
                else if (ev.Type == "assistant")
                {
                    lastTurnEntryType = "assistant";
                    if (ev.Model is not null) lastModel = ev.Model;
                    if (ev.StopReason is not null) lastStopReason = ev.StopReason;
                    if (ev.Timestamp is { } ats && (lastAssistantAt is null || ats > lastAssistantAt)) lastAssistantAt = ats;

                    var evTotal = SaturatingSum(ev.RawIn, ev.CacheCreate, ev.CacheRead, ev.Output);
                    if (evTotal > 0)
                    {
                        if (ev.MsgKey is { } key)
                        {
                            if (!keyedUsage.TryGetValue(key, out var prev) || evTotal >= prev.Total)
                                keyedUsage[key] = (ev.RawIn, ev.CacheCreate, ev.CacheRead, ev.Output, evTotal);
                        }
                        else
                        {
                            unkeyedUsage.Add((ev.RawIn, ev.CacheCreate, ev.CacheRead, ev.Output));
                        }
                    }
                }
                else if (ev.DurationMs is { } dms)
                {
                    turnDurationMs = dms;
                }
            }

            if (lastTurnEntryType is null && lastEventAt is null && keyedUsage.Count == 0 && unkeyedUsage.Count == 0)
                return null;

            long rawInput = 0, cacheCreate = 0, cacheRead = 0, output = 0;
            foreach (var entry in keyedUsage.Values)
            {
                rawInput = TokenActivityView.SaturatingAdd(rawInput, entry.RawIn);
                cacheCreate = TokenActivityView.SaturatingAdd(cacheCreate, entry.CacheCreate);
                cacheRead = TokenActivityView.SaturatingAdd(cacheRead, entry.CacheRead);
                output = TokenActivityView.SaturatingAdd(output, entry.Output);
            }
            foreach (var entry in unkeyedUsage)
            {
                rawInput = TokenActivityView.SaturatingAdd(rawInput, entry.RawIn);
                cacheCreate = TokenActivityView.SaturatingAdd(cacheCreate, entry.CacheCreate);
                cacheRead = TokenActivityView.SaturatingAdd(cacheRead, entry.CacheRead);
                output = TokenActivityView.SaturatingAdd(output, entry.Output);
            }

            var totalInput = TokenActivityView.SaturatingAdd(rawInput, TokenActivityView.SaturatingAdd(cacheCreate, cacheRead));
            var observedAt = ClampObserved(lastEventAt ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), now);
            var startedAt = turnStart is { } st && st <= observedAt ? st : observedAt;
            var completed = string.Equals(lastTurnEntryType, "assistant", StringComparison.Ordinal)
                && lastStopReason is "end_turn" or "stop_sequence" or "max_tokens";
            var elapsedSeconds = turnDurationMs is >= 1000
                ? turnDurationMs.Value / 1000.0
                : lastAssistantAt is { } endAt && turnStart is { } beginAt && (endAt - beginAt).TotalSeconds >= 1.0
                    ? (endAt - beginAt).TotalSeconds
                    : 0d;

            return new CachedTurnData(
                ExternalProviderTokenLedger.ClaudeProviderId,
                "Claude",
                sessionId,
                $"turn-{startIndex}",
                lastModel,
                effort,
                titleOut,
                projectOut,
                startedAt,
                observedAt,
                totalInput > 0 ? totalInput : null,
                totalInput > 0 && cacheRead <= totalInput ? cacheRead : null,
                totalInput > 0 && cacheCreate <= totalInput ? cacheCreate : null,
                output,
                elapsedSeconds,
                completed,
                completed && elapsedSeconds >= 1.0 && output > 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CodexConversationTurn? BuildTurn(CachedTurnData data, DateTimeOffset now)
    {
        var stale = !data.Completed && now - data.ObservedAt > RunningTimeout;
        var active = !data.Completed && !stale;
        var hasTokens = data.OutputTokens > 0 || (data.InputTokens ?? 0) > 0;
        if (!active && !hasTokens) return null;

        return new CodexConversationTurn(
            data.SessionId,
            data.TurnId,
            data.Model,
            data.Effort,
            data.StartedAt,
            data.ObservedAt,
            data.OutputTokens,
            data.ElapsedSeconds,
            data.Completed,
            data.UsageComplete)
        {
            ProviderId = data.ProviderId,
            InstanceLabel = data.InstanceLabel,
            Title = data.Title,
            ProjectName = data.ProjectName,
            InputTokens = data.InputTokens,
            CachedInputTokens = data.CachedInputTokens,
            CacheWriteInputTokens = data.CacheWriteInputTokens,
            Stale = stale,
            HasRecordedUsage = hasTokens,
        };
    }

    private static string? ExtractClaudeUserText(JsonElement root)
    {
        if (root.ObjectProperty("message") is not { } msg) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return SanitizeTitle(content.GetString());
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.StringProperty("type") == "text")
            {
                var candidate = SanitizeTitle(item.StringProperty("text"));
                if (candidate is not null) return candidate;
            }
        }
        return null;
    }

    private static bool IsClaudeToolResultOnly(JsonElement root)
    {
        if (root.ObjectProperty("message") is not { } msg) return false;
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
        var hasBlock = false;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            hasBlock = true;
            if (!string.Equals(item.StringProperty("type"), "tool_result", StringComparison.Ordinal))
                return false;
        }
        return hasBlock;
    }

    private static bool HasNonEmptyToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("tool_calls", out var calls)) return false;
        return calls.ValueKind switch
        {
            JsonValueKind.Array => calls.GetArrayLength() > 0,
            JsonValueKind.Object => calls.EnumerateObject().Any(),
            _ => false,
        };
    }

    private static IReadOnlyList<string> ReadHeadLines(string path, int maxBytes)
    {
        using var stream = OpenReadShared(path);
        var length = stream.Length;
        if (length <= 0) return [];
        var count = (int)Math.Min(length, maxBytes);
        var buffer = new byte[count];
        var read = ReadFully(stream, buffer, count);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var lines = text.Split('\n');
        var end = length > maxBytes && lines.Length > 1 ? lines.Length - 1 : lines.Length;
        var result = new List<string>(end);
        for (var i = 0; i < end; i++)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result;
    }

    private static IReadOnlyList<string> ReadTailLines(string path, int maxBytes)
    {
        using var stream = OpenReadShared(path);
        var length = stream.Length;
        if (length <= 0) return [];
        var offset = Math.Max(0L, length - maxBytes);
        if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
        var count = (int)(length - offset);
        var buffer = new byte[count];
        var read = ReadFully(stream, buffer, count);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var lines = text.Split('\n');
        var start = offset > 0 && lines.Length > 1 ? 1 : 0;
        var result = new List<string>(Math.Max(0, lines.Length - start));
        for (var i = start; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result;
    }

    private static int ReadFully(FileStream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static FileStream OpenReadShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, StreamBufferBytes, FileOptions.SequentialScan);

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) yield break;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(root));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            if (!visited.Add(current)) continue;
            yield return current;

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }

            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push(Path.GetFullPath(child));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static FileInfo? SafeFileInfo(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FileStamp StampOf(FileInfo? info) =>
        info is null ? default : new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);

    private static DateTime MaxUtc(DateTime? a, DateTime? b, DateTime? c)
    {
        var max = DateTime.MinValue;
        if (a is { } va && va > max) max = va;
        if (b is { } vb && vb > max) max = vb;
        if (c is { } vc && vc > max) max = vc;
        return max;
    }

    private static DateTimeOffset? MaxTimestamp(params DateTimeOffset?[] values)
    {
        DateTimeOffset? max = null;
        foreach (var value in values)
        {
            if (value is { } item && (max is null || item > max.Value)) max = item;
        }
        return max;
    }

    private static DateTimeOffset ClampObserved(DateTimeOffset observed, DateTimeOffset now) =>
        observed > now ? now : observed;

    private static DateTimeOffset? ParseTimestamp(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var element)) return null;
        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (!string.IsNullOrWhiteSpace(raw)
                && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                return dto;
            }
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var unix)
            && double.IsFinite(unix)
            && unix > 0)
        {
            try
            {
                return unix < 1_000_000_000_000d
                    ? DateTimeOffset.FromUnixTimeSeconds((long)unix)
                    : DateTimeOffset.FromUnixTimeMilliseconds((long)unix);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? SanitizeTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw
            .Replace("<USER_REQUEST>", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("</USER_REQUEST>", string.Empty, StringComparison.OrdinalIgnoreCase);
        foreach (var segment in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0) continue;
            var cleaned = new string(trimmed.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
            var collapsed = string.Join(' ', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (collapsed.Length == 0) continue;
            return collapsed.Length <= 80 ? collapsed : collapsed[..80];
        }
        return null;
    }

    private static string? DirectoryBasename(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var name = cwd.TrimEnd('/', '\\').Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) return null;
        return name.Length <= 64 ? name : name[..64];
    }

    private static bool IsTrue(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.True;

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryReadNonNegativeInt64(JsonElement obj, string property, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number)
            return false;
        if (element.TryGetInt64(out var parsed))
        {
            value = Math.Max(0, parsed);
            return true;
        }
        if (element.TryGetDouble(out var dbl) && double.IsFinite(dbl) && dbl >= 0 && dbl <= long.MaxValue)
        {
            value = (long)Math.Round(dbl);
            return true;
        }
        return false;
    }

    private static long SaturatingSum(long a, long b, long c, long d) =>
        TokenActivityView.SaturatingAdd(
            TokenActivityView.SaturatingAdd(a, b),
            TokenActivityView.SaturatingAdd(c, d));

    private readonly record struct FileStamp(long Length, long LastWriteUtcTicks);

    private sealed record CachedEntry(
        FileStamp PrimaryStamp,
        FileStamp SecondaryStamp,
        FileStamp TertiaryStamp,
        string? CachedTitle,
        string? CachedProject,
        CachedTurnData? Turn);

    private sealed record CachedTurnData(
        string ProviderId,
        string InstanceLabel,
        string SessionId,
        string TurnId,
        string? Model,
        string? Effort,
        string? Title,
        string? ProjectName,
        DateTimeOffset StartedAt,
        DateTimeOffset ObservedAt,
        long? InputTokens,
        long? CachedInputTokens,
        long? CacheWriteInputTokens,
        long OutputTokens,
        double ElapsedSeconds,
        bool Completed,
        bool UsageComplete);
}
