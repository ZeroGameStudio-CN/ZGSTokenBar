using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZGSTokenBar.Core;

public sealed record RadarIqSample(
    DateTimeOffset ObservedAt,
    double Score);

public sealed record RadarModel(
    string Model,
    string Label,
    string? ReasoningEffort,
    double? Score,
    string? Status,
    long? Passed,
    long? ValidTasks,
    double? CostUsd,
    double? AverageTaskSeconds,
    string? WallTime)
{
    public IReadOnlyList<RadarIqSample> IqHistory { get; init; } = [];
    public long? IncompleteCostSamples { get; init; }
}

public sealed record RadarRecommendationItem(
    RadarModel Model,
    string? Slot,
    string? Rule,
    long? CostSamples,
    long? DurationSamples,
    double? CombinedCostIndex);

public sealed record RadarRecommendationGroup(
    string Key,
    string Title,
    string? Rule,
    IReadOnlyList<RadarRecommendationItem> Items);

public sealed record RadarRecommendationFeed(
    int? Schema,
    string? Mode,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? SourceUpdatedAt,
    IReadOnlyList<RadarRecommendationGroup> Groups);

public sealed record RadarMeasurementFeed(
    DateTimeOffset? SourceUpdatedAt,
    IReadOnlyList<RadarModel> Models);

public sealed record RadarResetWindow(
    bool Open,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? ClosedAt,
    string? SourceUrl)
{
    public DateTimeOffset? TargetAt { get; init; }
    public string? Scope { get; init; }
}

public sealed record ProviderRadarSnapshot(
    ProviderKind Provider,
    string EventId,
    DateTimeOffset? SourceUpdatedAt,
    DateTimeOffset CapturedAt,
    RadarModel Primary,
    IReadOnlyList<RadarModel> Comparisons)
{
    public RadarRecommendationFeed? RecommendationFeed { get; init; }
    public RadarResetWindow? ResetWindow { get; init; }
}

internal static class RadarSnapshotLimits
{
    internal const int MaxTrackedModels = 128;
    internal const int MaxComparisonModels = MaxTrackedModels - 1;
    internal const int MaxRecommendationGroups = 32;
    internal const int MaxRecommendationItems = 128;
    internal const int MaxIqHistorySamples = 96;

    internal static ProviderRadarSnapshot Trim(ProviderRadarSnapshot snapshot)
    {
        var primary = TrimModel(snapshot.Primary);
        return snapshot with
        {
            Primary = primary,
            Comparisons = TrimComparisons(primary, snapshot.Comparisons),
            RecommendationFeed = snapshot.RecommendationFeed is { } feed
                ? Trim(feed)
                : null,
        };
    }

    internal static RadarRecommendationFeed Trim(RadarRecommendationFeed feed)
    {
        var remainingItems = MaxRecommendationItems;
        var groups = new List<RadarRecommendationGroup>();
        foreach (var group in feed.Groups.Take(MaxRecommendationGroups))
        {
            if (remainingItems == 0) break;
            var items = group.Items
                .GroupBy(item => ModelKey(item.Model), StringComparer.OrdinalIgnoreCase)
                .Select(itemsByModel => itemsByModel.First())
                .Take(remainingItems)
                .Select(item => item with { Model = TrimModel(item.Model) })
                .ToArray();
            if (items.Length == 0) continue;
            remainingItems -= items.Length;
            groups.Add(group with { Items = items });
        }
        return feed with { Groups = groups };
    }

    internal static IReadOnlyList<RadarModel> TrimComparisons(
        RadarModel primary,
        IEnumerable<RadarModel> comparisons) =>
        comparisons
            .Where(model => !SameModel(model, primary))
            .GroupBy(ModelKey, StringComparer.OrdinalIgnoreCase)
            .Select(models => TrimModel(models.First()))
            .Take(MaxComparisonModels)
            .ToArray();

    private static RadarModel TrimModel(RadarModel model) =>
        model with
        {
            IqHistory = model.IqHistory
                .Where(sample => double.IsFinite(sample.Score))
                .OrderBy(sample => sample.ObservedAt)
                .TakeLast(MaxIqHistorySamples)
                .ToArray(),
        };

    private static string ModelKey(RadarModel model) =>
        $"{model.Model}\n{model.ReasoningEffort}";

