namespace ZGSTokenBar.Core;

public sealed record CodexResponseSample(string SessionId, string? Model, DateTimeOffset Start, DateTimeOffset End, long Output);
public sealed record CodexLiveSession(string SessionId, string? Model, string Phase, DateTimeOffset? InFlightStart, DateTimeOffset LastActivity);
public sealed record CodexLiveRateFeed(DateTimeOffset CapturedAt, IReadOnlyList<CodexResponseSample> Responses,
    IReadOnlyList<CodexLiveSession> Sessions, int Pending);
public sealed record LiveOutputRate(DateTimeOffset CapturedAt, double? Total, double Measured, double Estimated,
    int Generating, int Tools, int Sessions, int Pending)
{
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
        var measured = responses.Sum(r => r.Output * Overlap(window, now, r.Start, r.End) / (r.End - r.Start).TotalSeconds) / 3;
        double estimated = 0;
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
            estimated += speed * Overlap(window, now, start, now) / 3;
        }
        var known = sessions.Length > 0 || responses.Length > 0 || feed.Pending == 0;
        return new(now, known ? measured + estimated : null, measured, estimated, generating.Length,
            sessions.Count(s => s.Phase == "tools" && now - s.LastActivity <= TimeSpan.FromMinutes(5)), sessions.Length, feed.Pending);
    }
    private static double Overlap(DateTimeOffset a, DateTimeOffset b, DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, ((b < end ? b : end) - (a > start ? a : start)).TotalSeconds);
}
