using ZGSTokenBar.Core;

namespace ZGSTokenBar.App;

internal enum CodexStrategyState { Ready, Waiting, Refresh, Expired }
internal enum CodexStrategyDeadline { Reset, Subscription, ResetCredit }

internal sealed record CodexStrategyEvent(
    CodexStrategyDeadline Kind,
    DateTimeOffset At,
    string? WindowLabel = null,
    bool Recovery = false,
    int? CardCount = null,
    DateTimeOffset? CardExpiresAt = null,
    bool CoversMainQuota = false);

internal sealed record CodexStrategyProgress(QuotaWindow Window, QuotaPaceEstimate? Pace);

internal sealed record CodexRenewalAdvice(
    CodexAccountInfo Account,
    DateTimeOffset RecordedResetAt,
    DateTimeOffset LastObservedAt,
    double HighestUsedPercent);

internal sealed record CodexStrategyAccount(
    CodexAccountInfo Account,
    IReadOnlyList<QuotaWindow> Windows,
    CodexStrategyState State,
    CodexStrategyDeadline? DeadlineKind,
    DateTimeOffset? Deadline,
    DateTimeOffset? Recovery,
    DateTimeOffset? CardExpiry,
    DateTimeOffset? RedeemBy,
    DateTimeOffset? ReviewBy,
    int? Cards,
    bool CanRedeem,
    bool ResetBeforeCard)
{
    public bool HasLittleQuota => Windows.Any(window => window.UsedPercent >= 95);
    public int? Priority { get; init; }
    public IReadOnlyList<CodexStrategyEvent> Events { get; init; } = [];
    public IReadOnlyList<CodexStrategyEvent> UpcomingEvents { get; init; } = [];
    public DateTimeOffset? SuggestedStartAt { get; init; }
    public double? ConsumptionPressure { get; init; }
    public CodexStrategyEvent? PressureEvent { get; init; }
    public bool PressureIncludesReset { get; init; }
    public bool PressureIncludesCredit { get; init; }
    public double? ConsumptionHours { get; init; }
    public int? UnresolvedCards { get; init; }
    public IReadOnlyList<CodexStrategyProgress> Progress { get; init; } = [];
    public DateTimeOffset? PriorityDeadline =>
        RedeemBy is { } redeem && (Deadline is null || redeem < Deadline) ? redeem : Deadline;
    public CodexStrategyDeadline? PriorityReason =>
        RedeemBy is { } redeem && (Deadline is null || redeem < Deadline)
            ? CodexStrategyDeadline.ResetCredit
            : DeadlineKind;
}