    private static bool SameModel(RadarModel left, RadarModel right) =>
        string.Equals(left.Model, right.Model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            left.ReasoningEffort,
            right.ReasoningEffort,
            StringComparison.OrdinalIgnoreCase);
}

public enum RadarErrorCode
{
    Timeout,
    SchemaChanged,
    StateSaveFailed,
    Unavailable,
}

public sealed record RadarViewState(
    ProviderRadarSnapshot? Snapshot,
    DateTimeOffset? LastSuccessfulFetchAt,
    bool Loading,
    RadarErrorCode? Error,
    bool HasUnread = false,
    IReadOnlySet<string>? UnreadSurfaceIds = null)
{
    public bool HasUnreadFor(string surfaceId) =>
        UnreadSurfaceIds?.Contains(surfaceId) ?? HasUnread;

    public bool IsStale(DateTimeOffset now) =>
        LastSuccessfulFetchAt is null || now - LastSuccessfulFetchAt > TimeSpan.FromMinutes(2);
}

public interface IProviderRadarModule
{
    ProviderKind Provider { get; }
    Uri SourceUri { get; }
    TimeSpan PollInterval { get; }
    Task<ProviderRadarSnapshot> FetchAsync(CancellationToken cancellationToken = default);
    RadarAlertDecision Evaluate(RadarAlertState state, ProviderRadarSnapshot snapshot);
}

public sealed class RadarService : IProviderRadarModule, IDisposable
{
    public const string SiteUrl = "https://codexradar.com/";
    public static readonly Uri SiteUri = new(SiteUrl);
    public static readonly Uri SummaryUri = new("https://codexradar.com/current.json");
    public static readonly Uri RecommendationsUri = new("https://codexradar.com/api/radar-insights");
    public static readonly Uri MeasurementsUri = new("https://codexradar.com/data/intelligence-efficiency.json");
    private static readonly TimeSpan SupplementalRefreshInterval = TimeSpan.FromMinutes(10);
    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _utcNow;
    private RadarRecommendationFeed? _recommendationCache;
    private DateTimeOffset? _recommendationsFetchedAt;
    private RadarMeasurementFeed? _measurementCache;
    private DateTimeOffset? _measurementsFetchedAt;
    private DateTimeOffset? _resetTargetCache;
    private DateTimeOffset? _resetTargetFetchedAt;
    private DateTimeOffset? _resetTargetFailedAt;
    private string? _resetTargetWindowKey;

    public RadarService(HttpMessageHandler? handler = null)
        : this(handler, () => DateTimeOffset.UtcNow)
    {
    }

    internal RadarService(HttpMessageHandler? handler, Func<DateTimeOffset> utcNow)
    {
        _httpClient = handler is null
            ? new HttpClient(new HttpClientHandler { UseCookies = false })
            : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(12);
        // The public intelligence feed is currently about 1.3 MiB and grows
        // with its bounded history. Keep a bounded buffer without dropping
        // the supplemental measurements (including DeepSeek rows).
        _httpClient.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        _utcNow = utcNow;
    }

    public ProviderKind Provider => ProviderKind.Codex;
    public Uri SourceUri => SiteUri;
    public TimeSpan PollInterval => TimeSpan.FromMinutes(1);

    public void RestoreRecommendationCache(ProviderRadarSnapshot? snapshot)
    {
        if (snapshot is null) return;
        snapshot = RadarSnapshotLimits.Trim(snapshot);
        if (snapshot.RecommendationFeed is { Groups.Count: > 0 } feed)
        {
            _recommendationCache = feed;
            _recommendationsFetchedAt = null;
        }
        _measurementCache = new RadarMeasurementFeed(
            snapshot.SourceUpdatedAt,
            [snapshot.Primary, .. snapshot.Comparisons]);
        _measurementsFetchedAt = null;
    }

    public async Task<ProviderRadarSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var now = _utcNow();
        var snapshot = RadarParser.Parse(
            await FetchJsonAsync(SummaryUri, cancellationToken),
            now);
        var resetWindowTask = EnrichResetWindowAsync(snapshot.ResetWindow, now, cancellationToken);
        var measurementsTask = FetchMeasurementsAsync(now, cancellationToken);
        var recommendationsTask = FetchRecommendationsAsync(now, cancellationToken);
        await Task.WhenAll(resetWindowTask, measurementsTask, recommendationsTask);

