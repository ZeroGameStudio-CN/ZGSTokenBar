namespace ZGSTokenBar.Core;

/// <summary>Observed log-counter deltas, not server decoding throughput.</summary>
public sealed class ObservedOutputRate
{
    private Dictionary<(string Session, string Turn), long> _previous = [];
    private DateTimeOffset? _at;
    public double? Observe(IReadOnlyList<CodexConversationTurn> turns, DateTimeOffset now)
    {
        var rows = turns.GroupBy(turn => (turn.SessionId, turn.TurnId))
            .Select(group => group.OrderByDescending(turn => turn.ObservedAt).First()).ToArray();
        var elapsed = _at is { } at ? (now - at).TotalSeconds : 0;
        var readable = rows.Where(turn => !turn.Stale && turn.UsageComplete && turn.OutputTokens >= 0).ToArray();
        var valid = elapsed is > 0 and <= 15 && readable.Length > 0;
        double delta = 0;
        foreach (var turn in readable)
        {
            if (_previous.TryGetValue((turn.SessionId, turn.TurnId), out var before))
            {
                if (turn.OutputTokens < before) valid = false;
                else delta += turn.OutputTokens - before;
            }
            else if (_at is { } start && turn.StartedAt >= start && turn.ObservedAt <= now.AddSeconds(5))
                delta += turn.OutputTokens;
        }
        _previous = readable.ToDictionary(turn => (turn.SessionId, turn.TurnId), turn => turn.OutputTokens);
        _at = now;
        return valid ? delta / elapsed : null;
    }
}
