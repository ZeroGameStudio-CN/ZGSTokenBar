namespace ZGSTokenBar.Core;

public sealed record CodexResponseSample(string SessionId, string? Model, DateTimeOffset Start, DateTimeOffset End, long Output)
{
    public string? TurnId { get; init; }
    public string? ProviderId { get; init; }
}

public sealed record CodexLiveSession(string SessionId, string? Model, string Phase, DateTimeOffset? InFlightStart, DateTimeOffset LastActivity)
{
    public string? TurnId { get; init; }
    public string? ProviderId { get; init; }
}

public sealed record ConversationOutputRate(string SessionId, string? TurnId, double Measured, double Estimated)
{
    public double Total => Measured + Estimated;
    public DateTimeOffset? RecordedAt { get; init; }
    public string? ProviderId { get; init; }
    public bool HasRecordedResponse => RecordedAt is not null || Measured > 0;
}

public sealed record ProviderOutputRateShare(
    string ProviderId,
    string DisplayName,
    double Measured,
    double Estimated,
    double SharePercent);

public sealed record CodexLiveRateFeed(DateTimeOffset CapturedAt, IReadOnlyList<CodexResponseSample> Responses,
    IReadOnlyList<CodexLiveSession> Sessions, int Pending);

public sealed record LiveOutputRate(DateTimeOffset CapturedAt, double? Total, double Measured, double Estimated,
    int Generating, int Tools, int Sessions, int Pending)
{
    /// <summary>The primary UI readout excludes all model-based in-flight extrapolation.</summary>
    public double? RecordedTotal => RecordedAt is null ? null : Measured;
    public DateTimeOffset? RecordedAt { get; init; }
    public IReadOnlyList<ConversationOutputRate> Conversations { get; init; } = [];
    public IReadOnlyList<ProviderOutputRateShare> ProviderRates { get; init; } = [];
    public bool HasEstimate => Estimated > 0;
    public double EstimatedShare => Total is > 0 ? Estimated / Total.Value : 0;
}

/// <summary>Latest recorded response averages per session, with separate in-flight estimates.</summary>
public sealed class ObservedOutputRate
{
    public const int RecordedResponseMaxAgeSeconds = 60;

    public static string NormalizeProviderId(string? providerId) =>
        string.IsNullOrWhiteSpace(providerId) ? ExternalProviderTokenLedger.CodexProviderId : providerId;

    public static CodexLiveRateFeed? CombineFeeds(
        CodexLiveRateFeed? primary,
        CodexLiveRateFeed? secondary,
        DateTimeOffset now)
    {
        static bool IsFresh(CodexLiveRateFeed? feed, DateTimeOffset at) =>
            feed is not null && at >= feed.CapturedAt && at - feed.CapturedAt <= TimeSpan.FromSeconds(15);

        var primaryFresh = IsFresh(primary, now);
        var secondaryFresh = IsFresh(secondary, now);
        if (primaryFresh && secondaryFresh)
        {
            var capturedAt = primary!.CapturedAt >= secondary!.CapturedAt
                ? primary.CapturedAt
                : secondary.CapturedAt;
            return new CodexLiveRateFeed(
                capturedAt,
                primary.Responses.Concat(secondary.Responses).ToArray(),
                primary.Sessions.Concat(secondary.Sessions).ToArray(),
                primary.Pending + secondary.Pending);
        }

        if (primaryFresh) return primary;
        if (secondaryFresh) return secondary;
        return primary ?? secondary;
    }

