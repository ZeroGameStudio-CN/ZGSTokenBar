namespace ZGSTokenBar.Core;

public sealed record CodexResponseSample(string SessionId, string? Model, DateTimeOffset Start, DateTimeOffset End, long Output)
{ public string? TurnId { get; init; } }
public sealed record CodexLiveSession(string SessionId, string? Model, string Phase, DateTimeOffset? InFlightStart, DateTimeOffset LastActivity)
{ public string? TurnId { get; init; } }
public sealed record ConversationOutputRate(string SessionId, string? TurnId, double Measured, double Estimated)
{ public double Total => Measured + Estimated; }
public sealed record CodexLiveRateFeed(DateTimeOffset CapturedAt, IReadOnlyList<CodexResponseSample> Responses,
    IReadOnlyList<CodexLiveSession> Sessions, int Pending);
public sealed record LiveOutputRate(DateTimeOffset CapturedAt, double? Total, double Measured, double Estimated,
    int Generating, int Tools, int Sessions, int Pending)
{
    public IReadOnlyList<ConversationOutputRate> Conversations { get; init; } = [];
    public bool HasEstimate => Estimated > 0;
    public double EstimatedShare => Total is > 0 ? Estimated / Total.Value : 0;
}

/// <summary>Response-interval overlap in a three-second window, with explicitly separate in-flight estimates.</summary>
public sealed class ObservedOutputRate
{
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
        // A dialogue's live window belongs to its latest turn row, including output just before a turn boundary.
        var measured = responses.GroupBy(r => (r.SessionId, TurnId: sessions.FirstOrDefault(s => s.SessionId == r.SessionId)?.TurnId ?? r.TurnId)).ToDictionary(g => g.Key,
            g => g.Sum(r => r.Output * Overlap(window, now, r.Start, r.End) / (r.End - r.Start).TotalSeconds) / 3);
        var estimated = new Dictionary<(string SessionId, string? TurnId), double>();
        var generating = sessions.Where(s => s.Phase == "generating" && s.LastActivity <= now
            && now - s.LastActivity <= TimeSpan.FromMinutes(5) && s.InFlightStart is not null).ToArray();
        foreach (var session in generating)
        {
            var reference = responses.Where(r => r.SessionId == session.SessionId && r.End >= now.AddMinutes(-10)).Take(3).ToArray();
            if (reference.Length == 0 && session.Model is not null)
                reference = responses.Where(r => r.Model == session.Model).Take(10).ToArray();
            if (reference.Length == 0) reference = responses.Take(10).ToArray();
            if (reference.Length == 0) continue;
            var speed = reference.Sum(r => (double)r.Output) / reference.Sum(r => (r.End - r.Start).TotalSeconds);
            var end = responses.Where(r => r.SessionId == session.SessionId).Select(r => r.End).DefaultIfEmpty(session.InFlightStart!.Value).Max();
            var start = session.InFlightStart!.Value > end ? session.InFlightStart.Value : end;
            estimated[(session.SessionId, session.TurnId)] = speed * Overlap(window, now, start, now) / 3;
        }
        var rates = sessions.Select(s => (s.SessionId, s.TurnId))
            .Concat(measured.Where(pair => pair.Value > 0).Select(pair => pair.Key)).Distinct()
            .Select(key => new ConversationOutputRate(key.SessionId, key.TurnId,
                measured.GetValueOrDefault(key), estimated.GetValueOrDefault(key))).ToArray();
        var known = sessions.Length > 0 || responses.Length > 0;
        return new(now, known ? rates.Sum(r => r.Total) : null, rates.Sum(r => r.Measured), rates.Sum(r => r.Estimated), generating.Length,
            sessions.Count(s => s.Phase == "tools" && s.LastActivity <= now && now - s.LastActivity <= TimeSpan.FromMinutes(5)), sessions.Length, feed.Pending)
            { Conversations = rates };
    }
    private static double Overlap(DateTimeOffset a, DateTimeOffset b, DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, ((b < end ? b : end) - (a > start ? a : start)).TotalSeconds);
}