        snapshot = snapshot with { ResetWindow = await resetWindowTask };
        var measurements = await measurementsTask;
        if (measurements is not null)
        {
            snapshot = RadarMeasurementsParser.Merge(snapshot, measurements);
        }
        var recommendations = await recommendationsTask;
        return recommendations is null
            ? snapshot
            : RadarRecommendationsParser.Merge(snapshot, recommendations);
    }

    private async Task<RadarResetWindow?> EnrichResetWindowAsync(
        RadarResetWindow? window,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (window?.Open != true)
        {
            _resetTargetCache = null;
            _resetTargetFetchedAt = null;
            _resetTargetFailedAt = null;
            _resetTargetWindowKey = null;
            return window;
        }

        var windowKey = window.OpenedAt is { } openedAt
            ? string.Join(
                '\n',
                openedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                window.SourceUrl ?? string.Empty,
                window.Scope ?? string.Empty)
            : null;
        if (windowKey is null)
        {
            if (_resetTargetWindowKey is not null) _resetTargetFailedAt = null;
            _resetTargetCache = null;
            _resetTargetFetchedAt = null;
            _resetTargetWindowKey = null;
        }
        else if (!string.Equals(_resetTargetWindowKey, windowKey, StringComparison.Ordinal))
        {
            _resetTargetCache = null;
            _resetTargetFetchedAt = null;
            _resetTargetFailedAt = null;
            _resetTargetWindowKey = windowKey;
        }

        if (window.TargetAt is { } suppliedTarget)
        {
            if (windowKey is not null)
            {
                _resetTargetCache = suppliedTarget;
                _resetTargetFetchedAt = now;
            }
            _resetTargetFailedAt = null;
            return window;
        }

        if (windowKey is not null
            && _resetTargetFetchedAt is { } fetchedAt
            && now - fetchedAt < SupplementalRefreshInterval)
        {
            return window with { TargetAt = _resetTargetCache };
        }
        if (_resetTargetFailedAt is { } failedAt
            && now - failedAt < SupplementalRefreshInterval)
        {
            return window with { TargetAt = windowKey is null ? null : _resetTargetCache };
        }

        DateTimeOffset? fetchedTarget;
        try
        {
            fetchedTarget = RadarHomePageParser.ParseResetTarget(
                await FetchTextAsync(SiteUri, "text/html", cancellationToken));
            _resetTargetFailedAt = null;
            if (windowKey is not null)
            {
                _resetTargetCache = fetchedTarget;
                _resetTargetFetchedAt = now;
            }
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException
                or IOException
                or RegexMatchTimeoutException
                or TaskCanceledException)
        {
            // The page clock is supplemental; retain any cached target and the primary snapshot.
            _resetTargetFailedAt = now;
            return window with { TargetAt = windowKey is null ? null : _resetTargetCache };
        }

        return window with { TargetAt = fetchedTarget };
    }

    private async Task<RadarMeasurementFeed?> FetchMeasurementsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_measurementsFetchedAt is { } fetchedAt
            && now - fetchedAt < SupplementalRefreshInterval)
        {
            return _measurementCache;
        }

        try
        {
            var latest = RadarMeasurementsParser.Parse(
                await FetchJsonAsync(MeasurementsUri, cancellationToken));
            if (latest.Models.Count > 0)
            {
                _measurementCache = latest;
                _measurementsFetchedAt = now;
            }
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            // Full measurements are supplemental; retain the last usable set.
        }
        return _measurementCache;
    }

    private async Task<RadarRecommendationFeed?> FetchRecommendationsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (_recommendationsFetchedAt is { } fetchedAt
            && now - fetchedAt < SupplementalRefreshInterval)
        {
            return _recommendationCache;
        }

        try
        {
            var latest = RadarRecommendationsParser.Parse(
                await FetchJsonAsync(RecommendationsUri, cancellationToken));
            if (latest.Groups.Count > 0)
            {
                _recommendationCache = latest;
                _recommendationsFetchedAt = now;
            }
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            // Supplemental insights must not make the primary Radar unavailable.
        }
        return _recommendationCache;
    }

    private Task<string> FetchJsonAsync(Uri uri, CancellationToken cancellationToken) =>
        FetchTextAsync(uri, "application/json", cancellationToken);

    private async Task<string> FetchTextAsync(
        Uri uri,
        string mediaType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.UserAgent.ParseAdd("ZGSTokenBar/2.1");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public RadarAlertDecision Evaluate(RadarAlertState state, ProviderRadarSnapshot snapshot) =>
        RadarAlertTracker.Evaluate(state, snapshot);

    public void Dispose() => _httpClient.Dispose();
}

