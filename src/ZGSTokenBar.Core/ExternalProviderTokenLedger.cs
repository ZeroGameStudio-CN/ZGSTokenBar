using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.Core;

public enum TokenBreakdownPeriod
{
    Today,
    Week,
    ThirtyDays,
    AllTime,
}

public sealed record ProviderTokenShare(
    string ProviderId,
    string DisplayName,
    long Tokens,
    double SharePercent);

public sealed record ProviderTokenBreakdown(
    IReadOnlyDictionary<TokenBreakdownPeriod, IReadOnlyList<ProviderTokenShare>> Periods)
{
    public IReadOnlyList<ProviderTokenShare> For(TokenBreakdownPeriod period) =>
        Periods.TryGetValue(period, out var shares) ? shares : [];

    public bool HasAnyTokens => Periods.Values.Any(shares => shares.Count > 0);
}

public sealed record ExternalProviderTokenSummary(
    IReadOnlyDictionary<string, IReadOnlyDictionary<DateOnly, long>> ByProviderDay,
    IReadOnlyDictionary<string, long> ByProviderTotal,
    DateTimeOffset UpdatedAt)
{
    public long TotalTokens
    {
        get
        {
            long sum = 0;
            foreach (var tokens in ByProviderTotal.Values)
            {
                if (tokens > 0) sum = TokenActivityView.SaturatingAdd(sum, tokens);
            }
            return sum;
        }
    }
}

public sealed record AggregatedTokenUsage(
    TokenActivityView Activity,
    long TodayTokens,
    long AllTimeTotal,
    ProviderTokenBreakdown Breakdown);

internal sealed record ExternalProviderTokenFileEntry(
    string Provider,
    long Length,
    long LastWriteUtcTicks,
    Dictionary<string, long> ByDay);

internal sealed record ExternalProviderTokenState(
    int SchemaVersion,
    DateTimeOffset UpdatedAt,
    Dictionary<string, ExternalProviderTokenFileEntry> Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExternalProviderTokenState))]
internal partial class ExternalProviderTokenJsonContext : JsonSerializerContext;

/// <summary>
/// Incremental, stat-cached local token ledger for non-Codex tools (AGY, Grok, Claude Code).
/// Stored separately in <c>provider-token-ledger-v1.json</c> so <c>codex-token-usage-index.json</c>
/// and Codex's 1-second live tok/s loop remain completely untouched.
/// </summary>
public sealed class ExternalProviderTokenLedger
{
    public const string FileName = "provider-token-ledger-v1.json";
    public const string CodexProviderId = "codex";
    public const string AntigravityProviderId = "agy";
    public const string GrokProviderId = "grok";
    public const string ClaudeProviderId = "claude";

    private const int CurrentSchemaVersion = 1;
    private const long MaxSingleJsonFileBytes = 16L * 1024 * 1024;
    private const int StreamBufferBytes = 64 * 1024;

    private readonly string _path;
    private readonly UsagePaths _paths;
    private readonly IUsageEnvironment _environment;
    private readonly object _sync = new();
    private ExternalProviderTokenState? _state;

    public ExternalProviderTokenLedger(string dataDirectory)
        : this(dataDirectory, UsagePaths.Current(), ProcessUsageEnvironment.Instance)
    {
    }

    public ExternalProviderTokenLedger(string dataDirectory, UsagePaths paths, IUsageEnvironment environment)
    {
        _path = Path.Combine(dataDirectory, FileName);
        _paths = paths;
        _environment = environment;
    }

    public ExternalProviderTokenSummary? Snapshot()
    {
        lock (_sync)
        {
            var state = EnsureStateLoaded();
            return Summarize(state);
        }
    }

