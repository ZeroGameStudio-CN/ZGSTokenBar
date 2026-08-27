using System.Globalization;

namespace ZGSTokenBar.Core;

public sealed record CodexQuotaTokenObservation(
    string CardKey,
    string WindowLabel,
    long DurationTicks,
    DateTimeOffset CapturedAt,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    string SourceKey,
    long TotalTokens);

public sealed record CodexQuotaTokenSeriesKey(
    string CardKey,
    string WindowLabel,
    long DurationTicks)
{
    public override string ToString() =>
        string.Join(
            '\0',
            CardKey,
            WindowLabel,
            DurationTicks.ToString(CultureInfo.InvariantCulture));
}

public sealed record CodexQuotaTokenSummary(
    string CardKey,
    string WindowLabel,
    long DurationTicks,
    long? CurrentCapacityTokens,
    long? MaxCapacityTokens,
    double? AverageCapacityTokens,
    int CompletedCycleCount,
    bool HasCurrentObservation = false,
    long? CurrentObservedTokens = null,
    double? CurrentObservedSpanPercent = null,
    bool CoversCycleStart = false,
    bool IsCurrentLocalFallback = false,
    long? RecentWeeklyAverageTokens = null)
{
    public long? Current => CurrentCapacityTokens;
    public long? Highest => MaxCapacityTokens;
    public double? Average => AverageCapacityTokens;
    public long? CurrentCycleTokens => CoversCycleStart ? CurrentObservedTokens : null;
    public bool HasHistory => CompletedCycleCount > 0;
    public bool HasData => HasCurrentObservation || HasHistory;

    public long? EstimateUsedTokens(double? currentUsedPercent)
    {
        if (CoversCycleStart && CurrentObservedTokens is { } observedTokens)
        {
            return observedTokens;
        }

        if (CurrentCapacityTokens is not { } capacityTokens
            || currentUsedPercent is not { } usedPercent
            || !double.IsFinite(usedPercent)
            || usedPercent < 0)
        {
            return null;
        }

        var estimate = (decimal)capacityTokens
            * (decimal)Math.Min(usedPercent, 100)
            / 100m;
        if (estimate <= 0) return null;
        if (estimate >= long.MaxValue) return long.MaxValue;
        return (long)decimal.Round(estimate, 0, MidpointRounding.AwayFromZero);
    }
}

public sealed class CodexQuotaTokenHistory
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<CodexQuotaTokenSeriesHistory> Series { get; set; } = [];
}

public sealed class CodexQuotaTokenSeriesHistory
{
    public string CardKey { get; set; } = string.Empty;
    public string WindowLabel { get; set; } = string.Empty;
    public long DurationTicks { get; set; }
    public DateTimeOffset? CurrentResetsAt { get; set; }
    public double? CurrentMinUsedPercent { get; set; }
    public double? CurrentMaxUsedPercent { get; set; }
    public long CurrentObservedTokens { get; set; }
    public DateTimeOffset? CurrentLastObservedAt { get; set; }
    public List<CodexQuotaTokenSourceCursor> CurrentSources { get; set; } = [];
    public string? CurrentActiveSource { get; set; }
    public long CurrentActiveObservedTokens { get; set; }
    public DateTimeOffset? CurrentSourceStartedAt { get; set; }
    public double? CurrentSourceMinUsedPercent { get; set; }
    public double? CurrentSourceMaxUsedPercent { get; set; }
    public bool CurrentProfileRegressed { get; set; }
    public int CompletedCycleCount { get; set; }
    public long CapacityTokenSum { get; set; }
    public long? MaxCapacityTokens { get; set; }
}

public sealed class CodexQuotaTokenSourceCursor
{
    public string SourceKey { get; set; } = string.Empty;
    public DateTimeOffset LastCapturedAt { get; set; }
    public long LastTotalTokens { get; set; }
}

public sealed class CodexQuotaTokenTracker
{
    public const string ProfileLifetimeSourceKey = "profile-lifetime";
    public const string RolloutFallbackSourcePrefix = "rollout-fallback:";
    private const string RolloutFallbackModeKey = "rollout-fallback";
    public const double MinimumCurrentSpanPercent = 1.0;
    public const double MinimumCompletedSpanPercent = 5.0;
    public static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(2);

    private readonly Dictionary<CodexQuotaTokenSeriesKey, SeriesState> _states = [];