internal static class RadarHomePageParser
{
    private static readonly Regex WindowClockTagPattern = new(
        @"<[^>]*\bdata-window-clock\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex WindowClosesAtPattern = new(
        """\bdata-window-closes-at\s*=\s*(?:"(?<double>[^"]+)"|'(?<single>[^']+)')""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(250));

    internal static DateTimeOffset? ParseResetTarget(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        foreach (Match tag in WindowClockTagPattern.Matches(html))
        {
            var valueMatch = WindowClosesAtPattern.Match(tag.Value);
            if (!valueMatch.Success) continue;
            var value = valueMatch.Groups["double"].Success
                ? valueMatch.Groups["double"].Value
                : valueMatch.Groups["single"].Value;
            if (DateTimeOffset.TryParse(
                    WebUtility.HtmlDecode(value),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var target))
            {
                return target;
            }
        }

        return null;
    }
}

public static class RadarRecommendationsParser
{
    public static RadarRecommendationFeed Parse(string insightsJson)
    {
        using var insights = JsonDocument.Parse(insightsJson);
        var root = insights.RootElement;
        var groups = new List<RadarRecommendationGroup>();
        var recommendations = root.ArrayProperty("recommendations");
        if (recommendations is not null)
        {
            foreach (var groupValue in recommendations.Value.EnumerateArray())
            {
                var key = groupValue.StringProperty("key");
                var title = groupValue.StringProperty("title");
                var itemValues = groupValue.ArrayProperty("items");
                if (string.IsNullOrWhiteSpace(key)
                    || string.IsNullOrWhiteSpace(title)
                    || itemValues is null)
                {
                    continue;
                }

                var items = itemValues.Value
                    .EnumerateArray()
                    .Select(ParseItem)
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToArray();
                if (items.Length == 0) continue;

                groups.Add(new RadarRecommendationGroup(
                    key,
                    title,
                    groupValue.StringProperty("rule"),
                    items));
            }
        }

        return RadarSnapshotLimits.Trim(new RadarRecommendationFeed(
            NonNegativeInt(root.NumberProperty("schema")),
            root.StringProperty("mode"),
            ParseDate(root.StringProperty("generated_at")),
            ParseDate(root.StringProperty("source_updated_at")),
            groups));
    }

    public static ProviderRadarSnapshot Merge(
        ProviderRadarSnapshot snapshot,
        RadarRecommendationFeed feed)
    {
        feed = RadarSnapshotLimits.Trim(feed);
        var recommendedItems = feed.Groups
            .SelectMany(group => group.Items)
            .GroupBy(
                item => ModelKey(item.Model),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var primaryItem = recommendedItems.FirstOrDefault(item =>
            SameModel(item.Model, snapshot.Primary));
        var primary = primaryItem is null
            ? snapshot.Primary
            : Overlay(snapshot.Primary, primaryItem.Model);
        var comparisons = snapshot.Comparisons
            .Select(model =>
            {
                var item = recommendedItems.FirstOrDefault(candidate =>
                    SameModel(candidate.Model, model));
                return item is null ? model : Overlay(model, item.Model);
            })
            .ToList();

        foreach (var item in recommendedItems)
        {
            if (SameModel(primary, item.Model)
                || comparisons.Any(model => SameModel(model, item.Model)))
            {
                continue;
            }
            comparisons.Add(item.Model);
        }

        return RadarSnapshotLimits.Trim(snapshot with
        {
            SourceUpdatedAt = Latest(
                snapshot.SourceUpdatedAt,
                feed.SourceUpdatedAt ?? feed.GeneratedAt),
            Primary = primary,
            Comparisons = comparisons,
            RecommendationFeed = feed,
        });
    }

    private static RadarRecommendationItem? ParseItem(JsonElement value)
    {
        var model = value.StringProperty("model");
        var effort = value.StringProperty("effort");
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        var durationMinutes = FiniteNonNegative(
            value.NumberProperty("average_duration_minutes"));
        var radarModel = new RadarModel(
            model,
            $"{model} {effort}",
            effort,
            Finite(value.NumberProperty("iq")),
            null,
            NonNegativeLong(value.NumberProperty("passed")),
            NonNegativeLong(value.NumberProperty("samples")),
            FiniteNonNegative(value.NumberProperty("average_cost_usd")),
            durationMinutes is { } minutes ? minutes * 60 : null,
            null)
        {
            IqHistory = RadarIqHistoryParser.Parse(value.ArrayProperty("trend_48h"), "iq"),
        };
        return new RadarRecommendationItem(
            radarModel,
            value.StringProperty("slot"),
            value.StringProperty("rule"),
            NonNegativeLong(value.NumberProperty("cost_samples")),
            NonNegativeLong(value.NumberProperty("duration_samples")),
            FiniteNonNegative(value.NumberProperty("combined_cost_index")));
    }

    private static RadarModel Overlay(RadarModel existing, RadarModel upstream) =>
        existing with
        {
            Score = upstream.Score ?? existing.Score,
            Passed = upstream.Passed ?? existing.Passed,
            ValidTasks = upstream.ValidTasks ?? existing.ValidTasks,
            CostUsd = upstream.CostUsd ?? existing.CostUsd,
            AverageTaskSeconds = upstream.AverageTaskSeconds ?? existing.AverageTaskSeconds,
            IqHistory = upstream.IqHistory.Count > 0 ? upstream.IqHistory : existing.IqHistory,
        };

    private static string ModelKey(RadarModel model) =>
        $"{model.Model}\n{model.ReasoningEffort}";

    private static bool SameModel(RadarModel left, RadarModel right) =>
        string.Equals(left.Model, right.Model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            left.ReasoningEffort,
            right.ReasoningEffort,
            StringComparison.OrdinalIgnoreCase);

    private static double? Finite(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static double? FiniteNonNegative(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

    private static long? NonNegativeLong(double? value) =>
        value is { } number
        && double.IsFinite(number)
        && number >= 0
        && number < long.MaxValue
            ? (long)Math.Round(number)
            : null;

    private static int? NonNegativeInt(double? value) =>
        value is { } number
        && double.IsFinite(number)
        && number >= 0
        && number <= int.MaxValue
            ? (int)Math.Round(number)
            : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null || left >= right ? left : right;
}

public static class RadarMeasurementsParser
{
    public static RadarMeasurementFeed Parse(string measurementJson)
    {
        using var document = JsonDocument.Parse(measurementJson);
        var root = document.RootElement;
        var history = ParseHistory(root.ArrayProperty("history"));
        var points = root.ArrayProperty("points");
        var models = points is null
            ? []
            : points.Value
                .EnumerateArray()
                .Select(value => ParseModel(value, history))
                .Where(model => model is not null)
                .Select(model => model!)
                .GroupBy(ModelKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(RadarSnapshotLimits.MaxTrackedModels)
                .ToArray();
        return new RadarMeasurementFeed(
            ParseDate(root.StringProperty("source_updated_at")),
            models);
    }

    public static ProviderRadarSnapshot Merge(
        ProviderRadarSnapshot snapshot,
        RadarMeasurementFeed feed)
    {
        var primaryMeasurement = feed.Models.FirstOrDefault(model => SameModel(model, snapshot.Primary));
        var primary = primaryMeasurement is null
            ? snapshot.Primary
            : Overlay(snapshot.Primary, primaryMeasurement);
        var comparisons = snapshot.Comparisons
            .Select(model =>
            {
                var measurement = feed.Models.FirstOrDefault(candidate => SameModel(candidate, model));
                return measurement is null ? model : Overlay(model, measurement);
            })
            .ToList();

        foreach (var measurement in feed.Models)
        {
            if (SameModel(primary, measurement)
                || comparisons.Any(model => SameModel(model, measurement)))
            {
                continue;
            }
            comparisons.Add(measurement);
        }

        return RadarSnapshotLimits.Trim(snapshot with
        {
            SourceUpdatedAt = Latest(snapshot.SourceUpdatedAt, feed.SourceUpdatedAt),
            Primary = primary,
            Comparisons = comparisons,
        });
    }

    private static RadarModel? ParseModel(
        JsonElement value,
        IReadOnlyDictionary<string, IReadOnlyList<RadarIqSample>> history)
    {
        var model = value.StringProperty("model");
        var effort = value.StringProperty("effort");
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(effort)) return null;

        var durationMinutes = FiniteNonNegative(value.NumberProperty("average_minutes"));
        var key = ModelKey(model, effort);
        return new RadarModel(
            model,
            $"{model} {effort}",
            effort,
            Finite(value.NumberProperty("iq")),
            null,
            NonNegativeLong(value.NumberProperty("passed")),
            NonNegativeLong(value.NumberProperty("valid_tasks")),
            FiniteNonNegative(value.NumberProperty("average_price_usd")),
            durationMinutes is { } minutes ? minutes * 60 : null,
            null)
        {
            IqHistory = history.TryGetValue(key, out var samples) ? samples : [],
            IncompleteCostSamples = NonNegativeLong(value.NumberProperty("incomplete_cost_samples")),
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<RadarIqSample>> ParseHistory(
        JsonElement? historyValues)
    {
        var histories = new Dictionary<string, List<RadarIqSample>>(StringComparer.OrdinalIgnoreCase);
        if (historyValues is null) return new Dictionary<string, IReadOnlyList<RadarIqSample>>();

        foreach (var snapshot in historyValues.Value.EnumerateArray())
        {
            var observedAt = ParseDate(snapshot.StringProperty("at", "timestamp"));
            var points = snapshot.ArrayProperty("points");
            if (observedAt is null || points is null) continue;

            foreach (var point in points.Value.EnumerateArray())
            {
                var model = point.StringProperty("model");
                var effort = point.StringProperty("effort");
                var score = Finite(point.NumberProperty("iq"));
                if (string.IsNullOrWhiteSpace(model)
                    || string.IsNullOrWhiteSpace(effort)
                    || score is null)
                {
                    continue;
                }

                var key = ModelKey(model, effort);
                if (!histories.TryGetValue(key, out var samples))
                {
                    samples = [];
                    histories[key] = samples;
                }
                samples.Add(new RadarIqSample(observedAt.Value, score.Value));
            }
        }

        return histories.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<RadarIqSample>)entry.Value
                .GroupBy(sample => sample.ObservedAt)
                .Select(group => group.Last())
                .OrderBy(sample => sample.ObservedAt)
                .TakeLast(96)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static RadarModel Overlay(RadarModel existing, RadarModel measurement) =>
        existing with
        {
            Score = measurement.Score ?? existing.Score,
            Passed = measurement.Passed ?? existing.Passed,
            ValidTasks = measurement.ValidTasks ?? existing.ValidTasks,
            CostUsd = measurement.CostUsd ?? existing.CostUsd,
            AverageTaskSeconds = measurement.AverageTaskSeconds ?? existing.AverageTaskSeconds,
            IqHistory = measurement.IqHistory.Count > 0 ? measurement.IqHistory : existing.IqHistory,
            IncompleteCostSamples = measurement.IncompleteCostSamples ?? existing.IncompleteCostSamples,
        };

    private static string ModelKey(RadarModel model) => ModelKey(model.Model, model.ReasoningEffort);

    private static string ModelKey(string model, string? effort) => $"{model}\n{effort}";

    private static bool SameModel(RadarModel left, RadarModel right) =>
        string.Equals(left.Model, right.Model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ReasoningEffort, right.ReasoningEffort, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null || left >= right ? left : right;

    private static double? Finite(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static double? FiniteNonNegative(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

    private static long? NonNegativeLong(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0 && number < long.MaxValue
            ? (long)Math.Round(number)
            : null;
}

public static class RadarParser
{
    public static ProviderRadarSnapshot Parse(string summaryJson, DateTimeOffset now)
    {
        using var summary = JsonDocument.Parse(summaryJson);
        var root = summary.RootElement;
        var modelIq = root.ObjectProperty("model_iq")
            ?? throw new JsonException("Radar response did not include model_iq.");
        var latest = modelIq.ObjectProperty("latest")
            ?? throw new JsonException("Radar response did not include model_iq.latest.");
        var date = latest.StringProperty("date")
            ?? throw new JsonException("Radar response did not include model_iq.latest.date.");
        var primary = ParseModel(latest, null) with
        {
            IqHistory = RadarIqHistoryParser.Parse(modelIq.ArrayProperty("recent_days"), "score"),
        };
        var comparisons = new List<RadarModel>();
        var comparisonRows = modelIq.ObjectProperty("comparisons");
        if (comparisonRows is not null)
        {
            foreach (var comparison in comparisonRows.Value.EnumerateObject())
            {
                var value = comparison.Value.ObjectProperty("latest") ?? comparison.Value;
                if (!string.Equals(value.StringProperty("date"), date, StringComparison.Ordinal)) continue;
                comparisons.Add(ParseModel(value, comparison.Value.StringProperty("label")) with
                {
                    IqHistory = RadarIqHistoryParser.Parse(
                        comparison.Value.ArrayProperty("recent_days"),
                        "score"),
                });
            }
        }

        var deduplicated = RadarSnapshotLimits.TrimComparisons(primary, comparisons);
        return new ProviderRadarSnapshot(
            ProviderKind.Codex,
            $"model_iq:{date}",
            ParseDate(modelIq.StringProperty("updated_at"))
                ?? ParseDate(date)
                ?? ParseDate(root.StringProperty("monitored_at")),
            now,
            primary,
            deduplicated)
        {
            ResetWindow = ParseResetWindow(root),
        };
    }

    private static RadarResetWindow? ParseResetWindow(JsonElement root)
    {
        var window = root.ObjectProperty("window");
        if (window is null) return null;

        bool? open = null;
        if (window.Value.TryGetProperty("open", out var openValue)
            && openValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            open = openValue.GetBoolean();
        }

        var openedAt = ParseDate(window.Value.StringProperty("opened_at"));
        var closedAt = ParseDate(window.Value.StringProperty("closed_at"));
        if (open is null && openedAt is null && closedAt is null) return null;

        return new RadarResetWindow(
            open ?? false,
            openedAt,
            closedAt,
            window.Value.StringProperty("source_url", "source"))
        {
            TargetAt = ParseDate(window.Value.StringProperty(
                "target_at",
                "expected_at",
                "scheduled_at")),
            Scope = window.Value.StringProperty("scope"),
        };
    }

    private static RadarModel ParseModel(JsonElement value, string? preferredLabel)
    {
        var model = value.StringProperty("model") ?? preferredLabel ?? "Model";
        var effort = value.StringProperty("reasoning_effort");
        var label = preferredLabel
            ?? string.Join(' ', new[] { model, effort }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new RadarModel(
            model,
            label,
            effort,
            Finite(value.NumberProperty("score")),
            value.StringProperty("status"),
            Long(value, "passed"),
            Long(value, "valid_tasks", "tasks"),
            FiniteNonNegative(value.NumberProperty("average_cost_usd")),
            FiniteNonNegative(value.NumberProperty("average_task_seconds")),
            value.StringProperty("average_task_time_human"));
    }

    private static bool SameModel(RadarModel left, RadarModel right) =>
        string.Equals(left.Model, right.Model, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ReasoningEffort, right.ReasoningEffort, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static long? Long(JsonElement value, params string[] names)
    {
        var number = value.NumberProperty(names);
        return number is { } candidate
               && double.IsFinite(candidate)
               && candidate >= 0
               && candidate < long.MaxValue
            ? (long)Math.Round(candidate)
            : null;
    }

    private static double? Finite(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static double? FiniteNonNegative(double? value) =>
        value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
}

internal static class RadarIqHistoryParser
{
    public static IReadOnlyList<RadarIqSample> Parse(JsonElement? values, string scoreProperty)
    {
        if (values is null) return [];

        return values.Value
            .EnumerateArray()
            .Select(value =>
            {
                var observedAt = DateTimeOffset.TryParse(
                    value.StringProperty("timestamp", "date"),
                    out var parsed)
                        ? parsed
                        : (DateTimeOffset?)null;
                var score = value.NumberProperty(scoreProperty);
                return observedAt is not null
                       && score is { } number
                       && double.IsFinite(number)
                    ? new RadarIqSample(observedAt.Value, number)
                    : null;
            })
            .Where(sample => sample is not null)
            .Select(sample => sample!)
            .GroupBy(sample => sample.ObservedAt)
            .Select(group => group.Last())
            .OrderBy(sample => sample.ObservedAt)
            .TakeLast(RadarSnapshotLimits.MaxIqHistorySamples)
            .ToArray();
    }
}
