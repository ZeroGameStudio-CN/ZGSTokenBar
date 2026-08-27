namespace ZGSTokenBar.Core;

public sealed record QuotaMilestoneAlert(
    string CardLabel,
    string WindowLabel,
    int Threshold,
    double UsedPercent,
    DateTimeOffset? ResetsAt);

public sealed class QuotaMilestoneTracker
{
    private static readonly int[] Thresholds = [25, 50, 75, 90, 100];
    private static readonly TimeSpan ResetTimeTolerance = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, WindowState> _states = new(StringComparer.Ordinal);

    public QuotaMilestoneTracker(QuotaSnapshot initialSnapshot)
    {
        Seed(initialSnapshot);
    }

    public IReadOnlyList<QuotaMilestoneAlert> Observe(QuotaSnapshot snapshot)
    {
        var alerts = new List<QuotaMilestoneAlert>();
        foreach (var card in snapshot.Cards)
        {
            foreach (var window in card.Windows)
            {
                if (window.UsedPercent is null) continue;
                var used = Math.Clamp(window.UsedPercent.Value, 0, 100);
                var key = $"{card.Key}\0{window.Label}";
                if (!_states.TryGetValue(key, out var state))
                {
                    _states[key] = new WindowState(used, window.ResetsAt);
                    continue;
                }

                if (StartsNewCycle(state, window, used))
                {
                    state.HighestUsedPercent = used;
                    state.ResetsAt = window.ResetsAt;
                    continue;
                }

                var crossed = Thresholds
                    .Where(threshold => threshold > state.HighestUsedPercent && threshold <= used)
                    .DefaultIfEmpty(0)
                    .Max();

                state.HighestUsedPercent = Math.Max(state.HighestUsedPercent, used);
                if (window.ResetsAt is not null) state.ResetsAt = window.ResetsAt;
                if (crossed == 0) continue;

                alerts.Add(new QuotaMilestoneAlert(
                    card.Label,
                    window.Label,
                    crossed,
                    used,
                    window.ResetsAt));
            }
        }

        return alerts;
    }

    private void Seed(QuotaSnapshot snapshot)
    {
        foreach (var card in snapshot.Cards)
        {
            foreach (var window in card.Windows)
            {
                if (window.UsedPercent is null) continue;
                var key = $"{card.Key}\0{window.Label}";
                _states[key] = new WindowState(
                    Math.Clamp(window.UsedPercent.Value, 0, 100),
                    window.ResetsAt);
            }
        }
    }

    private static bool StartsNewCycle(WindowState state, QuotaWindow window, double used)
    {
        if (state.ResetsAt is not null && window.ResetsAt is not null)
        {
            return (state.ResetsAt.Value - window.ResetsAt.Value).Duration() > ResetTimeTolerance;
        }

        return used + 5 < state.HighestUsedPercent;
    }

    private sealed class WindowState(double highestUsedPercent, DateTimeOffset? resetsAt)
    {
        public double HighestUsedPercent { get; set; } = highestUsedPercent;
        public DateTimeOffset? ResetsAt { get; set; } = resetsAt;
    }
}