    public CodexQuotaTokenTracker(CodexQuotaTokenHistory? history = null)
    {
        if (history is null) return;
        foreach (var series in history.Series ?? [])
        {
            if (!TryReadSeries(series, out var state)) continue;
            var key = new CodexQuotaTokenSeriesKey(
                series.CardKey.Trim(),
                NormalizeWindowLabel(series.WindowLabel, series.DurationTicks),
                series.DurationTicks);
            if (_states.ContainsKey(key)) continue;
            _states[key] = state;
        }
    }

    public bool Observe(
        CodexQuotaTokenObservation observation,
        DateTimeOffset now) =>
        Merge([observation], now);

    public bool Merge(
        IEnumerable<CodexQuotaTokenObservation> observations,
        DateTimeOffset now)
    {
        if (observations is null) return false;
        var changed = false;
        foreach (var observation in observations
                     .Where(item => IsStructurallyValid(item, now))
                     .Select(Normalize)
                     .OrderBy(item => item.CapturedAt)
                     .ThenBy(item => item.CardKey, StringComparer.Ordinal)
                     .ThenBy(item => item.WindowLabel, StringComparer.Ordinal)
                     .ThenBy(item => item.DurationTicks)
                     .ThenBy(item => item.SourceKey, StringComparer.Ordinal))
        {
            changed |= ObserveOne(observation);
        }

        return changed;
    }

    public bool MergeObservations(
        IEnumerable<CodexQuotaTokenObservation> observations,
        DateTimeOffset now) =>
        Merge(observations, now);

    public static bool IsRolloutFallbackSource(string? sourceKey) =>
        sourceKey is not null
        && sourceKey.StartsWith(RolloutFallbackSourcePrefix, StringComparison.Ordinal)
        && sourceKey.Length > RolloutFallbackSourcePrefix.Length;

    public static string ToRolloutFallbackSourceKey(string sourceKey) =>
        IsRolloutFallbackSource(sourceKey)
            ? sourceKey
            : RolloutFallbackSourcePrefix + sourceKey.Trim();

    public bool IsRolloutFallbackEligible(
        CodexQuotaTokenObservation observation,
        DateTimeOffset now)
    {
        if (!IsStructurallyValid(observation, now)
            || string.Equals(
                observation.SourceKey.Trim(),
                ProfileLifetimeSourceKey,
                StringComparison.Ordinal)
            || IsRolloutFallbackSource(observation.SourceKey))
        {
            return false;
        }

        var key = MakeKey(
            observation.CardKey,
            observation.WindowLabel,
            observation.DurationTicks);
        if (!_states.TryGetValue(key, out var state)
            || state.CurrentResetsAt is not { } currentReset
            || !SameCycle(currentReset, observation.ResetsAt!.Value))
        {
            return true;
        }

        if (IsFallbackMode(state)) return true;

        if (!state.CurrentSources.TryGetValue(
                ProfileLifetimeSourceKey,
                out var profileCursor))
        {
            return true;
        }

        if (state.CurrentSourceStartedAt is { } sourceStartedAt
            && observation.CapturedAt < sourceStartedAt)
        {
            return false;
        }

        if (profileCursor.LastTotalTokens == 0)
        {
            return true;
        }

        var minimum = state.CurrentSourceMinUsedPercent is { } currentMinimum
            ? Math.Min(currentMinimum, observation.UsedPercent)
            : observation.UsedPercent;
        var maximum = state.CurrentSourceMaxUsedPercent is { } currentMaximum
            ? Math.Max(currentMaximum, observation.UsedPercent)
            : observation.UsedPercent;
        return maximum - minimum >= MinimumCurrentSpanPercent;
    }

    public bool IsRolloutFallbackReplayObservation(
        CodexQuotaTokenObservation observation,
        DateTimeOffset now)
    {
        if (!IsStructurallyValid(observation, now)) return false;
        var key = MakeKey(
            observation.CardKey,
            observation.WindowLabel,
            observation.DurationTicks);
        if (!_states.TryGetValue(key, out var state)
            || state.CurrentResetsAt is not { } currentReset
            || !SameCycle(currentReset, observation.ResetsAt!.Value))
        {
            return true;
        }

        if (state.CurrentSourceStartedAt is not { } sourceStartedAt) return true;
        return observation.CapturedAt >= sourceStartedAt;
    }

    public CodexQuotaTokenSummary? GetSummary(
        string cardKey,
        string windowLabel,
        long durationTicks)
    {
        var key = MakeKey(cardKey, windowLabel, durationTicks);
        return _states.TryGetValue(key, out var state)
            ? ToSummary(state)
            : null;
    }