internal sealed record CodexUsageStrategy(
    IReadOnlyList<CodexStrategyAccount> Accounts,
    IReadOnlyList<(CodexStrategyAccount Account, CodexStrategyDeadline Kind, DateTimeOffset At)> Urgent)
{
    public DateTimeOffset EvaluatedAt { get; init; }
    public IReadOnlyList<CodexRenewalAdvice> Renewals { get; init; } = [];
    private static readonly TimeSpan UrgencyWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumQuotaAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RedeemLead = TimeSpan.FromMinutes(30);

    public static CodexUsageStrategy Create(
        IReadOnlyList<CodexAccountInfo> accounts,
        IReadOnlyList<CodexAccountQuota> quotas,
        DateTimeOffset now,
        IReadOnlyDictionary<string, QuotaPaceEstimate>? paceEstimates = null,
        IReadOnlyDictionary<string, CodexQuotaTokenSummary>? tokenSummaries = null)
    {
        var byAccount = quotas.ToLookup(quota => quota.AccountId, StringComparer.Ordinal);
        var rows = accounts.Where(account => CodexAccountFormatting.PlanLabel(account.Plan) is not ("API key" or "free"))
            .Select(account => (Account: account, Quota: byAccount[account.AccountId]
                .OrderByDescending(quota => quota.CapturedAt).FirstOrDefault()))
            .GroupBy(item => item.Quota?.QuotaIdentity ?? item.Account.AccountId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Quota?.CapturedAt)
                .ThenByDescending(item => item.Account.Active).First())
            .Select(item => Project(item.Account, item.Quota, now, paceEstimates))
            .OrderBy(row => row.State)
            .ThenBy(row => row.SuggestedStartAt ?? row.PriorityDeadline ?? row.Recovery ?? DateTimeOffset.MaxValue)
            .ThenByDescending(row => row.ConsumptionPressure)
            .ThenByDescending(row => row.UpcomingEvents.Count(item => !item.Recovery))
            .ThenByDescending(row => row.UpcomingEvents.Sum(item => (long)(item.CardCount ?? 0)))
            .ThenBy(row => row.HasLittleQuota)
            .ThenBy(row => row.PriorityDeadline)
            .ThenByDescending(row => row.Account.Active)
            .ThenBy(row => row.Account.AccountId, StringComparer.Ordinal)
            .Select((row, index) => row.State == CodexStrategyState.Ready
                ? row with { Priority = index + 1 }
                : row)
            .ToArray();
        var urgent = rows.SelectMany(row => row.UpcomingEvents.Where(item => !item.Recovery)
                .Select(item => (Account: row, item.Kind, item.At)))
            .OrderBy(item => item.At)
            .ToArray();
        var renewals = accounts
            .Where(account => CodexAccountFormatting.PlanLabel(account.Plan) == "free"
                || Earlier(account.SubscriptionActiveUntil, byAccount[account.AccountId]
                    .OrderByDescending(quota => quota.CapturedAt).FirstOrDefault()?.SubscriptionActiveUntil) <= now)
            .Where(account => CodexAccountFormatting.PlanLabel(account.Plan) != "API key")
            .Select(account =>
            {
                var key = byAccount[account.AccountId].OrderByDescending(quota => quota.CapturedAt).FirstOrDefault()?.CardKey;
                // Only an identity-matched weekly record can inform renewal. Free monthly limits are a different allowance.
                var history = tokenSummaries?.Values.Where(summary => key is not null && summary.CardKey == key
                        && summary.DurationTicks == TimeSpan.FromDays(7).Ticks
                        && summary.LastObservedAt is { } observed && observed <= now
                        && summary.RecordedResetAt is { } reset && reset > observed
                        && reset - observed <= TimeSpan.FromDays(7)
                        && now - reset <= TimeSpan.FromDays(7)
                        && summary.HighestUsedPercent is >= 0 and <= 100)
                    .OrderByDescending(summary => summary.LastObservedAt).FirstOrDefault();
                return history is null ? null : new CodexRenewalAdvice(account,
                    history.RecordedResetAt!.Value, history.LastObservedAt!.Value, history.HighestUsedPercent!.Value);
            })
            .OfType<CodexRenewalAdvice>()
            .OrderBy(advice => advice.RecordedResetAt)
            .ToArray();
        return new(rows, urgent) { EvaluatedAt = now, Renewals = renewals };
    }

    private static CodexStrategyAccount Project(
        CodexAccountInfo account,
        CodexAccountQuota? quota,
        DateTimeOffset now,
        IReadOnlyDictionary<string, QuotaPaceEstimate>? paceEstimates)
    {
        var subscription = Earlier(account.SubscriptionActiveUntil, quota?.SubscriptionActiveUntil);
        var windows = quota?.Windows ?? [];
        var expired = subscription <= now;
        var confirmed = windows.Count > 0
            && quota?.CapturedAt is { } captured && captured <= now && now - captured <= MaximumQuotaAge
            && windows.All(window => window.UsedPercent is >= 0 and <= 100 && !(window.ResetsAt <= now));
        var blocked = confirmed
            ? windows.Where(window => window.UsedPercent == 100).ToArray()
            : [];
        var recovery = blocked.Length > 0 && blocked.All(window => window.ResetsAt is not null)
            ? blocked.Max(window => window.ResetsAt)
            : null;
        if (recovery >= subscription) recovery = null;
        var state = expired ? CodexStrategyState.Expired
            : !confirmed ? CodexStrategyState.Refresh
            : blocked.Length > 0 ? CodexStrategyState.Waiting
            : CodexStrategyState.Ready;
        var nextReset = confirmed ? windows.Min(window => window.ResetsAt) : null;
        var deadline = state == CodexStrategyState.Ready ? Earlier(nextReset, subscription) : null;
        var deadlineKind = deadline is null ? (CodexStrategyDeadline?)null
            : deadline == subscription ? CodexStrategyDeadline.Subscription : CodexStrategyDeadline.Reset;

        var events = new List<CodexStrategyEvent>();
        if (confirmed && !expired)
        {
            events.AddRange(windows.Where(window => window.ResetsAt > now
                    && (subscription is null || window.ResetsAt <= subscription))
                .GroupBy(window => window.ResetsAt!.Value)
                .Select(group => new CodexStrategyEvent(CodexStrategyDeadline.Reset, group.Key,
                    string.Join(" / ", group.Select(QuotaDisplayFormatting.FormatWindowShort).Distinct()),
                    Recovery: group.All(window => window.UsedPercent == 100))));
        }
        if (subscription > now)
            events.Add(new(CodexStrategyDeadline.Subscription, subscription.Value));

        var knownBatches = (quota?.ResetCreditBatches ?? [])
            .Where(batch => batch.Count > 0 && batch.ExpiresAt > now)
            .Distinct()
            .Take(256)
            .GroupBy(batch => batch.ExpiresAt)
            .Select(group => (Expiry: group.Key, Count: group.Sum(batch => (long)batch.Count),
                Full: group.All(batch => batch.CoversMainQuota)))
            .Where(group => group.Count <= int.MaxValue)
            .Select(group => new CodexResetCreditBatch(group.Expiry, (int)group.Count, group.Full))
            .OrderBy(batch => batch.ExpiresAt)
            .ToArray();
        if (expired || quota?.ResetCreditsAvailable == 0) knownBatches = [];
        if (quota?.ResetCreditsAvailable is { } total && knownBatches.Sum(batch => (long)batch.Count) > total)
            knownBatches = [];
        if (!expired && quota?.ResetCreditsAvailable != 0)
        {
            events.AddRange(knownBatches.Select(batch => new CodexStrategyEvent(
                CodexStrategyDeadline.ResetCredit, Earlier(batch.ExpiresAt, subscription)!.Value,
                CardCount: batch.Count, CardExpiresAt: batch.ExpiresAt, CoversMainQuota: batch.CoversMainQuota)));
            if (quota?.NextResetCreditExpiry is { } legacyExpiry && legacyExpiry > now
                && !knownBatches.Any(batch => batch.ExpiresAt == legacyExpiry)
                && (quota.ResetCreditsAvailable is null || knownBatches.Sum(batch => (long)batch.Count) < quota.ResetCreditsAvailable))
            {
                events.Add(new(CodexStrategyDeadline.ResetCredit, Earlier(legacyExpiry, subscription)!.Value,
                    CardCount: quota.ResetCreditsAvailable - knownBatches.Sum(batch => (long)batch.Count) == 1 ? 1 : null,
                    CardExpiresAt: legacyExpiry, CoversMainQuota: quota.NextResetCreditCoversMainQuota));
            }
        }
        var ordered = events.OrderBy(item => item.At).ThenBy(item => item.Kind)
            .ThenBy(item => item.CardExpiresAt).ToArray();
        var firstCredit = ordered.FirstOrDefault(item => item.Kind == CodexStrategyDeadline.ResetCredit);
        var cardExpiry = firstCredit?.CardExpiresAt;
        var redeemBy = firstCredit?.At;
        var lastReview = redeemBy is { } cutoff ? cutoff - RedeemLead : (DateTimeOffset?)null;
        var canRedeem = redeemBy is not null && confirmed && firstCredit!.CoversMainQuota
            && windows.Any(window => window.UsedPercent > 20);
        var row = new CodexStrategyAccount(account, windows, state, deadlineKind, deadline, recovery, cardExpiry, redeemBy,
            lastReview > now ? lastReview : null,
            quota?.ResetCreditsAvailable, canRedeem, nextReset < lastReview)
        {
            Events = ordered,
            UpcomingEvents = ordered.Where(item => item.At > now && item.At - now <= UrgencyWindow).ToArray(),
            UnresolvedCards = quota?.ResetCreditsAvailable is { } count
                ? (int)Math.Max(0, count - ordered.Sum(item => (long)(item.CardCount ?? 0)))
                : null,
            Progress = windows.Select(window => new CodexStrategyProgress(window,
                quota?.CardKey is { } key
                    ? paceEstimates?.GetValueOrDefault(QuotaPaceTracker.SeriesKey(key, window))?.ForDisplay(now)
                    : null)).ToArray(),
        };
        return WithConsumptionPressure(row, now);
    }

    private static CodexStrategyAccount WithConsumptionPressure(
        CodexStrategyAccount row,
        DateTimeOffset now)
    {
        if (row.State != CodexStrategyState.Ready) return row;
        var rates = new List<(QuotaWindow Window, double Rate)>();
        foreach (var progress in row.Progress)
        {
            var window = progress.Window;
            var estimate = progress.Pace;
            if (estimate is null || estimate.Status is not (QuotaPaceStatus.ProjectedExhaustion or QuotaPaceStatus.ResetsBeforeExhaustion)
                || estimate.ValidUntil is not { } validUntil || validUntil <= now
                || estimate.Recent is not { Confidence: not QuotaTrendConfidence.Provisional } trend
                || trend.ObservedSpan < TimeSpan.FromMinutes(5)
                || !double.IsFinite(trend.PercentPerHour) || trend.PercentPerHour <= 0) return row;
            rates.Add((window, trend.PercentPerHour));
        }
        if (rates.Count == 0) return row;
        var remaining = rates.Select(rate => (100 - rate.Window.UsedPercent!.Value) / rate.Rate).ToArray();
        var full = rates.Select(rate => 100 / rate.Rate).ToArray();
        if (remaining.Concat(full).Any(value => !double.IsFinite(value))) return row;
        var fullHours = full.Min();
        double ConsumeRemaining()
        {
            var hours = remaining.Min();
            for (var index = 0; index < remaining.Length; index++) remaining[index] = Math.Max(0, remaining[index] - hours);
            return hours;
        }
        DateTimeOffset? start = null;
        double? pressure = null;
        double? consumptionHours = null;
        CodexStrategyEvent? driver = null;
        var includesReset = false;
        var includesCredit = false;
        var sawReset = false;
        var sawCredit = false;
        var workHours = 0d;
        var subscription = row.Events.FirstOrDefault(item => item.Kind == CodexStrategyDeadline.Subscription)?.At;
        foreach (var item in row.UpcomingEvents.Where(item => !item.Recovery)
                     .OrderBy(item => item.At)
                     .ThenBy(item => item.Kind == CodexStrategyDeadline.Subscription ? 2
                         : item.Kind == CodexStrategyDeadline.ResetCredit ? 1 : 0))
        {
            if (item.Kind == CodexStrategyDeadline.ResetCredit)
            {
                if (item.CardCount is not { } count || !item.CoversMainQuota) continue;
                // Each redemption discards the other windows' unused allowance. Re-evaluate the
                // bottleneck for a full round instead of pooling capacities across multiple rounds.
                workHours += ConsumeRemaining() + Math.Max(0, count - 1) * fullHours;
                Array.Copy(full, remaining, full.Length);
                sawCredit = true;
            }
            else workHours += ConsumeRemaining();
            if (!double.IsFinite(workHours)) continue;
            var availableHours = (item.At - now).TotalHours;
            var candidateStart = now.AddHours(Math.Max(0, availableHours - workHours));
            var candidatePressure = workHours / availableHours;
            if (start is null || candidateStart < start || candidateStart == start && candidatePressure > pressure)
            {
                start = candidateStart;
                pressure = candidatePressure;
                consumptionHours = workHours;
                driver = item;
                includesReset = sawReset || item.Kind == CodexStrategyDeadline.Reset;
                includesCredit = sawCredit;
            }
            // Only a window's own observed reset replenishes it, never a new cycle at subscription end.
            // Later forecasts require a new observation when that reset actually happens.
            if (item.Kind == CodexStrategyDeadline.Reset && (subscription is null || item.At < subscription))
            {
                for (var index = 0; index < rates.Count; index++)
                    if (rates[index].Window.ResetsAt == item.At) remaining[index] = full[index];
                sawReset = true;
            }
        }
        // An unquantified earlier deadline still needs attention; a later pace estimate cannot postpone it.
        if (row.PriorityDeadline < start) return row;
        return row with
        {
            SuggestedStartAt = start, ConsumptionPressure = pressure, PressureEvent = driver,
            PressureIncludesReset = includesReset, PressureIncludesCredit = includesCredit, ConsumptionHours = consumptionHours,
        };
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right) =>
        right is null || left < right ? left : right;
}
