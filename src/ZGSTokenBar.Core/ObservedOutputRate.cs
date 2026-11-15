namespace ZGSTokenBar.Core;

public sealed record CodexResponseSample(string SessionId, string? Model, DateTimeOffset Start, DateTimeOffset End, long Output)
{ public string? TurnId { get; init; } }
public sealed record CodexLiveSession(string SessionId, string? Model, string Phase, DateTimeOffset? InFlightStart, DateTimeOffset LastActivity)
{ public string? TurnId { get; init; } }
public sealed record ConversationOutputRate(string SessionId, string? TurnId, double Measured, double Estimated)
{
    public double Total => Measured + Estimated;
    public DateTimeOffset? RecordedAt { get; init; }
    public bool HasRecordedResponse => RecordedAt is not null || Measured > 0;
}
public sealed record CodexLiveRateFeed(DateTimeOffset CapturedAt, IReadOnlyList<CodexResponseSample> Responses,
    IReadOnlyList<CodexLiveSession> Sessions, int Pending);
public sealed record LiveOutputRate(DateTimeOffset CapturedAt, double? Total, double Measured, double Estimated,
    int Generating, int Tools, int Sessions, int Pending)
{
    /// <summary>The primary UI readout excludes all model-based in-flight extrapolation.</summary>
    public double? RecordedTotal => RecordedAt is null ? null : Measured;
    public DateTimeOffset? RecordedAt { get; init; }
    public IReadOnlyList<ConversationOutputRate> Conversations { get; init; } = [];
    public bool HasEstimate => Estimated > 0;
    public double EstimatedShare => Total is > 0 ? Estimated / Total.Value : 0;
}

/// <summary>Latest recorded response averages per session, with separate in-flight estimates.</summary>
public sealed class ObservedOutputRate
{
    public const int RecordedResponseMaxAgeSeconds = 60;
    public LiveOutputRate Observe(CodexLiveRateFeed? feed, DateTimeOffset now)
    {
        if (feed is null || now < feed.CapturedAt || now - feed.CapturedAt > TimeSpan.FromSeconds(15))
            return new(now, null, 0, 0, 0, 0, 0, feed?.Pending ?? 0);
        var responses = feed.Responses.Where(r => r.Output >= 0 && r.End <= now && r.End >= now.AddMinutes(-30)
                && r.End >= r.Start && r.End - r.Start <= TimeSpan.FromMinutes(15))
            .GroupBy(r => (r.SessionId, r.End)).Select(g => g.Last())
            .Select(r => r with { Start = r.End - r.Start < TimeSpan.FromMilliseconds(500) ? r.End.AddMilliseconds(-500) : r.Start })
            .OrderByDescending(r => r.End).ToArray();
        var sessions = feed.Sessions.GroupBy(s => s.SessionId).Select(g => g.OrderByDescending(s => s.LastActivity).First()).ToArray();
        var window = now.AddSeconds(-3);
        // Reuse the recorded intervals without padding/dividing by the polling window.
        // A value changes on the next response, not every second after its completion.
        var recorded = responses.Where(r => now - r.End <= TimeSpan.FromSeconds(RecordedResponseMaxAgeSeconds))
            .GroupBy(r => (r.SessionId, TurnId: sessions.FirstOrDefault(s => s.SessionId == r.SessionId)?.TurnId ?? r.TurnId))
            .ToDictionary(g => g.Key, g => g.First());
        var measured = recorded.ToDictionary(pair => pair.Key,
            pair => pair.Value.Output / (pair.Value.End - pair.Value.Start).TotalSeconds);
        var estimated = new Dictionary<(string SessionId, string? TurnId), double>();
        var unavailable = new HashSet<(string SessionId, string? TurnId)>();
        var generating = sessions.Where(s => s.Phase == "generating" && s.LastActivity <= now
            && now - s.LastActivity <= TimeSpan.FromSeconds(15) && s.InFlightStart is not null).ToArray();
        foreach (var session in generating)
        {
            if (now - session.LastActivity > TimeSpan.FromSeconds(5)) continue;
            if (string.IsNullOrWhiteSpace(session.Model))
            {
                unavailable.Add((session.SessionId, session.TurnId));
                continue;
            }
            var matchingModel = responses.Where(r => r.End >= now.AddMinutes(-10)
                && string.Equals(r.Model, session.Model, StringComparison.Ordinal));
            var reference = matchingModel.Where(r => r.SessionId == session.SessionId
                && r.End >= now.AddMinutes(-10)).Take(3).ToArray();
            if (reference.Length == 0) reference = matchingModel.Take(10).ToArray();
            if (reference.Length == 0)
            {
                unavailable.Add((session.SessionId, session.TurnId));
                continue;
            }
            var speed = reference.Sum(r => (double)r.Output) / reference.Sum(r => (r.End - r.Start).TotalSeconds);
            var end = responses.Where(r => r.SessionId == session.SessionId).Select(r => r.End).DefaultIfEmpty(session.InFlightStart!.Value).Max();
            var start = session.InFlightStart!.Value > end ? session.InFlightStart.Value : end;
            estimated[(session.SessionId, session.TurnId)] = speed * Overlap(window, now, start, now) / 3;
        }
        var rates = sessions.Select(s => (s.SessionId, s.TurnId))
            .Concat(measured.Keys).Distinct()
            .Where(key => !unavailable.Contains(key) || measured.ContainsKey(key))
            .Select(key => new ConversationOutputRate(key.SessionId, key.TurnId,
                measured.GetValueOrDefault(key), estimated.GetValueOrDefault(key))
                { RecordedAt = recorded.GetValueOrDefault(key)?.End }).ToArray();
        var known = recorded.Count > 0 || (sessions.Length > 0 || responses.Length > 0)
            && !(unavailable.Count > 0 && rates.All(rate => rate.Total == 0));
        return new(now, known ? rates.Sum(r => r.Total) : null, rates.Sum(r => r.Measured), rates.Sum(r => r.Estimated), generating.Length,
            sessions.Count(s => s.Phase == "tools" && s.LastActivity <= now && now - s.LastActivity <= TimeSpan.FromSeconds(15)), sessions.Length, feed.Pending)
            { Conversations = rates, RecordedAt = recorded.Values.Select(r => (DateTimeOffset?)r.End).Max() };
    }
    private static double Overlap(DateTimeOffset a, DateTimeOffset b, DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, ((b < end ? b : end) - (a > start ? a : start)).TotalSeconds);
}
