namespace ZGSTokenBar.Core;

public sealed record CodexModelSpeed(string Model, string? Effort, string? ServiceTier,
    int SampleCount, double AverageTokensPerSecond, double MaximumTurnTokensPerSecond, DateTimeOffset LatestAt);

public static class CodexConversationSpeeds
{
    public static IReadOnlyList<CodexModelSpeed> Build(CodexTokenUsageSummary usage, DateTimeOffset now) =>
        usage.ConversationTurns.Where(turn => turn.Completed && !turn.Interrupted && !turn.Stale
            && turn.UsageComplete && !string.IsNullOrWhiteSpace(turn.Model) && turn.OutputTokens > 0
            && double.IsFinite(turn.ElapsedSeconds) && turn.ElapsedSeconds > 0
            && turn.ObservedAt >= now.AddHours(-24) && turn.ObservedAt <= now.AddMinutes(5))
        .GroupBy(turn => (turn.Model, turn.Effort, turn.ServiceTier))
        .Select(group => new CodexModelSpeed(group.Key.Model!, group.Key.Effort, group.Key.ServiceTier,
            group.Count(), group.Sum(turn => (double)turn.OutputTokens) / group.Sum(turn => turn.ElapsedSeconds),
            group.Max(turn => turn.OutputTokens / turn.ElapsedSeconds), group.Max(turn => turn.ObservedAt)))
        .OrderByDescending(row => row.LatestAt).ThenBy(row => row.Model, StringComparer.Ordinal)
        .ThenBy(row => row.Effort, StringComparer.Ordinal).ThenBy(row => row.ServiceTier, StringComparer.Ordinal).ToArray();
}
