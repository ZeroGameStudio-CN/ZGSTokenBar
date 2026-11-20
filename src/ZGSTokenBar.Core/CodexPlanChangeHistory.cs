namespace ZGSTokenBar.Core;

/// <summary>
/// One poll of an account's plan and quota windows. The account id is the stable provider id.
/// Email, tokens and conversation text are not part of this record.
/// </summary>
public sealed record CodexPlanObservation(
    string AccountId,
    DateTimeOffset ObservedAt,
    string? Plan,
    IReadOnlyList<QuotaWindow>? Windows);

public sealed record CodexPlanWindowRecord(string Label, DateTimeOffset? ResetsAt, long DurationTicks);

public sealed record CodexPlanTransition(
    string AccountId,
    string FromPlan,
    string ToPlan,
    DateTimeOffset PreviousObservedAt,
    DateTimeOffset ObservedAt,
    IReadOnlyList<CodexPlanWindowRecord> PreviousWindows)
{
    /// <summary>
    /// The plan differed between these two polls. The change happened somewhere in that interval;
    /// <see cref="ObservedAt"/> is the first poll that showed the new plan, not the downgrade instant.
    /// </summary>
    public (DateTimeOffset PreviousObservedAt, DateTimeOffset ObservedAt) ObservationInterval =>
        (PreviousObservedAt, ObservedAt);
}

public sealed class CodexPlanChangeHistoryDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<CodexPlanAccountRecord> Accounts { get; set; } = [];
}

public sealed class CodexPlanAccountRecord
{
    public string AccountId { get; set; } = "";
    public string Plan { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public List<CodexPlanWindowRecord> Windows { get; set; } = [];
    public List<CodexPlanTransition> Transitions { get; set; } = [];
}

/// <summary>
/// Bounded plan and quota-window history. A Pro→Free answer uses the last paid weekly
/// <c>reset_at</c> and the first Free poll, never the Free plan's new reset.
/// </summary>
public sealed class CodexPlanChangeHistory
{
    public const int MaximumAccounts = 32;
    public const int MaximumTransitionsPerAccount = 16;
    public const int MaximumTransitions = 64;
    public const int MaximumFileBytes = 48 * 1024;

    private readonly List<AccountState> _accounts = [];

    public static bool IsStableAccountId(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return false;
        var id = accountId.Trim();
        if (id.Length is 0 or > 128) return false;
        foreach (var character in id)
        {
            if (char.IsWhiteSpace(character) || character is '@' or '/' or '\\' or '"' or '\'') return false;
        }

        return true;
    }

    public static CodexPlanChangeHistory Load(CodexPlanChangeHistoryDocument? document)
    {
        var history = new CodexPlanChangeHistory();
        if (document?.Accounts is null) return history;
        foreach (var account in document.Accounts)
        {
            if (!IsStableAccountId(account.AccountId) || CodexPlanNormalization.Normalize(account.Plan) is not { } plan)
                continue;
            var state = new AccountState(account.AccountId.Trim(), plan, account.ObservedAt, SanitizeStored(account.Windows));
            foreach (var transition in account.Transitions ?? [])
            {
                if (CodexPlanNormalization.Normalize(transition.FromPlan) is not { } fromPlan
                    || CodexPlanNormalization.Normalize(transition.ToPlan) is not { } toPlan
                    || transition.ObservedAt < transition.PreviousObservedAt)
                {
                    continue;
                }

                state.Transitions.Add(transition with
                {
                    AccountId = state.AccountId,
                    FromPlan = fromPlan,
                    ToPlan = toPlan,
                    PreviousWindows = SanitizeStored(transition.PreviousWindows),
                });
            }

            history._accounts.Add(state);
        }

        history.Trim();
        return history;
    }

    public bool Observe(IEnumerable<CodexPlanObservation>? observations)
    {
        if (observations is null) return false;
        var changed = false;
        foreach (var observation in observations) changed |= Observe(observation);
        return changed;
    }

    public bool Observe(CodexPlanObservation observation)
    {
        if (!IsStableAccountId(observation.AccountId)) return false;
        var plan = CodexPlanNormalization.Normalize(observation.Plan);
        if (plan is null) return false;

        var accountId = observation.AccountId.Trim();
        var incoming = Sanitize(observation.Windows);
        var state = _accounts.FirstOrDefault(account => string.Equals(account.AccountId, accountId, StringComparison.Ordinal));
        if (state is null)
        {
            _accounts.Add(new AccountState(accountId, plan, observation.ObservedAt, incoming));
            Trim();
            return true;
        }

        if (observation.ObservedAt < state.ObservedAt) return false;
        if (string.Equals(plan, state.Plan, StringComparison.Ordinal))
        {
            var merged = incoming.Count == 0 ? state.Windows : MergeWindows(state.Windows, incoming);
            var sameInstant = observation.ObservedAt == state.ObservedAt && SameWindows(merged, state.Windows);
            state.ObservedAt = observation.ObservedAt;
            state.Windows = merged;
            return !sameInstant;
        }

        var previousWindows = state.Windows.Select(window => window).ToArray();
        state.Transitions.Add(new CodexPlanTransition(
            accountId,
            state.Plan,
            plan,
            state.ObservedAt,
            observation.ObservedAt,
            previousWindows));
        while (state.Transitions.Count > MaximumTransitionsPerAccount) state.Transitions.RemoveAt(0);
        state.Plan = plan;
        state.ObservedAt = observation.ObservedAt;
        state.Windows = incoming;
        Trim();
        return true;
    }

