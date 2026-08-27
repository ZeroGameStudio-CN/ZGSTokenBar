namespace ZGSTokenBar.Core;

public sealed record QuotaStabilizationResult(
    QuotaSnapshot Snapshot,
    bool ConfirmationRequired);

public sealed class QuotaSnapshotStabilizer
{
    private static readonly TimeSpan ResetGuard = TimeSpan.FromMinutes(2);
    private QuotaSnapshot? _pending;

    public QuotaStabilizationResult Apply(
        QuotaSnapshot previous,
        QuotaSnapshot candidate,
        DateTimeOffset now)
    {
        var suspiciousKeys = SuspiciousWindowKeys(previous, candidate, now);
        if (suspiciousKeys.Count == 0)
        {
            _pending = null;
            return new QuotaStabilizationResult(candidate, false);
        }

        if (_pending is not null && SameQuotaValues(_pending, candidate, suspiciousKeys))
        {
            _pending = null;
            return new QuotaStabilizationResult(candidate, false);
        }

        _pending = candidate;
        return new QuotaStabilizationResult(previous, true);
    }

    private static HashSet<string> SuspiciousWindowKeys(
        QuotaSnapshot previous,
        QuotaSnapshot candidate,
        DateTimeOffset now)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var oldCard in previous.Cards)
        {
            var newCard = candidate.Cards.FirstOrDefault(card => card.Key == oldCard.Key);
            if (newCard is null) continue;
            foreach (var oldWindow in oldCard.Windows)
            {
                if (oldWindow.UsedPercent is null
                    || oldWindow.ResetsAt is null
                    || oldWindow.ResetsAt <= now + ResetGuard)
                {
                    continue;
                }

                var newWindow = newCard.Windows.FirstOrDefault(window => window.Label == oldWindow.Label);
                if (newWindow?.UsedPercent is not { } newUsed) continue;
                if (oldWindow.UsedPercent.Value - newUsed >= 10)
                {
                    keys.Add(WindowKey(oldCard.Key, oldWindow.Label));
                }
            }
        }

        return keys;
    }

    private static bool SameQuotaValues(
        QuotaSnapshot left,
        QuotaSnapshot right,
        IReadOnlySet<string> keys)
    {
        var leftValues = Values(left);
        var rightValues = Values(right);
        return keys.All(key => leftValues.TryGetValue(key, out var leftValue)
            && rightValues.TryGetValue(key, out var rightValue)
            && Math.Abs(leftValue - rightValue) <= 2);
    }

    private static Dictionary<string, double> Values(QuotaSnapshot snapshot)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var card in snapshot.Cards)
        {
            foreach (var window in card.Windows)
            {
                if (window.UsedPercent is { } used)
                {
                    values[WindowKey(card.Key, window.Label)] = used;
                }
            }
        }

        return values;
    }

    private static string WindowKey(string cardKey, string windowLabel) => $"{cardKey}\0{windowLabel}";
}
