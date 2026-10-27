using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.Core;

public sealed record CodexConversationTurn(
    string SessionId, string TurnId, string? Model, string? Effort,
    DateTimeOffset StartedAt, DateTimeOffset ObservedAt, long OutputTokens,
    double ElapsedSeconds, bool Completed, bool UsageComplete)
{
    public string? ProjectName { get; init; }
    [JsonIgnore]
    public string? Title { get; init; }
    public string? ServiceTier { get; init; }
    public string? ResponseModels { get; init; }
    public string? RoutedFromModel { get; init; }
    public string? RoutedToModel { get; init; }
    public string? RoutingReason { get; init; }
    public bool Interrupted { get; init; }
    public bool Stale { get; init; }
    public double? FirstTokenSeconds { get; init; }
    public long? InputTokens { get; init; }
    public long? CachedInputTokens { get; init; }
    public long? CacheWriteInputTokens { get; init; }
    public long? NewInputTokens => InputTokens is { } input && CachedInputTokens is { } cached && cached <= input
        ? input - cached : null;
    [JsonIgnore]
    public double? CacheHitPercent => InputTokens is > 0 && CachedInputTokens is >= 0
        && CachedInputTokens <= InputTokens ? CachedInputTokens.Value * 100d / InputTokens.Value : null;
    public decimal? ApiEquivalentUsd { get; init; }
    public bool CostIsPartial { get; init; }
    public bool IsTerminal => Completed || Interrupted;
    public string State => Interrupted ? "interrupted" : Completed ? "completed" : Stale ? "stale" : "running";
    // Whole-turn wall time includes reasoning, tool execution and waiting; this is not decode speed.
    public double? OutputTokensPerSecond => UsageComplete && !Stale && ElapsedSeconds > 0
        ? OutputTokens / ElapsedSeconds : null;
}

public sealed class CodexConversationUsageReader
{
    internal const int MaximumSessions = 12;
    internal const int ScanBudgetBytes = 8 * 1024 * 1024;
    private const int BootstrapBytes = 2 * 1024 * 1024;
    private readonly Dictionary<string, FileState> _cache = new(StringComparer.OrdinalIgnoreCase);
    internal long ParsedFiles { get; private set; }
    internal long ScannedBytes { get; private set; }
    public int PendingSessions { get; private set; }

