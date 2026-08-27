using System.Globalization;
using System.Text.Json.Serialization;

namespace ZGSTokenBar.Core;

public sealed record RadarPrimaryState(
    string Model,
    [property: JsonPropertyName("reasoning_effort")]
    string? ReasoningEffort,
    double? Score,
    string? Status);

public enum RadarAlertChangeKind
{
    Model,
    Effort,
    Status,
    Score,
}

public sealed record RadarAlertChange(
    RadarAlertChangeKind Kind,
    string? PreviousValue,
    string? CurrentValue);

public sealed class RadarAlertState
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> NotifiedEventIds { get; set; } = [];
    public string? LastNotifiedEventId { get; set; }
    public DateTimeOffset? LastNotifiedAt { get; set; }
    public RadarPrimaryState? LastNotifiedModelIqSnapshot { get; set; }
    public DateTimeOffset? LastSuccessfulFetchAt { get; set; }
    public ProviderRadarSnapshot? LastSnapshot { get; set; }
    public string? UnreadEventId { get; set; }
    public Dictionary<string, string> ViewedEventIdsBySurface { get; set; } = new(StringComparer.Ordinal);
}

public static class RadarSurfaceIds
{
    public const string Codex = "zgstokenbar.radar.codex";
    public const string DeepSeek = "zgstokenbar.radar.deepseek";
    public static IReadOnlyList<string> BuiltIn { get; } = [Codex, DeepSeek];
}

public sealed record RadarAlertDecision(
    bool ShouldNotify,
    bool ShouldSeedBaseline,
    IReadOnlyList<RadarAlertChange> Changes,
    RadarPrimaryState Current);

public static class RadarAlertTracker
{
    public static RadarAlertDecision Evaluate(
        RadarAlertState state,
        ProviderRadarSnapshot snapshot)
    {
        var current = PrimaryState(snapshot);
        if (string.Equals(state.LastNotifiedEventId, snapshot.EventId, StringComparison.Ordinal)
            || state.NotifiedEventIds.Contains(snapshot.EventId, StringComparer.Ordinal))
        {
            return new RadarAlertDecision(false, false, [], current);
        }

        var previous = state.LastNotifiedModelIqSnapshot;
        if (previous is null)
        {
            return new RadarAlertDecision(false, true, [], current);
        }

        var changes = new List<RadarAlertChange>();
        if (!string.Equals(previous.Model, current.Model, StringComparison.Ordinal))
        {
            changes.Add(new RadarAlertChange(
                RadarAlertChangeKind.Model,
                previous.Model,
                current.Model));
        }
        if (!string.Equals(previous.ReasoningEffort, current.ReasoningEffort, StringComparison.Ordinal))
        {
            changes.Add(new RadarAlertChange(
                RadarAlertChangeKind.Effort,
                previous.ReasoningEffort,
                current.ReasoningEffort));
        }
        if (!string.Equals(previous.Status, current.Status, StringComparison.Ordinal))
        {
            changes.Add(new RadarAlertChange(
                RadarAlertChangeKind.Status,
                previous.Status,
                current.Status));
        }
        if (previous.Score.HasValue != current.Score.HasValue)
        {
            changes.Add(new RadarAlertChange(
                RadarAlertChangeKind.Score,
                Score(previous.Score),
                Score(current.Score)));
        }
        else if (previous.Score is { } oldScore
                 && current.Score is { } newScore
                 && Math.Abs(newScore - oldScore) >= 5.0)
        {
            changes.Add(new RadarAlertChange(
                RadarAlertChangeKind.Score,
                Score(oldScore),
                Score(newScore)));
        }

        return new RadarAlertDecision(
            changes.Count > 0,
            false,
            changes,
            current);
    }

    public static RadarAlertState RecordBaseline(
        RadarAlertState state,
        ProviderRadarSnapshot snapshot,
        DateTimeOffset now) =>
        RecordNotification(state, snapshot, now);

    public static RadarAlertState RecordNotification(
        RadarAlertState state,
        ProviderRadarSnapshot snapshot,
        DateTimeOffset now)
    {
        var ids = (state.NotifiedEventIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !string.Equals(id, snapshot.EventId, StringComparison.Ordinal))
            .Append(snapshot.EventId)
            .TakeLast(50)
            .ToList();
        return new RadarAlertState
        {
            SchemaVersion = 1,
            NotifiedEventIds = ids,
            LastNotifiedEventId = snapshot.EventId,
            LastNotifiedAt = now,
            LastNotifiedModelIqSnapshot = PrimaryState(snapshot),
            LastSuccessfulFetchAt = snapshot.CapturedAt,
            LastSnapshot = RadarSnapshotLimits.Trim(snapshot),
            UnreadEventId = state.UnreadEventId,
            ViewedEventIdsBySurface = CopyViewedEvents(state),
        };
    }

    public static RadarAlertState RecordFetch(
        RadarAlertState state,
        ProviderRadarSnapshot snapshot) =>
        new()
        {
            SchemaVersion = 1,
            NotifiedEventIds = (state.NotifiedEventIds ?? []).TakeLast(50).ToList(),
            LastNotifiedEventId = state.LastNotifiedEventId,
            LastNotifiedAt = state.LastNotifiedAt,
            LastNotifiedModelIqSnapshot = state.LastNotifiedModelIqSnapshot,
            LastSuccessfulFetchAt = snapshot.CapturedAt,
            LastSnapshot = RadarSnapshotLimits.Trim(snapshot),
            UnreadEventId = state.LastSnapshot is not null
                            && !string.Equals(
                                state.LastSnapshot.EventId,
                                snapshot.EventId,
                                StringComparison.Ordinal)
                ? snapshot.EventId
                : state.UnreadEventId,
            ViewedEventIdsBySurface = CopyViewedEvents(state),
        };

    public static bool HasUnread(RadarAlertState state, ProviderRadarSnapshot? snapshot) =>
        RadarSurfaceIds.BuiltIn.Any(surfaceId => HasUnread(state, snapshot, surfaceId));

    public static bool HasUnread(
        RadarAlertState state,
        ProviderRadarSnapshot? snapshot,
        string surfaceId) =>
        snapshot is not null
        && !string.IsNullOrWhiteSpace(surfaceId)
        && string.Equals(state.UnreadEventId, snapshot.EventId, StringComparison.Ordinal)
        && (!(state.ViewedEventIdsBySurface ?? []).TryGetValue(surfaceId, out var viewedEventId)
            || !string.Equals(viewedEventId, snapshot.EventId, StringComparison.Ordinal));

    public static void RecordViewed(RadarAlertState state, ProviderRadarSnapshot? snapshot)
    {
        foreach (var surfaceId in RadarSurfaceIds.BuiltIn)
        {
            RecordViewed(state, snapshot, surfaceId);
        }
    }

    public static void RecordViewed(
        RadarAlertState state,
        ProviderRadarSnapshot? snapshot,
        string surfaceId)
    {
        if (!HasUnread(state, snapshot, surfaceId) || snapshot is null) return;
        state.ViewedEventIdsBySurface = CopyViewedEvents(state);
        state.ViewedEventIdsBySurface[surfaceId] = snapshot.EventId;
    }

    private static Dictionary<string, string> CopyViewedEvents(RadarAlertState state) =>
        (state.ViewedEventIdsBySurface ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key)
                && !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private static RadarPrimaryState PrimaryState(ProviderRadarSnapshot snapshot) =>
        new(
            snapshot.Primary.Model,
            snapshot.Primary.ReasoningEffort,
            snapshot.Primary.Score,
            snapshot.Primary.Status);

    private static string Score(double? value) =>
        value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a";
}
