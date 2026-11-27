using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ZGSTokenBar.Core;

public sealed record ExternalConversationSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<CodexConversationTurn> Turns,
    CodexLiveRateFeed? LiveRateFeed);

/// <summary>
/// Bounded, in-memory-only, stat-cached reader of recent and active conversations for non-Codex tools
/// (Antigravity, Grok CLI, and Claude Code). Never touches <c>codex-token-usage-index.json</c> or writes
/// conversation content to disk.
/// </summary>
public sealed class ExternalConversationTracker
{
    private const int MaxCandidatesPerProvider = 16;
    private const int MaxSamplesPerSession = 8;
    private const int TailReadBytes = 256 * 1024;
    private const int HeadReadBytes = 16 * 1024;
    private const long MaxMetadataFileBytes = 2 * 1024 * 1024;
    private const int StreamBufferBytes = 64 * 1024;
    private static readonly TimeSpan HardMaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan RunningTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinResponseDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxResponseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SampleRetention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ClockSkewAllowance = TimeSpan.FromSeconds(5);

    private readonly UsagePaths _paths;
    private readonly IUsageEnvironment _environment;
    private readonly object _sync = new();
    private readonly Dictionary<string, CachedEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string? _claudeProjectsDirectory;
    private int _claudeMetadataPartition;

    public ExternalConversationTracker()
        : this(UsagePaths.Current(), ProcessUsageEnvironment.Instance)
    {
    }

    public ExternalConversationTracker(UsagePaths paths, IUsageEnvironment environment)
    {
        _paths = paths;
        _environment = environment;
    }

    public IReadOnlyList<CodexConversationTurn> Refresh(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        RefreshSnapshot(now, cancellationToken).Turns;

    public ExternalConversationSnapshot RefreshSnapshot(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var hardCutoffUtc = now.UtcDateTime - HardMaxAge;
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var results = new List<CodexConversationTurn>();
            var responses = new List<CodexResponseSample>();
            var sessions = new List<CodexLiveSession>();

            CollectAgy(now, hardCutoffUtc, activeKeys, results, responses, sessions, cancellationToken);
            CollectGrok(now, hardCutoffUtc, activeKeys, results, responses, sessions, cancellationToken);
            CollectClaude(now, hardCutoffUtc, activeKeys, results, responses, sessions, cancellationToken);

            if (_cache.Count > activeKeys.Count)
            {
                foreach (var staleKey in _cache.Keys.Where(k => !activeKeys.Contains(k)).ToArray())
                    _cache.Remove(staleKey);
            }

            var feed = responses.Count > 0 || sessions.Count > 0
                ? new CodexLiveRateFeed(now, responses, sessions, 0)
                : null;
            return new ExternalConversationSnapshot(now, results, feed);
        }
    }

    private void CollectAgy(
        DateTimeOffset now,
        DateTime hardCutoffUtc,
        HashSet<string> activeKeys,
        List<CodexConversationTurn> results,
        List<CodexResponseSample> responses,
        List<CodexLiveSession> sessions,
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
                EmitCached(cached, now, results, responses, sessions);
                continue;
            }