    public CodexPlanTransition? FirstTransition(string accountId, string fromPlan, string toPlan)
    {
        var from = CodexPlanNormalization.Normalize(fromPlan);
        var to = CodexPlanNormalization.Normalize(toPlan);
        if (!IsStableAccountId(accountId) || from is null || to is null) return null;
        return _accounts
            .FirstOrDefault(account => string.Equals(account.AccountId, accountId.Trim(), StringComparison.Ordinal))
            ?.Transitions
            .FirstOrDefault(transition => transition.FromPlan == from && transition.ToPlan == to);
    }

    /// <summary>
    /// Time from the first poll of the new plan until the previous plan's weekly reset.
    /// A negative span means that reset was already in the past at the first new-plan poll.
    /// </summary>
    public static TimeSpan? RemainingUntilPreviousWeekReset(CodexPlanTransition transition)
    {
        var weekly = transition.PreviousWindows
            .Where(window => window.ResetsAt is not null && IsWeek(window))
            .ToArray();
        var window = weekly.FirstOrDefault(item => item.Label == "7d") ?? weekly.FirstOrDefault();
        return window?.ResetsAt is { } reset ? reset - transition.ObservedAt : null;
    }

    public CodexPlanChangeHistoryDocument Export()
    {
        Trim();
        return new CodexPlanChangeHistoryDocument
        {
            SchemaVersion = CodexPlanChangeHistoryDocument.CurrentSchemaVersion,
            Accounts = _accounts.Select(account => new CodexPlanAccountRecord
            {
                AccountId = account.AccountId,
                Plan = account.Plan,
                ObservedAt = account.ObservedAt,
                Windows = account.Windows.ToList(),
                Transitions = account.Transitions.ToList(),
            }).ToList(),
        };
    }

    public static bool DropOldest(CodexPlanChangeHistoryDocument document)
    {
        var oldest = document.Accounts
            .SelectMany(account => account.Transitions.Select(transition => (account, transition)))
            .OrderBy(pair => pair.transition.ObservedAt)
            .ThenBy(pair => pair.account.AccountId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (oldest.account is not null && oldest.transition is not null)
        {
            oldest.account.Transitions.Remove(oldest.transition);
            return true;
        }

        if (document.Accounts.Count == 0) return false;
        var account = document.Accounts.OrderBy(item => item.ObservedAt).First();
        document.Accounts.Remove(account);
        return true;
    }

    private void Trim()
    {
        while (CountTransitions() > MaximumTransitions)
        {
            var oldest = _accounts
                .SelectMany(account => account.Transitions.Select(transition => (account, transition)))
                .OrderBy(pair => pair.transition.ObservedAt)
                .First();
            oldest.account.Transitions.Remove(oldest.transition);
        }

        while (_accounts.Count > MaximumAccounts)
        {
            var oldest = _accounts.OrderBy(account => account.ObservedAt).First();
            _accounts.Remove(oldest);
        }
    }

    private int CountTransitions() => _accounts.Sum(account => account.Transitions.Count);

    private static bool IsWeek(CodexPlanWindowRecord window) =>
        window.Label is "7d" or "1w" || window.DurationTicks == TimeSpan.FromDays(7).Ticks;

    private static List<CodexPlanWindowRecord> Sanitize(IReadOnlyList<QuotaWindow>? windows)
    {
        if (windows is null) return [];
        var list = new List<CodexPlanWindowRecord>();
        foreach (var window in windows)
        {
            if (string.IsNullOrWhiteSpace(window.Label)) continue;
            var label = window.Label.Trim();
            if (label.Length > 16 || label.Contains('@')) continue;
            if (window.ResetsAt is null && window.Duration <= TimeSpan.Zero) continue;
            if (list.Any(existing => existing.Label == label)) continue;
            list.Add(new CodexPlanWindowRecord(label, window.ResetsAt, window.Duration.Ticks < 0 ? 0 : window.Duration.Ticks));
            if (list.Count == 8) break;
        }

        return list;
    }

    private static List<CodexPlanWindowRecord> SanitizeStored(IReadOnlyList<CodexPlanWindowRecord>? windows)
    {
        if (windows is null) return [];
        return windows
            .Where(window => !string.IsNullOrWhiteSpace(window.Label)
                && window.Label.Length <= 16
                && !window.Label.Contains('@')
                && window.DurationTicks >= 0
                && (window.ResetsAt is not null || window.DurationTicks > 0))
            .GroupBy(window => window.Label, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(8)
            .ToList();
    }

    private static List<CodexPlanWindowRecord> MergeWindows(
        IReadOnlyList<CodexPlanWindowRecord> previous,
        IReadOnlyList<CodexPlanWindowRecord> incoming)
    {
        var merged = previous.ToList();
        foreach (var window in incoming)
        {
            var index = merged.FindIndex(item => item.Label == window.Label);
            if (index < 0)
            {
                if (merged.Count < 8) merged.Add(window);
                continue;
            }

            var prior = merged[index];
            merged[index] = new CodexPlanWindowRecord(
                window.Label,
                window.ResetsAt ?? prior.ResetsAt,
                window.DurationTicks > 0 ? window.DurationTicks : prior.DurationTicks);
        }

        return merged;
    }

    private static bool SameWindows(IReadOnlyList<CodexPlanWindowRecord> left, IReadOnlyList<CodexPlanWindowRecord> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair => pair.First.Label == pair.Second.Label
            && pair.First.ResetsAt == pair.Second.ResetsAt
            && pair.First.DurationTicks == pair.Second.DurationTicks);

    private sealed class AccountState(string accountId, string plan, DateTimeOffset observedAt, List<CodexPlanWindowRecord> windows)
    {
        public string AccountId { get; } = accountId;
        public string Plan { get; set; } = plan;
        public DateTimeOffset ObservedAt { get; set; } = observedAt;
        public List<CodexPlanWindowRecord> Windows { get; set; } = windows;
        public List<CodexPlanTransition> Transitions { get; } = [];
    }
}