    public ExternalProviderTokenSummary? Refresh(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var state = EnsureStateLoaded();
            var files = new Dictionary<string, ExternalProviderTokenFileEntry>(state.Files, StringComparer.OrdinalIgnoreCase);
            var changed = false;

            var discovered = DiscoverSourceFiles(cancellationToken);
            foreach (var stalePath in discovered.SupersededPaths)
            {
                if (files.Remove(stalePath))
                    changed = true;
            }

            foreach (var source in discovered.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo info;
                try
                {
                    info = new FileInfo(source.Path);
                    if (!info.Exists || info.Length <= 0) continue;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var length = info.Length;
                var ticks = info.LastWriteTimeUtc.Ticks;
                if (files.TryGetValue(source.Path, out var cached)
                    && string.Equals(cached.Provider, source.Provider, StringComparison.Ordinal)
                    && cached.Length == length
                    && cached.LastWriteUtcTicks == ticks)
                {
                    continue;
                }

                if (!TryParseSourceFile(source, info, out var byDay))
                    continue;

                var dayStrings = new Dictionary<string, long>(byDay.Count, StringComparer.Ordinal);
                foreach (var pair in byDay)
                {
                    if (pair.Value <= 0) continue;
                    dayStrings[pair.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = pair.Value;
                }

                files[source.Path] = new ExternalProviderTokenFileEntry(source.Provider, length, ticks, dayStrings);
                changed = true;
            }

            if (changed)
            {
                var nextState = new ExternalProviderTokenState(CurrentSchemaVersion, now, files);
                _state = nextState;
                TryPersistState(nextState);
                state = nextState;
            }

            return Summarize(state);
        }
    }

    private ExternalProviderTokenState EnsureStateLoaded()
    {
        if (_state is not null) return _state;
        _state = LoadState(_path);
        return _state;
    }

    private static ExternalProviderTokenState LoadState(string path)
    {
        try
        {
            if (!File.Exists(path))
                return EmptyState();
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                StreamBufferBytes,
                FileOptions.SequentialScan);
            var parsed = JsonSerializer.Deserialize(stream, ExternalProviderTokenJsonContext.Default.ExternalProviderTokenState);
            if (parsed is null || parsed.SchemaVersion != CurrentSchemaVersion || parsed.Files is null)
                return EmptyState();
            var normalized = new Dictionary<string, ExternalProviderTokenFileEntry>(parsed.Files.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parsed.Files)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.ByDay is null)
                    continue;
                normalized[Path.GetFullPath(pair.Key)] = pair.Value;
            }
            return new ExternalProviderTokenState(CurrentSchemaVersion, parsed.UpdatedAt, normalized);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return EmptyState();
        }
    }

    private void TryPersistState(ExternalProviderTokenState state)
    {
        try
        {
            using var writeLock = CredentialSupport.AcquireWriteLockFor(_path);
            if (writeLock is null) return;
            CredentialSupport.AtomicWriteLockedStream(_path, stream =>
                JsonSerializer.Serialize(stream, state, ExternalProviderTokenJsonContext.Default.ExternalProviderTokenState));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep updated in-memory summary if another writer momentarily holds the lock.
        }
    }

    private static ExternalProviderTokenState EmptyState() =>
        new(CurrentSchemaVersion, DateTimeOffset.MinValue, new Dictionary<string, ExternalProviderTokenFileEntry>(StringComparer.OrdinalIgnoreCase));

    private static ExternalProviderTokenSummary? Summarize(ExternalProviderTokenState state)
    {
        if (state.Files.Count == 0) return null;

        var byProviderDay = new Dictionary<string, IReadOnlyDictionary<DateOnly, long>>(StringComparer.Ordinal);
        var mutableByProviderDay = new Dictionary<string, Dictionary<DateOnly, long>>(StringComparer.Ordinal);
        var byProviderTotal = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var entry in state.Files.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.Provider) || entry.ByDay.Count == 0) continue;
            if (!mutableByProviderDay.TryGetValue(entry.Provider, out var providerDays))
            {
                providerDays = new Dictionary<DateOnly, long>();
                mutableByProviderDay[entry.Provider] = providerDays;
            }

            long fileTotal = 0;
            foreach (var pair in entry.ByDay)
            {
                if (pair.Value <= 0) continue;
                if (!DateOnly.TryParseExact(pair.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    continue;
                providerDays[day] = providerDays.TryGetValue(day, out var existing)
                    ? TokenActivityView.SaturatingAdd(existing, pair.Value)
                    : pair.Value;
                fileTotal = TokenActivityView.SaturatingAdd(fileTotal, pair.Value);
            }

            if (fileTotal > 0)
            {
                byProviderTotal[entry.Provider] = byProviderTotal.TryGetValue(entry.Provider, out var prevTotal)
                    ? TokenActivityView.SaturatingAdd(prevTotal, fileTotal)
                    : fileTotal;
            }
        }

        foreach (var pair in mutableByProviderDay)
            byProviderDay[pair.Key] = pair.Value;

        if (byProviderTotal.Count == 0) return null;
        return new ExternalProviderTokenSummary(byProviderDay, byProviderTotal, state.UpdatedAt);
    }

    private sealed record SourceFile(string Provider, string Path, SourceKind Kind);

    private enum SourceKind
    {
        AgyTranscript,
        GrokUsageJson,
        GrokUpdatesJsonl,
        ClaudeJsonl,
    }

    private sealed record DiscoveryResult(List<SourceFile> Sources, List<string> SupersededPaths);

    private DiscoveryResult DiscoverSourceFiles(CancellationToken cancellationToken)
    {
        var sources = new List<SourceFile>();
        var superseded = new List<string>();

        // 1. Antigravity: <agyRoot>/brain/<conv>/.system_generated/logs/transcript.jsonl (never transcript_full.jsonl).
        var agyRoot = NonEmpty(_environment.Get("ANTIGRAVITY_DATA_DIR"))
            ?? (string.IsNullOrWhiteSpace(_paths.Home) ? null : Path.Combine(_paths.Home, ".gemini", "antigravity"));
        if (!string.IsNullOrWhiteSpace(agyRoot))
        {
            var brainDir = Path.Combine(agyRoot, "brain");
            if (Directory.Exists(brainDir))
            {
                try
                {
                    foreach (var convDir in Directory.EnumerateDirectories(brainDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var transcriptPath = Path.Combine(convDir, ".system_generated", "logs", "transcript.jsonl");
                        if (File.Exists(transcriptPath))
                            sources.Add(new SourceFile(AntigravityProviderId, Path.GetFullPath(transcriptPath), SourceKind.AgyTranscript));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        // 2. Grok CLI: <grokRoot>/sessions/**/usage.json (preferred) else updates.jsonl (never both in one session dir).
        var grokRoot = NonEmpty(_environment.Get("GROK_HOME"))
            ?? (string.IsNullOrWhiteSpace(_paths.Home) ? null : Path.Combine(_paths.Home, ".grok"));
        if (!string.IsNullOrWhiteSpace(grokRoot))
        {
            var sessionsDir = Path.Combine(grokRoot, "sessions");
            foreach (var dir in EnumerateDirectoriesSafe(sessionsDir, cancellationToken))
            {
                var usagePath = Path.Combine(dir, "usage.json");
                var updatesPath = Path.Combine(dir, "updates.jsonl");
                var hasUsage = SafeFileExists(usagePath);
                var hasUpdates = SafeFileExists(updatesPath);
                if (hasUsage)
                {
                    sources.Add(new SourceFile(GrokProviderId, Path.GetFullPath(usagePath), SourceKind.GrokUsageJson));
                    superseded.Add(Path.GetFullPath(updatesPath));
                }
                else if (hasUpdates)
                {
                    sources.Add(new SourceFile(GrokProviderId, Path.GetFullPath(updatesPath), SourceKind.GrokUpdatesJsonl));
                }
            }
        }

        // 3. Claude Code: <claudeRoot>/projects/**/*.jsonl
        var claudeRoot = NonEmpty(_environment.Get("CLAUDE_CONFIG_DIR"))
            ?? (string.IsNullOrWhiteSpace(_paths.Home) ? null : Path.Combine(_paths.Home, ".claude"));
        if (!string.IsNullOrWhiteSpace(claudeRoot))
        {
            var projectsDir = Path.Combine(claudeRoot, "projects");
            foreach (var dir in EnumerateDirectoriesSafe(projectsDir, cancellationToken))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sources.Add(new SourceFile(ClaudeProviderId, Path.GetFullPath(file), SourceKind.ClaudeJsonl));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return new DiscoveryResult(sources, superseded);
    }

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
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                try
                {
                    var attrs = File.GetAttributes(child);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push(Path.GetFullPath(child));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool TryParseSourceFile(SourceFile source, FileInfo info, out Dictionary<DateOnly, long> byDay)
    {
        byDay = new Dictionary<DateOnly, long>();
        try
        {
            var fallbackDay = DateOnly.FromDateTime(info.LastWriteTime);
            return source.Kind switch
            {
                SourceKind.AgyTranscript => ParseAgyTranscript(source.Path, fallbackDay, byDay),
                SourceKind.GrokUsageJson => ParseGrokUsageJson(source.Path, info.Length, fallbackDay, byDay),
                SourceKind.GrokUpdatesJsonl => ParseGrokUpdatesJsonl(source.Path, fallbackDay, byDay),
                SourceKind.ClaudeJsonl => ParseClaudeJsonl(source.Path, fallbackDay, byDay),
                _ => false,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            byDay.Clear();
            return false;
        }
    }

    private static bool ParseAgyTranscript(string path, DateOnly fallbackDay, Dictionary<DateOnly, long> byDay)
    {
        using var stream = OpenReadShared(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, StreamBufferBytes);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || !line.Contains("\"output_tokens\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!TryReadNonNegativeInt64(root, "output_tokens", out var output))
                    continue;
                TryReadNonNegativeInt64(root, "input_tokens", out var input);
                TryReadNonNegativeInt64(root, "cache_read_tokens", out var cacheRead);
                TryReadNonNegativeInt64(root, "cache_creation_tokens", out var cacheCreate);
                var total = SaturatingSum(input, cacheRead, cacheCreate, output);
                if (total <= 0) continue;

                var day = ParseLocalDate(root, "created_at", fallbackDay);
                AddDay(byDay, day, total);
            }
            catch (JsonException)
            {
                // Ignore truncated line currently being written.
            }
        }
        return true;
    }

    private static bool ParseGrokUsageJson(string path, long length, DateOnly fallbackDay, Dictionary<DateOnly, long> byDay)
    {
        if (length > MaxSingleJsonFileBytes) return false;
        using var stream = OpenReadShared(path);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;

        var updatedDay = ParseLocalDate(root, "updatedAt", fallbackDay);
        long turnsSum = 0;

        if (root.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
        {
            foreach (var turn in turns.EnumerateArray())
            {
                if (turn.ValueKind != JsonValueKind.Object) continue;
                var turnTokens = ReadGrokUsageObjectTokens(turn);
                if (turnTokens <= 0) continue;
                var day = ParseLocalDate(turn, "endedAt", ParseLocalDate(turn, "startedAt", updatedDay));
                AddDay(byDay, day, turnTokens);
                turnsSum = TokenActivityView.SaturatingAdd(turnsSum, turnTokens);
            }
        }

        if (root.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object)
        {
            var sessionTotal = ReadGrokUsageObjectTokens(session);
            if (sessionTotal > turnsSum)
                AddDay(byDay, updatedDay, sessionTotal - turnsSum);
        }

        return true;
    }

    private static bool ParseGrokUpdatesJsonl(string path, DateOnly fallbackDay, Dictionary<DateOnly, long> byDay)
    {
        using var stream = OpenReadShared(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, StreamBufferBytes);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || !line.Contains("\"turn_completed\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("params", out var @params) || @params.ValueKind != JsonValueKind.Object) continue;
                if (!@params.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object) continue;
                if (!update.TryGetProperty("sessionUpdate", out var sessionUpdate)
                    || sessionUpdate.ValueKind != JsonValueKind.String
                    || !string.Equals(sessionUpdate.GetString(), "turn_completed", StringComparison.Ordinal))
                    continue;
                if (!update.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;

                var tokens = ReadGrokUsageObjectTokens(usage);
                if (tokens <= 0) continue;

                var day = ParseUnixLocalDate(root, "timestamp", fallbackDay);
                if (day == fallbackDay
                    && root.TryGetProperty("_meta", out var meta)
                    && meta.ValueKind == JsonValueKind.Object)
                {
                    day = ParseUnixLocalDate(meta, "agentTimestampMs", fallbackDay);
                }
                AddDay(byDay, day, tokens);
            }
            catch (JsonException)
            {
            }
        }
        return true;
    }

    private static bool ParseClaudeJsonl(string path, DateOnly fallbackDay, Dictionary<DateOnly, long> byDay)
    {
        // Claude streaming logs emit multiple lines sharing the same message.id; keep the max total per key.
        var keyed = new Dictionary<string, (DateOnly Day, long Tokens)>(StringComparer.Ordinal);
        using var stream = OpenReadShared(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, StreamBufferBytes);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || !line.Contains("\"output_tokens\"", StringComparison.Ordinal))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;

                TryReadNonNegativeInt64(usage, "input_tokens", out var input);
                TryReadNonNegativeInt64(usage, "cache_creation_input_tokens", out var cacheCreate);
                TryReadNonNegativeInt64(usage, "cache_read_input_tokens", out var cacheRead);
                TryReadNonNegativeInt64(usage, "output_tokens", out var output);
                var total = SaturatingSum(input, cacheCreate, cacheRead, output);
                if (total <= 0) continue;

                var day = ParseLocalDate(root, "timestamp", fallbackDay);
                var key = NonEmpty(ReadString(message, "id")) ?? NonEmpty(ReadString(root, "requestId"));
                if (key is null)
                {
                    AddDay(byDay, day, total);
                }
                else if (!keyed.TryGetValue(key, out var existing) || total >= existing.Tokens)
                {
                    keyed[key] = (day, total);
                }
            }
            catch (JsonException)
            {
            }
        }

        foreach (var entry in keyed.Values)
            AddDay(byDay, entry.Day, entry.Tokens);
        return true;
    }

    private static long ReadGrokUsageObjectTokens(JsonElement obj)
    {
        if (TryReadNonNegativeInt64(obj, "totalTokens", out var total))
            return total;
        TryReadNonNegativeInt64(obj, "inputTokens", out var input);
        TryReadNonNegativeInt64(obj, "outputTokens", out var output);
        return TokenActivityView.SaturatingAdd(input, output);
    }

    private static FileStream OpenReadShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, StreamBufferBytes, FileOptions.SequentialScan);

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    private static DateOnly ParseLocalDate(JsonElement obj, string property, DateOnly fallback)
    {
        if (!obj.TryGetProperty(property, out var element)) return fallback;
        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (!string.IsNullOrWhiteSpace(raw)
                && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                return DateOnly.FromDateTime(dto.ToLocalTime().Date);
            }
        }
        else if (element.ValueKind == JsonValueKind.Number)
        {
            return ParseUnixLocalDate(obj, property, fallback);
        }
        return fallback;
    }

    private static DateOnly ParseUnixLocalDate(JsonElement obj, string property, DateOnly fallback)
    {
        if (!obj.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number)
            return fallback;
        if (!element.TryGetDouble(out var raw) || !double.IsFinite(raw) || raw <= 0)
            return fallback;
        try
        {
            var dto = raw < 1_000_000_000_000d
                ? DateTimeOffset.FromUnixTimeSeconds((long)raw)
                : DateTimeOffset.FromUnixTimeMilliseconds((long)raw);
            return DateOnly.FromDateTime(dto.ToLocalTime().Date);
        }
        catch (ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }

    private static void AddDay(Dictionary<DateOnly, long> byDay, DateOnly day, long tokens)
    {
        if (tokens <= 0) return;
        byDay[day] = byDay.TryGetValue(day, out var existing)
            ? TokenActivityView.SaturatingAdd(existing, tokens)
            : tokens;
    }

    private static long SaturatingSum(long a, long b, long c, long d) =>
        TokenActivityView.SaturatingAdd(
            TokenActivityView.SaturatingAdd(a, b),
            TokenActivityView.SaturatingAdd(c, d));
}

public static class MultiProviderTokenAggregator
{
    private static readonly (string Id, string DisplayName)[] CanonicalProviders =
    [
        (ExternalProviderTokenLedger.CodexProviderId, "Codex"),
        (ExternalProviderTokenLedger.AntigravityProviderId, "AGY"),
        (ExternalProviderTokenLedger.GrokProviderId, "Grok"),
        (ExternalProviderTokenLedger.ClaudeProviderId, "Claude"),
    ];

    public static long? CombinedTotal(CodexTokenUsageSummary? codex, ExternalProviderTokenSummary? external)
    {
        var codexTokens = codex?.LocalTokens;
        var externalTokens = external?.TotalTokens;
        if (codexTokens is null && externalTokens is not > 0) return null;
        return TokenActivityView.SaturatingAdd(Math.Max(0, codexTokens ?? 0), Math.Max(0, externalTokens ?? 0));
    }

    public static AggregatedTokenUsage? Build(
        CodexTokenUsageSummary? codex,
        ExternalProviderTokenSummary? external,
        DateOnly today,
        int weekCount = TokenActivityView.DefaultWeekCount,
        TokenActivityGrain grain = TokenActivityGrain.Daily)
    {
        var externalTotal = external?.TotalTokens ?? 0;
        if (codex is null && externalTotal <= 0) return null;

        var codexByDay = new Dictionary<DateOnly, long>();
        if (codex is not null)
        {
            foreach (var day in codex.ActivityDays)
            {
                if (!DateOnly.TryParse(day.LocalDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                var total = Math.Max(0, day.Spend.TotalTokens);
                if (total <= 0) continue;
                codexByDay[date] = codexByDay.TryGetValue(date, out var existing)
                    ? TokenActivityView.SaturatingAdd(existing, total)
                    : total;
            }

            var codexToday = Math.Max(0, codex.TodayTokens);
            if (codexToday > codexByDay.GetValueOrDefault(today))
                codexByDay[today] = codexToday;
        }

        var mergedByDay = new Dictionary<DateOnly, long>(codexByDay);
        if (external is not null)
        {
            foreach (var providerDays in external.ByProviderDay.Values)
            {
                foreach (var pair in providerDays)
                {
                    if (pair.Value <= 0) continue;
                    mergedByDay[pair.Key] = mergedByDay.TryGetValue(pair.Key, out var existing)
                        ? TokenActivityView.SaturatingAdd(existing, pair.Value)
                        : pair.Value;
                }
            }
        }

        var codexAllTime = Math.Max(0, codex?.LocalTokens ?? 0);
        var allTimeTotal = TokenActivityView.SaturatingAdd(codexAllTime, externalTotal);
        var activity = TokenActivityCalendar.FromDailyTotals(mergedByDay, today, allTimeTotal, weekCount, grain);

        var weekStart = TokenActivityCalendar.StartMonday(today, 1);
        var thirtyDaysStart = today.AddDays(-29);

        long ProviderWindowTokens(string providerId, DateOnly start, DateOnly end)
        {
            if (string.Equals(providerId, ExternalProviderTokenLedger.CodexProviderId, StringComparison.Ordinal))
            {
                var windowSum = SumRange(codexByDay, start, end);
                if (start == thirtyDaysStart && end == today)
                    windowSum = Math.Max(windowSum, Math.Max(0, codex?.Last30DaysSpend?.TotalTokens ?? 0));
                return windowSum;
            }

            if (external is not null && external.ByProviderDay.TryGetValue(providerId, out var days))
                return SumRange(days, start, end);
            return 0;
        }

        long ProviderAllTimeTokens(string providerId)
        {
            if (string.Equals(providerId, ExternalProviderTokenLedger.CodexProviderId, StringComparison.Ordinal))
                return codexAllTime;
            return external is not null && external.ByProviderTotal.TryGetValue(providerId, out var total)
                ? Math.Max(0, total)
                : 0;
        }

        IReadOnlyList<ProviderTokenShare> BuildShares(Func<string, long> readTokens)
        {
            var raw = new List<(int Order, string Id, string Name, long Tokens)>(CanonicalProviders.Length);
            long periodTotal = 0;
            for (var i = 0; i < CanonicalProviders.Length; i++)
            {
                var (id, name) = CanonicalProviders[i];
                var tokens = readTokens(id);
                if (tokens <= 0) continue;
                raw.Add((i, id, name, tokens));
                periodTotal = TokenActivityView.SaturatingAdd(periodTotal, tokens);
            }
            if (raw.Count == 0 || periodTotal <= 0) return [];

            raw.Sort((a, b) =>
            {
                var cmp = b.Tokens.CompareTo(a.Tokens);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });

            var result = new ProviderTokenShare[raw.Count];
            for (var i = 0; i < raw.Count; i++)
            {
                var item = raw[i];
                var sharePercent = item.Tokens * 100.0 / periodTotal;
                result[i] = new ProviderTokenShare(item.Id, item.Name, item.Tokens, sharePercent);
            }
            return result;
        }

        var periods = new Dictionary<TokenBreakdownPeriod, IReadOnlyList<ProviderTokenShare>>
        {
            [TokenBreakdownPeriod.Today] = BuildShares(id => ProviderWindowTokens(id, today, today)),
            [TokenBreakdownPeriod.Week] = BuildShares(id => ProviderWindowTokens(id, weekStart, today)),
            [TokenBreakdownPeriod.ThirtyDays] = BuildShares(id => ProviderWindowTokens(id, thirtyDaysStart, today)),
            [TokenBreakdownPeriod.AllTime] = BuildShares(ProviderAllTimeTokens),
        };

        return new AggregatedTokenUsage(activity, activity.TodayTokens, allTimeTotal, new ProviderTokenBreakdown(periods));
    }

    private static long SumRange(IReadOnlyDictionary<DateOnly, long> byDay, DateOnly start, DateOnly end)
    {
        long sum = 0;
        foreach (var pair in byDay)
        {
            if (pair.Value <= 0 || pair.Key < start || pair.Key > end) continue;
            sum = TokenActivityView.SaturatingAdd(sum, pair.Value);
        }
        return sum;
    }
}