    public CodexQuotaTokenSummary? GetSummary(CodexQuotaTokenSeriesKey key) =>
        _states.TryGetValue(key, out var state) ? ToSummary(state) : null;

    public IReadOnlyList<CodexQuotaTokenSummary> ExportSummaries(DateTimeOffset now) =>
        _states.Values
            .Select(ToSummary)
            .Where(summary => summary.HasData)
            .OrderBy(summary => summary.CardKey, StringComparer.Ordinal)
            .ThenBy(summary => summary.WindowLabel, StringComparer.Ordinal)
            .ThenBy(summary => summary.DurationTicks)
            .ToArray();

    public IReadOnlyList<CodexQuotaTokenSummary> Summaries(DateTimeOffset now) =>
        ExportSummaries(now);

    public IReadOnlyList<CodexQuotaTokenSummary> GetSummaries(DateTimeOffset now) =>
        ExportSummaries(now);

    public long? GetProfileLifetimeTotal()
    {
        var perCard = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var state in _states.Values)
        {
            if (!state.CurrentSources.TryGetValue(ProfileLifetimeSourceKey, out var cursor)) continue;
            if (!perCard.TryGetValue(state.Key.CardKey, out var current)
                || cursor.LastTotalTokens > current)
            {
                perCard[state.Key.CardKey] = cursor.LastTotalTokens;
            }
        }