    public LiveOutputRate Observe(CodexLiveRateFeed? feed, DateTimeOffset now)
    {
        if (feed is null || now < feed.CapturedAt || now - feed.CapturedAt > TimeSpan.FromSeconds(15))
            return new(now, null, 0, 0, 0, 0, 0, feed?.Pending ?? 0);

        var responses = feed.Responses.Where(r => r.Output >= 0 && r.End <= now && r.End >= now.AddMinutes(-30)
                && r.End >= r.Start && r.End - r.Start <= TimeSpan.FromMinutes(15))
            .GroupBy(r => (Provider: NormalizeProviderId(r.ProviderId), r.SessionId, r.End))
            .Select(g => g.Last())
            .Select(r => r with { Start = r.End - r.Start < TimeSpan.FromMilliseconds(500) ? r.End.AddMilliseconds(-500) : r.Start })
            .OrderByDescending(r => r.End).ToArray();
        var sessions = feed.Sessions
            .GroupBy(s => (Provider: NormalizeProviderId(s.ProviderId), s.SessionId))
            .Select(g => g.OrderByDescending(s => s.LastActivity).First())
            .ToArray();
        var sessionsByKey = sessions.ToDictionary(s => (Provider: NormalizeProviderId(s.ProviderId), s.SessionId));

        var window = now.AddSeconds(-3);
        // Reuse the recorded intervals without padding/dividing by the polling window.
        // A value changes on the next response, not every second after its completion.
        var recorded = responses.Where(r => now - r.End <= TimeSpan.FromSeconds(RecordedResponseMaxAgeSeconds))
            .GroupBy(r =>
            {
                var provider = NormalizeProviderId(r.ProviderId);
                var turnId = sessionsByKey.GetValueOrDefault((provider, r.SessionId))?.TurnId ?? r.TurnId;
                return (Provider: provider, r.SessionId, TurnId: turnId);
            })
            .ToDictionary(g => g.Key, g => g.First());
        var measured = recorded.ToDictionary(pair => pair.Key,
            pair => pair.Value.Output / (pair.Value.End - pair.Value.Start).TotalSeconds);

        var estimated = new Dictionary<(string Provider, string SessionId, string? TurnId), double>();
        var unavailable = new HashSet<(string Provider, string SessionId, string? TurnId)>();
        var generating = sessions.Where(s => s.Phase == "generating" && s.LastActivity <= now
            && now - s.LastActivity <= TimeSpan.FromSeconds(15) && s.InFlightStart is not null).ToArray();
        foreach (var session in generating)
        {
            var provider = NormalizeProviderId(session.ProviderId);
            var key = (provider, session.SessionId, session.TurnId);
            if (now - session.LastActivity > TimeSpan.FromSeconds(5)) continue;
            if (string.IsNullOrWhiteSpace(session.Model))
            {
                unavailable.Add(key);
                continue;
            }
            var matchingModel = responses.Where(r => r.End >= now.AddMinutes(-10)
                && string.Equals(r.Model, session.Model, StringComparison.Ordinal));
            var reference = matchingModel.Where(r => NormalizeProviderId(r.ProviderId) == provider
                && r.SessionId == session.SessionId
                && r.End >= now.AddMinutes(-10)).Take(3).ToArray();
            if (reference.Length == 0) reference = matchingModel.Take(10).ToArray();
            if (reference.Length == 0)
            {
                unavailable.Add(key);
                continue;
            }
            var speed = reference.Sum(r => (double)r.Output) / reference.Sum(r => (r.End - r.Start).TotalSeconds);
            var end = responses.Where(r => NormalizeProviderId(r.ProviderId) == provider && r.SessionId == session.SessionId)
                .Select(r => r.End)
                .DefaultIfEmpty(session.InFlightStart!.Value)
                .Max();
            var start = session.InFlightStart!.Value > end ? session.InFlightStart.Value : end;
            estimated[key] = speed * Overlap(window, now, start, now) / 3;
        }

        var rawProviderByKey = new Dictionary<(string Provider, string SessionId, string? TurnId), string?>();
        foreach (var s in sessions)
        {
            rawProviderByKey[(NormalizeProviderId(s.ProviderId), s.SessionId, s.TurnId)] = s.ProviderId;
        }
        foreach (var pair in recorded)
        {
            rawProviderByKey.TryAdd(pair.Key, pair.Value.ProviderId);
        }

        var rates = sessions.Select(s => (Provider: NormalizeProviderId(s.ProviderId), s.SessionId, s.TurnId))
            .Concat(measured.Keys).Distinct()
            .Where(key => !unavailable.Contains(key) || measured.ContainsKey(key))
            .Select(key => new ConversationOutputRate(key.SessionId, key.TurnId,
                measured.GetValueOrDefault(key), estimated.GetValueOrDefault(key))
                {
                    RecordedAt = recorded.GetValueOrDefault(key)?.End,
                    ProviderId = rawProviderByKey.GetValueOrDefault(key),
                }).ToArray();

        var known = recorded.Count > 0 || (sessions.Length > 0 || responses.Length > 0)
            && !(unavailable.Count > 0 && rates.All(rate => rate.Total == 0));
        var totalMeasured = rates.Sum(r => r.Measured);
        var providerRates = BuildProviderShares(rates, totalMeasured);

        return new(now, known ? rates.Sum(r => r.Total) : null, totalMeasured, rates.Sum(r => r.Estimated), generating.Length,
            sessions.Count(s => s.Phase == "tools" && s.LastActivity <= now && now - s.LastActivity <= TimeSpan.FromSeconds(15)), sessions.Length, feed.Pending)
            {
                Conversations = rates,
                RecordedAt = recorded.Values.Select(r => (DateTimeOffset?)r.End).Max(),
                ProviderRates = providerRates,
            };
    }

    private static IReadOnlyList<ProviderOutputRateShare> BuildProviderShares(
        ConversationOutputRate[] rates,
        double totalMeasured)
    {
        if (rates.Length == 0) return [];
        var grouped = rates
            .Where(r => r.Measured > 0)
            .GroupBy(r => NormalizeProviderId(r.ProviderId), StringComparer.Ordinal)
            .Select(group =>
            {
                var measured = group.Sum(item => item.Measured);
                var estimated = group.Sum(item => item.Estimated);
                var sharePercent = totalMeasured > 0 ? measured / totalMeasured * 100.0 : 0d;
                return new ProviderOutputRateShare(group.Key, DisplayName(group.Key), measured, estimated, sharePercent);
            })
            .OrderByDescending(share => share.Measured)
            .ThenBy(share => ProviderOrder(share.ProviderId))
            .ThenBy(share => share.ProviderId, StringComparer.Ordinal)
            .ToArray();
        return grouped;
    }

    private static int ProviderOrder(string providerId) => providerId switch
    {
        ExternalProviderTokenLedger.CodexProviderId => 0,
        ExternalProviderTokenLedger.AntigravityProviderId => 1,
        ExternalProviderTokenLedger.GrokProviderId => 2,
        ExternalProviderTokenLedger.ClaudeProviderId => 3,
        _ => 99,
    };

    private static string DisplayName(string providerId) => providerId switch
    {
        ExternalProviderTokenLedger.CodexProviderId => "Codex",
        ExternalProviderTokenLedger.AntigravityProviderId => "AGY",
        ExternalProviderTokenLedger.GrokProviderId => "Grok",
        ExternalProviderTokenLedger.ClaudeProviderId => "Claude",
        _ => providerId,
    };

    private static double Overlap(DateTimeOffset a, DateTimeOffset b, DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, ((b < end ? b : end) - (a > start ? a : start)).TotalSeconds);
}