    public IReadOnlyList<CodexConversationTurn> Refresh(
        IReadOnlyList<string> paths, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var turns = new List<CodexConversationTurn>();
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PendingSessions = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumSessions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            retained.Add(path);
            _cache.TryGetValue(path, out var state);
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                var budget = ScanBudgetBytes;
                var changed = state is null || length != state.Length || file.LastWriteTimeUtc.Ticks != state.WriteTicks;
                if (state is null || length < state.Length || length < state.Offset
                    || changed && (length == state.Length || !state.MatchesCheckpoint(stream)))
                {
                    state = new FileState(SessionId(path));
                    if (length > BootstrapBytes)
                    {
                        state.Offset = length - BootstrapBytes;
                        stream.Position = state.Offset - 1;
                        state.LineTooLong = stream.ReadByte() != '\n';
                        var bootstrapRead = Scan(stream, length, state, now, cancellationToken, BootstrapBytes);
                        ScannedBytes += bootstrapRead;
                        budget -= bootstrapRead;
                        // A complete recent turn needs no historical replay. Long turns fall back to bounded catch-up.
                        if (state.Active is null || !state.Active.HasCounterBaseline) state = new FileState(SessionId(path));
                    }
                    _cache[path] = state;
                }
                if (changed || state.Offset < length)
                {
                    state.CaughtUp = false;
                    var consumed = Scan(stream, length, state, now, cancellationToken, budget);
                    ScannedBytes += consumed;
                    ParsedFiles++;
                    state.Length = length;
                    state.WriteTicks = file.LastWriteTimeUtc.Ticks;
                    state.SaveCheckpoint(stream);
                }
                state.CaughtUp = state.Offset >= length;
                if (state.Offset < length)
                {
                    PendingSessions++;
                    continue;
                }
                if (state.ToTurn(now) is { } turn) turns.Add(turn);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (state is { CaughtUp: false }) PendingSessions++;
                else if (state?.ToTurn(now) is { } cached) turns.Add(cached with { Stale = true });
            }
        }
        foreach (var key in _cache.Keys.Where(key => !retained.Contains(key)).ToArray()) _cache.Remove(key);
        return turns.GroupBy(turn => turn.SessionId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(turn => turn.ObservedAt).First())
            .OrderByDescending(turn => turn.ObservedAt).Take(MaximumSessions).ToArray();
    }

    private static int Scan(FileStream stream, long length, FileState state, DateTimeOffset now,
        CancellationToken cancellationToken, int budget)
    {
        stream.Position = state.Offset;
        var buffer = new byte[64 * 1024];
        var remaining = (int)Math.Min(budget, length - state.Offset);
        var consumed = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == '\n')
                {
                    if (!state.LineTooLong && state.Line.Count > 0)
                        state.ProcessLine(Encoding.UTF8.GetString(state.Line.ToArray()), now);
                    state.Line.Clear();
                    state.LineTooLong = false;
                }
                else if (!state.LineTooLong)
                {
                    if (state.Line.Count < CodexTokenUsageReader.MaximumLineBytes) state.Line.Add(value);
                    else
                    {
                        // Oversized tool output is skipped without allocating an unbounded string.
                        state.LineTooLong = true;
                        state.Line.Clear();
                    }
                }
            }
            state.Offset += read;
            consumed += read;
            remaining -= read;
        }
        return consumed;
    }

    private static string SessionId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length >= 36 && Guid.TryParse(name[^36..], out _) ? name[^36..]
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16].ToLowerInvariant();
    }

    private static DateTimeOffset? Date(JsonElement value, string name) =>
        DateTimeOffset.TryParse(value.StringProperty(name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private sealed class FileState(string sessionId)
    {
        public long Offset;
        public long Length;
        public long WriteTicks;
        public List<byte> Line = [];
        public bool LineTooLong;
        public bool CaughtUp;
        public TurnState? Active;
        private CodexTokenUsageEvent? _previousUsage;
        private string? _projectName;
        private string? _serviceTier;
        private byte[] _prefix = [];
        private byte[] _checkpoint = [];
        private int _signatureLength;

        public bool MatchesCheckpoint(FileStream stream) => _signatureLength == 0
            || Signature(stream, 0, _signatureLength).SequenceEqual(_prefix)
                && Signature(stream, Offset - _signatureLength, _signatureLength).SequenceEqual(_checkpoint);

        public void SaveCheckpoint(FileStream stream)
        {
            _signatureLength = (int)Math.Min(256, Offset);
            _prefix = Signature(stream, 0, _signatureLength);
            _checkpoint = Signature(stream, Offset - _signatureLength, _signatureLength);
        }

        private static byte[] Signature(FileStream stream, long offset, int count)
        {
            stream.Position = offset;
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            return SHA256.HashData(bytes);
        }

        public void ProcessLine(string line, DateTimeOffset now)
        {
            if (!line.Contains("\"event_msg\"", StringComparison.Ordinal)
                && !line.Contains("\"turn_context\"", StringComparison.Ordinal)
                && !line.Contains("\"session_meta\"", StringComparison.Ordinal)
                && !line.Contains("\"response.completed\"", StringComparison.Ordinal)
                && !line.Contains("\"model/rerouted\"", StringComparison.Ordinal)) return;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (Date(root, "timestamp") is not { } at || at > now.AddMinutes(5)) return;
                var payload = root.ObjectProperty("payload") ?? root.ObjectProperty("params") ?? root;
                var type = root.StringProperty("type");
                var evidenceType = root.StringProperty("method") ?? (type == "event_msg" ? payload.StringProperty("type") : type);
                if (evidenceType is "response.completed" or "model_rerouted" or "model/rerouted")
                {
                    RecordModelEvidence(payload, evidenceType, at);
                    return;
                }
                if (type is "session_meta" or "turn_context")
                {
                    if (payload.StringProperty("cwd") is { } cwd)
                    {
                        var name = cwd.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault();
                        if (!string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl))
                            _projectName = name[..Math.Min(64, name.Length)];
                    }
                    if (type == "turn_context" && Active is { } active && payload.StringProperty("turn_id") == active.Id)
                    {
                        var model = payload.StringProperty("model");
                        if (active.Model is not null && model != active.Model) active.MixedModel = true;
                        active.Model = model;
                        active.Effort = payload.StringProperty("effort");
                    }
                    if (payload.TryGetProperty("service_tier", out _)) _serviceTier = NormalizeTier(payload.StringProperty("service_tier"));
                    return;
                }
                if (type != "event_msg") return;
                switch (payload.StringProperty("type"))
                {
                    case "thread_settings_applied":
                        if (payload.ObjectProperty("thread_settings") is { } settings
                            && settings.TryGetProperty("service_tier", out _))
                            _serviceTier = NormalizeTier(settings.StringProperty("service_tier"));
                        break;
                    case "task_started":
                        if (payload.StringProperty("turn_id") is { Length: > 0 } id)
                        {
                            var startedAt = Date(payload, "started_at");
                            Active = new(id, startedAt is { } start && start <= at ? start : at, _previousUsage is not null);
                        }
                        break;
                    case "token_count":
                        if (!CodexTokenUsageReader.TryParseLine(line, out var usage)
                            || usage?.OutputTokens is not { } output) break;
                        if (_previousUsage is { } prior && usage.CapturedAt < prior.CapturedAt) break;
                        if (Active is { } current && usage.CapturedAt >= current.StartedAt)
                        {
                            var reset = _previousUsage is null || usage.TotalTokens < _previousUsage.TotalTokens;
                            var delta = Delta(_previousUsage?.OutputTokens, output, usage.LastOutputTokens, reset);
                            if (delta is >= 0 && delta <= long.MaxValue - current.Output)
                                current.Output += delta.Value;
                            else current.UsageComplete = false;
                            RecordInputAndCost(current, usage, delta, reset);
                            current.ObservedAt = at;
                        }
                        _previousUsage = usage;
                        break;
                    case "task_complete":
                    case "turn_aborted":
                        if (Active is not { } terminal
                            || payload.StringProperty("turn_id") is { } endedId && endedId != terminal.Id) break;
                        var end = Date(payload, "completed_at") ?? at;
                        if (end < terminal.StartedAt || end > at.AddMinutes(5)) end = at;
                        terminal.EndedAt = end;
                        terminal.ObservedAt = at;
                        terminal.Interrupted = payload.StringProperty("type") == "turn_aborted";
                        if (payload.NumberProperty("time_to_first_token_ms") is { } first
                            && double.IsFinite(first) && first >= 0 && first <= (end - terminal.StartedAt).TotalMilliseconds)
                            terminal.FirstTokenSeconds = first / 1000;
                        break;
                }
            }
            catch (JsonException)
            {
                if (Active is { } active)
                {
                    active.UsageComplete = false;
                    active.InputComplete = false;
                    active.CostIsPartial = true;
                }
            }
        }

        private static long? Delta(long? before, long? after, long? last, bool reset) =>
            !reset && before is { } previous && after is { } current && current >= previous
                ? current - previous : last;

        private void RecordModelEvidence(JsonElement payload, string type, DateTimeOffset at)
        {
            // Unscoped model fields, settings, catalog headers and sub-agent metadata are not response evidence.
            if (Active is not { } turn || at < turn.StartedAt
                || payload.StringProperty("turn_id", "turnId") != turn.Id
                || payload.StringProperty("thread_id", "threadId") is { } thread && thread != sessionId) return;
            if (type == "response.completed")
            {
                if (payload.ObjectProperty("response") is not { } response
                    || response.StringProperty("object") != "response" || response.StringProperty("status") != "completed"
                    || response.StringProperty("id") is not { } responseId || !responseId.StartsWith("resp_", StringComparison.Ordinal)
                    || ValidModel(response.StringProperty("model")) is not { } model) return;
                if (turn.ResponseModels.Count < 8) turn.ResponseModels.Add(model);
            }
            else if (ValidModel(payload.StringProperty("from_model", "fromModel")) is { } from
                && ValidModel(payload.StringProperty("to_model", "toModel")) is { } to)
            {
                if (turn.RoutingObservedAt is { } previous && at < previous) return;
                turn.RoutingObservedAt = at;
                turn.RoutedFromModel = from;
                turn.RoutedToModel = to;
                var reason = payload.StringProperty("reason");
                turn.RoutingReason = reason is { Length: <= 128 } && !reason.Any(char.IsControl) ? reason : null;
            }
        }

        private static string? ValidModel(string? value) => value is { Length: > 0 and <= 128 }
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '/' or ':') ? value : null;

        private static string? NormalizeTier(string? tier) => tier?.Trim().ToLowerInvariant() switch
        {
            null or "" or "default" or "auto" or "standard" => "standard",
            "priority" or "fast" => "fast",
            "ultrafast" => "ultrafast",
            "flex" => "flex",
            "batch" => "batch",
            _ => null,
        };

        private void RecordInputAndCost(TurnState turn, CodexTokenUsageEvent usage, long? output, bool reset)
        {
            var input = Delta(_previousUsage?.InputTokens, usage.InputTokens, usage.LastInputTokens, reset);
            var cached = Delta(_previousUsage?.CachedInputTokens, usage.CachedInputTokens, usage.LastCachedInputTokens, reset);
            var write = Delta(_previousUsage?.CacheWriteInputTokens, usage.CacheWriteInputTokens, usage.LastCacheWriteInputTokens, reset);
            if (input is not { } inputs || cached is not { } hits || hits > inputs
                || inputs > long.MaxValue - turn.Input || hits > long.MaxValue - turn.Cached)
            {
                turn.InputComplete = false;
                turn.CostIsPartial = true;
                return;
            }
            turn.Input += inputs;
            turn.Cached += hits;
            if (write is { } writes && writes <= inputs - hits && writes <= long.MaxValue - turn.CacheWrite)
                turn.CacheWrite += writes;
            else turn.CacheWriteComplete = false;

            if (inputs == 0 && output == 0) return;
            if (_serviceTier is null) turn.MissingServiceTier = true;
            else if (turn.ServiceTier is { } priorTier && priorTier != _serviceTier) turn.MixedServiceTier = true;
            else turn.ServiceTier = _serviceTier;
            var total = Delta(_previousUsage?.TotalTokens, usage.TotalTokens, usage.LastTotalTokens, reset);
            if (output is not { } outputs || write is not { } cacheWrites || cacheWrites > inputs - hits
                || total is not { } totalTokens || inputs > totalTokens || outputs > totalTokens - inputs
                || total != usage.LastTotalTokens || inputs != usage.LastInputTokens || outputs != usage.LastOutputTokens)
            {
                turn.CostIsPartial = true;
                return;
            }
            var estimate = CodexPricingCatalog.Estimate(new CodexModelTokenUsage(turn.Model,
                inputs, hits, outputs, IsLongContext: inputs > CodexPricingCatalog.LongContextInputTokenThreshold,
                CacheWriteInputTokens: cacheWrites, ServiceTier: _serviceTier));
            if (estimate.ApiEquivalentUsd is { } cost)
            {
                turn.Cost += cost;
                turn.HasPrice = true;
            }
            else turn.CostIsPartial = true;
        }

        public CodexConversationTurn? ToTurn(DateTimeOffset now)
        {
            if (Active is not { } turn || turn.Output == 0 && turn.Input == 0 || turn.ObservedAt < turn.StartedAt) return null;
            return new(sessionId, turn.Id, turn.MixedModel ? null : turn.Model, turn.MixedModel ? null : turn.Effort,
                turn.StartedAt, turn.ObservedAt, turn.Output,
                Math.Max(0, ((turn.EndedAt ?? now) - turn.StartedAt).TotalSeconds),
                turn.EndedAt is not null && !turn.Interrupted, turn.UsageComplete)
            {
                ProjectName = _projectName,
                ResponseModels = turn.ResponseModels.Count == 0 ? null : string.Join(" | ", turn.ResponseModels.Order(StringComparer.Ordinal)),
                RoutedFromModel = turn.RoutedFromModel,
                RoutedToModel = turn.RoutedToModel,
                RoutingReason = turn.RoutingReason,
                ServiceTier = turn.MixedServiceTier ? "mixed" : turn.MissingServiceTier ? null : turn.ServiceTier,
                Interrupted = turn.Interrupted,
                Stale = turn.EndedAt is null && now - turn.ObservedAt > TimeSpan.FromMinutes(2),
                FirstTokenSeconds = turn.FirstTokenSeconds,
                InputTokens = turn.InputComplete ? turn.Input : null,
                CachedInputTokens = turn.InputComplete ? turn.Cached : null,
                CacheWriteInputTokens = turn.InputComplete && turn.CacheWriteComplete ? turn.CacheWrite : null,
                ApiEquivalentUsd = turn.HasPrice ? turn.Cost : null,
                CostIsPartial = turn.CostIsPartial,
            };
        }
    }

    private sealed class TurnState(string id, DateTimeOffset startedAt, bool hasCounterBaseline)
    {
        public string Id = id;
        public bool HasCounterBaseline = hasCounterBaseline;
        public DateTimeOffset StartedAt = startedAt;
        public DateTimeOffset ObservedAt = startedAt;
        public string? Model;
        public string? Effort;
        public string? ServiceTier;
        public bool MixedServiceTier;
        public bool MissingServiceTier;
        public bool MixedModel;
        public HashSet<string> ResponseModels = new(StringComparer.Ordinal);
        public string? RoutedFromModel;
        public string? RoutedToModel;
        public string? RoutingReason;
        public DateTimeOffset? RoutingObservedAt;
        public long Output;
        public DateTimeOffset? EndedAt;
        public bool Interrupted;
        public bool UsageComplete = true;
        public double? FirstTokenSeconds;
        public long Input;
        public long Cached;
        public long CacheWrite;
        public bool InputComplete = true;
        public bool CacheWriteComplete = true;
        public decimal Cost;
        public bool HasPrice;
        public bool CostIsPartial;
    }
}
