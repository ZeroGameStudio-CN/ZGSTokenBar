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
    private static readonly TimeSpan UrgencyWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumQuotaAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RedeemLead = TimeSpan.FromMinutes(30);

    public static CodexUsageStrategy Create(
        IReadOnlyList<CodexAccountInfo> accounts,
        IReadOnlyList<CodexAccountQuota> quotas,
        DateTimeOffset now,
        IReadOnlyDictionary<string, QuotaPaceEstimate>? paceEstimates = null)
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
            .ThenBy(row => row.State == CodexStrategyState.Ready && row.HasLittleQuota)
            .ThenBy(row => row.SuggestedStartAt ?? row.PriorityDeadline ?? row.Recovery ?? DateTimeOffset.MaxValue)
            .ThenByDescending(row => row.ConsumptionPressure)
            .ThenByDescending(row => row.UpcomingEvents.Count(item => !item.Recovery))
            .ThenByDescending(row => row.UpcomingEvents.Sum(item => item.CardCount ?? 0))
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
        return new(rows, urgent) { EvaluatedAt = now };
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
        var currentHours = double.PositiveInfinity;
        var fullHours = double.PositiveInfinity;
        foreach (var progress in row.Progress)
        {
            var window = progress.Window;
            var estimate = progress.Pace;
            if (estimate is null || estimate.Status is not (QuotaPaceStatus.ProjectedExhaustion or QuotaPaceStatus.ResetsBeforeExhaustion)
                || estimate.ValidUntil is not { } validUntil || validUntil <= now
                || estimate.Recent is not { Confidence: not QuotaTrendConfidence.Provisional } trend
                || trend.ObservedSpan < TimeSpan.FromMinutes(5)
                || !double.IsFinite(trend.PercentPerHour) || trend.PercentPerHour <= 0) return row;
            currentHours = Math.Min(currentHours, (100 - window.UsedPercent!.Value) / trend.PercentPerHour);
            fullHours = Math.Min(fullHours, 100 / trend.PercentPerHour);
        }
        if (!double.IsFinite(currentHours) || !double.IsFinite(fullHours)) return row;
        DateTimeOffset? start = null;
        double? pressure = null;
        var cumulativeCards = 0L;
        var knownCardCounts = true;
        foreach (var item in row.UpcomingEvents.Where(item => !item.Recovery))
        {
            // These are alternative deadlines for the same remaining allowance, so do not sum that allowance repeatedly.
            var workHours = currentHours;
            if (item.Kind == CodexStrategyDeadline.ResetCredit)
            {
                knownCardCounts &= item.CardCount is not null && item.CoversMainQuota;
                cumulativeCards += item.CardCount ?? 0;
                // A scheduled reset changes the available round. Re-evaluate it from fresh quota at that time.
                if (!knownCardCounts || row.Events.Any(other =>
                        other.Kind == CodexStrategyDeadline.Reset && other.At <= item.At)) continue;
                var subscriptionEnds = row.Events.Any(other => other.Kind == CodexStrategyDeadline.Subscription && other.At <= item.At);
                workHours += (subscriptionEnds ? cumulativeCards : Math.Max(0, cumulativeCards - 1)) * fullHours;
            }
            else if (item.Kind == CodexStrategyDeadline.Subscription
                && !row.Events.Any(other => other.Kind == CodexStrategyDeadline.Reset && other.At <= item.At))
            {
                var credits = row.Events.Where(other => other.Kind == CodexStrategyDeadline.ResetCredit && other.At <= item.At).ToArray();
                if (credits.All(credit => credit.CardCount is not null && credit.CoversMainQuota))
                    workHours += credits.Sum(credit => (long)credit.CardCount!.Value) * fullHours;
            }
            var availableHours = (item.At - now).TotalHours;
            var candidateStart = now.AddHours(Math.Max(0, availableHours - workHours));
            var candidatePressure = workHours / availableHours;
            if (start is null || candidateStart < start || candidateStart == start && candidatePressure > pressure)
            {
                start = candidateStart;
                pressure = candidatePressure;
            }
        }
        return row with { SuggestedStartAt = start, ConsumptionPressure = pressure };
    }

    private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right) =>
        right is null || left < right ? left : right;
}