        if (perCard.Count == 0) return null;
        long total = 0;
        foreach (var tokens in perCard.Values) total = SaturatingAdd(total, tokens);
        return total;
    }

    public CodexQuotaTokenHistory Export() =>
        new()
        {
            SchemaVersion = CodexQuotaTokenHistory.CurrentSchemaVersion,
            Series = _states
                .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
                .Select(pair => ToHistory(pair.Key, pair.Value))
                .ToList(),
        };

    public CodexQuotaTokenHistory Export(DateTimeOffset now) => Export();

    public CodexQuotaTokenHistory ExportHistory() => Export();

    internal static bool IsValidHistory(CodexQuotaTokenHistory? history)
    {
        if (history is null
            || history.SchemaVersion != CodexQuotaTokenHistory.CurrentSchemaVersion
            || history.Series is null)
        {
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var series in history.Series)
        {
            if (!TryReadSeries(series, out _)) return false;
            var key = MakeKey(
                series.CardKey,
                series.WindowLabel,
                series.DurationTicks).ToString();
            if (!keys.Add(key)) return false;
        }

        return true;
    }

    private bool ObserveOne(CodexQuotaTokenObservation observation)
    {
        var key = MakeKey(
            observation.CardKey,
            observation.WindowLabel,
            observation.DurationTicks);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new SeriesState(key);
            _states[key] = state;
        }

        var reset = observation.ResetsAt!.Value.ToUniversalTime();
        if (state.CurrentResetsAt is { } currentReset
            && !SameCycle(currentReset, reset))
        {
            if (reset < currentReset - ResetTolerance
                || state.CurrentCycleStartedAt is { } startedAt
                && observation.CapturedAt < startedAt)
            {
                return false;
            }

            CompleteCycle(state);
            state.CurrentResetsAt = reset;
            state.CurrentCycleStartedAt = observation.CapturedAt;
            state.CurrentMinUsedPercent = null;
            state.CurrentMaxUsedPercent = null;
            state.CurrentObservedTokens = 0;
            state.CurrentLastObservedAt = null;
            state.CurrentSources.Clear();
            ResetActiveSource(state);
        }
        else if (state.CurrentResetsAt is null)
        {
            state.CurrentResetsAt = reset;
            state.CurrentCycleStartedAt = observation.CapturedAt;
        }

        if (string.Equals(
                observation.SourceKey,
                ProfileLifetimeSourceKey,
                StringComparison.Ordinal)
            && IsFallbackMode(state))
        {
            return ObserveProfileDuringFallback(state, observation);
        }

        if (IsRolloutFallbackSource(observation.SourceKey)
            && !PrepareRolloutFallback(state, observation))
        {
            return false;
        }

        MigrateCurrentCycleToProfileSource(state, observation.SourceKey, reset, observation.CapturedAt);

        if (!state.CurrentSources.TryGetValue(observation.SourceKey, out var cursor))
        {
            state.CurrentSources[observation.SourceKey] = new SourceState(
                observation.CapturedAt,
                observation.TotalTokens);
            InitializeActiveSource(state, observation);
            UpdateCurrentRange(state, observation.UsedPercent);
            state.CurrentLastObservedAt = Max(state.CurrentLastObservedAt, observation.CapturedAt);
            return true;
        }

        if (observation.CapturedAt < cursor.LastCapturedAt
            || observation.CapturedAt == cursor.LastCapturedAt
            && observation.TotalTokens <= cursor.LastTotalTokens)
        {
            return false;
        }

        if (observation.TotalTokens < cursor.LastTotalTokens)
        {
            cursor.LastCapturedAt = observation.CapturedAt;
            if (IsActiveProfile(state))
            {
                state.CurrentProfileRegressed = true;
                UpdateSourceRange(state, observation.UsedPercent);
            }
            else
            {
                cursor.LastTotalTokens = observation.TotalTokens;
            }
            UpdateCurrentRange(state, observation.UsedPercent);
            state.CurrentLastObservedAt = Max(state.CurrentLastObservedAt, observation.CapturedAt);
            return true;
        }

        var delta = observation.TotalTokens - cursor.LastTotalTokens;
        state.CurrentObservedTokens = SaturatingAdd(state.CurrentObservedTokens, delta);
        state.CurrentActiveObservedTokens = SaturatingAdd(
            state.CurrentActiveObservedTokens,
            delta);
        cursor.LastCapturedAt = observation.CapturedAt;
        cursor.LastTotalTokens = observation.TotalTokens;
        if (IsActiveProfile(state))
        {
            if (delta > 0) ResetSourceProgress(state, observation);
            else UpdateSourceRange(state, observation.UsedPercent);
        }
        UpdateCurrentRange(state, observation.UsedPercent);
        state.CurrentLastObservedAt = Max(state.CurrentLastObservedAt, observation.CapturedAt);
        return true;
    }

    private static void MigrateCurrentCycleToProfileSource(
        SeriesState state,
        string sourceKey,
        DateTimeOffset reset,
        DateTimeOffset capturedAt)
    {
        if (!string.Equals(sourceKey, ProfileLifetimeSourceKey, StringComparison.Ordinal)
            || state.CurrentSources.ContainsKey(ProfileLifetimeSourceKey)
            || state.CurrentSources.Count == 0
            || state.CurrentResetsAt is not { } currentReset
            || !SameCycle(currentReset, reset)
            || HasRolloutFallbackSource(state))
        {
            return;
        }

        // Older builds populated this current cycle from rollout logs. Keep
        // completed aggregates, but establish a clean lifetime-token baseline.
        state.CurrentResetsAt = reset;
        state.CurrentCycleStartedAt = capturedAt;
        state.CurrentMinUsedPercent = null;
        state.CurrentMaxUsedPercent = null;
        state.CurrentObservedTokens = 0;
        state.CurrentLastObservedAt = null;
        state.CurrentSources.Clear();
        ResetActiveSource(state);
    }

    private static bool PrepareRolloutFallback(
        SeriesState state,
        CodexQuotaTokenObservation observation)
    {
        if (IsFallbackMode(state)) return true;

        if (state.CurrentSources.TryGetValue(
                ProfileLifetimeSourceKey,
                out var profileCursor))
        {
            var profileCanFallback = profileCursor.LastTotalTokens == 0
                || ProjectedSourceSpan(state, observation.UsedPercent)
                    >= MinimumCurrentSpanPercent;
            if (!profileCanFallback) return false;
        }

        var preservedProfile = state.CurrentSources.TryGetValue(
            ProfileLifetimeSourceKey,
            out var profile)
                ? profile
                : null;
        state.CurrentSources.Clear();
        if (preservedProfile is not null)
        {
            state.CurrentSources[ProfileLifetimeSourceKey] = preservedProfile;
        }
        if (state.CurrentObservedTokens == 0)
        {
            state.CurrentMinUsedPercent = observation.UsedPercent;
            state.CurrentMaxUsedPercent = observation.UsedPercent;
        }
        state.CurrentActiveSource = RolloutFallbackModeKey;
        state.CurrentActiveObservedTokens = 0;
        state.CurrentSourceStartedAt = observation.CapturedAt;
        state.CurrentSourceMinUsedPercent = observation.UsedPercent;
        state.CurrentSourceMaxUsedPercent = observation.UsedPercent;
        return true;
    }

    private static bool ObserveProfileDuringFallback(
        SeriesState state,
        CodexQuotaTokenObservation observation)
    {
        if (!state.CurrentSources.TryGetValue(ProfileLifetimeSourceKey, out var cursor))
        {
            state.CurrentSources.Clear();
            state.CurrentSources[ProfileLifetimeSourceKey] = new SourceState(
                observation.CapturedAt,
                observation.TotalTokens);
            state.CurrentActiveSource = ProfileLifetimeSourceKey;
            state.CurrentActiveObservedTokens = 0;
            ResetSourceProgress(state, observation);
            UpdateCurrentRange(state, observation.UsedPercent);
            state.CurrentLastObservedAt = Max(state.CurrentLastObservedAt, observation.CapturedAt);
            return true;
        }

        if (observation.CapturedAt < cursor.LastCapturedAt
            || observation.CapturedAt == cursor.LastCapturedAt
            && observation.TotalTokens <= cursor.LastTotalTokens)
        {
            return false;
        }

        if (observation.TotalTokens <= cursor.LastTotalTokens)
        {
            cursor.LastCapturedAt = observation.CapturedAt;
            if (observation.TotalTokens < cursor.LastTotalTokens)
            {
                state.CurrentProfileRegressed = true;
            }
            return true;
        }

        if (state.CurrentProfileRegressed) return false;

        var officialDelta = observation.TotalTokens - cursor.LastTotalTokens;
        var completedSegments = Math.Max(
            0,
            state.CurrentObservedTokens - state.CurrentActiveObservedTokens);
        state.CurrentObservedTokens = SaturatingAdd(completedSegments, officialDelta);
        state.CurrentActiveObservedTokens = officialDelta;
        state.CurrentSources.Clear();
        state.CurrentSources[ProfileLifetimeSourceKey] = new SourceState(
            observation.CapturedAt,
            observation.TotalTokens);
        state.CurrentActiveSource = ProfileLifetimeSourceKey;
        ResetSourceProgress(state, observation);
        UpdateCurrentRange(state, observation.UsedPercent);
        state.CurrentLastObservedAt = Max(state.CurrentLastObservedAt, observation.CapturedAt);
        return true;
    }

    private static bool HasRolloutFallbackSource(SeriesState state) =>
        state.CurrentSources.Keys.Any(IsRolloutFallbackSource);

    private static bool IsFallbackMode(SeriesState state) =>
        string.Equals(
            state.CurrentActiveSource,
            RolloutFallbackModeKey,
            StringComparison.Ordinal)
        || state.CurrentActiveSource is null && HasRolloutFallbackSource(state);

    private static bool IsActiveProfile(SeriesState state) =>
        string.Equals(
            state.CurrentActiveSource,
            ProfileLifetimeSourceKey,
            StringComparison.Ordinal);

    private static double ProjectedSourceSpan(SeriesState state, double usedPercent)
    {
        var minimum = state.CurrentSourceMinUsedPercent is { } currentMinimum
            ? Math.Min(currentMinimum, usedPercent)
            : usedPercent;
        var maximum = state.CurrentSourceMaxUsedPercent is { } currentMaximum
            ? Math.Max(currentMaximum, usedPercent)
            : usedPercent;
        return maximum - minimum;
    }

    private static double CurrentSpan(SeriesState state) =>
        state.CurrentMinUsedPercent is { } minimum
        && state.CurrentMaxUsedPercent is { } maximum
            ? maximum - minimum
            : 0;

    private static void CompleteCycle(SeriesState state)
    {
        var span = state.CurrentMinUsedPercent is { } minimum
            && state.CurrentMaxUsedPercent is { } maximum
            ? maximum - minimum
            : 0;
        var capacity = CalculateCapacity(
            state.CurrentObservedTokens,
            span,
            MinimumCompletedSpanPercent);
        if (capacity is not { } completedCapacity) return;

        state.CompletedCycleCount = state.CompletedCycleCount == int.MaxValue
            ? int.MaxValue
            : state.CompletedCycleCount + 1;
        state.CapacityTokenSum = SaturatingAdd(
            state.CapacityTokenSum,
            completedCapacity);
        state.MaxCapacityTokens = state.MaxCapacityTokens is { } currentMax
            ? Math.Max(currentMax, completedCapacity)
            : completedCapacity;
    }

    private static CodexQuotaTokenSummary ToSummary(SeriesState state)
    {
        var span = state.CurrentMinUsedPercent is { } minimum
            && state.CurrentMaxUsedPercent is { } maximum
            ? maximum - minimum
            : 0;
        return new CodexQuotaTokenSummary(
            state.Key.CardKey,
            state.Key.WindowLabel,
            state.Key.DurationTicks,
            CalculateCapacity(
                state.CurrentObservedTokens,
                span,
                MinimumCurrentSpanPercent),
            state.MaxCapacityTokens,
            state.CompletedCycleCount > 0
                ? state.CapacityTokenSum / (double)state.CompletedCycleCount
                : null,
            state.CompletedCycleCount,
            state.CurrentMinUsedPercent is not null,
            state.CurrentMinUsedPercent is not null
                ? state.CurrentObservedTokens
                : null,
            state.CurrentMinUsedPercent is not null ? span : null,
            state.CurrentMinUsedPercent is <= 0,
            IsFallbackMode(state));
    }

    private static CodexQuotaTokenSeriesHistory ToHistory(
        CodexQuotaTokenSeriesKey key,
        SeriesState state) =>
        new()
        {
            CardKey = key.CardKey,
            WindowLabel = key.WindowLabel,
            DurationTicks = key.DurationTicks,
            CurrentResetsAt = state.CurrentResetsAt,
            CurrentMinUsedPercent = state.CurrentMinUsedPercent,
            CurrentMaxUsedPercent = state.CurrentMaxUsedPercent,
            CurrentObservedTokens = state.CurrentObservedTokens,
            CurrentLastObservedAt = state.CurrentLastObservedAt,
            CurrentActiveSource = state.CurrentActiveSource,
            CurrentActiveObservedTokens = state.CurrentActiveObservedTokens,
            CurrentSourceStartedAt = state.CurrentSourceStartedAt,
            CurrentSourceMinUsedPercent = state.CurrentSourceMinUsedPercent,
            CurrentSourceMaxUsedPercent = state.CurrentSourceMaxUsedPercent,
            CurrentProfileRegressed = state.CurrentProfileRegressed,
            CurrentSources = state.CurrentSources
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new CodexQuotaTokenSourceCursor
                {
                    SourceKey = pair.Key,
                    LastCapturedAt = pair.Value.LastCapturedAt,
                    LastTotalTokens = pair.Value.LastTotalTokens,
                })
                .ToList(),
            CompletedCycleCount = state.CompletedCycleCount,
            CapacityTokenSum = state.CapacityTokenSum,
            MaxCapacityTokens = state.MaxCapacityTokens,
        };

    private static bool TryReadSeries(
        CodexQuotaTokenSeriesHistory? source,
        out SeriesState state)
    {
        state = null!;
        if (source is null
            || string.IsNullOrWhiteSpace(source.CardKey)
            || string.IsNullOrWhiteSpace(source.WindowLabel)
            || source.DurationTicks <= 0
            || source.CompletedCycleCount < 0
            || source.CapacityTokenSum < 0
            || source.MaxCapacityTokens is < 0
            || source.CurrentObservedTokens < 0
            || source.CurrentActiveObservedTokens < 0
            || source.CurrentActiveObservedTokens > source.CurrentObservedTokens
            || source.CurrentSources is null)
        {
            return false;
        }

        var cardKey = source.CardKey.Trim();
        var windowLabel = NormalizeWindowLabel(source.WindowLabel, source.DurationTicks);
        var minimum = source.CurrentMinUsedPercent;
        var maximum = source.CurrentMaxUsedPercent;
        if ((minimum is { } min
                && (!double.IsFinite(min) || min is < 0 or > 100))
            || (maximum is { } max
                && (!double.IsFinite(max) || max is < 0 or > 100))
            || ((minimum is null) != (maximum is null))
            || (minimum is { } lower
                && maximum is { } upper
                && lower > upper))
        {
            return false;
        }

        var sourceMinimum = source.CurrentSourceMinUsedPercent;
        var sourceMaximum = source.CurrentSourceMaxUsedPercent;
        if ((sourceMinimum is { } activeMin
                && (!double.IsFinite(activeMin) || activeMin is < 0 or > 100))
            || (sourceMaximum is { } activeMax
                && (!double.IsFinite(activeMax) || activeMax is < 0 or > 100))
            || ((sourceMinimum is null) != (sourceMaximum is null))
            || (sourceMinimum is { } activeLower
                && sourceMaximum is { } activeUpper
                && activeLower > activeUpper))
        {
            return false;
        }

        var hasCurrent = minimum is not null;
        if ((source.CurrentResetsAt is not null) != hasCurrent
            || (source.CurrentLastObservedAt is not null) != hasCurrent)
        {
            return false;
        }

        var cursors = new Dictionary<string, SourceState>(StringComparer.Ordinal);
        foreach (var cursor in source.CurrentSources)
        {
            if (cursor is null
                || string.IsNullOrWhiteSpace(cursor.SourceKey)
                || cursor.LastCapturedAt == default
                || cursor.LastTotalTokens < 0
                || !cursors.TryAdd(
                    cursor.SourceKey.Trim(),
                    new SourceState(cursor.LastCapturedAt, cursor.LastTotalTokens)))
            {
                return false;
            }
        }

        if (minimum is not null && cursors.Count == 0) return false;
        if (source.CompletedCycleCount > 0 && source.MaxCapacityTokens is null) return false;
        var inferredActiveSource = source.CurrentActiveSource;
        if (string.IsNullOrWhiteSpace(inferredActiveSource))
        {
            inferredActiveSource = cursors.ContainsKey(ProfileLifetimeSourceKey)
                ? ProfileLifetimeSourceKey
                : cursors.Keys.Any(IsRolloutFallbackSource)
                    ? RolloutFallbackModeKey
                    : null;
        }
        if (inferredActiveSource is not null
            && !string.Equals(inferredActiveSource, ProfileLifetimeSourceKey, StringComparison.Ordinal)
            && !string.Equals(inferredActiveSource, RolloutFallbackModeKey, StringComparison.Ordinal))
        {
            return false;
        }
        var activeObservedTokens = source.CurrentActiveObservedTokens;
        if (source.CurrentActiveSource is null && inferredActiveSource is not null)
        {
            activeObservedTokens = source.CurrentObservedTokens;
        }
        state = new SeriesState(
            new CodexQuotaTokenSeriesKey(cardKey, windowLabel, source.DurationTicks))
        {
            CurrentResetsAt = source.CurrentResetsAt?.ToUniversalTime(),
            CurrentCycleStartedAt = source.CurrentLastObservedAt,
            CurrentMinUsedPercent = minimum,
            CurrentMaxUsedPercent = maximum,
            CurrentObservedTokens = source.CurrentObservedTokens,
            CurrentLastObservedAt = source.CurrentLastObservedAt,
            CurrentActiveSource = inferredActiveSource,
            CurrentActiveObservedTokens = activeObservedTokens,
            CurrentSourceStartedAt = source.CurrentSourceStartedAt
                ?? source.CurrentLastObservedAt,
            CurrentSourceMinUsedPercent = sourceMinimum ?? maximum,
            CurrentSourceMaxUsedPercent = sourceMaximum ?? maximum,
            CurrentProfileRegressed = source.CurrentProfileRegressed,
            CompletedCycleCount = source.CompletedCycleCount,
            CapacityTokenSum = source.CapacityTokenSum,
            MaxCapacityTokens = source.MaxCapacityTokens,
        };
        foreach (var pair in cursors) state.CurrentSources[pair.Key] = pair.Value;
        return true;
    }

    private static bool IsStructurallyValid(
        CodexQuotaTokenObservation observation,
        DateTimeOffset now) =>
        observation is not null
        && !string.IsNullOrWhiteSpace(observation.CardKey)
        && !string.IsNullOrWhiteSpace(observation.WindowLabel)
        && observation.DurationTicks > 0
        && observation.ResetsAt is not null
        && observation.CapturedAt != default
        && observation.CapturedAt <= now.AddMinutes(1)
        && !string.IsNullOrWhiteSpace(observation.SourceKey)
        && observation.TotalTokens >= 0
        && double.IsFinite(observation.UsedPercent)
        && observation.UsedPercent is >= 0 and <= 100;

    private static CodexQuotaTokenObservation Normalize(
        CodexQuotaTokenObservation observation) =>
        observation with
        {
            CardKey = observation.CardKey.Trim(),
            WindowLabel = NormalizeWindowLabel(observation.WindowLabel, observation.DurationTicks),
            CapturedAt = observation.CapturedAt.ToUniversalTime(),
            ResetsAt = observation.ResetsAt!.Value.ToUniversalTime(),
            SourceKey = observation.SourceKey.Trim(),
        };

    private static CodexQuotaTokenSeriesKey MakeKey(
        string cardKey,
        string windowLabel,
        long durationTicks) =>
        new(
            cardKey.Trim(),
            NormalizeWindowLabel(windowLabel, durationTicks),
            durationTicks);

    private static string NormalizeWindowLabel(string label, long durationTicks)
    {
        var normalized = label.Trim().ToLowerInvariant();
        var duration = TimeSpan.FromTicks(durationTicks);
        if (duration == TimeSpan.FromHours(5) && normalized is "primary" or "5h") return "5h";
        return normalized is "1w" or "week" or "7d" ? "7d" : normalized;
    }

    private static bool SameCycle(DateTimeOffset left, DateTimeOffset right) =>
        (left - right).Duration() <= ResetTolerance;

    private static long? CalculateCapacity(
        long observedTokens,
        double observedPercent,
        double minimumPercent)
    {
        if (observedTokens <= 0
            || !double.IsFinite(observedPercent)
            || observedPercent < minimumPercent)
        {
            return null;
        }

        var capacity = (decimal)observedTokens * 100m / (decimal)observedPercent;
        if (capacity <= 0) return null;
        if (capacity >= long.MaxValue) return long.MaxValue;
        return (long)decimal.Round(capacity, 0, MidpointRounding.AwayFromZero);
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset right) =>
        left is { } existing && existing >= right ? existing : right;

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private sealed class SeriesState(CodexQuotaTokenSeriesKey key)
    {
        public CodexQuotaTokenSeriesKey Key { get; } = key;
        public DateTimeOffset? CurrentResetsAt { get; set; }
        public DateTimeOffset? CurrentCycleStartedAt { get; set; }
        public double? CurrentMinUsedPercent { get; set; }
        public double? CurrentMaxUsedPercent { get; set; }
        public long CurrentObservedTokens { get; set; }
        public DateTimeOffset? CurrentLastObservedAt { get; set; }
        public Dictionary<string, SourceState> CurrentSources { get; } = new(StringComparer.Ordinal);
        public string? CurrentActiveSource { get; set; }
        public long CurrentActiveObservedTokens { get; set; }
        public DateTimeOffset? CurrentSourceStartedAt { get; set; }
        public double? CurrentSourceMinUsedPercent { get; set; }
        public double? CurrentSourceMaxUsedPercent { get; set; }
        public bool CurrentProfileRegressed { get; set; }
        public int CompletedCycleCount { get; set; }
        public long CapacityTokenSum { get; set; }
        public long? MaxCapacityTokens { get; set; }
    }

    private sealed class SourceState(DateTimeOffset lastCapturedAt, long lastTotalTokens)
    {
        public DateTimeOffset LastCapturedAt { get; set; } = lastCapturedAt;
        public long LastTotalTokens { get; set; } = lastTotalTokens;
    }

    private static void UpdateCurrentRange(SeriesState state, double usedPercent)
    {
        state.CurrentMinUsedPercent = state.CurrentMinUsedPercent is { } minimum
            ? Math.Min(minimum, usedPercent)
            : usedPercent;
        state.CurrentMaxUsedPercent = state.CurrentMaxUsedPercent is { } maximum
            ? Math.Max(maximum, usedPercent)
            : usedPercent;
    }

    private static void InitializeActiveSource(
        SeriesState state,
        CodexQuotaTokenObservation observation)
    {
        if (IsRolloutFallbackSource(observation.SourceKey))
        {
            state.CurrentActiveSource = RolloutFallbackModeKey;
        }
        else if (string.Equals(
                     observation.SourceKey,
                     ProfileLifetimeSourceKey,
                     StringComparison.Ordinal))
        {
            state.CurrentActiveSource = ProfileLifetimeSourceKey;
        }
        state.CurrentSourceStartedAt ??= observation.CapturedAt;
        state.CurrentSourceMinUsedPercent ??= observation.UsedPercent;
        state.CurrentSourceMaxUsedPercent ??= observation.UsedPercent;
    }

    private static void ResetSourceProgress(
        SeriesState state,
        CodexQuotaTokenObservation observation)
    {
        state.CurrentSourceStartedAt = observation.CapturedAt;
        state.CurrentSourceMinUsedPercent = observation.UsedPercent;
        state.CurrentSourceMaxUsedPercent = observation.UsedPercent;
    }

    private static void UpdateSourceRange(SeriesState state, double usedPercent)
    {
        state.CurrentSourceMinUsedPercent = state.CurrentSourceMinUsedPercent is { } minimum
            ? Math.Min(minimum, usedPercent)
            : usedPercent;
        state.CurrentSourceMaxUsedPercent = state.CurrentSourceMaxUsedPercent is { } maximum
            ? Math.Max(maximum, usedPercent)
            : usedPercent;
    }

    private static void ResetActiveSource(SeriesState state)
    {
        state.CurrentActiveSource = null;
        state.CurrentActiveObservedTokens = 0;
        state.CurrentSourceStartedAt = null;
        state.CurrentSourceMinUsedPercent = null;
        state.CurrentSourceMaxUsedPercent = null;
        state.CurrentProfileRegressed = false;
    }
}