            var keepHead = cached is not null && candidate.Info.Length >= cached.PrimaryStamp.Length;
            var cachedTitle = keepHead ? cached!.CachedTitle : null;
            var cachedModel = keepHead ? cached!.CachedModel : null;
            var cachedSamples = keepHead ? cached!.CachedSamples : [];
            var parsed = ParseAgySession(
                candidate.ConvId,
                candidate.Path,
                candidate.Info,
                cachedTitle,
                cachedModel,
                cachedSamples,
                now,
                out var nextTitle,
                out var nextModel,
                out var nextSamples);
            _cache[cacheKey] = new CachedEntry(stamp, default, default, nextTitle, null, nextModel, nextSamples, parsed);
            EmitParsed(parsed, nextSamples, now, results, responses, sessions);
        }
    }

    private void CollectGrok(
        DateTimeOffset now,
        DateTime hardCutoffUtc,
        HashSet<string> activeKeys,
        List<CodexConversationTurn> results,
        List<CodexResponseSample> responses,
        List<CodexLiveSession> sessions,
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
                EmitCached(cached, now, results, responses, sessions);
                continue;
            }

            var parsed = ParseGrokSession(
                candidate.SessionId,
                candidate.Summary,
                candidate.Usage,
                candidate.Updates,
                candidate.LatestUtc,
                cached?.CachedModel,
                now,
                out var nextSamples);
            _cache[cacheKey] = new CachedEntry(sumStamp, usgStamp, updStamp, parsed?.Title, parsed?.ProjectName, parsed?.Model, nextSamples, parsed);
            EmitParsed(parsed, nextSamples, now, results, responses, sessions);
        }
    }

    private void CollectClaude(
        DateTimeOffset now,
        DateTime hardCutoffUtc,
        HashSet<string> activeKeys,
        List<CodexConversationTurn> results,
        List<CodexResponseSample> responses,
        List<CodexLiveSession> sessions,
        CancellationToken cancellationToken)
    {
        var claudeRoot = NonEmpty(_environment.Get("CLAUDE_CONFIG_DIR"))
            ?? (string.IsNullOrWhiteSpace(_paths.Home) ? null : Path.Combine(_paths.Home, ".claude"));
        if (string.IsNullOrWhiteSpace(claudeRoot)) return;

        const string cachePrefix = "claude:";
        var projectsDir = Path.GetFullPath(Path.Combine(claudeRoot, "projects"));
        if (!Directory.Exists(projectsDir))
        {
            _claudeProjectsDirectory = null;
            return;
        }

        var firstScan = !string.Equals(_claudeProjectsDirectory, projectsDir, StringComparison.OrdinalIgnoreCase);
        _claudeProjectsDirectory = projectsDir;
        _claudeMetadataPartition ^= 1;
        var trackedFiles = _cache.Keys
            .Where(key => key.StartsWith(cachePrefix, StringComparison.Ordinal))
            .Select(key => key[cachePrefix.Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Path, long WriteTicks)>();
        foreach (var dir in EnumerateDirectoriesSafe(projectsDir, cancellationToken))
        {
            try
            {
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Path.GetFileNameWithoutExtension(file.Name.AsSpan()).Trim().IsEmpty) continue;

                    var path = file.FullName;
                    // Windows directory entries can lag a writer. Refresh tracked files every poll
                    // and alternate the other paths so older sessions can reenter within two polls.
                    if (firstScan || trackedFiles.Contains(path) || file.Length == 0
                        || (StringComparer.OrdinalIgnoreCase.GetHashCode(path) & 1) == _claudeMetadataPartition)
                    {
                        file.Refresh();
                    }

                    var writeTicks = file.LastWriteTimeUtc.Ticks;
                    if (file.Exists && file.Length > 0 && writeTicks >= hardCutoffUtc.Ticks)
                        candidates.Add((path, writeTicks));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var candidate in candidates.OrderByDescending(c => c.WriteTicks).Take(MaxCandidatesPerProvider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = OpenReadShared(candidate.Path);
                var writeUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                var stamp = new FileStamp(stream.Length, writeUtc.Ticks);
                if (stamp.Length <= 0 || writeUtc < hardCutoffUtc) continue;

                var cacheKey = cachePrefix + candidate.Path;
                activeKeys.Add(cacheKey);
                if (_cache.TryGetValue(cacheKey, out var cached) && cached.PrimaryStamp == stamp)
                {
                    EmitCached(cached, now, results, responses, sessions);
                    continue;
                }

                var keepHead = cached is not null && stamp.Length >= cached.PrimaryStamp.Length;
                var parsed = ParseClaudeSession(
                    Path.GetFileNameWithoutExtension(candidate.Path),
                    stream,
                    stamp.Length,
                    writeUtc,
                    keepHead ? cached!.CachedTitle : null,
                    keepHead ? cached!.CachedProject : null,
                    keepHead ? cached!.CachedModel : null,
                    now,
                    out var nextTitle,
                    out var nextProject,
                    out var nextModel,
                    out var nextSamples);
                _cache[cacheKey] = new CachedEntry(stamp, default, default, nextTitle, nextProject, nextModel, nextSamples, parsed);
                EmitParsed(parsed, nextSamples, now, results, responses, sessions);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void EmitCached(
        CachedEntry cached,
        DateTimeOffset now,
        List<CodexConversationTurn> results,
        List<CodexResponseSample> responses,
        List<CodexLiveSession> sessions) =>
        EmitParsed(cached.Turn, cached.CachedSamples, now, results, responses, sessions);

    private static void EmitParsed(
        CachedTurnData? data,
        IReadOnlyList<CodexResponseSample> samples,
        DateTimeOffset now,
        List<CodexConversationTurn> results,
        List<CodexResponseSample> responses,
        List<CodexLiveSession> sessions)
    {
        if (data is not null)
        {
            if (BuildTurn(data, now) is { } turn)
                results.Add(turn);
            if (BuildLiveSession(data, now) is { } session)
                sessions.Add(session);
        }

        if (samples.Count == 0) return;
        var sampleCutoff = now - SampleRetention;
        foreach (var sample in samples)
        {
            if (sample.End <= now && sample.End >= sampleCutoff)
                responses.Add(sample);
        }
    }

    private static CachedTurnData? ParseAgySession(
        string convId,
        string path,
        FileInfo info,
        string? cachedTitle,
        string? cachedModel,
        IReadOnlyList<CodexResponseSample> cachedSamples,
        DateTimeOffset now,
        out string? titleOut,
        out string? modelOut,
        out IReadOnlyList<CodexResponseSample> samplesOut)
    {
        titleOut = cachedTitle;
        modelOut = cachedModel;
        samplesOut = [];
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

            var events = new List<AgyParsedEvent>(tailLines.Count);
            var lastUserIndex = -1;
            string? lastKnownModel = null;
            foreach (var line in tailLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var type = root.StringProperty("type");
                    if (string.IsNullOrWhiteSpace(type)) continue;

                    long? stepIndex = null;
                    if (root.TryGetProperty("step_index", out var stepEl) && stepEl.ValueKind == JsonValueKind.Number && stepEl.TryGetInt64(out var stepVal))
                        stepIndex = stepVal;
                    var status = root.StringProperty("status");
                    var createdAt = ParseTimestamp(root, "created_at");

                    long rawIn = 0, stepCacheRead = 0, stepCacheCreate = 0, stepOut = 0;
                    string? evModel = null;
                    var hasToolCalls = false;
                    if (type == "USER_INPUT")
                    {
                        lastUserIndex = events.Count;
                        if (titleOut is null)
                            titleOut = SanitizeTitle(root.StringProperty("content"));
                    }
                    else if (type == "PLANNER_RESPONSE")
                    {
                        TryReadNonNegativeInt64(root, "input_tokens", out rawIn);
                        TryReadNonNegativeInt64(root, "cache_read_tokens", out stepCacheRead);
                        TryReadNonNegativeInt64(root, "cache_creation_tokens", out stepCacheCreate);
                        TryReadNonNegativeInt64(root, "output_tokens", out stepOut);
                        evModel = NonEmpty(root.StringProperty("model"));
                        if (evModel is not null) lastKnownModel = evModel;
                        hasToolCalls = HasNonEmptyToolCalls(root);
                    }

                    events.Add(new AgyParsedEvent(stepIndex, type, status, createdAt, evModel, rawIn, stepCacheRead, stepCacheCreate, stepOut, hasToolCalls));
                }
                catch (JsonException) { }
            }

            if (events.Count == 0) return null;

            var startIndex = lastUserIndex >= 0 ? lastUserIndex : 0;
            long rawInput = 0, cacheRead = 0, cacheCreate = 0, output = 0;
            string? turnModel = null;
            string? lastStatus = null;
            string? lastEventType = null;
            var lastHasToolCalls = false;
            DateTimeOffset? turnStart = null;
            DateTimeOffset? lastEventAt = null;
            long? lastStep = null;

            for (var i = startIndex; i < events.Count; i++)
            {
                var ev = events[i];
                lastEventType = ev.Type;
                if (ev.StepIndex is { } s) lastStep = s;
                lastStatus = ev.Status;

                if (ev.CreatedAt is { } ts)
                {
                    turnStart ??= ts;
                    if (lastEventAt is null || ts > lastEventAt) lastEventAt = ts;
                }

                if (ev.Type == "PLANNER_RESPONSE")
                {
                    rawInput = TokenActivityView.SaturatingAdd(rawInput, ev.RawIn);
                    cacheRead = TokenActivityView.SaturatingAdd(cacheRead, ev.CacheRead);
                    cacheCreate = TokenActivityView.SaturatingAdd(cacheCreate, ev.CacheCreate);
                    output = TokenActivityView.SaturatingAdd(output, ev.Output);
                    if (ev.Model is not null) turnModel = ev.Model;
                    lastHasToolCalls = ev.HasToolCalls;
                }
            }

            if (lastEventType is null) return null;
            var effectiveModel = turnModel ?? lastKnownModel ?? cachedModel;
            modelOut = effectiveModel;
            var turnId = $"turn-{lastStep ?? 0}";
            var fileMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            var rawSamples = new List<CodexResponseSample>();
            var runningModel = cachedModel;
            for (var i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev.Model is not null) runningModel = ev.Model;
                if (ev.Type != "PLANNER_RESPONSE" || ev.Output <= 0 || ev.CreatedAt is not { } start)
                    continue;

                var sampleModel = ev.Model ?? runningModel ?? effectiveModel;
                DateTimeOffset? end = null;
                if (ev.HasToolCalls)
                {
                    for (var j = i + 1; j < events.Count; j++)
                    {
                        if (events[j].Type == "USER_INPUT") break;
                        if (events[j].CreatedAt is { } nextTs && nextTs >= start)
                        {
                            end = nextTs;
                            break;
                        }
                    }

                    if (end is null
                        && i == events.Count - 1
                        && IsAgyDoneOrEmpty(ev.Status)
                        && IsValidMtimeEnd(fileMtime, start, now))
                    {
                        end = ClampObserved(fileMtime, now);
                    }
                }
                else
                {
                    if (i == events.Count - 1
                        && IsAgyDoneOrEmpty(ev.Status)
                        && IsValidMtimeEnd(fileMtime, start, now))
                    {
                        end = ClampObserved(fileMtime, now);
                    }
                    else if (i < events.Count - 1)
                    {
                        var reused = cachedSamples.LastOrDefault(s => s.Start == start && s.Output == ev.Output);
                        if (reused is not null && reused.End <= now && reused.End >= now - SampleRetention)
                        {
                            rawSamples.Add(reused with { TurnId = turnId, Model = sampleModel ?? reused.Model });
                        }
                        continue;
                    }
                }

                if (end is { } resolvedEnd
                    && resolvedEnd <= now
                    && resolvedEnd - start >= MinResponseDuration
                    && resolvedEnd - start <= MaxResponseDuration)
                {
                    rawSamples.Add(new CodexResponseSample(convId, sampleModel, start, resolvedEnd, ev.Output)
                    {
                        TurnId = turnId,
                        ProviderId = ExternalProviderTokenLedger.AntigravityProviderId,
                    });
                }
            }

            samplesOut = RetainRecentSamples(rawSamples, now);

            var totalInput = TokenActivityView.SaturatingAdd(rawInput, TokenActivityView.SaturatingAdd(cacheRead, cacheCreate));
            var completed = string.Equals(lastEventType, "PLANNER_RESPONSE", StringComparison.Ordinal)
                && !lastHasToolCalls
                && IsAgyDoneOrEmpty(lastStatus);
            var elapsedSeconds = lastEventAt is { } endAt && turnStart is { } beginAt && (endAt - beginAt).TotalSeconds >= 1.0
                ? (endAt - beginAt).TotalSeconds
                : 0d;

            var observedAt = ClampObserved(lastEventAt ?? fileMtime, now);
            if (!completed
                && lastEventAt is { } evAt
                && fileMtime > evAt
                && fileMtime <= now + ClockSkewAllowance
                && fileMtime - evAt <= MaxResponseDuration)
            {
                observedAt = ClampObserved(fileMtime, now);
            }
            var startedAt = turnStart is { } st && st <= observedAt ? st : observedAt;
            var activePhase = completed
                ? null
                : ((string.Equals(lastEventType, "PLANNER_RESPONSE", StringComparison.Ordinal) && lastHasToolCalls)
                    || (string.Equals(lastEventType, "GENERIC", StringComparison.Ordinal) && string.Equals(lastStatus, "RUNNING", StringComparison.OrdinalIgnoreCase))
                        ? "tools"
                        : "generating");

            return new CachedTurnData(
                ExternalProviderTokenLedger.AntigravityProviderId,
                "AGY",
                convId,
                turnId,
                effectiveModel,
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
                completed && elapsedSeconds >= 1.0 && output > 0,
                activePhase);
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
        string? cachedModel,
        DateTimeOffset now,
        out IReadOnlyList<CodexResponseSample> samplesOut)
    {
        samplesOut = [];
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

        var effectiveModel = model ?? cachedModel;
        string? lastSessionUpdate = null;
        DateTimeOffset? lastUpdateAt = null;
        var updatesHasLines = false;
        long updInput = 0, updCacheRead = 0, updCacheWrite = 0, updOutput = 0;
        var hasUpdateUsage = false;
        double? updateElapsedMs = null;
        var rawSamples = new List<CodexResponseSample>();
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
                            double? turnElapsedMs = null;
                            if (update.NumberProperty("elapsed_ms") is { } ems && double.IsFinite(ems) && ems >= 0)
                                turnElapsedMs = ems;
                            long lineOut = 0;
                            if (update.ObjectProperty("usage") is { } u)
                            {
                                hasUpdateUsage = true;
                                TryReadNonNegativeInt64(u, "inputTokens", out updInput);
                                TryReadNonNegativeInt64(u, "cachedReadTokens", out updCacheRead);
                                TryReadNonNegativeInt64(u, "cacheCreationTokens", out updCacheWrite);
                                TryReadNonNegativeInt64(u, "outputTokens", out updOutput);
                                lineOut = updOutput;
                                if (updInput == 0 && TryReadNonNegativeInt64(u, "totalTokens", out var updTotal) && updTotal > updOutput)
                                    updInput = updTotal - updOutput;
                                if (turnElapsedMs is null && u.NumberProperty("apiDurationMs") is { } adm && double.IsFinite(adm) && adm >= 0)
                                    turnElapsedMs = adm;
                            }
                            if (turnElapsedMs is not null) updateElapsedMs = turnElapsedMs;

                            if (lineOut > 0 && ts is { } lineEnd && turnElapsedMs is { } msVal)
                            {
                                var dur = TimeSpan.FromMilliseconds(msVal);
                                if (dur >= MinResponseDuration && dur <= MaxResponseDuration && lineEnd <= now + ClockSkewAllowance)
                                {
                                    var resolvedEnd = ClampObserved(lineEnd, now);
                                    rawSamples.Add(new CodexResponseSample(sessionId, effectiveModel, resolvedEnd - dur, resolvedEnd, lineOut)
                                    {
                                        TurnId = turnId,
                                        ProviderId = ExternalProviderTokenLedger.GrokProviderId,
                                    });
                                }
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

        if (rawSamples.Count == 0 && completed && output > 0 && startedAt is { } sb && endedAt is { } se)
        {
            var dur = se - sb;
            if (dur >= MinResponseDuration && dur <= MaxResponseDuration && se <= now + ClockSkewAllowance)
            {
                var resolvedEnd = ClampObserved(se, now);
                rawSamples.Add(new CodexResponseSample(sessionId, effectiveModel, sb, resolvedEnd, output)
                {
                    TurnId = turnId,
                    ProviderId = ExternalProviderTokenLedger.GrokProviderId,
                });
            }
        }
        else if (rawSamples.Count == 0 && completed && output > 0 && endedAt is { } seOnly && updateElapsedMs is { } ums)
        {
            var dur = TimeSpan.FromMilliseconds(ums);
            if (dur >= MinResponseDuration && dur <= MaxResponseDuration && seOnly <= now + ClockSkewAllowance)
            {
                var resolvedEnd = ClampObserved(seOnly, now);
                rawSamples.Add(new CodexResponseSample(sessionId, effectiveModel, resolvedEnd - dur, resolvedEnd, output)
                {
                    TurnId = turnId,
                    ProviderId = ExternalProviderTokenLedger.GrokProviderId,
                });
            }
        }
        samplesOut = RetainRecentSamples(rawSamples, now);

        var latestTimestamp = MaxTimestamp(lastUpdateAt, endedAt, usageUpdatedAt, summaryAt);
        var observedAt = ClampObserved(latestTimestamp ?? new DateTimeOffset(latestFileUtc, TimeSpan.Zero), now);
        var effectiveStart = startedAt is { } st && st <= observedAt ? st : observedAt;
        var elapsedSeconds = endedAt is { } en && startedAt is { } bg && (en - bg).TotalSeconds >= 1.0
            ? (en - bg).TotalSeconds
            : updateElapsedMs is >= 1000
                ? updateElapsedMs.Value / 1000.0
                : 0d;
        var activePhase = completed
            ? null
            : (lastSessionUpdate is "tool_call" or "tool_call_update" ? "tools" : "generating");

        return new CachedTurnData(
            ExternalProviderTokenLedger.GrokProviderId,
            "Grok",
            sessionId,
            turnId,
            effectiveModel,
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
            completed && elapsedSeconds >= 1.0 && output > 0,
            activePhase);
    }

    private static CachedTurnData? ParseClaudeSession(
        string sessionId,
        FileStream stream,
        long length,
        DateTime writeUtc,
        string? cachedTitle,
        string? cachedProject,
        string? cachedModel,
        DateTimeOffset now,
        out string? titleOut,
        out string? projectOut,
        out string? modelOut,
        out IReadOnlyList<CodexResponseSample> samplesOut)
    {
        titleOut = cachedTitle;
        projectOut = cachedProject;
        modelOut = cachedModel;
        samplesOut = [];
        try
        {
            if (titleOut is null || projectOut is null)
            {
                foreach (var headLine in ReadHeadLines(stream, length, HeadReadBytes))
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

            var tailLines = ReadTailLines(stream, length, TailReadBytes);
            if (tailLines.Count == 0) return null;

            string? lastPromptTitle = null;
            string? effort = null;
            string? lastKnownModel = null;
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
                        if (model is not null) lastKnownModel = model;
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

            var effectiveModel = lastModel ?? lastKnownModel ?? cachedModel;
            modelOut = effectiveModel;
            var turnId = $"turn-{startIndex}";

            // Extract completed assistant response samples across parsed tail turns.
            var rawSamples = new List<CodexResponseSample>();
            var groupedMessages = new List<ClaudeGroupedMessage>();
            var keyedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            DateTimeOffset? prevBoundaryTs = null;
            double? currentTurnDurMs = null;
            var currentTurnMsgIndices = new List<int>();
            for (var i = 0; i < parsedEvents.Count; i++)
            {
                var ev = parsedEvents[i];
                if (ev.Type == "user" && !ev.IsMeta && !ev.IsToolResultUser)
                {
                    AssignSingleTurnDuration(groupedMessages, currentTurnMsgIndices, currentTurnDurMs);
                    currentTurnMsgIndices.Clear();
                    currentTurnDurMs = null;
                    if (ev.Timestamp is { } uts) prevBoundaryTs = uts;
                    continue;
                }

                if (ev.DurationMs is { } dms)
                {
                    currentTurnDurMs = dms;
                    if (ev.Timestamp is { } dts) prevBoundaryTs = dts;
                    continue;
                }

                if (ev.Type == "assistant")
                {
                    if (ev.MsgKey is { } key && keyedIndex.TryGetValue(key, out var idx))
                    {
                        var cur = groupedMessages[idx];
                        groupedMessages[idx] = cur with
                        {
                            Model = ev.Model ?? cur.Model,
                            StopReason = ev.StopReason ?? cur.StopReason,
                            LastTs = ev.Timestamp ?? cur.LastTs,
                            FirstTs = cur.FirstTs ?? ev.Timestamp,
                            Output = Math.Max(cur.Output, ev.Output),
                        };
                        if (ev.Timestamp is { } ats) prevBoundaryTs = ats;
                    }
                    else
                    {
                        var entry = new ClaudeGroupedMessage(
                            ev.Model,
                            ev.StopReason,
                            prevBoundaryTs,
                            ev.Timestamp,
                            ev.Timestamp,
                            ev.Output,
                            null);
                        var newIdx = groupedMessages.Count;
                        groupedMessages.Add(entry);
                        currentTurnMsgIndices.Add(newIdx);
                        if (ev.MsgKey is { } keyNew) keyedIndex[keyNew] = newIdx;
                        if (ev.Timestamp is { } ats) prevBoundaryTs = ats;
                    }
                }
                else if (ev.Timestamp is { } ots)
                {
                    prevBoundaryTs = ots;
                }
            }
            AssignSingleTurnDuration(groupedMessages, currentTurnMsgIndices, currentTurnDurMs);

            foreach (var gm in groupedMessages)
            {
                if (gm.StopReason is null || gm.Output <= 0 || gm.LastTs is not { } end || end > now + ClockSkewAllowance)
                    continue;
                var resolvedEnd = ClampObserved(end, now);
                DateTimeOffset? start = null;
                if (gm.PrevEventTs is { } pts && resolvedEnd >= pts)
                    start = pts;
                else if (gm.TurnDurationMs is >= 500)
                    start = resolvedEnd.AddMilliseconds(-gm.TurnDurationMs.Value);
                else if (gm.FirstTs is { } fts && resolvedEnd - fts >= MinResponseDuration)
                    start = fts;

                if (start is { } s && resolvedEnd - s >= MinResponseDuration && resolvedEnd - s <= MaxResponseDuration)
                {
                    rawSamples.Add(new CodexResponseSample(sessionId, gm.Model ?? effectiveModel, s, resolvedEnd, gm.Output)
                    {
                        TurnId = turnId,
                        ProviderId = ExternalProviderTokenLedger.ClaudeProviderId,
                    });
                }
            }
            samplesOut = RetainRecentSamples(rawSamples, now);

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
            var observedAt = ClampObserved(lastEventAt ?? new DateTimeOffset(writeUtc, TimeSpan.Zero), now);
            var startedAt = turnStart is { } st && st <= observedAt ? st : observedAt;
            var completed = string.Equals(lastTurnEntryType, "assistant", StringComparison.Ordinal)
                && lastStopReason is "end_turn" or "stop_sequence" or "max_tokens";
            var elapsedSeconds = turnDurationMs is >= 1000
                ? turnDurationMs.Value / 1000.0
                : lastAssistantAt is { } endAt && turnStart is { } beginAt && (endAt - beginAt).TotalSeconds >= 1.0
                    ? (endAt - beginAt).TotalSeconds
                    : 0d;
            var activePhase = completed
                ? null
                : (string.Equals(lastTurnEntryType, "assistant", StringComparison.Ordinal)
                    && string.Equals(lastStopReason, "tool_use", StringComparison.Ordinal)
                        ? "tools"
                        : "generating");

            return new CachedTurnData(
                ExternalProviderTokenLedger.ClaudeProviderId,
                "Claude",
                sessionId,
                turnId,
                effectiveModel,
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
                completed && elapsedSeconds >= 1.0 && output > 0,
                activePhase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsAgyDoneOrEmpty(string? status) =>
        string.IsNullOrWhiteSpace(status) || string.Equals(status, "DONE", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidMtimeEnd(DateTimeOffset fileMtime, DateTimeOffset start, DateTimeOffset now) =>
        fileMtime >= start + MinResponseDuration
        && fileMtime <= start + MaxResponseDuration
        && fileMtime <= now + ClockSkewAllowance;

    private static IReadOnlyList<CodexResponseSample> RetainRecentSamples(
        List<CodexResponseSample> samples,
        DateTimeOffset now)
    {
        if (samples.Count == 0) return [];
        var cutoff = now - SampleRetention;
        var filtered = samples
            .Where(s => s.End <= now && s.End >= cutoff && s.End - s.Start >= MinResponseDuration && s.End - s.Start <= MaxResponseDuration)
            .OrderByDescending(s => s.End)
            .Take(MaxSamplesPerSession)
            .ToArray();
        Array.Reverse(filtered);
        return filtered;
    }

    private static void AssignSingleTurnDuration(
        List<ClaudeGroupedMessage> grouped,
        List<int> indices,
        double? durationMs)
    {
        if (durationMs is not { } ms || indices.Count != 1) return;
        var idx = indices[0];
        grouped[idx] = grouped[idx] with { TurnDurationMs = ms };
    }

    private static CodexLiveSession? BuildLiveSession(CachedTurnData data, DateTimeOffset now)
    {
        if (data.Completed || data.ActivePhase is null || now - data.ObservedAt > RunningTimeout)
            return null;
        var phase = data.ActivePhase;
        return new CodexLiveSession(
            data.SessionId,
            data.Model,
            phase,
            string.Equals(phase, "generating", StringComparison.Ordinal) ? data.ObservedAt : null,
            data.ObservedAt)
        {
            TurnId = data.TurnId,
            ProviderId = data.ProviderId,
        };
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
        return ReadHeadLines(stream, stream.Length, maxBytes);
    }

    private static IReadOnlyList<string> ReadHeadLines(FileStream stream, long length, int maxBytes)
    {
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
        return ReadTailLines(stream, stream.Length, maxBytes);
    }

    private static IReadOnlyList<string> ReadTailLines(FileStream stream, long length, int maxBytes)
    {
        if (length <= 0) return [];
        var offset = Math.Max(0L, length - maxBytes);
        stream.Position = offset;
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

    private readonly record struct AgyParsedEvent(
        long? StepIndex,
        string Type,
        string? Status,
        DateTimeOffset? CreatedAt,
        string? Model,
        long RawIn,
        long CacheRead,
        long CacheCreate,
        long Output,
        bool HasToolCalls);

    private sealed record ClaudeGroupedMessage(
        string? Model,
        string? StopReason,
        DateTimeOffset? PrevEventTs,
        DateTimeOffset? FirstTs,
        DateTimeOffset? LastTs,
        long Output,
        double? TurnDurationMs);

    private sealed record CachedEntry(
        FileStamp PrimaryStamp,
        FileStamp SecondaryStamp,
        FileStamp TertiaryStamp,
        string? CachedTitle,
        string? CachedProject,
        string? CachedModel,
        IReadOnlyList<CodexResponseSample> CachedSamples,
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
        bool UsageComplete,
        string? ActivePhase);
}
